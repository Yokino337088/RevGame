// ============================================================
// ABAssetTreeView.cs —— 「分包」页签右侧的资源表（对标 AssetBundle Browser 的 Asset 列表）
//
// 位置：Editor\RevResourceSystem\ABTool\Window\
//
// 【比原来的"一行一个 [定位][移出] 按钮"好在哪】
//   · 多列表头：资源 / 所在包 / 大小 / 标记来源 / 路径，点表头排序（找"最大的那个"一眼就有）；
//   · 多选 + Delete 批量移出；双击 = 在 Project 里定位并选中；右键：定位 / 移出 / 移到其他包 / 复制路径；
//   · 能拖：选中几行拖到左边某个包上 = 换包（AssetBundle Browser 同款）；
//   · 标记来源一列直接说清"自己标的"还是"继承自哪个文件夹"—— 回答"为什么它在这个包里"。
// ============================================================
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;

namespace Revolution.Editor
{
    /// <summary>资源表（右侧，多列）</summary>
    internal sealed class ABAssetTreeView : TreeView
    {
        /// <summary>一行资源</summary>
        internal sealed class Row : TreeViewItem
        {
            public string assetPath;
            public string logic;
            public string bundle;
            public long bytes;

            /// <summary>标记写在哪：资源自己身上，或某个上级文件夹上（null = 取不到，理论上不会发生）</summary>
            public string markSource;

            /// <summary>包是从上级文件夹继承来的（自己没写标记）</summary>
            public bool Inherited => markSource != null && markSource != assetPath;
        }

        private enum Column { Name, Bundle, Size, Source, Path }

        private readonly ABBundleBrowserView _owner;
        private List<Row> _rows = new List<Row>();
        private readonly Dictionary<int, Row> _byId = new Dictionary<int, Row>();

        private static GUIStyle _grey;

        private static GUIStyle Grey
            => _grey ??= new GUIStyle(EditorStyles.label) { normal = { textColor = new Color(0.55f, 0.55f, 0.55f) } };

        private static GUIStyle _right;

        private static GUIStyle Right
            => _right ??= new GUIStyle(EditorStyles.label) { alignment = TextAnchor.MiddleRight };

        public ABAssetTreeView(TreeViewState state, MultiColumnHeader header, ABBundleBrowserView owner)
            : base(state, header)
        {
            _owner = owner;
            showBorder = true;
            showAlternatingRowBackgrounds = true;
            header.sortingChanged += _ => Reload();
            Reload();
        }

        /// <summary>表头定义（列宽 / 能否排序 / 默认排序）；持久化状态由窗口序列化保存</summary>
        public static MultiColumnHeaderState CreateHeaderState()
        {
            MultiColumnHeaderState.Column Col(string title, string tip, float width, float min, bool autoResize)
                => new MultiColumnHeaderState.Column
                {
                    headerContent = new GUIContent(title, tip),
                    width = width,
                    minWidth = min,
                    autoResize = autoResize,
                    canSort = true,
                    allowToggleVisibility = true,
                    headerTextAlignment = TextAlignment.Left,
                    sortingArrowAlignment = TextAlignment.Right,
                };

            var columns = new[]
            {
                Col("资源", "资源名（灰色 = 包是从上级文件夹继承来的）", 190f, 90f, true),
                Col("所在包", "资源实际进哪个包（同时选中多个包时用来区分）", 120f, 60f, true),
                Col("大小", "源文件体积（不是打包后的 AB 体积）", 72f, 50f, false),
                Col("标记来源", "标记写在资源自己身上，还是继承自上级文件夹", 150f, 70f, true),
                Col("路径", "资源在工程里的路径", 240f, 80f, true),
            };
            columns[(int)Column.Name].allowToggleVisibility = false;

            return new MultiColumnHeaderState(columns);
        }

        // ============================================================
        // 数据
        // ============================================================

        public void SetRows(List<Row> rows)
        {
            _rows = rows ?? new List<Row>();
            Reload();
        }

        public List<Row> SelectedRows()
        {
            var list = new List<Row>();
            foreach (int id in GetSelection())
                if (_byId.TryGetValue(id, out Row row)) list.Add(row);
            return list;
        }

        public int RowCount => _rows.Count;

        protected override TreeViewItem BuildRoot()
        {
            var root = new TreeViewItem { id = 0, depth = -1, displayName = "root", children = new List<TreeViewItem>() };
            _byId.Clear();

            var sorted = new List<Row>(_rows);
            Sort(sorted);

            foreach (Row row in sorted)
            {
                row.depth = 0;
                row.children = null;
                root.AddChild(row);
                _byId[row.id] = row;
            }

            return root;
        }

        private void Sort(List<Row> rows)
        {
            int column = multiColumnHeader.sortedColumnIndex;
            bool ascending = column < 0 || multiColumnHeader.IsSortedAscending(column);

            Comparison<Row> compare;
            switch ((Column)Math.Max(0, column))
            {
                case Column.Bundle: compare = (a, b) => string.CompareOrdinal(a.bundle, b.bundle); break;
                case Column.Size: compare = (a, b) => a.bytes.CompareTo(b.bytes); break;
                case Column.Source: compare = (a, b) => a.Inherited.CompareTo(b.Inherited); break;
                case Column.Path: compare = (a, b) => string.CompareOrdinal(a.assetPath, b.assetPath); break;
                default: compare = (a, b) => string.Compare(a.displayName, b.displayName, StringComparison.OrdinalIgnoreCase); break;
            }

            // 主键相同时按路径排，保证顺序稳定（否则每次重扫都可能跳一下）
            rows.Sort((a, b) =>
            {
                int result = compare(a, b);
                if (result == 0) result = string.CompareOrdinal(a.assetPath, b.assetPath);
                return ascending ? result : -result;
            });
        }

