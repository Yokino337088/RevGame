// ============================================================
// RevExcelTableList.cs —— 导表窗口左侧：按工作簿分组的表列表
//
// 位置：Editor\RevExcelTool\Window\
//
// 【一行告诉你三件事】
//   · 能不能导：✖ 有错误（红） / ⚠ 有警告 / 灰色 = 忽略的工作表（空白 / # 开头）；
//   · 有多大：N 列 · N 行；
//   · 要不要导：「新」= 还没导出过，● = 和已导出的数据不一样（改过 Excel 了），什么都没有 = 已是最新。
//
// 【操作】双击表 = 用 Excel 打开所在工作簿；右键：在 Excel 中打开 / 在资源管理器中显示 / 定位数据文件 / 复制类名。
// ============================================================
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;

namespace Revolution.Editor.ExcelTool
{
    internal sealed class RevExcelTableList : TreeView
    {
        internal sealed class Item : TreeViewItem
        {
            public string file;             // 工作簿完整路径
            public ExcelTable table;        // null = 工作簿节点
            public string fileError;        // 工作簿读不出来的原因
            public int tableCount;
        }

        private readonly Action<ExcelTable> _onSelect;
        private RevExcelReadResult _read;
        private ExcelExportPlan _plan;
        private readonly Dictionary<int, Item> _byId = new Dictionary<int, Item>();

        private static readonly Color Accent = new Color(0.35f, 0.62f, 1f);
        private static readonly Color ErrorColor = new Color(0.95f, 0.38f, 0.33f);

        private static GUIStyle _right, _grey, _badge;

        private static GUIStyle Right => _right ??= new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.MiddleRight };

        private static GUIStyle Grey => _grey ??= new GUIStyle(EditorStyles.label)
        {
            normal = { textColor = new Color(0.5f, 0.5f, 0.5f) },
            clipping = TextClipping.Clip
        };

        private static GUIStyle Badge => _badge ??= new GUIStyle(EditorStyles.miniBoldLabel)
        {
            alignment = TextAnchor.MiddleCenter,
            normal = { textColor = Accent }
        };

        public RevExcelTableList(TreeViewState state, Action<ExcelTable> onSelect) : base(state)
        {
            _onSelect = onSelect;
            showBorder = true;
            showAlternatingRowBackgrounds = true;
            rowHeight = 20f;
            Reload();
        }

        public void SetData(RevExcelReadResult read, ExcelExportPlan plan)
        {
            _read = read;
            _plan = plan;
            Reload();
        }

        /// <summary>换一份导出计划（只影响"要不要导"的角标）</summary>
        public void SetPlan(ExcelExportPlan plan)
        {
            _plan = plan;
            Repaint();
        }

        public ExcelTable SelectedTable
        {
            get
            {
                IList<int> ids = GetSelection();
                return ids.Count > 0 && _byId.TryGetValue(ids[0], out Item item) ? item.table : null;
            }
        }

        /// <summary>选中某张表（按"工作簿 + 表名"认；重新读取后用它恢复选中）</summary>
        public bool Select(string key)
        {
            foreach (Item item in _byId.Values)
            {
                if (item.table == null || KeyOf(item.table) != key) continue;
                SetSelection(new[] { item.id }, TreeViewSelectionOptions.RevealAndFrame | TreeViewSelectionOptions.FireSelectionChanged);
                return true;
            }
            return false;
        }

        /// <summary>选中第一张能导出的表（没有就第一张）</summary>
        public void SelectFirst()
        {
            Item fallback = null;
            foreach (TreeViewItem row in GetRows())
            {
                if (!(row is Item item) || item.table == null) continue;
                fallback ??= item;
                if (!item.table.IsValid) continue;
                fallback = item;
                break;
            }

            if (fallback != null)
                SetSelection(new[] { fallback.id }, TreeViewSelectionOptions.RevealAndFrame | TreeViewSelectionOptions.FireSelectionChanged);
        }

        public static string KeyOf(ExcelTable table) => table == null ? null : table.SourcePath + "|" + table.Name;

