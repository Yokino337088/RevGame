// ============================================================
// ABMarkerWatcher.cs —— 分包标记变更监视器（窗口的"自动同步"）
//
// 位置：Editor\RevResourceSystem\ABTool\Integration\
//
// 【要解决的问题】
//   分包信息会在很多地方被改：Project 里拖拽、Inspector 右下角的 AssetBundle 栏、
//   批量脚本、git 切分支…… 窗口如果只在"手动点刷新"时才更新，看到的就可能是过期数据 ——
//   最坏的情况是"以为这个资源在包里，其实早被移出去了"。
//
// 【怎么知道"改了"】四条来源，互补；任何一条命中都只置一个脏标记（很便宜），
// 真正的重扫由监视器自己的 tick 在"最后一次变更过去抖时间之后"执行一次：
//   ① AssetPostprocessor.OnPostprocessAllAssets（主通道）
//        改 AB 标记会让资源【或文件夹】被重新导入 → Unity 报到这里。
//        Project 拖拽 / Inspector 改 / 脚本改，都会走到这条。
//   ② 包名清单指纹（兜底轮询，1.5 秒一次）
//        把所有包名排序后拼起来比对 —— 抓"包名增删改"这类不一定触发资源导入的变动
//        （外部工具、VCS 切分支都能被它追上）。
//   ③ 工具自己改完标记后调 MarkChanged()：我们当然知道自己干了什么，不必等回调。
//   ④ 「刷新」按钮（手动兜底）：任何没被上面三条捕获的方式，点一下立刻同步。
//
// 【什么时候才干活】
//   由 EditorApplication.update 驱动，但**只有打包窗口开着**（OnEnable 时注册进来）且
//   「自动同步」勾着才真正跑；窗口一关就完全静默，不在后台空转。
//   ★ 为什么不靠 OnGUI 驱动：窗口静止（鼠标不动）时不一定重绘，
//     那样"1.5 秒兜底轮询"和"去抖到点"都会迟迟不执行 —— 实时同步就成了碰运气。
//
// 【为什么要去抖 + 拖拽保护】
//   · 拖 20 个资源进包 = 20 次导入回调：不去抖就会重扫 20 次。这里合并成一个脏标记，
//     最后一次变更过去抖时间（MinInterval）后才真正重扫一次；
//   · 拖拽过程中重扫会让左右两个列表在鼠标下方重建 → 高亮和实际投放位置错位，
//     所以【拖拽期间一律不重扫】，等松手后的下一帧补上。
//
// 【职责边界：只同步"显示"，绝不写文件】
//   这里只让扫描结果缓存（ABCollectCache）失效，一个字节都不写 ——
//   ResMap.txt / RevResPath.cs 必须由使用者显式点「仅生成映射」/「打包」生成。
//
// 【成本提示】
//   一次自动同步 = 一次只读扫描（含漏标检测要遍历资源根目录下的文件）。
//   工程特别大、或正在大批量导资源时，可以把「自动同步」勾掉（开关就在分包浏览页签顶部）。
// ============================================================
using System;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Revolution.Editor
{
    [InitializeOnLoad]
    internal static class ABMarkerWatcher
    {
        /// <summary>自动同步开关的持久化键（关了 Unity 再开还记得）</summary>
        private const string EnabledKey = "Revolution.RevAB.AutoSync";

        /// <summary>更名前（LiteAB）的键：第一次读时把旧值迁过来，升级后开关状态不变</summary>
        private const string LegacyEnabledKey = "Revolution.LiteAB.AutoSync";

        /// <summary>去抖：最后一次变更之后至少隔这么久才重扫（把连续导入合并成一次）</summary>
        private const double MinInterval = 0.35;

        /// <summary>兜底轮询间隔：万一有"没触发导入回调"的改动，最多这么久也能追上</summary>
        private const double PollInterval = 1.5;

        /// <summary>开着的打包窗口（由窗口 OnEnable/OnDisable 注册）—— 没窗口就别干活</summary>
        private static readonly System.Collections.Generic.HashSet<ABBuildWindow> _windows =
            new System.Collections.Generic.HashSet<ABBuildWindow>();

        private static bool _enabled;

        private static bool _dirty;                 // 有变更还没消费
        private static string _reason = "";         // 待消费的变更原因（显示用）
        private static double _dirtyAt;             // 最后一次变更的时间
        private static double _lastPoll;            // 上次兜底轮询的时间
        private static string _signature = "";      // 上次看到的包名清单指纹

        private static int _syncCount;              // 本次会话已同步次数
        private static string _lastSyncReason = "";
        private static string _lastSyncClock = "";

        /// <summary>
        /// "项目里的分包可能变了"的单调计数：**每次发现变更就 +1**，与窗口开没开、
        /// 自动同步开没开都无关（MarkChanged 的第一件事就是它）。
        /// 给"不在本窗口里显示、但也要跟着变"的地方用 —— 目前是 Project 窗口的分包角标
        /// （ABProjectWindowOverlay）：它用这个当"该清缓存了"的信号，比自己轮询更早。
        /// </summary>
        public static int Revision { get; private set; }

        static ABMarkerWatcher()
        {
            _enabled = ABBuildSetting.GetPrefBool(EnabledKey, LegacyEnabledKey, true);

            // 延后到编辑器空闲时取指纹基线：AssetDatabase 在静态构造阶段还没就绪
            // （和 ABBuildConfigAutoSync 里同样的处理）
            EditorApplication.delayCall += () => _signature = CurrentSignature();

            // tick 只做极便宜的判断（脏标记 / 1.5 秒一次的指纹），真正的扫描仍然是低频的
            EditorApplication.update += Tick;
        }

        /// <summary>窗口开/关时注册进来：没窗口开着就一个判断直接返回，不空转</summary>
        public static void Attach(ABBuildWindow window) { if (window != null) _windows.Add(window); }
        public static void Detach(ABBuildWindow window) { if (window != null) _windows.Remove(window); }

        private static void Tick()
        {
            if (!_enabled || _windows.Count == 0) return;

            // 拖拽期间不重扫（列表重建会让投放位置错位）—— 这里的判断和窗口里那处是同一个意思
            SyncIfDue(IsDragging());
        }

        /// <summary>
        /// 当前是否有"会落到本工具里"的拖拽：
        /// Project 拖来的资源（DragAndDrop.paths）或工具内部拖的包 / 资源（带 RevAB 标记的自定义数据）。
        /// ★ 工具内部的拖拽**故意不填 DragAndDrop.paths**：否则松手在 Project 窗口上会被当成"移动文件"。
        /// </summary>
        internal static bool IsDragging()
            => (DragAndDrop.paths != null && DragAndDrop.paths.Length > 0)
               || DragAndDrop.GetGenericData(DragDataAssets) != null
               || DragAndDrop.GetGenericData(DragDataBundles) != null;

        /// <summary>工具内部拖资源（资源表 → 包树）时挂在 DragAndDrop 上的数据键（值是资源路径数组）</summary>
        internal const string DragDataAssets = "RevAB.Assets";

        /// <summary>工具内部拖包（包树 → 包树，改层级）时挂在 DragAndDrop 上的数据键（值是包名数组）</summary>
        internal const string DragDataBundles = "RevAB.Bundles";

        // ==================== 对外状态 ====================

        public static bool Enabled
        {
            get => _enabled;
            set
            {
                if (_enabled == value) return;
                _enabled = value;
                EditorPrefs.SetBool(EnabledKey, value);

                // 刚打开：立刻补一次，免得显示的还是关着的时候那份旧数据
                if (value) RefreshNow("开启自动同步");
                RepaintOpenWindows();
            }
        }

        public static int SyncCount => _syncCount;

        /// <summary>状态文字（状态 + 悬停说明），给窗口顶部显示</summary>
        public static GUIContent StatusContent()
        {
            if (!_enabled)
                return new GUIContent("自动同步已关",
                    "勾上「自动同步」后：别处改了分包标记，这个窗口会自动跟随更新。\n不勾也可以随时点「刷新」。");

            if (_syncCount == 0)
                return new GUIContent("自动同步中",
                    "已开启：Project 拖拽 / Inspector 改标记 / 脚本改标记 都会自动同步过来。\n" +
                    "（只在" + PollInterval + " 秒内无新变更时才重扫一次，避免连续导入把编辑器拖卡。）");

            return new GUIContent($"已同步 {_lastSyncClock}",
                $"上一次同步原因：{_lastSyncReason}\n本次会话已同步 {_syncCount} 次。\n点「刷新」可手动立即同步。");
        }

        // ==================== 对外动作 ====================

        /// <summary>有东西可能改了分包信息 —— 这里只置脏标记（很便宜），重扫留给窗口绘制时做</summary>
        public static void MarkChanged(string reason)
        {
            Revision++;                            // ★ 先记"变过"：与自动同步开关无关（见 Revision 的说明）

            if (!_enabled) return;

            _dirty = true;
            // ★ 每次都刷新时间戳 = "尾部去抖"：连续变更（比如拖 20 个资源）只在【最后一下】之后重扫一次。
            //   如果只在第一次记时间，一次大量导入会被切成好几次重扫（每次都在导入还没结束时动手）。
            _dirtyAt = EditorApplication.timeSinceStartup;
            _reason = reason;
            RepaintOpenWindows();          // 让窗口立刻重绘，OnGUI 里就有机会消费这个脏标记
        }

        /// <summary>手动刷新 / 工具自己改完标记：不等去抖，立刻重扫</summary>
        public static void RefreshNow(string reason)
        {
            if (!_enabled)
            {
                ABCollectCache.Invalidate();       // 关了自动同步也要让手动刷新生效
                return;
            }

            MarkChanged(reason);
            _dirtyAt = 0;                          // 让它这一帧就该刷（跳过 MinInterval）
            SyncIfDue(false);
        }

        /// <summary>
        /// 窗口每帧调一次（OnGUI 开头）：到点了就重扫。
        /// 返回本次同步的原因（没同步返回 null）。
        /// </summary>
        /// <param name="dragging">当前是否正在拖拽 —— 拖拽中绝不重扫（列表重建会让投放位置错位）</param>
        public static string SyncIfDue(bool dragging)
        {
            if (!_enabled) return null;
            if (dragging) return null;

            double now = EditorApplication.timeSinceStartup;

            // 兜底轮询：抓"没走导入回调"的改动（外部工具 / VCS）。只在窗口打开时才会跑到这里。
            if (!_dirty && now - _lastPoll >= PollInterval)
            {
                _lastPoll = now;
                if (SignatureChanged()) MarkChanged("包名清单变更");
            }

            if (!_dirty) return null;
            if (now - _dirtyAt < MinInterval) return null;

            _dirty = false;
            ABCollectCache.Invalidate();            // ★ 分包/依赖/体积/检查 四个视图共用这份缓存，失效它一个就够
            RepaintOpenWindows();                   // ★ 失效之后要重绘，新数据才会显示出来

            _syncCount++;
            _lastSyncReason = string.IsNullOrEmpty(_reason) ? "项目变更" : _reason;
            _lastSyncClock = DateTime.Now.ToString("HH:mm:ss");
            _reason = "";

            return _lastSyncReason;
        }

        // ==================== 资源导入回调（主通道）====================

        /// <summary>
        /// AssetPostprocessor 转发到这里：统一判断"这次导入要不要当回事"。
        /// ★ 这里只做**极便宜**的判断，绝不做扫描 —— 导入回调里做重活会拖慢整个导入流程。
        /// </summary>
        internal static void OnAssetsChanged(string[] imported, string[] deleted,
            string[] movedTo, string[] movedFrom)
        {
            // ① 包名清单变了（新增 / 删除 / 重命名包）→ 一定相关，不用再看具体是哪个资源
            //    ② 变动里出现"资源根目录下的资源"或"已经标进包的资源"→ 相关
            //
            // ★ 判断顺序：**先判断相关性、再管自动同步开关** ——
            //   MarkChanged 内部第一步就 Revision++（Project 窗口的角标靠它），
            //   所以这里不能因为"自动同步关了"就提前 return，否则角标会停在上一次的状态。
            bool signatureChanged = SignatureChanged();
            bool relevant = signatureChanged
                || IsRelevant(imported) || IsRelevant(deleted)
                || IsRelevant(movedTo) || IsRelevant(movedFrom);

            if (!relevant) return;

            MarkChanged(signatureChanged ? "包名清单变更" : "资源导入");
        }

        /// <summary>
        /// 这批变动会不会影响映射表？
        /// 判据很宽（宁可多同步一次，也不要漏），但都不贵：
        ///   · 在资源根目录下 → 相关（增删改都会改逻辑路径或标记）；
        ///   · 不在根目录下 → 只有"已经被标进某个包"的才相关（如第三方库资源被手动打了标记）。
        /// </summary>
        private static bool IsRelevant(string[] paths)
        {
            if (paths == null || paths.Length == 0) return false;

            string root = ResRoot();

            foreach (string path in paths)
            {
                if (string.IsNullOrEmpty(path)) continue;

                string norm = path.Replace('\\', '/');

                if (root.Length > 0 && norm.StartsWith(root + "/", StringComparison.Ordinal))
                    return true;

                // ★ 只读加载配置：不用 ABBuildConfig.Instance（那会顺手创建配置资产，
                //   在导入回调这种"随时可能被调用"的地方不该有副作用）
                AssetImporter importer = AssetImporter.GetAtPath(norm);
                if (importer != null && !string.IsNullOrEmpty(importer.assetBundleName))
                    return true;
            }

            return false;
        }

        // ==================== 内部小工具 ====================

        /// <summary>资源根目录（未配置返回空串）；只读加载，不创建配置资产</summary>
        private static string ResRoot()
        {
            ABBuildConfig cfg = AssetDatabase.LoadAssetAtPath<ABBuildConfig>(ABBuildSetting.ConfigAssetPath);
            return cfg == null ? "" : cfg.GetResRoot();
        }

        private static bool SignatureChanged()
        {
            string now = CurrentSignature();
            if (now == _signature) return false;

            _signature = now;
            return true;
        }

        /// <summary>包名清单指纹：排序后拼接（顺序稳定，避免"其实没变却报变了"）</summary>
        private static string CurrentSignature()
        {
            string[] names = AssetDatabase.GetAllAssetBundleNames();
            if (names == null || names.Length == 0) return "";

            Array.Sort(names, StringComparer.Ordinal);

            var sb = new StringBuilder(names.Length * 12);
            foreach (string name in names) sb.Append(name).Append('\n');
            return sb.ToString();
        }

        /// <summary>让所有打开着的打包窗口重绘（只影响本工具的窗口，不动 Project/Inspector）</summary>
        internal static void RepaintOpenWindows()
        {
            ABBuildWindow[] windows = Resources.FindObjectsOfTypeAll<ABBuildWindow>();
            for (int i = 0; i < windows.Length; i++) windows[i].Repaint();
        }
    }

    /// <summary>
    /// 资源导入回调 → 转发给监视器。
    /// 单独一个类是为了让 ABMarkerWatcher 保持"纯逻辑、可单独读"，
    /// 也避免把 AssetPostprocessor 的回调签名混进监视器的主流程里。
    /// </summary>
    internal sealed class ABMarkerPostprocessor : AssetPostprocessor
    {
        private static void OnPostprocessAllAssets(string[] importedAssets, string[] deletedAssets,
            string[] movedAssets, string[] movedFromAssetPaths)
        {
            ABMarkerWatcher.OnAssetsChanged(importedAssets, deletedAssets, movedAssets, movedFromAssetPaths);
        }
    }
}
