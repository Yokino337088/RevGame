// ============================================================
// ABBundleTreeView.cs —— 「分包」页签左侧的包树（对标 AssetBundle Browser 的 Bundle 列表）
//
// 位置：Editor\RevResourceSystem\ABTool\Window\
//
// 【为什么换成 TreeView】
//   以前是一列按钮：只能单选、没有层级、没有键盘操作、改名要去底部输入框。
//   Unity 自带的 IMGUI TreeView 把这些都给了（AssetBundle Browser 用的也是它）：
//     · 包名里的 '/' 显示成层级（ui/main、ui/top 收在 ui 下面），可折叠；
//     · 多选（Ctrl / Shift）、方向键导航、搜索（匹配完整包名）；
//     · F2 / 双击 原地重命名，Delete 删除，右键菜单；
//     · 拖放：Project 里的资源拖到包上 = 加入；拖到文件夹节点 / 空白处 = 以资源名新建包；
//             把包拖到另一个节点上 = 改层级（"ui_main" 拖进 "ui" → "ui/ui_main"）。
//
// 【节点 = 包名路径】
//   "ui/main" 会产生两个节点："ui"（文件夹）和 "ui/main"（包）。一个路径可以既是包又有子包，
//   这时它显示成包、下面还挂着子包。节点 id 由路径哈希得到（同一组包名 → 同一组 id），
//   所以重扫之后选中和展开状态都还在。
//
// 【它只管"显示和接收操作"】真正改标记的动作全部回调给 ABBundleBrowserView（再走 ABBundleEditor）。
// ============================================================
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;

namespace Revolution.Editor
{
    /// <summary>包树（左侧）</summary>
    internal sealed class ABBundleTreeView : TreeView
    {
        /// <summary>一个包在树里要显示的数据（由浏览视图在每次重扫后填好）</summary>
        internal sealed class BundleInfo
        {
            public string name;
            public int count;
            public long bytes;

            /// <summary>本次在工具里新建、还没有资源的包（Unity 里还不存在，只挂在界面上）</summary>
            public bool pending;

            /// <summary>问题说明（空 = 没问题），显示成行尾的警告图标 + 悬停提示</summary>
            public string problem;
        }

        internal sealed class Node : TreeViewItem
        {
            /// <summary>完整路径（包名，或包"文件夹"的前缀）</summary>
            public readonly string path;

            /// <summary>有没有一个包就叫这个名字（没有 = 纯文件夹节点）</summary>
            public bool isBundle;

            public BundleInfo info;

            /// <summary>自己 + 所有子包的合计（折叠起来也能看到这一组有多大）</summary>
            public int totalCount;
            public long totalBytes;

            /// <summary>自己或某个子包有问题</summary>
            public bool anyProblem;

            public Node(int id, int depth, string path, string leaf) : base(id, depth, leaf) { this.path = path; }
        }

        private readonly ABBundleBrowserView _owner;
        private List<BundleInfo> _bundles = new List<BundleInfo>();
        private readonly Dictionary<string, Node> _nodes = new Dictionary<string, Node>(StringComparer.Ordinal);
        private readonly Dictionary<int, Node> _byId = new Dictionary<int, Node>();

        private static GUIStyle _rightLabel;

        private static GUIStyle RightLabel
            => _rightLabel ??= new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.MiddleRight };

        public ABBundleTreeView(TreeViewState state, ABBundleBrowserView owner) : base(state)
        {
            _owner = owner;
            showBorder = true;
            showAlternatingRowBackgrounds = true;
            Reload();
        }

        // ============================================================
        // 数据
        // ============================================================

        /// <summary>换一批包数据并重建（选中 / 展开按路径保留）</summary>
        public void SetData(List<BundleInfo> bundles)
        {
            _bundles = bundles ?? new List<BundleInfo>();
            Reload();
        }

        /// <summary>当前选中节点的路径</summary>
        public List<string> SelectedPaths()
        {
            var list = new List<string>();
            foreach (int id in GetSelection())
                if (_byId.TryGetValue(id, out Node node)) list.Add(node.path);
            return list;
        }

        public bool IsBundle(string path) => _nodes.TryGetValue(path, out Node node) && node.isBundle;

        public bool Exists(string path) => _nodes.ContainsKey(path);

        /// <summary>选中这些路径（展开父节点并滚动到可见），并触发选中变化回调</summary>
        public void SelectPaths(IEnumerable<string> paths)
        {
            var ids = new List<int>();
            foreach (string path in paths)
                if (_nodes.TryGetValue(path, out Node node)) ids.Add(node.id);

            SetSelection(ids, TreeViewSelectionOptions.RevealAndFrame | TreeViewSelectionOptions.FireSelectionChanged);
        }

