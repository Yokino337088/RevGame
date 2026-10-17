// ============================================================
// RevExcelPreview.cs —— 导表窗口右侧：数据预览表格
//
// 位置：Editor\RevExcelTool\Window\
//
// 【比 WPF 版的预览多了什么】
//   · 全部行都能看（TreeView 只画可见行，几万行也不卡），不再只截前 50 行；
//   · 表头两行：字段名 + 类型，★ 标主键；悬停看描述、C# 字段名、Excel 列字母；
//   · 第一列是 Excel 行号（和 Excel 左侧一致）；点表头排序（数值列按数值排）；
//   · 运行时解析不了的单元格直接标色：红 = int / float 写错（运行时会变成 0），
//     黄 = bool 认不出（运行时会变成 false）—— 不用翻错误列表就能看到在哪；
//   · 右键复制一行 / 复制单元格。
// ============================================================
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;

namespace Revolution.Editor.ExcelTool
{
    internal sealed class RevExcelPreview : TreeView
    {
        private sealed class RowItem : TreeViewItem
        {
            public ExcelRow row;
        }

        /// <summary>带第二行说明的列（第二行显示字段类型）</summary>
        private sealed class FieldColumn : MultiColumnHeaderState.Column
        {
            public string subtitle;
            public bool isKey;
        }

        /// <summary>两行表头：字段名（粗体）+ 类型（小字）</summary>
        private sealed class FieldHeader : MultiColumnHeader
        {
            private static GUIStyle _title, _sub;

            private static GUIStyle Title => _title ??= new GUIStyle(EditorStyles.boldLabel) { clipping = TextClipping.Clip };
            private static GUIStyle Sub => _sub ??= new GUIStyle(EditorStyles.miniLabel) { clipping = TextClipping.Clip };

            public FieldHeader(MultiColumnHeaderState state) : base(state) { height = 34f; }

            protected override void ColumnHeaderGUI(MultiColumnHeaderState.Column column, Rect headerRect, int columnIndex)
            {
                if (canSort && column.canSort) SortingButton(column, headerRect, columnIndex);

                var title = new Rect(headerRect.x + 4f, headerRect.y + 2f, headerRect.width - 16f, 16f);
                GUI.Label(title, column.headerContent, Title);

                if (column is FieldColumn field && !string.IsNullOrEmpty(field.subtitle))
                    GUI.Label(new Rect(title.x, headerRect.y + 17f, title.width, 14f), field.subtitle, Sub);
            }
        }

        private static readonly Color BadNumber = new Color(0.95f, 0.36f, 0.30f, 0.30f);
        private static readonly Color BadBool = new Color(0.95f, 0.75f, 0.20f, 0.30f);

        private static GUIStyle _cell, _rowNumber;

        private static GUIStyle Cell => _cell ??= new GUIStyle(EditorStyles.label) { clipping = TextClipping.Clip };

        private static GUIStyle RowNumber => _rowNumber ??= new GUIStyle(EditorStyles.miniLabel)
        {
            alignment = TextAnchor.MiddleRight,
            normal = { textColor = new Color(0.5f, 0.5f, 0.5f) }
        };

        private readonly ExcelTable _table;

        public ExcelTable Table => _table;

        public static RevExcelPreview Create(TreeViewState state, ExcelTable table)
        {
            state.selectedIDs.Clear();
            state.scrollPos = Vector2.zero;
            return new RevExcelPreview(state, new FieldHeader(BuildHeader(table)), table);
        }

        private RevExcelPreview(TreeViewState state, MultiColumnHeader header, ExcelTable table) : base(state, header)
        {
            _table = table;
            showBorder = true;
            showAlternatingRowBackgrounds = true;
            rowHeight = 20f;
            header.sortingChanged += _ => Reload();
            Reload();
        }

        private static MultiColumnHeaderState BuildHeader(ExcelTable table)
        {
            var columns = new List<MultiColumnHeaderState.Column>
            {
                new FieldColumn
                {
                    headerContent = new GUIContent("行", "Excel 里的行号"),
                    subtitle = "",
                    width = 42f, minWidth = 36f, maxWidth = 70f,
                    autoResize = false, canSort = true, allowToggleVisibility = false,
                }
            };

            foreach (ExcelField field in table.Fields)
            {
                string tip = $"{field.Name}（{field.ColumnName} 列）\n类型：{field.TypeText}\nC# 字段：{field.CSharpName()}" +
                             (field.Desc.Length > 0 ? "\n描述：" + field.Desc : "") +
                             (field.IsKey ? "\n★ 主键（第一个字段）" : "");

                columns.Add(new FieldColumn
                {
                    headerContent = new GUIContent((field.IsKey ? "★ " : "") + field.Name, tip),
                    subtitle = field.TypeText.ToLowerInvariant() + (field.Desc.Length > 0 ? " · " + field.Desc : ""),
                    isKey = field.IsKey,
                    width = Mathf.Clamp(40f + field.Name.Length * 8f, 80f, 200f),
                    minWidth = 40f,
                    autoResize = false, canSort = true, allowToggleVisibility = true,
                });
            }

            return new MultiColumnHeaderState(columns.ToArray());
        }

