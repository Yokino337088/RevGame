// ============================================================
// ABBundleBrowserView.cs —— 「分包」页签（界面部分；各种改标记的动作在 ABBundleBrowserView.Actions.cs）
//
// 位置：Editor\RevResourceSystem\ABTool\Window\
//
// 【布局（对标 Unity 官方 AssetBundle Browser 的 Configure 页）】
//   ┌ 工具栏：刷新 · 新建包 · 清理未使用包名 ············ 自动同步 · Project 显示包名 · 同步状态 ┐
//   ├──────────────┬────────────────────────────────────────────────┤
//   │ 包树（搜索）   │ 资源表（搜索 · 多列 · 排序）                          │
//   │ 层级 / 多选    │────────────────────────────────────────────────│
//   │ F2 / Delete   │ 详情：选中包的依赖与问题 / 选中资源的依赖与标记来源          │
//   └──────────────┴────────────────────────────────────────────────┘
//   两条分隔条都能拖；宽度比例、列宽、选中、展开都会记住（切页签 / 重编译不丢）。
//
// 【放资源进包】从 Project 拖到左侧包上 = 加入；拖到文件夹节点 / 空白处 = 以资源名新建包；
//   拖到右侧资源表 = 加进当前选中的包；资源表里选中几行拖到左边别的包上 = 换包。
//
// 【数据】全程只读扫描（ABCollectCache），绝不触发 AutoByFolder 的自动重标记；
//   别处改了标记由 ABMarkerWatcher 自动发现并失效缓存，这里按缓存代数（Version）重建列表。
// ============================================================
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;

namespace Revolution.Editor
{
    /// <summary>「分包」页签：包树 + 资源表 + 详情（状态可序列化，窗口重编译后保留）</summary>
    [Serializable]
    internal sealed partial class ABBundleBrowserView
    {
        // ---------------- 需要跨重编译保留的状态 ----------------
        [SerializeField] private TreeViewState _bundleState = new TreeViewState();
        [SerializeField] private TreeViewState _assetState = new TreeViewState();
        [SerializeField] private MultiColumnHeaderState _assetHeaderState;
        [SerializeField] private float _splitRatio = 0.32f;          // 左侧包树占的宽度比例
        [SerializeField] private float _detailRatio = 0.68f;         // 右侧资源表占的高度比例（其余是详情）

        /// <summary>本次在工具里新建、还没有资源的包（Unity 里不存在，只挂在界面上）</summary>
        [SerializeField] private List<string> _pending = new List<string>();

        // ---------------- 运行时对象（重编译后按需重建） ----------------
        [NonSerialized] private ABBundleTreeView _bundleTree;
        [NonSerialized] private ABAssetTreeView _assetTree;
        [NonSerialized] private SearchField _bundleSearch;
        [NonSerialized] private SearchField _assetSearch;
        [NonSerialized] private ABCollectResult _collect;
        [NonSerialized] private int _builtVersion = -1;
        [NonSerialized] private bool _treeDirty = true;
        [NonSerialized] private bool _rowsDirty = true;
        [NonSerialized] private bool _draggingSplit, _draggingDetail;
        [NonSerialized] private Vector2 _detailScroll;

        // 详情里"单个资源的依赖"只在选中变了 / 扫描换代时重算
        [NonSerialized] private string _detailKey;
        [NonSerialized] private List<string> _detailBundles = new List<string>();
        [NonSerialized] private List<string> _detailLoose = new List<string>();

        private const float ToolbarHeight = 21f;
        private const float SearchHeight = 20f;
        private const float HintHeight = 18f;

        // ============================================================
        // 主绘制
        // ============================================================