        // ============================================================
        // 建树
        // ============================================================

        protected override TreeViewItem BuildRoot()
        {
            var root = new TreeViewItem { id = 0, depth = -1, displayName = "root", children = new List<TreeViewItem>() };
            _byId.Clear();
            if (_read == null) return root;

            var byFile = new Dictionary<string, Item>(StringComparer.OrdinalIgnoreCase);

            foreach (string file in _read.Files)
            {
                var item = new Item { id = Id(file), depth = 0, displayName = Path.GetFileName(file), file = file };
                byFile[file] = item;
                root.AddChild(item);
                _byId[item.id] = item;
            }

            foreach (KeyValuePair<string, string> error in _read.FileErrors)
                if (byFile.TryGetValue(error.Key, out Item fileItem)) fileItem.fileError = error.Value;

            foreach (ExcelTable table in _read.Tables)
            {
                if (!byFile.TryGetValue(table.SourcePath, out Item fileItem)) continue;

                var item = new Item { id = Id(KeyOf(table)), depth = 1, displayName = table.Name, file = table.SourcePath, table = table };
                fileItem.AddChild(item);
                fileItem.tableCount++;
                _byId[item.id] = item;
            }

            // 首次显示全部展开（以后按用户的折叠状态来）
            if (state.expandedIDs.Count == 0)
                foreach (Item fileItem in byFile.Values) state.expandedIDs.Add(fileItem.id);

            SetupDepthsFromParentsAndChildren(root);
            return root;
        }

        private int Id(string key)
        {
            unchecked
            {
                int hash = (int)2166136261;
                foreach (char c in key) hash = (hash ^ c) * 16777619;
                while (hash == 0 || _byId.ContainsKey(hash)) hash++;
                return hash;
            }
        }

        protected override bool DoesItemMatchSearch(TreeViewItem item, string search)
            => item is Item i && i.table != null && i.table.Name.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0;

        // ============================================================
        // 绘制
        // ============================================================

        protected override void RowGUI(RowGUIArgs args)
        {
            if (!(args.item is Item item)) { base.RowGUI(args); return; }

            Rect row = args.rowRect;
            float x = row.x + GetContentIndent(item);
            var iconRect = new Rect(x, row.y + 2f, 16f, 16f);

            if (item.table == null) DrawFile(item, row, iconRect);
            else DrawTable(item, row, iconRect);
        }

        private static void DrawFile(Item item, Rect row, Rect iconRect)
        {
            Texture icon = item.fileError != null ? ABGUI.ErrorIcon : ABGUI.Icon("TextAsset Icon");
            if (icon != null) GUI.DrawTexture(iconRect, icon, ScaleMode.ScaleToFit);

            string right = item.fileError != null ? "读取失败" : $"{item.tableCount} 张表";
            float rightWidth = Right.CalcSize(new GUIContent(right)).x + 4f;

            GUI.Label(new Rect(iconRect.xMax + 3f, row.y, row.xMax - iconRect.xMax - rightWidth - 10f, row.height),
                new GUIContent(item.displayName, item.fileError ?? item.file), EditorStyles.boldLabel);

            Color old = GUI.color;
            if (item.fileError != null) GUI.color = ErrorColor;
            GUI.Label(new Rect(row.xMax - rightWidth - 4f, row.y, rightWidth, row.height), right, Right);
            GUI.color = old;
        }

