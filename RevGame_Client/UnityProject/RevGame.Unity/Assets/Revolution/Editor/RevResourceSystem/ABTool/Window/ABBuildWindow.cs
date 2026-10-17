// ============================================================
// ABBuildWindow.cs —— RevAB 打包窗口（原名 LiteAB）
//
// 位置：Editor\RevResourceSystem\ABTool\Window\
//
// 【打开方式】菜单 Revolution.Tools/资源/RevAB 打包工具（另有 RevAB 分包浏览 直达「分包」页签）
//
// 【六个页签（对标 Unity 官方 AssetBundle Browser 的 Configure / Build / Inspect，再加三个检查页）】
//   · 打包：选平台 → 一键打包 / 仅生成映射 / 仅校验（主操作放最上面）；配置按分组折叠；结果可一键打开输出目录
//   · 分包：包树 + 资源表 + 详情（拖拽建包 / 换包、F2 改名、Delete 删除、右键菜单），见 ABBundleBrowserView
//   · 依赖 / 体积 / 检查 / 快照：只读分析，发现问题能直接跳到分包页签或一键处理
//   「检查」页签标题上带问题数：不用点进去就知道现在能不能打包。
//
// 【更名】LiteAB → RevAB：菜单、窗口标题、日志 tag、本机偏好键、快照目录都改了；
//   旧的本机偏好与快照目录在第一次使用时自动迁移（见 ABBuildSetting.GetPrefBool / ABLayoutSnapshotStore）。
// ============================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Revolution.Editor
{
    public class ABBuildWindow : EditorWindow
    {
        private const string Title = "RevAB 打包工具";
        private const float TabHeight = 26f;

        private enum Tab { Build = 0, Bundles = 1, Dependency = 2, Size = 3, Check = 4, Snapshot = 5 }

        // ---------------- 需要跨重编译保留的状态 ----------------
        [SerializeField] private int _tab;
        [SerializeField] private ABBundleBrowserView _browser = new ABBundleBrowserView();
        [SerializeField] private Vector2 _buildScroll;
        [SerializeField] private bool _showAllIssues;

        // ---------------- 本次会话的结果（重编译后需要重新校验 / 打包，界面会提示） ----------------
        [NonSerialized] private ABValidateResult _validate;
        [NonSerialized] private ABDependencyReport _dependency;
        [NonSerialized] private ABBuildResult _build;
        [NonSerialized] private int _validateSyncStamp;
        [NonSerialized] private bool _validateReadOnly;
        [NonSerialized] private double _buildSeconds;
        [NonSerialized] private long _buildBytes;

        private readonly ABDependencyView _dependencyView = new ABDependencyView();
        private readonly ABSizeView _sizeView = new ABSizeView();
        private readonly ABDuplicateView _checkView = new ABDuplicateView();
        private readonly ABSnapshotView _snapshotView = new ABSnapshotView();

        // ============================================================
        // 打开
        // ============================================================

        [MenuItem("Revolution.Tools/资源/RevAB 打包工具", false, 1)]
        public static void Open() => ShowTab(Tab.Build);

        /// <summary>直达「分包」页签（不用先开打包窗口再切页签）</summary>
        [MenuItem("Revolution.Tools/资源/RevAB 分包浏览", false, 2)]
        private static void OpenBrowser() => ShowTab(Tab.Bundles);

        private static ABBuildWindow ShowTab(Tab tab)
        {
            var win = GetWindow<ABBuildWindow>();
            win.titleContent = new GUIContent(Title, ABGUI.BundleIcon);
            win.minSize = new Vector2(660, 480);
            win._tab = (int)tab;
            win.Show();
            return win;
        }

        /// <summary>从别的页签跳到「分包」并选中某个包（依赖 / 体积 / 检查 页签里点包名用）</summary>
        internal static void RevealBundle(string bundle)
        {
            ABBuildWindow win = ShowTab(Tab.Bundles);
            win._browser.Reveal(bundle);
            win.Repaint();
        }

        /// <summary>切到某个页签（打包结果里的"去检查页签看"之类的链接用）</summary>
        internal static void SwitchTo(int tab)
        {
            ABBuildWindow win = GetWindow<ABBuildWindow>();
            win._tab = tab;
            win.Repaint();
        }

        // 注册进 ABMarkerWatcher：只有窗口开着它才干活（关掉窗口后自动同步完全静默）
        private void OnEnable()
        {
            titleContent = new GUIContent(Title, ABGUI.BundleIcon);
            _browser ??= new ABBundleBrowserView();
            ABMarkerWatcher.Attach(this);
        }

        private void OnDisable() => ABMarkerWatcher.Detach(this);

        // ============================================================
        // 主绘制
        // ============================================================

        private void OnGUI()
        {
            // 自动同步的兜底消费点（主驱动在 ABMarkerWatcher 的 tick；拖拽中不重扫）
            ABMarkerWatcher.SyncIfDue(ABMarkerWatcher.IsDragging());

            DrawTabs();

            var body = new Rect(0f, TabHeight, position.width, position.height - TabHeight);

            switch ((Tab)_tab)
            {
                case Tab.Build: DrawBuildTab(body); break;
                case Tab.Bundles: _browser.OnGUI(body); break;
                case Tab.Dependency: DrawInArea(body, _dependencyView); break;
                case Tab.Size: DrawInArea(body, _sizeView); break;
                case Tab.Check: DrawInArea(body, _checkView); break;
                default: DrawInArea(body, _snapshotView.Draw); break;
            }
        }

        private static void DrawInArea(Rect body, ABAnalysisView view) => DrawInArea(body, view.Draw);

        private static void DrawInArea(Rect body, Action<float> draw)
        {
            GUILayout.BeginArea(new Rect(body.x + 4f, body.y + 2f, body.width - 8f, body.height - 4f));
            draw(body.height - 6f);
            GUILayout.EndArea();
        }

        private void DrawTabs()
        {
            int issues = ABIssueCounter.Count(ABCollectCache.Get());

            var tabs = new[]
            {
                new GUIContent(" 打包", ABGUI.Icon("BuildSettings.Editor.Small"), "选平台、一键打包、生成映射与路径常量"),
                new GUIContent(" 分包", ABGUI.BundleIcon, "看 / 改分包：包树 + 资源表 + 拖拽"),
                new GUIContent(" 依赖", "包之间的依赖、会被复制多份的资源、循环依赖"),
                new GUIContent(" 体积", "每个包多大（含会被一起打进去的资源）、最大的资源"),
                issues > 0
                    ? new GUIContent($" 检查 ({issues})", ABGUI.WarnIcon, $"发现 {issues} 个会阻止打包 / 运行时出错的问题")
                    : new GUIContent(" 检查", "同包重名 / 逻辑路径冲突 / 空包 / 漏标"),
                new GUIContent(" 快照", "记录分包布局，对比前后到底改了什么"),
            };

            GUILayout.BeginArea(new Rect(0f, 0f, position.width, TabHeight), EditorStyles.toolbar);
            _tab = GUILayout.Toolbar(_tab, tabs, EditorStyles.toolbarButton, GUILayout.Height(TabHeight - 4f));
            GUILayout.EndArea();
        }

        // ============================================================
        // 「打包」页签
        // ============================================================

        private void DrawBuildTab(Rect body)
        {
            ABBuildConfig cfg = ABBuildConfig.Instance;

            GUILayout.BeginArea(new Rect(body.x + 6f, body.y + 4f, body.width - 12f, body.height - 6f));

            DrawBuildHeader(cfg);

            _buildScroll = EditorGUILayout.BeginScrollView(_buildScroll);

            var so = new SerializedObject(cfg);
            so.Update();

            DrawRootSection(cfg);
            DrawMarkSection(cfg, so);
            DrawOptionsSection(so);
            DrawCodeGenSection();

            if (so.ApplyModifiedProperties()) SaveConfig(cfg);        // 只在真的改了才落盘

            DrawValidateSection();
            DrawBuildResultSection();

            EditorGUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        // ---------------- 顶部：平台 / 输出目录 / 三个动作 ----------------

        private void DrawBuildHeader(ABBuildConfig cfg)
        {
            BuildTarget target = ABBuildSetting.ResolveBuildTarget();
            bool installed = ABBuildSetting.IsBuildTargetInstalled(target);
            string outDir = ABBuildSetting.GetOutputDir(target).Replace('\\', '/');

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    DrawTargetPopup();

                    GUILayout.Space(10);
                    EditorGUILayout.LabelField(new GUIContent("输出目录", "相对工程根目录；按平台分子目录，多平台产物互不覆盖"),
                        GUILayout.Width(52));
                    EditorGUILayout.SelectableLabel(outDir, EditorStyles.textField, GUILayout.Height(18));

                    using (new EditorGUI.DisabledScope(!Directory.Exists(outDir)))
                    {
                        if (GUILayout.Button(new GUIContent("打开", "在资源管理器里打开输出目录"), GUILayout.Width(44)))
                            EditorUtility.RevealInFinder(Path.GetFullPath(outDir));
                    }
                }

                EditorGUILayout.Space(2);

                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUI.DisabledScope(!cfg.HasResRoot || !installed))
                    {
                        if (ABGUI.PrimaryButton(new GUIContent($"打包（{PlatformLabel(target)}）",
                                "收集 → 校验（有错误就中止）→ 打包 → 写 ResMap / 产物清单 → 生成 RevResPath →（可选）拷到 StreamingAssets"),
                                30f))
                            ABGUI.Defer(DoOneClickBuild);
                    }

                    using (new EditorGUI.DisabledScope(!cfg.HasResRoot))
                    {
                        if (GUILayout.Button(new GUIContent("仅生成映射",
                                "不打包：只按当前分包写 ResMap.txt + 生成 RevResPath（编辑器里改完分包、想先跑起来时用）"),
                                GUILayout.Height(30), GUILayout.Width(96)))
                            ABGUI.Defer(DoGenerateMap);

                        if (GUILayout.Button(new GUIContent("仅校验", "只检查，不改任何标记、不写任何文件"),
                                GUILayout.Height(30), GUILayout.Width(64)))
                            ABGUI.Defer(DoValidate);
                    }
                }

                if (!cfg.HasResRoot)
                    EditorGUILayout.HelpBox("还没设置「资源根目录」：在下面「资源目录」里把文件夹拖进槽里，或点「选择…」。" +
                                            "逻辑路径、RevResPath 常量、编辑器直读都依赖它。", MessageType.Error);
                else if (!installed)
                    EditorGUILayout.HelpBox($"这台机器没装 {PlatformLabel(target)} 的构建支持：到 Unity Hub → 安装 → 添加模块，" +
                                            "或在上面换一个平台。", MessageType.Warning);
            }
        }

        private static void DrawTargetPopup()
        {
            BuildTarget active = EditorUserBuildSettings.activeBuildTarget;
            BuildTarget? preferred = ABBuildSetting.PreferredBuildTarget;

            var targets = new List<BuildTarget>(ABBuildSetting.CommonBuildTargets);
            if (preferred.HasValue && !targets.Contains(preferred.Value)) targets.Add(preferred.Value);

            var labels = new List<GUIContent> { new GUIContent($"跟随当前平台（{PlatformLabel(active)}）") };
            foreach (BuildTarget t in targets)
                labels.Add(new GUIContent(PlatformLabel(t) + (ABBuildSetting.IsBuildTargetInstalled(t) ? "" : "（未安装）")));

            int index = preferred.HasValue ? targets.IndexOf(preferred.Value) + 1 : 0;

            EditorGUILayout.LabelField(new GUIContent("目标平台",
                "打哪个平台的包（不用先切换编辑器平台）。这是本机偏好，不影响别人；CI 打包永远用当前平台。"),
                GUILayout.Width(52));
            int picked = EditorGUILayout.Popup(index, labels.ToArray(), GUILayout.Width(200));

            if (picked != index)
                ABBuildSetting.PreferredBuildTarget = picked == 0 ? (BuildTarget?)null : targets[picked - 1];
        }

        private static string PlatformLabel(BuildTarget target)
        {
            switch (target)
            {
                case BuildTarget.StandaloneWindows:
                case BuildTarget.StandaloneWindows64: return "Windows";
                case BuildTarget.StandaloneOSX: return "macOS";
                case BuildTarget.StandaloneLinux64: return "Linux";
                default: return target.ToString();
            }
        }

        // ---------------- 资源目录 ----------------

        private void DrawRootSection(ABBuildConfig cfg)
        {
            if (!ABGUI.Foldout("root", "资源目录", true, cfg.HasResRoot ? null : "（未设置）") && cfg.HasResRoot) return;

            EditorGUI.indentLevel++;

            // ---------- 资源根目录：可拖拽的文件夹槽 + "选择…"按钮（★ 代码里没有任何默认路径）----------
            using (new EditorGUILayout.HorizontalScope())
            {
                // ★ 空路径不能丢给 LoadAssetAtPath（Unity 会报 "path must start with Assets/"）
                var current = cfg.HasResRoot ? AssetDatabase.LoadAssetAtPath<DefaultAsset>(cfg.GetResRoot()) : null;
                var picked = (DefaultAsset)EditorGUILayout.ObjectField(
                    new GUIContent("资源根目录", "它下面的资源按'相对此目录的路径'生成逻辑名（必填）"),
                    current, typeof(DefaultAsset), false);

                if (picked != null)
                {
                    string p = AssetDatabase.GetAssetPath(picked);
                    if (AssetDatabase.IsValidFolder(p) && p != cfg.resRoot) SetResRoot(cfg, p);
                }

                if (GUILayout.Button("选择…", GUILayout.Width(60)))
                {
                    string asset = PickFolder("选择资源根目录");
                    if (asset != null) SetResRoot(cfg, asset);
                }
            }

            // ---------- 音效目录：音效系统（RevSoundSystem）加载音效 / BGM 的地方 ----------
            if (cfg.HasResRoot)
            {
                DrawSubFolderRow(cfg, "音效目录", cfg.GetSfxRoot(), ABBuildConfig.DefaultSfxRoot, v => cfg.sfxRoot = v);
                DrawSubFolderRow(cfg, "BGM 目录", cfg.GetBgmRoot(), ABBuildConfig.DefaultBgmRoot, v => cfg.bgmRoot = v);

                if (!AssetDatabase.IsValidFolder(cfg.GetSfxFolderPath()) || !AssetDatabase.IsValidFolder(cfg.GetBgmFolderPath()))
                    EditorGUILayout.HelpBox("目录还不存在（把音频放进去就行）：\n" +
                                            cfg.GetSfxFolderPath() + "\n" + cfg.GetBgmFolderPath(), MessageType.Info);

                DrawSubFolderHint(cfg.GetSfxFolderPath(), "音效目录");
            }

            EditorGUI.indentLevel--;
            EditorGUILayout.Space(4);
        }

        private static void SetResRoot(ABBuildConfig cfg, string assetPath)
        {
            cfg.resRoot = assetPath;
            cfg.ApplyToRuntime();
            SaveConfig(cfg);                            // ★ 落盘：不然重启后这个设置就没了
            RegeneratePathConsts();                     // 根目录变了 → 旧常量全部作废，重生成
            ABMarkerWatcher.RefreshNow("资源根目录变更");  // 漏标检测等都依赖根目录 → 立刻重扫
        }

        /// <summary>弹系统目录框，只接受工程 Assets 内的目录；返回 "Assets/xxx"，取消 / 非法返回 null</summary>
        private static string PickFolder(string title)
        {
            string abs = EditorUtility.OpenFolderPanel(title, "Assets", "");
            if (string.IsNullOrEmpty(abs)) return null;

            string projectRoot = Directory.GetParent(Application.dataPath).FullName.Replace('\\', '/');
            abs = abs.Replace('\\', '/');

            if (abs == projectRoot + "/Assets" || abs.StartsWith(projectRoot + "/Assets/", StringComparison.Ordinal))
                return abs.Substring(projectRoot.Length + 1);

            EditorUtility.DisplayDialog("无效目录", "请选择工程 Assets 目录内的文件夹", "确定");
            return null;
        }

        /// <summary>
        /// 一行"资源根目录下的子目录"选择：拖文件夹进来 / 「选择…」/ 「默认」。
        /// ★ 存的是**相对资源根目录的逻辑段**（如 Audio/Sfx）—— 换工程、换盘符都不失效。
        /// </summary>
        private static void DrawSubFolderRow(ABBuildConfig cfg, string label, string current, string defaultSegment,
                                             Action<string> apply)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                string full = current.Length > 0 ? cfg.GetResRoot() + "/" + current : "";
                var currentAsset = full.Length > 0 ? AssetDatabase.LoadAssetAtPath<DefaultAsset>(full) : null;

                var picked = (DefaultAsset)EditorGUILayout.ObjectField(
                    new GUIContent(label, $"RevSoundSystem 从这里加载（当前：{current}）；必须在资源根目录之内"),
                    currentAsset, typeof(DefaultAsset), false);

                if (picked != null)
                {
                    string p = AssetDatabase.GetAssetPath(picked);
                    if (AssetDatabase.IsValidFolder(p)) SetSubFolder(cfg, current, apply, p);
                }

                if (GUILayout.Button("选择…", GUILayout.Width(60)))
                {
                    string asset = PickFolder("选择" + label);
                    if (asset != null) SetSubFolder(cfg, current, apply, asset);
                }

                if (GUILayout.Button(new GUIContent("默认", "恢复成 " + defaultSegment), GUILayout.Width(46)))
                {
                    apply(defaultSegment);
                    SaveConfig(cfg);
                    RegenerateSoundPathConsts();
                }
            }
        }

        /// <summary>把音效目录下已有的子目录列出来（音效名里可以带子目录）</summary>
        private static void DrawSubFolderHint(string fullFolder, string label)
        {
            if (fullFolder.Length == 0 || !AssetDatabase.IsValidFolder(fullFolder)) return;

            List<string> subs = ResPathNaming.CollectFolders(fullFolder);
            if (subs.Count == 0) return;

            int show = Mathf.Min(subs.Count, 8);
            string list = string.Join("、", subs.GetRange(0, show)) + (subs.Count > show ? " …" : "");

            EditorGUILayout.LabelField($"{label}下有 {subs.Count} 个子目录：{list}（调用时写进名字里：RevSound.Play(\"{subs[0]}/ui_click\")）",
                EditorStyles.wordWrappedMiniLabel);
        }

        /// <summary>工程内路径 → 相对资源根目录的逻辑段；不合法 / 没变化就什么都不做</summary>
        private static void SetSubFolder(ABBuildConfig cfg, string current, Action<string> apply, string assetPath)
        {
            string root = cfg.GetResRoot();
            string normalized = assetPath.Replace('\\', '/').TrimEnd('/');

            if (!normalized.StartsWith(root + "/", StringComparison.Ordinal))
            {
                EditorUtility.DisplayDialog("无效目录",
                    "音效目录必须放在「资源根目录」之内：\n" + root + "\n\n" +
                    "放在外面的话：编辑器直读拼不出路径，RevResPath 也不会为它生成常量。", "确定");
                return;
            }

            string segment = cfg.NormalizeSubRoot(normalized);
            if (segment.Length == 0 || segment == cfg.NormalizeSubRoot(current)) return;

            apply(segment);
            SaveConfig(cfg);
            RegenerateSoundPathConsts();
        }

        // ---------------- 分包方式 ----------------

        private static readonly GUIContent[] MarkModeNames =
        {
            new GUIContent("使用已有标记（推荐）", "分包由你在「分包」页签 / Inspector 里设置，工具只读取、绝不改动"),
            new GUIContent("按目录自动分包", "每次打包 / 生成映射都会先清空全部标记，再按资源根目录下的顶层目录重新标记"),
        };

        private void DrawMarkSection(ABBuildConfig cfg, SerializedObject so)
        {
            if (!ABGUI.Foldout("mark", "分包方式")) return;
            EditorGUI.indentLevel++;

            SerializedProperty mode = so.FindProperty("markMode");
            int picked = EditorGUILayout.Popup(new GUIContent("分包方式"), mode.enumValueIndex, MarkModeNames);

            if (picked != mode.enumValueIndex)
            {
                if (picked == (int)ABMarkMode.AutoByFolder)
                {
                    // ★ 破坏性：切过去之后下一次打包会清空所有手动分包 —— 必须先确认
                    ABGUI.Defer(() =>
                    {
                        if (!EditorUtility.DisplayDialog("切换为「按目录自动分包」？",
                                "切换后，每次「打包」/「仅生成映射」都会先清空工程里的全部 AB 标记，" +
                                "再按资源根目录下的顶层目录重新标记。\n\n你在「分包」页签里手动做的分包会被覆盖。",
                                "切换", "取消"))
                            return;

                        cfg.markMode = ABMarkMode.AutoByFolder;
                        SaveConfig(cfg);
                    });
                }
                else
                {
                    mode.enumValueIndex = picked;
                }
            }

            if (cfg.markMode == ABMarkMode.AutoByFolder)
                EditorGUILayout.HelpBox("自动分包模式：打包前会清空全部标记再按顶层目录重打（UI/xxx → 包 ui）。", MessageType.Warning);

            EditorGUILayout.PropertyField(so.FindProperty("excludeFolders"),
                new GUIContent("排除的目录名", "这些目录不参与自动分包，也不算进「漏标」（放源文件 / 参考图的目录）"), true);
            EditorGUILayout.PropertyField(so.FindProperty("excludeExtensions"),
                new GUIContent("排除的后缀", "这些后缀不进包。★ 别加 .txt：数据表就是 .txt"), true);
            EditorGUILayout.PropertyField(so.FindProperty("checkUnmarkedAssets"),
                new GUIContent("打包前检查漏标", "资源根目录下没有任何 AB 标记的资源不会进包，运行时必然加载失败 —— 打包前拦下"));

            EditorGUI.indentLevel--;
            EditorGUILayout.Space(4);
        }

        // ---------------- 打包选项 ----------------

        private static readonly GUIContent[] CompressNames =
        {
            new GUIContent("LZ4（推荐）", "块压缩：加载快、体积中等"),
            new GUIContent("LZMA", "整体压缩：体积最小、首次加载最慢 —— 适合下载包"),
            new GUIContent("不压缩", "加载最快、体积最大 —— 只用于调试"),
        };

        private static void DrawOptionsSection(SerializedObject so)
        {
            if (!ABGUI.Foldout("options", "打包选项")) return;
            EditorGUI.indentLevel++;

            SerializedProperty compress = so.FindProperty("compressMode");
            compress.enumValueIndex = EditorGUILayout.Popup(new GUIContent("压缩方式"), compress.enumValueIndex, CompressNames);

            EditorGUILayout.PropertyField(so.FindProperty("forceRebuild"),
                new GUIContent("强制全部重打", "关闭 = 增量打包：只重打内容变化了的包（推荐）"));
            EditorGUILayout.PropertyField(so.FindProperty("deterministic"),
                new GUIContent("确定性打包", "相同输入产出相同字节：CI 可缓存、团队 diff 干净、热更差异比对更准（推荐开）"));
            EditorGUILayout.PropertyField(so.FindProperty("stripUnityVersion"),
                new GUIContent("去掉 Unity 版本号", "省一点包体；开了就要记住：换 Unity 版本必须先「强制全部重打」"));
            EditorGUILayout.PropertyField(so.FindProperty("cleanOutputBeforeBuild"),
                new GUIContent("打包前清空输出目录", "排查问题时勾；会让增量打包失效"));

            SerializedProperty copy = so.FindProperty("copyToStreamingAssets");
            EditorGUILayout.PropertyField(copy, new GUIContent("打包后拷到 StreamingAssets", "产物随安装包一起发布"));
            using (new EditorGUI.DisabledScope(!copy.boolValue))
            {
                EditorGUILayout.PropertyField(so.FindProperty("copyTarget"),
                    new GUIContent("拷贝目标目录", "相对 Assets，例如 StreamingAssets"));
            }

            EditorGUILayout.PropertyField(so.FindProperty("version"),
                new GUIContent("版本号", "写进产物清单 BuildManifest.json"));

            EditorGUI.indentLevel--;
            EditorGUILayout.Space(4);
        }

        // ---------------- 代码生成 ----------------

        private static void DrawCodeGenSection()
        {
            if (!ABGUI.Foldout("codegen", "代码生成", false)) return;

            EditorGUILayout.LabelField("「打包」「仅生成映射」都会顺带生成 RevResPath；音效目录改了会自动重生成 RevSoundPath。这里是手动入口。",
                EditorStyles.wordWrappedMiniLabel);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("生成路径常量 RevResPath")) ABGUI.Defer(() => ABResPathGenerator.Generate());
                if (GUILayout.Button("打开 RevResPath.cs", GUILayout.Width(150))) OpenCode(ABBuildSetting.ResPathCodePath, "生成路径常量");
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("生成音效目录常量 RevSoundPath")) ABGUI.Defer(() => ABSoundPathGenerator.Generate());
                if (GUILayout.Button("打开 RevSoundPath.cs", GUILayout.Width(150))) OpenCode(ABBuildSetting.SoundPathCodePath, "生成音效目录常量");
            }

            EditorGUILayout.Space(4);
        }

        private static void OpenCode(string path, string generateButton)
        {
            var obj = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
            if (obj == null)
            {
                EditorUtility.DisplayDialog("还没生成", $"{Path.GetFileName(path)} 还不存在。\n先点「{generateButton}」。", "好");
                return;
            }
            AssetDatabase.OpenAsset(obj);
        }

        // ---------------- 校验结果 ----------------

        private void DrawValidateSection()
        {
            if (_validate == null) return;

            string badge = _validate.errors.Count > 0
                ? $"✖ {_validate.errors.Count} 个错误  ⚠ {_validate.warnings.Count} 个警告"
                : $"✔ 可以打包  ⚠ {_validate.warnings.Count} 个警告";

            if (!ABGUI.Foldout("validate", "校验结果", true, badge)) return;

            EditorGUILayout.LabelField(
                $"{_validate.bundleCount} 个包 · {_validate.assetCount} 个资源 · 源文件 {ABGUI.FormatSize(_validate.totalBytes)}" +
                (_validateReadOnly && ABBuildConfig.Instance.markMode == ABMarkMode.AutoByFolder
                    ? "（「仅校验」看的是当前标记；自动分包模式下打包时会先重新标记）" : ""),
                EditorStyles.miniLabel);

            // 这份结果属于"点按钮那一刻"：之后项目里又改过分包，就已经过期了
            if (ABMarkerWatcher.SyncCount != _validateSyncStamp)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.HelpBox("这之后项目里的分包标记又变过，下面是旧结果。", MessageType.Warning);
                    if (GUILayout.Button("重新校验", GUILayout.Width(72), GUILayout.Height(38))) ABGUI.Defer(DoValidate);
                }
            }

            int limit = _showAllIssues ? int.MaxValue : 8;
            DrawMessages(_validate.errors, MessageType.Error, limit);
            DrawMessages(_validate.warnings, MessageType.Warning, limit);

            using (new EditorGUILayout.HorizontalScope())
            {
                int total = _validate.errors.Count + _validate.warnings.Count;
                if (total > 16 || _showAllIssues)
                    _showAllIssues = GUILayout.Toggle(_showAllIssues, "显示全部", EditorStyles.miniButton, GUILayout.Width(70));

                GUILayout.FlexibleSpace();
                if (GUILayout.Button("去「检查」页签逐条定位", EditorStyles.miniButton, GUILayout.Width(150)))
                    _tab = (int)Tab.Check;
            }

            EditorGUILayout.Space(4);
        }

        private static void DrawMessages(List<string> messages, MessageType type, int limit)
        {
            for (int i = 0; i < Math.Min(limit, messages.Count); i++)
                EditorGUILayout.HelpBox(messages[i], type);

            if (messages.Count > limit)
                EditorGUILayout.LabelField($"… 还有 {messages.Count - limit} 条", EditorStyles.miniLabel);
        }

        // ---------------- 打包结果 ----------------

        private void DrawBuildResultSection()
        {
            if (_build == null) return;
            if (!ABGUI.Foldout("result", "打包结果", true, _build.success ? "✔ 成功" : "✖ 失败")) return;

            string outDir = _build.outputDir.Replace('\\', '/');

            if (!_build.success)
            {
                EditorGUILayout.HelpBox("打包失败：详细原因见 Console（通常是脚本编译错误、平台模块没装，或资源导入报错）。",
                    MessageType.Error);
                return;
            }

            EditorGUILayout.HelpBox(
                $"{PlatformLabel(_build.target)} · {_build.manifest.allBundles.Length} 个包 · 产物 {ABGUI.FormatSize(_buildBytes)} · " +
                $"用时 {_buildSeconds:F1} 秒\n→ {outDir}", MessageType.Info);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("打开输出目录")) EditorUtility.RevealInFinder(Path.GetFullPath(outDir));
                if (GUILayout.Button("复制路径")) EditorGUIUtility.systemCopyBuffer = Path.GetFullPath(outDir);
                if (GUILayout.Button("看产物清单"))
                    EditorUtility.RevealInFinder(Path.GetFullPath(Path.Combine(outDir, "BuildManifest.json")));
            }

            if (_dependency == null) return;

            if (_dependency.circularBundles.Count > 0)
                EditorGUILayout.HelpBox("循环依赖：" + string.Join("、", _dependency.circularBundles), MessageType.Error);

            if (_dependency.sharedAssets.Count > 0)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.HelpBox($"有 {_dependency.sharedAssets.Count} 个资源被多个包共享、又没单独分包（会被复制多份）。",
                        MessageType.Warning);
                    if (GUILayout.Button("去「依赖」页签处理", GUILayout.Width(120), GUILayout.Height(38)))
                        _tab = (int)Tab.Dependency;
                }
            }
        }

        // ============================================================
        // 动作（都经 ABGUI.Defer 在下一帧执行：它们会弹框 / 打包，不能在 OnGUI 中途做）
        // ============================================================

        /// <summary>仅校验：只读扫描，绝不改标记（自动分包模式下也不会重新标记）</summary>
        private void DoValidate()
        {
            _validate = ABValidator.Validate(ABCollector.CollectReadOnly());
            _validateSyncStamp = ABMarkerWatcher.SyncCount;
            _validateReadOnly = true;
            Repaint();
        }

        /// <summary>收集（自动分包模式会先重新标记）+ 校验，供打包 / 生成映射用</summary>
        private ABCollectResult CollectForOutput()
        {
            ABCollectResult collect = ABCollector.Collect();
            _validate = ABValidator.Validate(collect);
            _validateSyncStamp = ABMarkerWatcher.SyncCount;
            _validateReadOnly = false;

            if (ABBuildConfig.Instance.markMode == ABMarkMode.AutoByFolder)
                ABMarkerWatcher.RefreshNow("自动分包重新标记");     // 标记被重写过 → 其它页签立刻跟上

            return collect;
        }

        /// <summary>仅生成映射：写 ResMap + 生成 RevResPath（不打包）；校验有错误时先问一句</summary>
        private void DoGenerateMap()
        {
            ABCollectResult collect = CollectForOutput();

            if (!_validate.CanBuild &&
                !EditorUtility.DisplayDialog("校验有错误",
                    $"有 {_validate.errors.Count} 个错误（逻辑路径冲突 / 同包重名 / 漏标 …）。\n" +
                    "仍然生成的话，出错的资源在运行时会加载失败。\n\n要继续吗？", "仍然生成", "取消"))
            {
                Repaint();
                return;
            }

            int count = ABMapGenerator.WriteOutputs(collect, "仅生成映射");
            ShowNotification(new GUIContent($"已生成映射：{count} 条"));
            Repaint();
        }

        /// <summary>一键打包：收集 → 校验 → 打包 → 依赖分析 → ResMap + 清单 → 快照留底 → RevResPath → 拷贝</summary>
        private void DoOneClickBuild()
        {
            ABBuildConfig cfg = ABBuildConfig.Instance;
            if (!cfg.HasResRoot)
            {
                EditorUtility.DisplayDialog("还没设置资源根目录", "请先在「资源目录」里设置「资源根目录」。", "好");
                return;
            }

            BuildTarget target = ABBuildSetting.ResolveBuildTarget();
            if (!ABBuildSetting.IsBuildTargetInstalled(target))
            {
                EditorUtility.DisplayDialog("平台没装", $"这台机器没装 {PlatformLabel(target)} 的构建支持（Unity Hub → 添加模块）。", "好");
                return;
            }

            // 1. 收集 + 2. 校验：Android 材质 Shader 在 AB 与 Player 中都没有时，必须在出包前拦下。
            ABCollectResult collect = CollectForOutput();
            ABValidator.ValidateAndroidShaders(collect, _validate, target);
            if (!_validate.CanBuild)
            {
                RevABLog.Error($"[RevAB] 校验未通过，共 {_validate.errors.Count} 个错误，已中止打包");
                ShowNotification(new GUIContent($"校验有 {_validate.errors.Count} 个错误，已中止"));
                Repaint();
                return;
            }

            // 3. 打包
            var clock = Stopwatch.StartNew();
            _build = ABBuilderCore.Build(target);
            _buildSeconds = clock.Elapsed.TotalSeconds;
            _dependency = null;

            if (!_build.success)
            {
                RevABLog.Error("[RevAB] 打包失败");
                Repaint();
                return;
            }

            // 4. 依赖分析（读 Unity 真正算出的 Manifest）
            _dependency = ABDependencyAnalyzer.Analyze(_build.manifest.raw);

            // 5. 写 ResMap + 产物清单；快照留底
            int mapCount = ABManifestWriter.WriteResMap(collect);
            ABManifestWriter.WriteBuildManifest(_build);
            ABLayoutSnapshotStore.WriteLastBuild(collect, "打包");

            // 6. 生成 RevResPath 常量类 + 可选拷贝到 StreamingAssets
            ABResPathGenerator.Generate();
            ABBuilderCore.CopyToStreamingAssets(_build.outputDir);
            AssetDatabase.Refresh();

            _buildBytes = 0;
            foreach (string bundle in _build.manifest.allBundles)
            {
                string file = Path.Combine(_build.outputDir, bundle);
                if (File.Exists(file)) _buildBytes += new FileInfo(file).Length;
            }

            RevABLog.Info($"[RevAB] 打包完成：{_build.manifest.allBundles.Length} 个包，映射 {mapCount} 条，" +
                          $"用时 {_buildSeconds:F1}s → {_build.outputDir}");
            ShowNotification(new GUIContent($"打包完成：{_build.manifest.allBundles.Length} 个包"));
            Repaint();
        }

        // ============================================================
        // 配置落盘与重生成
        // ============================================================

        /// <summary>
        /// 音效目录变了 → 重新生成 RevSoundPath.cs（音效系统读的就是它）。
        /// ★ 延后一帧：Generate 会写 .cs 并触发脚本编译，OnGUI 绘制过程中不允许编译。
        /// </summary>
        private static void RegenerateSoundPathConsts()
            => EditorApplication.delayCall += () => ABSoundPathGenerator.Generate();

        /// <summary>资源根目录变了 → 旧常量全部作废，重生成一次（同样延后一帧）</summary>
        private static void RegeneratePathConsts()
            => EditorApplication.delayCall += () => ABResPathGenerator.Generate();

        /// <summary>
        /// 把配置改动落盘。
        /// ★ 必须显式保存：改 ScriptableObject 的字段只是改了内存里的对象，不落盘的话重启 / 重编译就可能丢。
        /// </summary>
        private static void SaveConfig(ABBuildConfig cfg)
        {
            EditorUtility.SetDirty(cfg);
            EditorApplication.delayCall += AssetDatabase.SaveAssets;    // OnGUI 期间不做资产写入
        }
    }
}