        /// <summary>在 area 里画整个页签（由 ABBuildWindow 调用）</summary>
        public void OnGUI(Rect area)
        {
            EnsureControls();
            SyncData();

            float y = area.y;
            DrawToolbar(new Rect(area.x, y, area.width, ToolbarHeight));
            y += ToolbarHeight + 2f;

            if (ABBuildConfig.Instance.markMode == ABMarkMode.AutoByFolder)
            {
                var warn = new Rect(area.x + 2f, y, area.width - 4f, 34f);
                EditorGUI.HelpBox(warn,
                    "当前是「按目录自动分包」模式：下次打包会先清空所有标记、再按目录重打，这里手动改的分包会被覆盖。" +
                    "要手动分包，请到「打包」页签把「分包方式」改成「使用已有标记」。", MessageType.Warning);
                y += 36f;
            }

            var main = new Rect(area.x, y, area.width, area.yMax - y - HintHeight);
            DrawMain(main);

            GUI.Label(new Rect(area.x + 4f, area.yMax - HintHeight, area.width - 8f, HintHeight),
                "从 Project 拖资源到左侧包上 = 加入；拖到空白处 = 新建包 · 选中后 F2 改名、Delete 删除 · 右键看更多操作 · " +
                "改完到「打包」页签点「生成映射」，ResMap 与 RevResPath 才会更新",
                EditorStyles.miniLabel);
        }

        private void EnsureControls()
        {
            if (_bundleTree != null && _assetTree != null) return;

            _bundleState ??= new TreeViewState();
            _assetState ??= new TreeViewState();

            // 表头：新的列定义能覆盖旧的序列化状态时，保留用户调过的列宽 / 排序
            MultiColumnHeaderState header = ABAssetTreeView.CreateHeaderState();
            bool firstTime = _assetHeaderState == null;
            if (MultiColumnHeaderState.CanOverwriteSerializedFields(_assetHeaderState, header))
                MultiColumnHeaderState.OverwriteSerializedFields(_assetHeaderState, header);
            _assetHeaderState = header;

            var multiHeader = new MultiColumnHeader(header);
            if (firstTime) multiHeader.ResizeToFit();

            _bundleTree = new ABBundleTreeView(_bundleState, this);
            _assetTree = new ABAssetTreeView(_assetState, multiHeader, this);
            _bundleSearch = new SearchField();
            _assetSearch = new SearchField();

            _treeDirty = true;
            _rowsDirty = true;
        }

        /// <summary>扫描换代 / 本地改动之后重建两棵树的数据（不是每帧）</summary>
        private void SyncData()
        {
            _collect = ABCollectCache.Get();

            if (_builtVersion != ABCollectCache.Version)
            {
                _builtVersion = ABCollectCache.Version;
                _pending.RemoveAll(name => _collect.bundleToAssets.ContainsKey(name));   // 已经有资源了，不再是"空包"
                _treeDirty = true;
                _rowsDirty = true;
                _detailKey = null;
            }

            if (_treeDirty)
            {
                _treeDirty = false;
                _bundleTree.SetData(BuildBundleInfos());
                _rowsDirty = true;
            }

            if (_rowsDirty)
            {
                _rowsDirty = false;
                _assetTree.SetRows(BuildAssetRows());
            }
        }

        // ============================================================
        // 工具栏
        // ============================================================

        private void DrawToolbar(Rect rect)
        {
            GUILayout.BeginArea(rect, EditorStyles.toolbar);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(new GUIContent(" 刷新", ABGUI.RefreshIcon, "立刻重新扫描工程里的分包标记"),
                        EditorStyles.toolbarButton, GUILayout.Width(58)))
                    ABMarkerWatcher.RefreshNow("手动刷新");

                if (GUILayout.Button(new GUIContent(" 新建包", ABGUI.PlusIcon, "新建一个空包（之后把资源拖进来）"),
                        EditorStyles.toolbarButton, GUILayout.Width(70)))
                    CreateBundle(ParentForNewBundle());

                if (GUILayout.Button(new GUIContent("清理未使用包名", "删掉工程里已经没有任何资源使用的包名（改名 / 删资源后的残留）"),
                        EditorStyles.toolbarButton, GUILayout.Width(96)))
                    CleanupUnusedNames();

                GUILayout.Space(8);
                GUILayout.Label($"{_collect.bundleToAssets.Count + _collect.emptyBundles.Count} 个包 · " +
                                $"{_collect.logicToAssetPath.Count} 个资源", EditorStyles.miniLabel);

