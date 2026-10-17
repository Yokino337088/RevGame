// ============================================================
// RevResAutoUnloader.cs —— 自动卸载门卫
//
// 位置：Runtime\RevResourceSystem\Support\
//
// 【它解决什么问题？】
//   引用计数只能告诉你"这个资源现在没人用了"，但不能决定"什么时候真的把它卸掉"。
//   如果一归零就立刻卸载 → 频繁装卸抖动；
//   如果永远不卸 → 内存只涨不跌。
//   门卫负责在"合适的时机、以安全的顺序"把该放的资源放掉。
//
// 【5 个触发源】
//   ① 定时（CheckInterval）       —— 常规体检
//   ② 内存压力（lowMemory 事件）  —— 立即强清（跳过冷却期）
//   ③ 缓存超阈值（MaxCachedHandles）—— 按 LRU 淘汰到水位线
//   ④ 空闲判定（OnlyWhenIdle）    —— 只在没有在途加载时动手，避免和前台抢资源
//   ⑤ 切场景（调用 RevResBootstrap.Shutdown）—— 见 Support\RevResBootstrap.cs
//
// 【安全规则：什么样的资源绝不自动卸？】
//   · RefCount > 0            （还有人用）
//   · 带 Resident 标志         （声明了常驻）
//   · 带 MarkedUnused 之外的    （不在"未使用表"里 —— 说明还没到释放时机）
//   · State == Loading        （正在加载）
//   · 冷却期内                 （刚归零不久，可能马上又被用）
//   · 带 Preloaded 标志         （预加载是"故意留着"的，默认保留）
// ============================================================
using System.Collections.Generic;
using UnityEngine;

namespace Revolution
{
    public sealed class RevResAutoUnloader : MonoBehaviour
    {
        // ==================== 策略配置（运行时可调） ====================

        /// <summary>是否启用自动卸载</summary>
        public static bool Enable = true;

        /// <summary>定时检查间隔（秒）</summary>
        public static float CheckInterval = 30f;

        /// <summary>资源归零后至少保留多久才允许被淘汰（防抖动）</summary>
        public static float UnusedCooldown = 5f;

        /// <summary>缓存条目上限（超过就按 LRU 淘汰；0 = 不限制数量）</summary>
        public static int MaxCachedHandles = 800;

        /// <summary>超限时淘汰到多少条（水位线，避免每次都贴着上限反复淘汰）</summary>
        public static int TrimDownToCount = 600;

        /// <summary>内存告急时，是否连"预加载持有"的资源也一起放掉</summary>
        public static bool UnloadPreloadedOnLowMemory = false;

        /// <summary>是否只在"没有在途加载"时才动手（推荐 true）</summary>
        public static bool OnlyWhenIdle = true;

        /// <summary>首次访问时是否自动创建宿主 GameObject</summary>
        public static bool AutoStart = true;

        // ==================== 统计（排查 / 可视化工具有用） ====================

        /// <summary>上一次淘汰了几个</summary>
        public static int LastTrimCount { get; private set; }

        /// <summary>累计淘汰了几个</summary>
        public static int TotalTrimCount { get; private set; }

        // ==================== 运行时 ====================

        private static RevResAutoUnloader _instance;
        private float _timer;

        /// <summary>启动门卫（幂等；RevResBootstrap.Init 会调它）</summary>
        public static void EnsureRunning()
        {
            if (_instance != null) return;

            var go = new GameObject("[RevResAutoUnloader]");
            DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            _instance = go.AddComponent<RevResAutoUnloader>();
        }

        /// <summary>停止门卫（一般不用调；退出时 Unity 会自己销毁）</summary>
        public static void Stop()
        {
            if (_instance == null) return;
            Destroy(_instance.gameObject);
            _instance = null;
        }

        /// <summary>手动触发一次自动卸载（调试 / 读条结束时想主动瘦身）</summary>
        /// <param name="force">true = 跳过冷却期与空闲判定（内存告急用）</param>
        public static void RunOnce(bool force = false) => Trim(force);