        protected override bool DoesItemMatchSearch(TreeViewItem item, string search)
            => item is Row row && (row.assetPath.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0
                                   || row.bundle.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0);

        // ============================================================
        // 绘制
        // ============================================================

        protected override void RowGUI(RowGUIArgs args)
        {
            if (!(args.item is Row row))
            {
                base.RowGUI(args);
                return;
            }

            for (int i = 0; i < args.GetNumVisibleColumns(); i++)
                CellGUI(args.GetCellRect(i), row, (Column)args.GetColumn(i));
        }

        private void CellGUI(Rect cell, Row row, Column column)
        {
            CenterRectUsingSingleLineHeight(ref cell);
            GUIStyle text = row.Inherited ? Grey : EditorStyles.label;

            switch (column)
            {
                case Column.Name:
                {
                    Texture icon = AssetDatabase.GetCachedIcon(row.assetPath);
                    if (icon != null) GUI.DrawTexture(new Rect(cell.x, cell.y, 16f, 16f), icon, ScaleMode.ScaleToFit);
                    GUI.Label(new Rect(cell.x + 18f, cell.y, cell.width - 18f, cell.height),
                        new GUIContent(row.displayName, row.assetPath), text);
                    break;
                }

                case Column.Bundle:
                    GUI.Label(cell, row.bundle, text);
                    break;

                case Column.Size:
                    GUI.Label(cell, ABGUI.FormatSize(row.bytes), Right);
                    break;

                case Column.Source:
                    GUI.Label(cell, row.Inherited
                        ? new GUIContent("继承自 " + ABGUI.Short(row.markSource),
                            "包是从这个文件夹的标记继承来的：单独「移出」移不掉，要改这个文件夹（或把资源换到别的包）")
                        : new GUIContent("自己标记"), text);
                    break;

                case Column.Path:
                    GUI.Label(cell, new GUIContent(ABGUI.Short(row.assetPath), row.assetPath), text);
                    break;
            }
        }

        // ============================================================
        // 交互
        // ============================================================

        protected override bool CanMultiSelect(TreeViewItem item) => true;

        protected override void SelectionChanged(IList<int> selectedIds) => _owner.OnAssetSelectionChanged();

        protected override void DoubleClickedItem(int id)
        {
            if (_byId.TryGetValue(id, out Row row)) ABGUI.Ping(row.assetPath);
        }

        protected override void ContextClickedItem(int id)
        {
            _owner.ShowAssetContextMenu(SelectedRows());
            Event.current.Use();
        }

        protected override void KeyEvent()
        {
            Event e = Event.current;
            if (e.type != EventType.KeyDown) return;

            bool delete = e.keyCode == KeyCode.Delete || (e.keyCode == KeyCode.Backspace && (e.command || e.control));
            if (!delete || !HasSelection()) return;

            _owner.RemoveAssets(SelectedRows());
            e.Use();
        }

        // ---------------- 拖出去：拖到左边的包上 = 换包 ----------------

        protected override bool CanStartDrag(CanStartDragArgs args) => true;

        protected override void SetupDragAndDrop(SetupDragAndDropArgs args)
        {
            var paths = new List<string>();
            foreach (int id in args.draggedItemIDs)
                if (_byId.TryGetValue(id, out Row row)) paths.Add(row.assetPath);

            if (paths.Count == 0) return;

            DragAndDrop.PrepareStartDrag();
            // ★ 只挂自定义数据，不填 DragAndDrop.paths / objectReferences：
            //   否则一不小心松手在 Project 窗口的文件夹上，会被当成"把文件移过去"
            DragAndDrop.SetGenericData(ABMarkerWatcher.DragDataAssets, paths.ToArray());
            DragAndDrop.StartDrag(paths.Count == 1 ? System.IO.Path.GetFileName(paths[0]) : $"{paths.Count} 个资源");
        }

        // ---------------- 拖进来：Project 里的资源拖到表里 = 加进当前选中的包 ----------------

        protected override DragAndDropVisualMode HandleDragAndDrop(DragAndDropArgs args)
        {
            if (DragAndDrop.GetGenericData(ABMarkerWatcher.DragDataAssets) != null) return DragAndDropVisualMode.None;  // 表内自己拖自己：没意义

            string target = _owner.SingleSelectedBundle;
            if (target == null) return DragAndDropVisualMode.Rejected;           // 没选中（或选了多个）包：不知道加进哪个

            string[] assets = ABBundleBrowserView.DraggedAssets();
            if (assets.Length == 0) return DragAndDropVisualMode.None;

            if (args.performDrop) _owner.DropAssets(target, assets);
            return DragAndDropVisualMode.Copy;
        }
    }
}
