using UnityEngine;

// RevResHandle.cs —— 资源实体
// 包装「内容 + 状态机 + 位标志 + 引用计数 + 失败原因」。
// 业务拿到的不是 UnityEngine.Object，而是这个可管理的句柄。
namespace Revolution
{
    public class RevResHandle
    {
        // ===== 身份信息 =====
        // 【为什么用 ulong Key 而不是字符串路径做字典键？】
        //   字符串做键，每次查找都要重算哈希 + 逐字符比较，还会产生临时对象；
        //   ulong 是值类型，比较/哈希都在栈上，快得多，也不产生 GC 垃圾。
        public ulong Key { get; internal set; }   // 唯一 Key（路径的 FNV-1a 哈希）
        public string StandardPath { get; internal set; }   // 标准路径（业务传入：如 "Hero/1001"）
        public string RealPath { get; internal set; }   // 真实路径（策略映射后，给 Loader 用）
        public System.Type ContentType { get; internal set; }   // 内容类型 typeof(T)

        // ===== 生命周期 =====

        /// <summary>
        /// 归属分组（★ 不参与加载，只用于"按组卸载"点名）。
        ///
        /// 【语义】加载时首次确定，之后不再变更 —— 一个资源只属于一个分组。
        ///   它是 UnloadGroup / Shutdown / ReleasePreloaded 筛句柄时唯一的依据。
        ///
        /// 【为什么必须"不可覆盖"？】
        ///   若允许后来者覆盖（谁最后加载算谁的），会出现：
        ///     Battle 先加载 → UI 后加载同一资源 → 归属被改成 UI
        ///     → Shutdown(Battle) 点名不到它 → 永远不会被分组释放（内存泄漏）。
        ///   而它又不参与加载，所以在加载阶段怎么测都测不出问题。
        /// </summary>
        public RevResGroup Group { get; internal set; }
        public RevResState State { get; internal set; }
        public RevResInstanceFlag Flags { get; internal set; }

        //引用计数
        public int RefCount { get; internal set; }

        // AB 资源成功取得了包级引用才置位；释放时仅归还这一次，并保留原 loader 所有权。
        internal bool BundleAcquired { get; set; }
        internal RevABLoader BundleLoader { get; set; }
        internal string BundleName { get; set; }
        internal string[] BundleDependencies { get; set; }
        internal int BundleGeneration { get; set; }

        // 弱类型预热与具体类型请求共享 IO，但每个具体请求有自己的结果视图与一次归还标记。
        internal RevResHandle ReferenceOwner;
        internal bool ReferenceReleased;

        /// <summary>失败原因（不打日志的前提下，这是唯一的错误线索）</summary>
        public RevResLoadErrorReason ErrorReason { get; internal set; }

        // ===== 使用统计（自动卸载 / LRU 淘汰用）=====

        /// <summary>最近一次被使用的时间（基于 Time.realtimeSinceStartup）</summary>
        public float LastUseTime { get; internal set; }

        /// <summary>进入"未使用表"的时间（冷却期判断用，防止刚释放又立刻被淘汰）</summary>
        public float UnusedTime { get; internal set; }

        /// <summary>被访问次数（统计 / 排查用）</summary>
        public int AccessCount { get; internal set; }

        /// <summary>标记一次"被使用"：刷新 LRU 时间戳</summary>
        internal void Touch()
        {
            LastUseTime = UnityEngine.Time.realtimeSinceStartup;
            AccessCount++;
        }

        // ===== 内容（两种形式）=====

        // 用 object 装内容：既可能是 UnityEngine.Object，也可能是 byte[] / TextAsset。
        // 对外只暴露 Content / Data 两个属性按需取用，避免业务强转出错。
        private object _content;

        //下面这两个是暴露给外部调用的属性

        /// <summary>Unity 对象形式（Sprite / Prefab / Texture …）</summary>
        public UnityEngine.Object Content => _content as UnityEngine.Object;

        /// <summary>二进制形式（配置表 / 文本）</summary>
        public byte[] Data
        {
            get
            {
                // is 类型 临时遍历 这种是C#特有的写法，可以用于下面这种类型转换的场景
                if(_content is byte[] bytes)
                    return bytes;
                if (_content is TextAsset ta)
                    return ta.bytes;
                return null;
            }
        }

        //这些都是提供给外部来进行查询加载状态的属性

        /// <summary>是否已加载完成</summary>
        public bool IsLoaded => State == RevResState.Loaded;
        /// <summary>是否正在加载中</summary>
        public bool IsLoading => State == RevResState.Loading;
        /// <summary>内容是否有效</summary>
        public bool IsValid => _content != null;

        /// <summary>便捷泛型取用</summary>
        public T Get<T>() where T : UnityEngine.Object => _content as T; //这里直接把_content转换成对应的类型

        // ===== 状态变更（内部使用）=====

        //标记加载中
        internal void MarkLoading() => State = RevResState.Loading;

        //标记加载出错
        internal void MarkError() => State = RevResState.LoadErr;

        /// <summary>
        /// 加载结果收口：写入内容，并同步状态机。仅框架内部（Loader 回调）调用。
        /// </summary>
        /// <param name="content"></param>
        internal void SetContent(object content)
        {
            _content = content;
            State = content != null ? RevResState.Loaded : RevResState.LoadErr;
        }

        // ===== 位标志操作 =====
        // Flags 是 [Flags] 枚举，用「一个 int 的每一位」表示一种属性的开/关，7 种属性只占 4 字节。
        //   例：NeedCache = 1<<0 = 0b000_0001
        //       Resident  = 1<<1 = 0b000_0010
        //       Preloaded = 1<<6 = 0b100_0000
        //   多种属性并存 = 把对应位"或"起来，如 NeedCache | Preloaded = 0b100_0001。
        //   前提：枚举值必须是互不重叠的 2 的幂（1<<n），否则会互相污染。

        /// <summary>
        /// 查询是否"置了位"：把 Flags 与 f 做按位与（&amp;），只要对应位有一个是 1 就返回 true。
        ///   Flags = 0b100_0001，f = Preloaded(0b100_0000) → 0b100_0000 != 0 → true
        ///   Flags = 0b100_0001，f = Resident (0b000_0010) → 0           → false
        /// f 也可以传组合（如 A|B），语义是"其中任一位置 1"。
        /// </summary>
        public bool HasFlag(RevResInstanceFlag f) => (Flags & f) != 0;

        /// <summary>
        /// 置位（打开）：按位或（|=），把 f 对应的位置成 1，其它位保持不动。
        /// 幂等：重复打开同一个标志结果不变。传组合则一次打开多个。
        /// </summary>
        public void AddFlag(RevResInstanceFlag f) => Flags |= f;

        /// <summary>
        /// 清位（关闭）：先按位取反（~）得到"目标位为 0、其余位为 1"的掩码，再按位与（&amp;=），
        /// 从而只把 f 对应的位清 0，其它位不受影响。传组合则一次关闭多个。
        /// </summary>
        public void RemoveFlag(RevResInstanceFlag f) => Flags &= ~f;

        /// <summary>
        /// 空对象：资源不存在 / 加载失败时返回它，业务判 Content == null 即可，
        /// 永远不会空引用崩溃，同时还能通过 State / ErrorReason 知道失败原因。
        /// </summary>
        public static readonly RevResHandle Empty = new RevResHandle
        {
            Key = 0,
            StandardPath = string.Empty,
            RealPath = string.Empty,
            State = RevResState.LoadErr
        };
    }
}