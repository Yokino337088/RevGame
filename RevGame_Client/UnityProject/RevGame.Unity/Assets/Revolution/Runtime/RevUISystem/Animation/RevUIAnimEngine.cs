// ============================================================
// RevUIAnimEngine.cs —— UI 动画引擎内核（纯 C#，不引用 UnityEngine）
//
// 位置：Runtime\RevUISystem\Animation\（UI 动画库）
//
// 【它是什么】
//   一颗**采样模型**的动画心跳（设计文档 02 篇哲学）：
//     · 引擎只管"时间 → 系数"，**不碰任何 Unity 对象**（写目标是 Support 侧的事）；
//     · 于是它能被工程外断言：30 / 60 / 120 fps 三种假时钟跑出来的值序列必须一致。
//
// 【三条铁律（对应设计文档的核心设计）】
//   ① **采样模型**：每帧由 `Elapsed / Duration` 算出归一化时间再求缓动 ——
//      不是"每帧累加一点"，所以掉帧不改变动画总时长，也不会累积误差。
//   ② **帧余量结转**：循环 / 往返 / 延迟三种情况下，超出的时间**带进下一段**
//      （按除法 / 取模一次跳过完整段），所以 1 帧 200ms 和 4 帧 50ms 结果一样。
//   ③ **零 GC 热路径**：运行时对象池化复用；每帧只做索引遍历，不产生垃圾；
//      句柄带**版本号** —— 池化复用后旧句柄自动失效，不会误 Stop 到别人的动画。
//
// 【谁调它】
//   Support\RevUIAnimDriver（挂在框架的 RevMono 每帧驱动上）→ 这里 `Step(dt)`。
//   纯 C# 环境（断言 / 服务端做表现）也可以自己按固定步长调 Step。
// ============================================================
using System;
using System.Collections.Generic;

namespace Revolution
{
    /// <summary>
    /// 动画句柄：业务持有的唯一安全形态（**只存它，别存内部对象**）。
    /// 对应设计文档 07 篇的"版本号句柄"：动画播完回池后，旧句柄自动失效。
    /// </summary>
    public readonly struct RevUIAnimHandle : IEquatable<RevUIAnimHandle>
    {
        internal readonly int Id;
        internal readonly int Version;

        internal RevUIAnimHandle(int id, int version)
        {
            Id = id;
            Version = version;
        }

        /// <summary>空句柄（表示"没有动画"；对它 Stop 是安全空操作）</summary>
        public static RevUIAnimHandle None => default;

        /// <summary>这个句柄现在还指向一个在播的动画吗</summary>
        public bool IsValid => RevUIAnimEngine.IsAlive(this);

        /// <summary>停止这个动画（重复调用安全）</summary>
        public void Stop() => RevUIAnimEngine.Stop(this);

        public bool Equals(RevUIAnimHandle other) => Id == other.Id && Version == other.Version;

        public override bool Equals(object obj) => obj is RevUIAnimHandle other && Equals(other);

        public override int GetHashCode() => (Id * 397) ^ Version;

        public static bool operator ==(RevUIAnimHandle a, RevUIAnimHandle b) => a.Equals(b);

        public static bool operator !=(RevUIAnimHandle a, RevUIAnimHandle b) => !a.Equals(b);

        public override string ToString()
            => Id == 0 ? "动画（无）" : (IsValid ? $"动画#{Id}v{Version}" : $"动画#{Id}v{Version}（已失效）");
    }

    /// <summary>一次正在播的动画（池化复用；业务不要直接引用它）。</summary>
    internal sealed class RevUIAnimRuntime
    {
        public int Id;
        public int Version = 1;
        public int ActiveIndex = -1;

        public RevUIAnimSpec Spec;
        public Action<float> OnSample;
        public Action OnDone;
        public object Owner;

        public bool Alive;
        public float Elapsed;        // 正式播放已用时长（不含延迟）
        public float DelayLeft;
        public int LoopsDone;
        public bool Backward;        // PingPong 的反向段
        public bool EmittedStart;    // 是否已落过一次起始态

        /// <summary>清掉本次播放的痕迹（**保留 Id / Version** —— 版本号是句柄失效的依据）。</summary>
        public void Reset()
        {
            Spec = default;
            OnSample = null;
            OnDone = null;
            Owner = null;
            Alive = false;
            ActiveIndex = -1;
            Elapsed = 0f;
            DelayLeft = 0f;
            LoopsDone = 0;
            Backward = false;
            EmittedStart = false;
        }
    }