                GUILayout.FlexibleSpace();

                bool autoSync = GUILayout.Toggle(ABMarkerWatcher.Enabled,
                    new GUIContent("自动同步", "别处（Project / Inspector / 脚本 / 切分支）改了分包标记，这里自动跟随更新"),
                    EditorStyles.toolbarButton, GUILayout.Width(62));
                if (autoSync != ABMarkerWatcher.Enabled) ABMarkerWatcher.Enabled = autoSync;

                bool badge = GUILayout.Toggle(ABProjectWindowOverlay.Enabled,
                    new GUIContent("Project 显示包名", "Project 窗口每行右端标出它属于哪个包（▪ 自己标的 / ▫ 继承自文件夹）"),
                    EditorStyles.toolbarButton, GUILayout.Width(100));
                if (badge != ABProjectWindowOverlay.Enabled) ABProjectWindowOverlay.Enabled = badge;

                GUILayout.Label(ABMarkerWatcher.StatusContent(), EditorStyles.miniLabel, GUILayout.Width(110));
            }
            GUILayout.EndArea();
        }

        // ============================================================
        // 主区：包树 | 资源表 / 详情
        // ============================================================

        private void DrawMain(Rect main)
        {
            _splitRatio = ABGUI.VerticalSplitter(main, _splitRatio, 170f, 280f, ref _draggingSplit);
            float split = main.x + main.width * _splitRatio;

            // ---------- 左：包树 ----------
            var left = new Rect(main.x + 2f, main.y, split - main.x - 4f, main.height);
            _bundleTree.searchString = _bundleSearch.OnGUI(
                new Rect(left.x, left.y, left.width, SearchHeight - 2f), _bundleTree.searchString);
            _bundleTree.OnGUI(new Rect(left.x, left.y + SearchHeight, left.width, left.height - SearchHeight));

            // ---------- 右：资源表 + 详情 ----------
            var right = new Rect(split + 3f, main.y, main.xMax - split - 5f, main.height);
            var body = new Rect(right.x, right.y + SearchHeight, right.width, right.height - SearchHeight);

            _detailRatio = ABGUI.HorizontalSplitter(body, _detailRatio, 90f, 70f, ref _draggingDetail);
            float cut = body.y + body.height * _detailRatio;

            DrawAssetHeader(new Rect(right.x, right.y, right.width, SearchHeight - 2f));

            var table = new Rect(body.x, body.y, body.width, cut - body.y - 2f);
            if (_bundleTree.HasSelection()) _assetTree.OnGUI(table);
            else EditorGUI.HelpBox(table,
                "左边选一个包（或包文件夹），这里列出它包含的资源。\n" +
                "也可以直接把 Project 里的资源拖到左侧：拖到包上 = 加入，拖到空白处 = 新建一个包。", MessageType.Info);

            var details = new Rect(body.x, cut + 3f, body.width, body.yMax - cut - 3f);
            GUILayout.BeginArea(details, EditorStyles.helpBox);
            _detailScroll = EditorGUILayout.BeginScrollView(_detailScroll);
            DrawDetails();
            EditorGUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        private void DrawAssetHeader(Rect rect)
        {
            List<string> selected = _bundleTree.SelectedPaths();
            string title = selected.Count == 0 ? "资源"
                : selected.Count == 1 ? $"「{selected[0]}」· {_assetTree.RowCount} 个资源"
                : $"{selected.Count} 项 · {_assetTree.RowCount} 个资源";

            float titleWidth = Mathf.Min(rect.width * 0.5f, EditorStyles.boldLabel.CalcSize(new GUIContent(title)).x + 8f);
            GUI.Label(new Rect(rect.x, rect.y, titleWidth, rect.height), title, EditorStyles.boldLabel);

            _assetTree.searchString = _assetSearch.OnGUI(
                new Rect(rect.x + titleWidth + 4f, rect.y, rect.width - titleWidth - 4f, rect.height), _assetTree.searchString);
        }

        // ============================================================
        // 详情
        // ============================================================

        private void DrawDetails()
        {
            List<ABAssetTreeView.Row> rows = _assetTree.SelectedRows();
            if (rows.Count == 1) { DrawAssetDetail(rows[0]); return; }
            if (rows.Count > 1) { DrawMultiAssetDetail(rows); return; }

            List<string> bundles = _bundleTree.SelectedPaths();
            if (bundles.Count == 1 && _bundleTree.IsBundle(bundles[0])) { DrawBundleDetail(bundles[0]); return; }

            EditorGUILayout.LabelField(
                bundles.Count == 0 ? "选中一个包或资源，这里显示它的依赖和问题。" : "选中单个包或资源查看详情。",
                EditorStyles.miniLabel);
        }

        private void DrawBundleDetail(string bundle)
        {
            int count = _collect.bundleToAssets.TryGetValue(bundle, out List<string> logics) ? logics.Count : 0;
            EditorGUILayout.LabelField($"包 {bundle}",
                $"{count} 个资源 · 源文件 {ABGUI.FormatSize(ABSizeCache.OfBundle(_collect, bundle))}", EditorStyles.boldLabel);

            if (_pending.Contains(bundle))
                EditorGUILayout.HelpBox("这是刚新建的空包：Unity 里包名要有资源用它才真正存在 —— 把资源拖到左侧这个包上。", MessageType.Info);

            foreach (string problem in ProblemsOf(bundle))
                EditorGUILayout.HelpBox(problem, MessageType.Warning);

            ABDependencyReport report = ABDependencyCache.Peek(_collect);
            if (report == null)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField("依赖的包：还没分析", EditorStyles.miniLabel);
                    if (GUILayout.Button("分析依赖", EditorStyles.miniButton, GUILayout.Width(70)))
                        ABGUI.Defer(() => { ABDependencyCache.ClearCancelled(); ABDependencyCache.Get(ABCollectCache.Get()); });
                }
                return;
            }

            report.directDeps.TryGetValue(bundle, out string[] deps);
            EditorGUILayout.LabelField("依赖的包",
                deps == null || deps.Length == 0 ? "—（不依赖别的包）" : string.Join("、", deps), EditorStyles.wordWrappedMiniLabel);

            report.extraBytes.TryGetValue(bundle, out long extra);
            if (extra > 0)
                EditorGUILayout.LabelField("会一起打进来的未分包资源", ABGUI.FormatSize(extra), EditorStyles.miniLabel);
        }

        private void DrawAssetDetail(ABAssetTreeView.Row row)
        {
            EditorGUILayout.LabelField(row.displayName, ABGUI.FormatSize(row.bytes), EditorStyles.boldLabel);
            EditorGUILayout.SelectableLabel(row.assetPath, EditorStyles.miniLabel, GUILayout.Height(16));
            EditorGUILayout.LabelField("所在包", row.bundle, EditorStyles.miniLabel);
            EditorGUILayout.LabelField("逻辑名", row.logic, EditorStyles.miniLabel);
            EditorGUILayout.LabelField("标记来源",
                row.Inherited ? "继承自文件夹 " + ABGUI.Short(row.markSource) + "（单独移出要改这个文件夹）" : "资源自己标记",
                EditorStyles.miniLabel);

            RefreshAssetDependencies(row.assetPath);

            EditorGUILayout.LabelField("依赖的其他包",
                _detailBundles.Count == 0 ? "—" : string.Join("、", _detailBundles), EditorStyles.wordWrappedMiniLabel);

            if (_detailLoose.Count == 0) return;

            EditorGUILayout.LabelField($"会被一起打进这个包的未分包资源（{_detailLoose.Count} 个）", EditorStyles.miniBoldLabel);
            for (int i = 0; i < Math.Min(8, _detailLoose.Count); i++)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("定位", EditorStyles.miniButton, GUILayout.Width(38))) ABGUI.Ping(_detailLoose[i]);
                    EditorGUILayout.LabelField(ABGUI.Short(_detailLoose[i]), EditorStyles.miniLabel);
                }
            }
            if (_detailLoose.Count > 8)
                EditorGUILayout.LabelField($"… 还有 {_detailLoose.Count - 8} 个", EditorStyles.miniLabel);
        }

        private void DrawMultiAssetDetail(List<ABAssetTreeView.Row> rows)
        {
            long total = 0;
            int inherited = 0;
            foreach (ABAssetTreeView.Row row in rows)
            {
                total += row.bytes;
                if (row.Inherited) inherited++;
            }

            EditorGUILayout.LabelField($"选中 {rows.Count} 个资源", ABGUI.FormatSize(total), EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                inherited == 0 ? "都是资源自己标记的" : $"其中 {inherited} 个的包继承自文件夹（按 Delete 时会询问是否改文件夹）",
                EditorStyles.miniLabel);
            EditorGUILayout.LabelField("拖到左侧某个包上 = 一起换包；右键可「移到」指定包。", EditorStyles.miniLabel);
        }

        /// <summary>单个资源的依赖：按"别的包 / 没分包（会被复制进来）"分两类（选中变化或扫描换代才重算）</summary>
        private void RefreshAssetDependencies(string assetPath)
        {
            string key = ABCollectCache.Version + "|" + assetPath;
            if (key == _detailKey) return;
            _detailKey = key;

            _detailBundles.Clear();
            _detailLoose.Clear();

            string own = AssetDatabase.GetImplicitAssetBundleName(assetPath);
            var bundles = new SortedSet<string>(StringComparer.Ordinal);

            foreach (string dep in AssetDatabase.GetDependencies(assetPath, true))
            {
                if (dep == assetPath || !dep.StartsWith("Assets/", StringComparison.Ordinal)) continue;
                if (dep.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) continue;

                string owner = AssetDatabase.GetImplicitAssetBundleName(dep);
                if (string.IsNullOrEmpty(owner)) _detailLoose.Add(dep);
                else if (owner != own) bundles.Add(owner);
            }

            _detailBundles.AddRange(bundles);
            _detailLoose.Sort(string.CompareOrdinal);
        }

        // ============================================================
        // 数据
        // ============================================================

        /// <summary>当前所有包名：工程里真实存在的 + 残留的空包名 + 本次新建还空着的</summary>
        private List<string> AllBundleNames()
        {
            var set = new HashSet<string>(_collect.bundleToAssets.Keys, StringComparer.Ordinal);
            foreach (string empty in _collect.emptyBundles) set.Add(empty);
            foreach (string pending in _pending) set.Add(pending);

            var list = new List<string>(set);
            list.Sort(StringComparer.Ordinal);
            return list;
        }

        private List<ABBundleTreeView.BundleInfo> BuildBundleInfos()
        {
            var infos = new List<ABBundleTreeView.BundleInfo>();
            foreach (string name in AllBundleNames())
            {
                int count = _collect.bundleToAssets.TryGetValue(name, out List<string> logics) ? logics.Count : 0;
                List<string> problems = ProblemsOf(name);

                infos.Add(new ABBundleTreeView.BundleInfo
                {
                    name = name,
                    count = count,
                    bytes = ABSizeCache.OfBundle(_collect, name),
                    pending = count == 0 && _pending.Contains(name),
                    problem = problems.Count == 0 ? null : string.Join("\n", problems),
                });
            }
            return infos;
        }

        /// <summary>某个包的问题（行尾警告图标 + 详情区都用这一份）</summary>
        private List<string> ProblemsOf(string bundle)
        {
            var problems = new List<string>();

            if (_collect.emptyBundles.Contains(bundle))
                problems.Add("空包：有包名但没有任何资源（通常是改名 / 删资源的残留）→ 工具栏「清理未使用包名」");

            string prefix = bundle + " → ";
            var sameNames = new List<string>();
            foreach (string dup in _collect.duplicateAssets)
                if (dup.StartsWith(prefix, StringComparison.Ordinal)) sameNames.Add(dup.Substring(prefix.Length));
            if (sameNames.Count > 0)
                problems.Add($"包内有同名资源：{string.Join("、", sameNames)}（LoadAsset 取到哪个不确定，改名或拆包）");

            ABDependencyReport report = ABDependencyCache.Peek(_collect);
            if (report != null && report.circularBundles.Contains(bundle))
                problems.Add("循环依赖：它和别的包互相依赖，加载顺序无解，必须拆开");

            return problems;
        }

        /// <summary>资源表的行：所选包（选中包文件夹 = 它下面的所有包）里的资源</summary>
        private List<ABAssetTreeView.Row> BuildAssetRows()
        {
            var rows = new List<ABAssetTreeView.Row>();
            List<string> selected = _bundleTree.SelectedPaths();
            if (selected.Count == 0) return rows;

            var ids = new HashSet<int>();
            foreach (KeyValuePair<string, List<string>> pair in _collect.bundleToAssets)
            {
                if (!selected.Exists(path => ABBundleEditor.IsUnder(pair.Key, path))) continue;

                foreach (string logic in pair.Value)
                {
                    if (!_collect.logicToAssetPath.TryGetValue(logic, out string assetPath)) continue;

                    int id = StableId(assetPath);
                    while (!ids.Add(id)) id++;

                    rows.Add(new ABAssetTreeView.Row
                    {
                        id = id,
                        displayName = System.IO.Path.GetFileName(assetPath),
                        assetPath = assetPath,
                        logic = logic,
                        bundle = pair.Key,
                        bytes = ABSizeCache.OfAsset(assetPath),
                        markSource = ABBundleEditor.FindMarkSource(assetPath),
                    });
                }
            }
            return rows;
        }

        private static int StableId(string path)
        {
            unchecked
            {
                int hash = (int)2166136261;
                foreach (char c in path) hash = (hash ^ c) * 16777619;
                return hash == 0 ? 1 : hash;
            }
        }

        // ============================================================
        // 两棵树回调过来的（选中变化 / 查询）
        // ============================================================

        internal void OnBundleSelectionChanged()
        {
            _rowsDirty = true;
            _assetState.selectedIDs.Clear();
        }

        internal void OnAssetSelectionChanged() { }

        /// <summary>从别的页签跳过来：选中某个包并滚动到可见</summary>
        internal void Reveal(string bundle)
        {
            EnsureControls();
            _treeDirty = true;
            SyncData();
            _bundleTree.searchString = string.Empty;
            _bundleTree.SelectPaths(new[] { bundle });
        }

        /// <summary>恰好选中一个"真正的包"时返回它（资源表接收拖放要知道加进哪个包）</summary>
        internal string SingleSelectedBundle
        {
            get
            {
                List<string> selected = _bundleTree.SelectedPaths();
                return selected.Count == 1 && _bundleTree.IsBundle(selected[0]) ? selected[0] : null;
            }
        }

        /// <summary>
        /// 当前拖拽里的"工程内资源"：工具内部拖的（资源表 → 包树）优先，其次是 Project 拖来的。
        /// 不是 Assets/ 下的东西（包缓存里的、外部文件）一律不收。
        /// </summary>
        internal static string[] DraggedAssets()
        {
            if (DragAndDrop.GetGenericData(ABMarkerWatcher.DragDataAssets) is string[] fromTool) return fromTool;

            string[] raw = DragAndDrop.paths;
            if (raw == null || raw.Length == 0) return Array.Empty<string>();

            var list = new List<string>(raw.Length);
            foreach (string p in raw)
            {
                string norm = p.Replace('\\', '/');
                if (norm.StartsWith("Assets/", StringComparison.Ordinal)) list.Add(norm);
            }
            return list.ToArray();
        }

        /// <summary>新建包放在哪一层：选中的是包 → 和它同一层；选中的是文件夹 → 放进去；没选 → 最外层</summary>
        private string ParentForNewBundle()
        {
            List<string> selected = _bundleTree.SelectedPaths();
            if (selected.Count != 1) return string.Empty;

            string path = selected[0];
            return _bundleTree.IsBundle(path) ? ABBundleEditor.ParentOf(path) : path;
        }
    }
}