        // ==================== 驱动 ====================

        private void Update()
        {
            if (!Enable) return;

            // 用 unscaledDeltaTime：即使游戏暂停/慢放，内存该清还是要清
            _timer += Time.unscaledDeltaTime;
            if (_timer < CheckInterval) return;
            _timer = 0f;

            Trim(false);
        }

        private void OnEnable() => Application.lowMemory += OnLowMemory;
        private void OnDisable() => Application.lowMemory -= OnLowMemory;

        private void OnDestroy() => Application.lowMemory -= OnLowMemory;

        /// <summary>内存告急：跳过一切保护期，立刻清理</summary>
        private void OnLowMemory() => Trim(true);

        // ==================== 核心：淘汰 ====================

        private static void Trim(bool force)
        {
            if (force && UnloadPreloadedOnLowMemory)
                RevResManager.ReleasePreloadedAll();

            // ① 空闲判定：有在途加载就先不动手（避免和前台抢 IO / 抢内存）
            if (!force && OnlyWhenIdle &&
                (RevAsyncLoadPump.WaitingCount > 0 || RevAsyncLoadPump.LoadingCount > 0))
                return;

            List<RevResHandle> all = RevResManager.GetAllHandles();
            if (all.Count == 0) return;

            float now = Time.realtimeSinceStartup;

            // ② 挑"可淘汰候选"
            var candidates = new List<RevResHandle>();
            foreach (RevResHandle h in all)
            {
                // 不在未使用表 → 说明还有人用 / 还没到释放时机，一律不动
                if (!h.HasFlag(RevResInstanceFlag.MarkedUnused) || h.RefCount > 0) continue;
                // 常驻资源永不自动卸（FlushUnused 同样遵守）
                if (h.HasFlag(RevResInstanceFlag.Resident)) continue;
                // 正在加载的不动
                if (h.State == RevResState.Loading) continue;
                // 预加载持有：默认保留（它是"故意留着"的）；内存告急且允许时才放
                if (h.HasFlag(RevResInstanceFlag.Preloaded) && !(force && UnloadPreloadedOnLowMemory)) continue;

                // 冷却期：刚归零不久，可能马上又被用（force 可跳过）
                if (!force && now - h.UnusedTime < UnusedCooldown) continue;

                candidates.Add(h);
            }

            if (candidates.Count == 0)
            {
                LastTrimCount = 0;
                return;
            }

            // ③ 决定淘汰多少个
            int removeCount;
            if (MaxCachedHandles > 0 && all.Count > MaxCachedHandles)
            {
                // 超过上限 → 按 LRU 淘汰到有效水位线（配置超过当前条目数时至少淘汰一个）
                int target = TrimDownToCount < 0 ? 0 : TrimDownToCount;
                if (target >= MaxCachedHandles) target = MaxCachedHandles - 1;
                int over = all.Count - target;
                removeCount = over < candidates.Count ? over : candidates.Count;
            }
            else
            {
                // 没超上限 → 只清"已经归零且过了冷却期"的（正常回收）
                removeCount = candidates.Count;
            }

            if (removeCount <= 0)
            {
                LastTrimCount = 0;
                return;
            }

            // ④ LRU 排序：最久未用的排前面，优先淘汰
            candidates.Sort((a, b) => a.LastUseTime.CompareTo(b.LastUseTime));

            int removed = 0;
            for (int i = 0; i < removeCount && i < candidates.Count; i++)
            {
                RevResHandle h = candidates[i];

                h.RemoveFlag(RevResInstanceFlag.MarkedUnused);
                RevResManager.ForceRemove(h.Key);     // 从缓存移除 + 归还 AB 包引用
                removed++;
            }

            // ⑤ 请求 Unity 回收刚被强制摘除的对象。不能调用 FlushUnused：那会连冷却期内
            //    及 LRU 本次未选中的条目一起清掉，也不能让 Resident 绕过保护。
            Resources.UnloadUnusedAssets();

            LastTrimCount = removed;
            TotalTrimCount += removed;
        }
    }
}