        /// <summary>对某个路径开始原地重命名（新建包之后立刻让用户起名字）</summary>
        public void BeginRenamePath(string path)
        {
            if (_nodes.TryGetValue(path, out Node node)) BeginRename(node);
        }

        // ============================================================
        // 建树
        // ============================================================

        protected override TreeViewItem BuildRoot()
        {
            var root = new TreeViewItem { id = 0, depth = -1, displayName = "root", children = new List<TreeViewItem>() };

            _nodes.Clear();
            _byId.Clear();

            var sorted = new List<BundleInfo>(_bundles);
            sorted.Sort((a, b) => string.CompareOrdinal(a.name, b.name));

            foreach (BundleInfo bundle in sorted)
            {
                string[] segments = bundle.name.Split('/');
                TreeViewItem parent = root;
                string path = string.Empty;

                for (int i = 0; i < segments.Length; i++)
                {
                    path = i == 0 ? segments[0] : path + "/" + segments[i];

                    if (!_nodes.TryGetValue(path, out Node node))
                    {
                        node = new Node(StableId(path), i, path, segments[i]);
                        _nodes[path] = node;
                        _byId[node.id] = node;
                        parent.AddChild(node);
                    }

                    if (i == segments.Length - 1)
                    {
                        node.isBundle = true;
                        node.info = bundle;
                    }

                    node.totalCount += bundle.count;
                    node.totalBytes += bundle.bytes;
                    if (!string.IsNullOrEmpty(bundle.problem)) node.anyProblem = true;

                    parent = node;
                }
            }

            return root;
        }

        /// <summary>
        /// 路径 → 稳定 id（FNV-1a）。同一组包名永远得到同一组 id：重扫之后选中 / 展开状态不丢。
        /// 碰撞时顺延（0 留给根节点）。
        /// </summary>
        private int StableId(string path)
        {
            unchecked
            {
                int hash = (int)2166136261;
                foreach (char c in path) hash = (hash ^ c) * 16777619;

                while (hash == 0 || _byId.ContainsKey(hash)) hash++;
                return hash;
            }
        }

        protected override bool DoesItemMatchSearch(TreeViewItem item, string search)
            => item is Node node && node.path.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0;

        // ============================================================
        // 绘制
        // ============================================================

        protected override void RowGUI(RowGUIArgs args)
        {
            if (!(args.item is Node node))
            {
                base.RowGUI(args);
                return;
            }

            Rect row = args.rowRect;
            float indent = GetContentIndent(node);

            var iconRect = new Rect(row.x + indent, row.y + 1f, 16f, 16f);
            Texture icon = node.isBundle ? ABGUI.BundleIcon : ABGUI.FolderIcon;
            if (icon != null) GUI.DrawTexture(iconRect, icon, ScaleMode.ScaleToFit);

            // 右侧：数量 · 体积（空包给一句提示）；有问题再加一个警告图标
            string right = node.isBundle && node.info.pending
                ? "空包 · 拖资源进来"
                : $"{node.totalCount}  ·  {ABGUI.FormatSize(node.totalBytes)}";

            float rightWidth = RightLabel.CalcSize(new GUIContent(right)).x + 6f;
            var rightRect = new Rect(row.xMax - rightWidth - 4f, row.y, rightWidth, row.height);

            if (node.anyProblem && ABGUI.WarnIcon != null)
            {
                var warnRect = new Rect(rightRect.x - 18f, row.y + 1f, 16f, 16f);
                string tip = node.isBundle && !string.IsNullOrEmpty(node.info.problem)
                    ? node.info.problem
                    : "这一组里有包存在问题（展开看是哪个）";
                GUI.Label(warnRect, new GUIContent(ABGUI.WarnIcon, tip));
                rightRect.x -= 18f;
            }

            if (!args.isRenaming)
            {
                var labelRect = new Rect(iconRect.xMax + 3f, row.y, Mathf.Max(0f, rightRect.x - iconRect.xMax - 6f), row.height);

                // 搜索时是一张平铺的结果表 —— 只显示最后一段就分不清是哪个包了，所以显示完整包名
                string text = hasSearch ? node.path : node.displayName;
                var content = new GUIContent(text, node.path);

                GUIStyle style = node.isBundle && node.info.pending ? EditorStyles.miniLabel : EditorStyles.label;
                GUI.Label(labelRect, content, style);
            }

            GUI.Label(rightRect, right, RightLabel);
        }

        protected override Rect GetRenameRect(Rect rowRect, int row, TreeViewItem item)
        {
            float indent = GetContentIndent(item) + 19f;
            return new Rect(rowRect.x + indent, rowRect.y, Mathf.Max(60f, rowRect.width - indent - 90f), rowRect.height);
        }

        // ============================================================
        // 交互
        // ============================================================

        protected override bool CanMultiSelect(TreeViewItem item) => true;

        protected override bool CanRename(TreeViewItem item) => item is Node;