        private void DrawTable(Item item, Rect row, Rect iconRect)
        {
            ExcelTable table = item.table;

            Texture icon = table.Ignored ? null
                : !table.IsValid ? ABGUI.ErrorIcon
                : table.Warnings.Count > 0 ? ABGUI.WarnIcon
                : ABGUI.Icon("TestPassed");
            if (icon != null) GUI.DrawTexture(iconRect, icon, ScaleMode.ScaleToFit);

            // 右侧：规模 / 错误数；再往左是"要不要导"的角标
            string right = table.Ignored ? "忽略"
                : !table.IsValid ? $"{table.Errors.Count} 个错误"
                : $"{table.Fields.Count} 列 · {table.Rows.Count} 行";

            float rightWidth = Right.CalcSize(new GUIContent(right)).x + 4f;
            var rightRect = new Rect(row.xMax - rightWidth - 4f, row.y, rightWidth, row.height);

            Color old = GUI.color;
            if (!table.Ignored && !table.IsValid) GUI.color = ErrorColor;
            GUI.Label(rightRect, right, Right);
            GUI.color = old;

            float labelRight = rightRect.x - 4f;

            ExcelPlannedFile planned = table.IsValid ? _plan?.DataFileOf(table) : null;
            if (planned != null && planned.Change != ExcelFileChange.Unchanged)
            {
                bool isNew = planned.Change == ExcelFileChange.New;
                var badgeRect = new Rect(labelRight - (isNew ? 20f : 14f), row.y, isNew ? 20f : 14f, row.height);
                GUI.Label(badgeRect, new GUIContent(isNew ? "新" : "●",
                    isNew ? "还没导出过这张表" : "Excel 里的数据和已导出的不一样：需要重新导出"), Badge);
                labelRight = badgeRect.x - 2f;
            }

            string tip = table.Ignored ? table.IgnoreReason
                : !table.IsValid ? table.Errors[0].Message
                : $"{table.StructName()} / {table.ContainerName()}\n来自 {table.SourceFile}";

            GUI.Label(new Rect(iconRect.xMax + 3f, row.y, Mathf.Max(0f, labelRight - iconRect.xMax - 3f), row.height),
                new GUIContent(hasSearch ? $"{table.Name}  ({table.SourceFile})" : table.Name, tip),
                table.Ignored ? Grey : EditorStyles.label);
        }

        // ============================================================
        // 交互
        // ============================================================

        protected override bool CanMultiSelect(TreeViewItem item) => false;

        protected override void SelectionChanged(IList<int> selectedIds)
        {
            if (selectedIds.Count > 0 && _byId.TryGetValue(selectedIds[0], out Item item)) _onSelect?.Invoke(item.table);
        }

        protected override void DoubleClickedItem(int id)
        {
            if (!_byId.TryGetValue(id, out Item item)) return;

            if (item.table == null) SetExpanded(id, !IsExpanded(id));
            else OpenInExcel(item.file);
        }

        protected override void ContextClickedItem(int id)
        {
            if (!_byId.TryGetValue(id, out Item item)) return;

            var menu = new GenericMenu();
            menu.AddItem(new GUIContent("在 Excel 中打开"), false, () => OpenInExcel(item.file));
            menu.AddItem(new GUIContent("在资源管理器中显示"), false, () => EditorUtility.RevealInFinder(item.file));

            if (item.table != null && item.table.IsValid)
            {
                menu.AddSeparator("");

                ExcelPlannedFile data = _plan?.DataFileOf(item.table);
                if (data != null && File.Exists(data.Path))
                    menu.AddItem(new GUIContent("定位数据文件"), false, () => ABGUI.Ping(data.Path));
                else
                    menu.AddDisabledItem(new GUIContent("定位数据文件（还没导出）"));

                string container = ExcelCodeGenerator.Identifier(item.table.ContainerName());
                string structName = ExcelCodeGenerator.Identifier(item.table.StructName());
                menu.AddItem(new GUIContent($"复制容器类名 {container}"), false, () => EditorGUIUtility.systemCopyBuffer = container);
                menu.AddItem(new GUIContent($"复制加载代码"), false, () => EditorGUIUtility.systemCopyBuffer =
                    $"{container} table = await RevDataTableManager.LoadAsync<{container}>();\n" +
                    $"if (table.FindByKey(key, out {structName} row)) {{ }}");
            }

            menu.ShowAsContext();
            Event.current.Use();
        }

        /// <summary>用系统关联的程序（一般是 Excel / WPS）打开工作簿</summary>
        public static void OpenInExcel(string file)
        {
            if (File.Exists(file)) EditorUtility.OpenWithDefaultApp(file);
        }
    }
}