    /// <summary>UI 动画引擎：池化 + 采样推进 + 版本号句柄（纯 C#）。</summary>
    public static class RevUIAnimEngine
    {
        private static readonly List<RevUIAnimRuntime> Active = new List<RevUIAnimRuntime>(32);
        private static readonly Dictionary<int, RevUIAnimRuntime> ActiveById = new Dictionary<int, RevUIAnimRuntime>(32);
        private static readonly Stack<RevUIAnimRuntime> Pool = new Stack<RevUIAnimRuntime>(32);

        private static int _nextId = 1;
        private static bool _stepping;
        private static readonly Stack<List<RevUIAnimHandle>> Snapshots = new Stack<List<RevUIAnimHandle>>();

        private static List<RevUIAnimHandle> Snapshot(object owner = null)
        {
            var result = Snapshots.Count > 0 ? Snapshots.Pop() : new List<RevUIAnimHandle>(32);
            for (int i = Active.Count - 1; i >= 0; i--)
            {
                RevUIAnimRuntime rt = Active[i];
                if (owner == null || ReferenceEquals(rt.Owner, owner)) { result.Add(new RevUIAnimHandle(rt.Id, rt.Version)); }
            }
            return result;
        }

        private static void ReturnSnapshot(List<RevUIAnimHandle> snapshot)
        {
            snapshot.Clear();
            Snapshots.Push(snapshot);
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private static void Report(Exception error, string context)
        {
            // 上报也是外部回调；不能让上报异常破坏动画回收。
            try { OnException?.Invoke(error, context); }
            catch { /* 保留原动画终态；Support 的日志出口负责上报本身的诊断。 */ }
        }

        /// <summary>采样 / 完成回调抛异常时上报（Support 侧接到 RevUILog；框架本身不打日志）</summary>
        public static Action<Exception, string> OnException;

        /// <summary>正在播的动画数</summary>
        public static int ActiveCount => Active.Count;

        /// <summary>池里闲置的运行时对象数（稳态下应该 > 0，说明没在新建对象）</summary>
        public static int PooledCount => Pool.Count;

        /// <summary>累计：起播次数 / 播完次数 / 被停次数（诊断用）</summary>
        public static int TotalPlayed;
        public static int TotalFinished;
        public static int TotalStopped;

        /// <summary>Step 被调用的次数（诊断用）</summary>
        public static int StepCount;

        // ── 起播 ────────────────────────────────────────────

        /// <summary>
        /// 起播一个动画。
        /// </summary>
        /// <param name="spec">规格（时长 / 曲线 / 通道）</param>
        /// <param name="onSample">采样回调：参数是**缓动后的系数**（0→1），Support 侧照着写目标值</param>
        /// <param name="onDone">播完回调（**先**回调再回池；回调里再起动画是安全的）</param>
        /// <param name="owner">归属对象（面板 / Part 自己）—— 销毁时一行 <c>StopAllOf(owner)</c> 全停</param>
        public static RevUIAnimHandle Play(in RevUIAnimSpec spec, Action<float> onSample,
            Action onDone = null, object owner = null)
        {
            if (onSample == null) { return RevUIAnimHandle.None; }
            if (!IsFinite(spec.Duration) || !IsFinite(spec.Delay))
            {
                throw new ArgumentOutOfRangeException(nameof(spec), "动画时长与延迟必须是有限数值");
            }

            RevUIAnimRuntime rt = Rent();
            rt.Spec = spec;
            rt.OnSample = onSample;
            rt.OnDone = onDone;
            rt.Owner = owner;
            rt.Alive = true;
            rt.Elapsed = 0f;
            rt.DelayLeft = spec.Delay > 0f ? spec.Delay : 0f;
            rt.LoopsDone = 0;
            rt.Backward = false;
            rt.EmittedStart = false;
            TotalPlayed++;

            // 规格无效 / 时长≈0：当场给终态，不进活动列表（省掉每帧一次遍历）
            if (!spec.IsValid || spec.Duration <= 0.0001f)
            {
                EmitSample(rt, 1f);
                Finish(rt, out RevUIAnimRuntime done);
                Recycle(done);
                return RevUIAnimHandle.None;
            }

            // 先把"起始态"落一次屏：有延迟时也不会在错误的位置上等
            var handle = new RevUIAnimHandle(rt.Id, rt.Version);
            rt.ActiveIndex = Active.Count;
            Active.Add(rt);
            ActiveById[rt.Id] = rt;
            rt.EmittedStart = true;
            EmitSample(rt, 0f);
            return IsAlive(handle) ? handle : RevUIAnimHandle.None;
        }

        // ── 推进 ────────────────────────────────────────────

        /// <summary>推进一帧（Support 的驱动每帧调一次；也可以按固定步长在工程外跑）。</summary>
        public static void Step(float deltaTime)
        {
            if (!IsFinite(deltaTime)) { throw new ArgumentOutOfRangeException(nameof(deltaTime), "动画步长必须是有限数值"); }
            if (_stepping) { return; }
            StepCount++;
            if (Active.Count == 0) { return; }
            if (deltaTime < 0f) { deltaTime = 0f; }
            _stepping = true;
            List<RevUIAnimHandle> snapshot = Snapshot();
            try
            {
                // 回调可能删除其它动画、清空列表并复用对象。只推进本帧开始时的身份快照，新增动画留到下一帧。
                foreach (RevUIAnimHandle handle in snapshot)
                {
                    int index = IndexOf(handle);
                    if (index < 0) { continue; }
                    RevUIAnimRuntime rt = Active[index];
                    if (!Advance(rt, deltaTime)) { continue; }
                    index = IndexOf(handle);
                    if (index < 0) { continue; }
                    RemoveAt(index);
                    Finish(rt, out RevUIAnimRuntime done);
                    Recycle(done);
                }
            }
            finally
            {
                ReturnSnapshot(snapshot);
                _stepping = false;
            }
        }

        /// <summary>推进一步；返回 true = 本步到终态（内部用）。</summary>
        private static bool Advance(RevUIAnimRuntime rt, float deltaTime)
        {
            float duration = rt.Spec.Duration > 0.0001f ? rt.Spec.Duration : 0.0001f;
            float dt = deltaTime;

            // ① 延迟：余量结转到正式播放（掉帧不缩短动画）
            if (rt.DelayLeft > 0f)
            {
                if (dt <= rt.DelayLeft)
                {
                    rt.DelayLeft -= dt;
                    return false;
                }
                dt -= rt.DelayLeft;
                rt.DelayLeft = 0f;
            }

            // 用 double 结转并一次跳过完整段，禁止按段 while：极大步长也必须有界完成。
            double elapsed = (double)rt.Elapsed + dt;
            double segments = Math.Floor(elapsed / duration);
            if (segments >= 1d)
            {
                bool finite = rt.Spec.Wrap == RevUIAnimWrap.Once || rt.Spec.Loops >= 0;
                int remaining = rt.Spec.Wrap == RevUIAnimWrap.Once ? 1 : Math.Max(1, rt.Spec.Loops) - rt.LoopsDone;
                if (finite && segments >= remaining)
                {
                    bool finalBackward = rt.Backward;
                    if (rt.Spec.Wrap == RevUIAnimWrap.PingPong && ((remaining - 1) & 1) != 0) { finalBackward = !finalBackward; }
                    EmitSample(rt, finalBackward ? 0f : 1f);
                    return true;
                }
                if (rt.Spec.Wrap == RevUIAnimWrap.PingPong && segments % 2d >= 1d) { rt.Backward = !rt.Backward; }
                if (finite) { rt.LoopsDone += (int)segments; }
                elapsed %= duration;
            }
            rt.Elapsed = (float)elapsed;

            float t = rt.Elapsed / duration;
            if (rt.Backward) t = 1f - t;
            EmitSample(rt, t);
            return false;
        }

        /// <summary>把一个归一化时间点采样出去（缓动 + 异常隔离）。</summary>
        private static void EmitSample(RevUIAnimRuntime rt, float t)
        {
            RevUIEase ease = rt.Backward ? RevUIEaseUtil.Reverse(rt.Spec.Ease) : rt.Spec.Ease;
            float eased = RevUIEaseUtil.Evaluate(ease, t);

            Action<float> cb = rt.OnSample;
            if (cb == null) return;

            try
            {
                cb(eased);
            }
            catch (Exception e)
            {
                // 一个动画的书写错不该把整帧 UI 带崩：隔离 + 上报
                Report(e, "动画采样");
            }
        }

        /// <summary>到终态：先回调（回调里可以再起动画），再清引用交给回收。</summary>
        private static void Finish(RevUIAnimRuntime rt, out RevUIAnimRuntime recycled)
        {
            rt.Alive = false;
            TotalFinished++;

            Action done = rt.OnDone;
            rt.OnDone = null;
            rt.OnSample = null;

            recycled = rt;

            if (done == null) return;
            try
            {
                done();
            }
            catch (Exception e)
            {
                Report(e, "动画完成回调");
            }
        }

        // ── 停止 ────────────────────────────────────────────

        /// <summary>停一个动画（句柄失效 / 重复调用都是安全空操作）。</summary>
        public static void Stop(RevUIAnimHandle handle)
        {
            int index = IndexOf(handle);
            if (index < 0) return;

            RevUIAnimRuntime rt = Active[index];
            RemoveAt(index);
            rt.Alive = false;
            rt.OnSample = null;
            TotalStopped++;

            // ★ 停 = "就地续接"：把完成回调放出去，避免调用方卡在"等回调"的状态
            //   （面板的关闭动画被外部 Stop 掉时，仍然必须走到"关闭完成"，否则会一直卡在 Closing）
            Action done = rt.OnDone;
            rt.OnDone = null;
            Recycle(rt);
            Guard(done, "动画被停止时的完成回调");
        }

        /// <summary>停掉某个归属对象的全部动画（面板 / Part 关闭时一行清理）。返回停掉几个。</summary>
        public static int StopAllOf(object owner)
        {
            if (owner == null) { return 0; }
            List<RevUIAnimHandle> snapshot = Snapshot(owner);
            int stopped = 0;
            try
            {
                foreach (RevUIAnimHandle handle in snapshot)
                {
                    if (!IsAlive(handle)) { continue; }
                    stopped++;
                    Stop(handle);
                }
            }
            finally { ReturnSnapshot(snapshot); }
            return stopped;
        }

        /// <summary>停止调用开始时的全部动画；完成回调中新建的动画属于下一批。</summary>
        public static void StopAll()
        {
            List<RevUIAnimHandle> snapshot = Snapshot();
            try
            {
                foreach (RevUIAnimHandle handle in snapshot) { Stop(handle); }
            }
            finally { ReturnSnapshot(snapshot); }
        }

        /// <summary>执行一个回调并隔离异常（热路径之外的小工具）。</summary>
        private static void Guard(Action action, string what)
        {
            if (action == null) return;
            try
            {
                action();
            }
            catch (Exception e)
            {
                Report(e, what);
            }
        }

        /// <summary>这个句柄还指向在播的动画吗。</summary>
        public static bool IsAlive(RevUIAnimHandle handle)
        {
            int index = IndexOf(handle);
            return index >= 0 && Active[index].Alive;
        }

        /// <summary>清空一切（域重载 / 断言用）。</summary>
        public static void Reset()
        {
            Active.Clear();
            ActiveById.Clear();
            Pool.Clear();
            // 不重置 ID：关闭 Domain Reload 时旧句柄可能被业务静态字段持有；
            // 新会话若从 1 重新分配并复用相同 Version，旧句柄会误命中新动画。
            TotalPlayed = TotalFinished = TotalStopped = StepCount = 0;
        }

        /// <summary>一行诊断（业务打到控制台用；只在调用时分配字符串）。</summary>
        public static string Dump()
            => $"RevUIAnim 在播={Active.Count} 池={Pool.Count} 起播={TotalPlayed} 播完={TotalFinished} 停={TotalStopped}";

        // ── 池与查找 ────────────────────────────────────────

        private static int IndexOf(RevUIAnimHandle handle)
        {
            if (handle.Id != 0 && ActiveById.TryGetValue(handle.Id, out RevUIAnimRuntime rt)
                && rt.Alive && rt.Version == handle.Version) { return rt.ActiveIndex; }
            return -1;
        }

        /// <summary>swap-remove 同步索引，使身份快照逐项查询保持 O(1)。</summary>
        private static void RemoveAt(int index)
        {
            int last = Active.Count - 1;
            RevUIAnimRuntime removed = Active[index];
            ActiveById.Remove(removed.Id);
            if (index != last)
            {
                Active[index] = Active[last];
                Active[index].ActiveIndex = index;
            }
            Active.RemoveAt(last);
            removed.ActiveIndex = -1;
        }

        private static RevUIAnimRuntime Rent()
        {
            RevUIAnimRuntime rt = Pool.Count > 0 ? Pool.Pop() : new RevUIAnimRuntime();
            if (rt.Id == 0) rt.Id = _nextId++;
            rt.Version++;               // ★ 换新版本号：上一次的句柄立刻失效
            return rt;
        }

        private static void Recycle(RevUIAnimRuntime rt)
        {
            rt.Reset();
            Pool.Push(rt);
        }
    }
}