        // ============================================================
        // 数据
        // ============================================================

        protected override TreeViewItem BuildRoot()
        {
            var root = new TreeViewItem { id = 0, depth = -1, displayName = "root", children = new List<TreeViewItem>() };
            if (_table == null) return root;

            var rows = new List<ExcelRow>(_table.Rows);
            SortRows(rows);

            foreach (ExcelRow row in rows)
                root.AddChild(new RowItem { id = row.ExcelRowNumber, depth = 0, displayName = row.Get(0), row = row });

            return root;
        }

        private void SortRows(List<ExcelRow> rows)
        {
            int column = multiColumnHeader.sortedColumnIndex;
            if (column < 0) return;                                    // 默认：Excel 里的顺序

            bool ascending = multiColumnHeader.IsSortedAscending(column);
            int field = column - 1;
            bool numeric = field >= 0 && (_table.Fields[field].Type == FieldType.Int || _table.Fields[field].Type == FieldType.Float);

            rows.Sort((a, b) =>
            {
                int result;
                if (field < 0) result = a.ExcelRowNumber.CompareTo(b.ExcelRowNumber);
                else if (numeric) result = Number(a.Get(field)).CompareTo(Number(b.Get(field)));
                else result = string.CompareOrdinal(a.Get(field), b.Get(field));

                if (result == 0) result = a.ExcelRowNumber.CompareTo(b.ExcelRowNumber);
                return ascending ? result : -result;
            });
        }

        private static double Number(string cell)
            => double.TryParse(cell, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : double.MinValue;

        protected override bool DoesItemMatchSearch(TreeViewItem item, string search)
        {
            if (!(item is RowItem r)) return false;
            foreach (string cell in r.row.Cells)
                if (cell != null && cell.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        /// <summary>选中 Excel 第 N 行并滚动到它（错误列表里点"定位"用）；这一行没进数据（如主键重复被跳过）返回 false</summary>
        public bool RevealRow(int excelRow)
        {
            if (FindItem(excelRow, rootItem) == null) return false;
            searchString = string.Empty;
            SetSelection(new[] { excelRow }, TreeViewSelectionOptions.RevealAndFrame);
            SetFocus();
            return true;
        }

        // ============================================================
        // 绘制
        // ============================================================

        protected override void RowGUI(RowGUIArgs args)
        {
            if (!(args.item is RowItem item)) { base.RowGUI(args); return; }

            for (int i = 0; i < args.GetNumVisibleColumns(); i++)
            {
                Rect rect = args.GetCellRect(i);
                int column = args.GetColumn(i);

                if (column == 0)
                {
                    GUI.Label(rect, item.row.ExcelRowNumber.ToString(CultureInfo.InvariantCulture), RowNumber);
                    continue;
                }

                ExcelField field = _table.Fields[column - 1];
                string cell = item.row.Get(column - 1);

                string tip = null;
                if (!field.Accepts(cell))
                {
                    bool isBool = field.Type == FieldType.Bool;
                    EditorGUI.DrawRect(rect, isBool ? BadBool : BadNumber);
                    tip = isBool
                        ? $"\"{cell}\" 不是可识别的 bool，运行时按 false 处理"
                        : $"\"{cell}\" 不是合法的 {field.TypeText.ToLowerInvariant()}，运行时会取成 0";
                }
                else if (cell.Length > 12)
                {
                    tip = cell;                                         // 长文本：悬停看全文
                }

                CenterRectUsingSingleLineHeight(ref rect);
                GUI.Label(rect, new GUIContent(cell.Replace('\n', '⏎'), tip),
                    field.IsKey ? EditorStyles.boldLabel : Cell);
            }
        }

        // ============================================================
        // 交互
        // ============================================================

        protected override bool CanMultiSelect(TreeViewItem item) => true;

        protected override void ContextClickedItem(int id)
        {
            var rows = new List<ExcelRow>();
            foreach (int selected in GetSelection())
                if (FindItem(selected, rootItem) is RowItem r) rows.Add(r.row);
            if (rows.Count == 0) return;

            rows.Sort((a, b) => a.ExcelRowNumber.CompareTo(b.ExcelRowNumber));

            var menu = new GenericMenu();
            menu.AddItem(new GUIContent(rows.Count == 1 ? "复制本行（制表符分隔，可直接粘回 Excel）" : $"复制 {rows.Count} 行（制表符分隔）"),
                false, () => EditorGUIUtility.systemCopyBuffer = Join(rows));
            menu.AddItem(new GUIContent("复制主键"), false, () =>
            {
                var sb = new StringBuilder();
                foreach (ExcelRow r in rows) sb.Append(r.Get(0)).Append('\n');
                EditorGUIUtility.systemCopyBuffer = sb.ToString().TrimEnd('\n');
            });
            menu.ShowAsContext();
            Event.current.Use();
        }

        private static string Join(List<ExcelRow> rows)
        {
            var sb = new StringBuilder();
            foreach (ExcelRow row in rows) sb.Append(string.Join("\t", row.Cells)).Append('\n');
            return sb.ToString().TrimEnd('\n');
        }
    }
}