        protected override void RenameEnded(RenameEndedArgs args)
        {
            if (!args.acceptedRename || !_byId.TryGetValue(args.itemID, out Node node)) return;

            string leaf = ABBundleEditor.Normalize(args.newName);
            if (leaf.Length == 0 || leaf == node.displayName) return;

            // 只改最后一段；新名字里带 '/' = 顺便挪进更深的层级（和 AssetBundle Browser 一致）
            string newPath = ABBundleEditor.Combine(ABBundleEditor.ParentOf(node.path), leaf);
            _owner.RenameNode(node.path, newPath);
        }

        protected override void SelectionChanged(IList<int> selectedIds) => _owner.OnBundleSelectionChanged();

        protected override void DoubleClickedItem(int id)
        {
            if (!_byId.TryGetValue(id, out Node node)) return;

            // 包：双击改名（最常用的操作）；纯文件夹：双击展开 / 折叠
            if (node.isBundle) BeginRename(node);
            else SetExpanded(id, !IsExpanded(id));
        }

        protected override void ContextClickedItem(int id)
        {
            _owner.ShowBundleContextMenu(SelectedPaths());
            Event.current.Use();                       // 吃掉事件：否则还会再弹一次"空白处"菜单
        }

        protected override void ContextClicked()
        {
            _owner.ShowEmptyContextMenu();
            Event.current.Use();
        }

        protected override void KeyEvent()
        {
            Event e = Event.current;
            if (e.type != EventType.KeyDown) return;

            bool delete = e.keyCode == KeyCode.Delete || (e.keyCode == KeyCode.Backspace && (e.command || e.control));
            if (delete && HasSelection())
            {
                _owner.DeleteNodes(SelectedPaths());
                e.Use();
                return;
            }

            if (e.keyCode == KeyCode.F2 && _byId.TryGetValue(state.lastClickedID, out Node node))
            {
                BeginRename(node);
                e.Use();
            }
        }

        // ============================================================
        // 拖放
        // ============================================================

        protected override bool CanStartDrag(CanStartDragArgs args) => !hasSearch;

        protected override void SetupDragAndDrop(SetupDragAndDropArgs args)
        {
            // 只拖"最上层"的选中项：同时选了 ui 和 ui/main，拖 ui 就够了（ui/main 会跟着走）
            var paths = new List<string>();
            foreach (int id in args.draggedItemIDs)
                if (_byId.TryGetValue(id, out Node node)) paths.Add(node.path);

            paths.RemoveAll(p => paths.Exists(other => other != p && ABBundleEditor.IsUnder(p, other)));
            if (paths.Count == 0) return;

            DragAndDrop.PrepareStartDrag();
            DragAndDrop.SetGenericData(ABMarkerWatcher.DragDataBundles, paths.ToArray());
            DragAndDrop.objectReferences = Array.Empty<UnityEngine.Object>();   // 不给 Project / 场景可接的东西
            DragAndDrop.StartDrag(paths.Count == 1 ? paths[0] : $"{paths.Count} 个包");
        }

        protected override DragAndDropVisualMode HandleDragAndDrop(DragAndDropArgs args)
        {
            Node target = args.parentItem as Node;
            bool upon = args.dragAndDropPosition == DragAndDropPosition.UponItem;

            // ---------- ① 拖的是包：改层级 ----------
            if (DragAndDrop.GetGenericData(ABMarkerWatcher.DragDataBundles) is string[] bundles)
            {
                // 放到某个节点上 = 挪进它下面；放在两行之间 / 空白处 = 挪到那一层（根 = 最外层）
                string newParent = target != null ? target.path : string.Empty;

                foreach (string bundle in bundles)
                    if (ABBundleEditor.IsUnder(newParent, bundle)) return DragAndDropVisualMode.Rejected;   // 不能挪进自己下面

                if (args.performDrop) _owner.MoveBundles(bundles, newParent);
                return DragAndDropVisualMode.Move;
            }

            // ---------- ② 拖的是资源（Project 里的，或右侧资源表里的） ----------
            string[] assets = ABBundleBrowserView.DraggedAssets();
            if (assets.Length == 0) return DragAndDropVisualMode.None;

            bool fromTool = DragAndDrop.GetGenericData(ABMarkerWatcher.DragDataAssets) != null;

            if (upon && target != null && target.isBundle)
            {
                if (args.performDrop) _owner.DropAssets(target.path, assets);               // 放到包上 = 加入 / 换进这个包
            }
            else
            {
                // 放到文件夹节点上 / 两行之间 / 空白处 = 以第一个资源的名字新建一个包
                string parent = target != null ? target.path : string.Empty;
                if (args.performDrop) _owner.DropAssetsAsNewBundle(parent, assets);
            }

            return fromTool ? DragAndDropVisualMode.Move : DragAndDropVisualMode.Copy;
        }
    }
}
