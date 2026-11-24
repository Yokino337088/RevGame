// ============================================================
// RevResBootstrap.cs —— 资源系统启动装配
//
// 位置：Runtime\资源加载\
//
// 【它负责三件事】
//   ① 决定注册哪些策略、以什么顺序（顺序 = 优先级）；
//   ② 读取打包工具生成的 ResMap 映射表；
//   ③ 提供切场景时的统一清理入口。
//
// 【两种模式】
//   · 开发模式（默认）：编辑器下只注册 RevEditorResPolicy，
//     所有资源一律 AssetDatabase 直读，AB / Resources 完全不参与。
//   · AB 模式：真机自动使用；编辑器下需手动开启
//     （菜单 Revolution.Tools/资源/AB 加载模式（编辑器），或代码设 UseABInEditor = true）。
// ============================================================
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Revolution
{
    /// <summary>为每次运行配置统一资源策略，并协调资源缓存和原生包的清理。</summary>
    public class RevResBootstrap : RevSingleton<RevResBootstrap>
    {
        // 单例：构造函数写 private 只是"防止外部 new"的可选加固，不写也能正常工作
        private RevResBootstrap() { }

        private RevABLoader _abLoader;

#if UNITY_EDITOR
        // ============================================================
        // 编辑器专用开关：是否强制走 AB 模式
        //   用 EditorPrefs 持久化 —— 关掉 Unity 再打开仍记得上次的选择。
        //   默认 false：开发期一律走编辑器直读。
        // ============================================================
        private const string UseABInEditorKey = "Revolution.Res.UseABInEditor";
        private const string MenuPath = "Revolution.Tools/资源/AB 加载模式（编辑器）";

        /// <summary>编辑器中启用实际 AB 解析。新映射验证失败时恢复原设置和已装配策略。</summary>
        public static bool UseABInEditor
        {
            get => UnityEditor.EditorPrefs.GetBool(UseABInEditorKey, false);
            set
            {
                bool previous = UseABInEditor;
                if (previous == value)
                {
                    return;
                }
                UnityEditor.EditorPrefs.SetBool(UseABInEditorKey, value);
                try
                {
                    Instance.Init();
                }
                catch
                {
                    UnityEditor.EditorPrefs.SetBool(UseABInEditorKey, previous);
                    throw;
                }
            }
        }

        [UnityEditor.MenuItem(MenuPath, false, 100)]
        private static void ToggleABMode() => UseABInEditor = !UseABInEditor;

        [UnityEditor.MenuItem(MenuPath, true)]
        private static bool ToggleABModeValidate()
        {
            UnityEditor.Menu.SetChecked(MenuPath, UseABInEditor);   // 菜单上显示勾号
            return true;
        }
#endif

        // ============================================================
        // 映射表覆盖钩子（RevHotUpdate 热更包使用；本框架自身永不设置它）
        // ============================================================

        /// <summary>
        /// ResMap 映射表的覆盖来源：热更包用它把"逻辑名 → 包名|资源名"换成热更版本 ——
        /// 否则"新增资源 / 改包"永远加载不到（表在包体的 Resources 里，运行时只读）。
        /// <para>★ 返回 null → 只用内置表。不设置时（默认 null）行为与从前完全一致 —— 零影响。</para>
        /// <para>★ 语义是"覆盖合并"：热更表覆盖内置表的同名键，内置表独有的键保留 ——
        /// 新资源能热更、老资源在热更表残缺时仍有内置表兜底（两边都要）。</para>
        /// </summary>
        public static Func<Dictionary<string, string>> ResMapOverride { get; set; }

        /// <summary>已验证的完整版本映射。存在时整体使用该版本，返回空值时使用内置首包和显式覆盖。</summary>
        public static Func<Dictionary<string, string>> ResMapSnapshot { get; set; }

        /// <summary>扩展提供已验证的整版地址索引。返回空值时才尝试旧版入口和内置首包。</summary>
        public static Func<RevResMap> ResolvedResMapSnapshot { get; set; }

        // ==================== 初始化 ====================

        /// <summary>结束上一轮 Play 的资源回执和覆盖委托。新会话的扩展随后通过正常启动入口重新注册。</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetForNewSession()
        {
            RevAsyncLoadPump.ResetForNewSession();
            RevResManager.ResetForNewSession();
            RevResourceBackends.Reset();

            // 委托可能捕获旧场景对象。当前会话可以在 RevInitialize 的资源装配回调中重新安装自己的解析器。
            ResMapOverride = null;
            ResMapSnapshot = null;
            ResolvedResMapSnapshot = null;
            RevABLoader.BundlePathResolver = null;
            RevABLoader.BundleCacheKeyResolver = null;
            RevABLoader.DependenciesOverride = null;
        }

        /// <summary>验证目标映射后重建资源策略。非法映射会在释放当前策略前中断。</summary>
        public void Init()
        {
            // 先准备扩展与映射。失败时让原策略继续服务。
            RevResourceBackend backend = RevResourceBackends.CreateSelected(out string backendId);
            RevABLoader loader = backend?.Loader;
            IRevResPolicy policy = backend?.Policy;
            if (backend == null)
            {
#if UNITY_EDITOR
                loader = UseABInEditor ? new RevABLoader() : null;
                policy = loader == null ? (IRevResPolicy)new RevEditorResPolicy() : CreateBundlePolicy(loader);
#else
                loader = new RevABLoader();
                policy = CreateBundlePolicy(loader);
#endif
            }

            // 先终止旧代、归还包与预热租约，再发布新策略。
            RevAsyncLoadPump.CancelAll();
            RevResPreloader.ReleaseAll();
            RevResManager.UnloadAll();
            _abLoader?.ReleaseAll();
            RevResManager.ClearAllPolicies();

            if (RevResAutoUnloader.AutoStart)
            {
                RevResAutoUnloader.EnsureRunning();
            }
            _abLoader = loader;
            RevResManager.RegisterPolicy(policy);
            // 自带直读沿用原规则。扩展与真实 AB 也支持 Res/ 显式入口。
            if (loader != null || backend != null)
            {
                RevResManager.RegisterPolicy(new RevResourcesResPolicy());
            }
            RevResourceBackends.ActiveId = backendId;
        }

        /// <summary>在没有活跃资源租约或在途读取的交接点刷新后端。</summary>
        public bool TryRefreshBackend()
        {
            foreach (var entry in RevResManager.CachedHandles)
            {
                if (entry.Value.RefCount > 0 || entry.Value.IsLoading)
                {
                    return false;
                }
            }
            Init();
            return true;
        }

        /// <summary>选择本次 AB 映射来源。内置与类型化扩展直接持有地址索引，旧钩子沿用原来字典。</summary>
        private static RevABResPolicy CreateBundlePolicy(RevABLoader loader)
        {
            RevResMap resolved = ResolvedResMapSnapshot?.Invoke();
            if (resolved != null)
            {
                return new RevABResPolicy(resolved, loader);
            }
            Dictionary<string, string> snapshot = ResMapSnapshot?.Invoke();
            if (snapshot != null)
            {
                return new RevABResPolicy(snapshot, loader);
            }

            string text = ReadBuiltinResMapText();
            Func<Dictionary<string, string>> overrideSource = ResMapOverride;
            if (overrideSource == null)
            {
                return new RevABResPolicy(RevResMap.Parse(text), loader);
            }

            // 旧的增量覆盖是宿主接口。它保持文本字典语义，版本级热更新则提供完整的类型化快照。
            Dictionary<string, string> map = RevResMapCodec.ParseText(text);
            Dictionary<string, string> hot = overrideSource();
            if (hot != null)
            {
                foreach (KeyValuePair<string, string> entry in hot)
                {
                    map[entry.Key] = entry.Value;
                }
            }
            return new RevABResPolicy(map, loader);
        }

        /// <summary>读取一份内置引导资产。派生副本损坏时中止，旧格式只在压缩资产不存在时读取。</summary>
        private static string ReadBuiltinResMapText()
        {
            TextAsset asset = Resources.Load<TextAsset>(RevResMapCodec.PackedResourcePath);
            bool packed = asset != null;
            if (!packed)
            {
                asset = Resources.Load<TextAsset>(RevResMapCodec.LegacyResourcePath);
            }
            if (asset == null)
            {
                return string.Empty; // 纯远端或 Resources 项目。
            }

            try
            {
                byte[] bytes = asset.bytes;
                return packed ? RevResMapCodec.Unpack(bytes) : RevResMapCodec.DecodeText(bytes);
            }
            finally
            {
                Resources.UnloadAsset(asset);
            }
        }

        // ==================== 清理 ====================

        /// <summary>
        /// 切场景 / 退出战斗：取消该组任务 → 归还该组预加载引用 → 按组卸载 → 清理未使用表（取消令牌按任务独立，不需要全局复位）。
        ///
        /// 【关于 force】（配合 RevResManager.UnloadGroup 的语义）
        ///   true（默认）：连"仍被引用的也一并清账" —— 适合"整个业务域一次性销毁"，
        ///                 不必要求业务把每个资源都 Release 一遍。
        ///   false       ：只卸载引用已归零的 —— 安全，但业务若忘了 Release，资源会残留。
        ///
        /// 【force = true 会不会误伤别的域？】
        ///   会 —— 前提是"该分组里混进了跨域共享的资源"。
        ///   对这类资源请打 RevResInstanceFlag.Resident 标志，它永不参与分组卸载（force 也不行）。
        /// </summary>
        /// <param name="group">要清理的业务分组</param>
        /// <param name="force">是否解除"还有人在用"的保护（默认 true）</param>
        public void Shutdown(RevResGroup group, bool force = true)
        {
            RevAsyncLoadPump.CancelGroup(group);       // ① 只中断该业务组的等待/在途加载
            RevResPreloader.Release(group);             // ② 归还该组的"预加载持有"引用
            RevResManager.UnloadGroup(group, force);    // ③ 按业务域批量卸载
            RevResManager.FlushUnused();                // ④ 真正释放未使用资源
        }

        /// <summary>全部释放（退出 / 回登录）</summary>
        public void ShutdownAll()
        {
            RevAsyncLoadPump.CancelAll();               // ① 中断所有等待/在途加载
            RevResPreloader.ReleaseAll();               // ② 归还全部预加载引用
            RevResManager.UnloadAll();                  // ③ 在旧策略仍有效时清缓存并释放包级引用
            _abLoader?.ReleaseAll();                    // ④ 最后释放主包/Manifest 与剩余包
        }
    }
}
