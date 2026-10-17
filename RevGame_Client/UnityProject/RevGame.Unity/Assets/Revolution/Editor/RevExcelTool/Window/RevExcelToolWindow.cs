// ============================================================
// RevExcelToolWindow.cs —— 导表工具（Unity 编辑器版）主窗口
//
// 位置：Editor\RevExcelTool\Window\
// 打开：菜单 Revolution.Tools/配置表/导表工具（或"快速导出"一步到位，见 RevExcelCommands）
//
// 【布局：一眼看完，一键导出】
//   ┌ 工具栏：来源 ▾ · 添加文件 · 添加文件夹 · 重新读取 ··························· 自动重读 · 规则 ┐
//   ├ 概览：5 张表 · 4 张可导出 · 1 张有错误      状态：有 3 个文件待更新          [ 导出 4 张表 ][▾] ┤
//   ├ 输出：代码 → Assets/Revolution/Generation · 数据 → Assets/GameRes/Data · AB 包 → data   [设置]  ┤
//   ├──────────────┬─────────────────────────────────────────────────────┤
//   │ 表列表         │ Hero → Hero / HeroTable            [在 Excel 中打开][定位数据]      │
//   │ （按工作簿分组） │ 问题列表（点「定位」跳到那一行）                                      │
//   │ ✖ / ⚠ / 新 / ● │ [数据预览][字段]                                                       │
//   ├──────────────┴─────────────────────────────────────────────────────┤
//   │ [导出结果][日志]   导出了什么、哪些没变、AB 标记 / 资源映射 / 孤儿文件 —— 需要处理的都带按钮     │
//   └────────────────────────────────────────────────────────────────────┘
//
// 【和 WPF 版相比，省掉了哪些步骤】
//   · 不用先"读取"再"导出"：选好来源自动读；Excel 保存后自动重读（窗口开着时）；
//   · 不用关 Excel：开着也能读；
//   · 不用选三个输出目录：代码默认进 Generation 程序集、数据默认跟随资源根目录；选错了当场提示；
//   · 不用把产物拷进工程、也不用再去打包工具点"生成映射"：直接写进工程、新增表自动生成映射；
//   · 看得到"要不要导"：每张表都标着有没有改动，没改动的文件不写（不触发脚本编译）。
//
// 【快捷键】F5 重新读取 · Ctrl/Cmd + Enter 导出 · 把 .xlsx / 文件夹拖进窗口 = 添加来源
// ============================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;

namespace Revolution.Editor.ExcelTool
{
    public sealed class RevExcelToolWindow : EditorWindow
    {
        private const string WindowTitle = "导表工具";
        private const double PollInterval = 1.5;
        private const int MaxLog = 400;

        [Serializable]
        private struct LogEntry
        {
            public int level;          // 0 信息 · 1 警告 · 2 错误 · 3 成功
            public string time;
            public string text;
        }

        // ---------------- 需要跨重编译保留的（导出代码后 Unity 会重编译、窗口对象会重建） ----------------
        [SerializeField] private TreeViewState _listState = new TreeViewState();
        [SerializeField] private TreeViewState _previewState = new TreeViewState();
        [SerializeField] private float _split = 0.3f;
        [SerializeField] private float _bottomRatio = 0.7f;
        [SerializeField] private bool _bottomOpen = true;
        [SerializeField] private int _bottomTab;
        [SerializeField] private int _detailTab;
        [SerializeField] private bool _showSettings;
        [SerializeField] private bool _showAdvanced;
        [SerializeField] private bool _showAllIssues;
        [SerializeField] private string _selectedKey;
        [SerializeField] private RevExcelReport _report;
        [SerializeField] private List<LogEntry> _log = new List<LogEntry>();

        // ---------------- 运行时 ----------------
        [NonSerialized] private RevExcelReadResult _read;
        [NonSerialized] private ExcelExportPlan _plan;
        [NonSerialized] private RevExcelTableList _list;
        [NonSerialized] private RevExcelPreview _preview;
        [NonSerialized] private SearchField _listSearch;
        [NonSerialized] private SearchField _previewSearch;
        [NonSerialized] private ExcelTable _selected;

        [NonSerialized] private int _validCount, _errorCount, _ignoredCount, _rowCount;
        [NonSerialized] private RevExcelCheck _codeCheck, _containerCheck, _dataCheck;
        [NonSerialized] private string _bundle;
        [NonSerialized] private bool _autoByFolder;

        [NonSerialized] private bool _stale;
        [NonSerialized] private double _nextPoll;
        [NonSerialized] private float _topHeight = 90f;
        [NonSerialized] private bool _draggingSplit, _draggingBottom;
        [NonSerialized] private bool _dropHover;
        [NonSerialized] private bool _scrollLogToEnd;
        [NonSerialized] private string[] _dragProbePaths;
        [NonSerialized] private bool _dragProbeResult;
        [NonSerialized] private Vector2 _issueScroll, _fieldScroll, _bottomScroll, _emptyScroll;

        // ============================================================
        // 打开
        // ============================================================

        [MenuItem("Revolution.Tools/配置表/导表工具", false, 20)]
        private static void Menu() => Open();

        public static RevExcelToolWindow Open()
        {
            var window = GetWindow<RevExcelToolWindow>();
            window.titleContent = new GUIContent(WindowTitle, ABGUI.Icon("TextAsset Icon"));
            window.minSize = new Vector2(760, 480);
            window.Show();
            return window;
        }

        /// <summary>把一次导出结果显示出来（快速导出遇到要处理的事时用）</summary>
        internal void ShowReport(RevExcelReport report)
        {
            _report = report;
            _bottomOpen = true;
            _bottomTab = 0;
            LogReport(report);
            Repaint();
        }

        /// <summary>导出会删掉上次导出过的表的代码时，先问一句（窗口和快速导出共用）</summary>
        internal static bool ConfirmRemoval(ExcelExportPlan plan)
        {
            string list = string.Join("、", plan.RemovedTables.Take(12)) + (plan.RemovedTables.Count > 12 ? " …" : "");
            return EditorUtility.DisplayDialog("导出会移除表",
                $"上次导出过的 {plan.RemovedTables.Count} 张表不在这次的 Excel 源里（或这次有错被跳过）：\n\n{list}\n\n" +
                "继续导出，它们的类会从生成代码里删掉 —— 业务代码还在引用的话会编译不过。\n" +
                "如果只是漏选了某个工作簿，先把它加进来源。",
                "仍然导出", "取消");
        }

        private void OnEnable()
        {
            titleContent = new GUIContent(WindowTitle, ABGUI.Icon("TextAsset Icon"));
            _listState ??= new TreeViewState();
            _previewState ??= new TreeViewState();
            _log ??= new List<LogEntry>();

            // Unity 反序列化时会把 null 的可序列化类字段填成一个空对象：没有时间戳 = 其实没导出过
            if (_report != null && string.IsNullOrEmpty(_report.time)) _report = null;

            // 延后一帧：窗口先画出来，再做 IO（导出代码 → 重编译后也走这里，自动恢复现场）
            EditorApplication.delayCall += () =>
            {
                if (this == null) return;
                if (_read == null && RevExcelUserSettings.instance.sources.Count > 0) Reload(null);
                else RefreshChecks();
                Repaint();
            };
        }

        private void OnFocus()
        {
            _nextPoll = 0;                       // 切回窗口时立刻看一眼 Excel 有没有被改过
            if (_read != null) Replan();
        }

        private void Update()
        {
            double now = EditorApplication.timeSinceStartup;
            if (now < _nextPoll) return;
            _nextPoll = now + PollInterval;

            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            Poll();
        }

        /// <summary>Excel 保存过 / 来源文件夹里增删了文件 → 自动重读（或提示）</summary>
        private void Poll()
        {
            RevExcelUserSettings user = RevExcelUserSettings.instance;
            if (_read == null || user.sources.Count == 0) return;

            string signature = RevExcelService.Signature(RevExcelService.CollectFiles(user.sources, user.includeSubfolders));
            if (signature == _read.Signature)
            {
                if (_stale) { _stale = false; Repaint(); }
                return;
            }

            if (user.autoReload) Reload("检测到 Excel 有变化，已自动重新读取");
            else if (!_stale) { _stale = true; Repaint(); }
        }

        // ============================================================
        // 读取 / 计划
        // ============================================================

        private void EnsureControls()
        {
            _list ??= new RevExcelTableList(_listState, OnTableSelected);
            _listSearch ??= new SearchField();
            _previewSearch ??= new SearchField();
        }

        private void Reload(string reason)
        {
            EnsureControls();
            RevExcelUserSettings user = RevExcelUserSettings.instance;

            _read = user.sources.Count == 0 ? null : RevExcelService.Read(user.sources, user.includeSubfolders, true);
            _stale = false;

            _validCount = _errorCount = _ignoredCount = _rowCount = 0;
            if (_read != null)
            {
                foreach (ExcelTable t in _read.Tables)
                {
                    if (t.Ignored) _ignoredCount++;
                    else if (t.IsValid) { _validCount++; _rowCount += t.Rows.Count; }
                    else _errorCount++;
                }
            }

            _plan = null;
            Replan();
            _list.SetData(_read, _plan);

            _selected = null;
            _preview = null;
            if (!_list.Select(_selectedKey)) _list.SelectFirst();

            if (_read != null)
            {
                if (reason != null) Log(0, reason);

                if (_read.Files.Count == 0)
                    Log(2, "来源里没有找到任何 .xlsx：" + string.Join("；", user.sources.Select(RevExcelService.Display)));
                else
                    Log(_errorCount > 0 || _read.FileErrors.Count > 0 ? 1 : 0,
                        $"读取 {_read.Files.Count} 个文件：{_validCount} 张表可导出" +
                        (_errorCount > 0 ? $"，{_errorCount} 张有错误（导出时跳过）" : "") +
                        (_ignoredCount > 0 ? $"，{_ignoredCount} 张忽略" : ""));

                foreach (KeyValuePair<string, string> error in _read.FileErrors)
                    Log(2, $"读取失败 {Path.GetFileName(error.Key)}：{error.Value}");
            }

            Repaint();
        }

        private void Replan()
        {
            _plan = _read != null && _read.Tables.Count > 0 ? RevExcelService.Plan(_read.Tables) : null;
            _list?.SetPlan(_plan);
            RefreshChecks();
        }

        private void RefreshChecks()
        {
            RevExcelProjectSettings s = RevExcelProjectSettings.instance;
            _codeCheck = RevExcelService.CheckCodeDir(s.structDir);
            _containerCheck = RevExcelService.CheckCodeDir(s.containerDir);
            _dataCheck = RevExcelService.CheckDataDir();
            _bundle = RevExcelService.DataDirBundle(out _autoByFolder);
        }

        private void OnTableSelected(ExcelTable table)
        {
            _selected = table;
            if (table != null) _selectedKey = RevExcelTableList.KeyOf(table);
            _preview = table != null && table.Fields.Count > 0 ? RevExcelPreview.Create(_previewState, table) : null;
            _issueScroll = Vector2.zero;
            Repaint();
        }

        // ============================================================
        // 来源
        // ============================================================

        private void AddSources(IEnumerable<string> paths)
        {
            RevExcelUserSettings user = RevExcelUserSettings.instance;
            var existing = new HashSet<string>(user.sources.Select(RevExcelService.Full), StringComparer.OrdinalIgnoreCase);

            int added = 0;
            foreach (string raw in paths)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                string full = RevExcelService.Full(raw);
                if (!existing.Add(full)) continue;

                user.sources.Add(RevExcelService.ToProjectPath(full));
                added++;
            }

            if (added == 0) return;
            user.SaveNow();
            Reload($"添加了 {added} 个来源");
        }

        private void RemoveSource(int index)
        {
            RevExcelUserSettings user = RevExcelUserSettings.instance;
            if (index < 0 || index >= user.sources.Count) return;

            string removed = user.sources[index];
            user.sources.RemoveAt(index);
            user.SaveNow();
            Reload("移除来源 " + RevExcelService.Display(removed));
        }

        private void PickFiles()
        {
            string path = EditorUtility.OpenFilePanelWithFilters("选择 Excel 工作簿", StartDir(), new[] { "Excel 工作簿", "xlsx" });
            if (!string.IsNullOrEmpty(path)) AddSources(new[] { path });
        }

        private void PickFolder()
        {
            string path = EditorUtility.OpenFolderPanel("选择装着 .xlsx 的文件夹", StartDir(), "");
            if (!string.IsNullOrEmpty(path)) AddSources(new[] { path });
        }

        /// <summary>对话框从哪开始：上一个来源所在的目录</summary>
        private static string StartDir()
        {
            List<string> sources = RevExcelUserSettings.instance.sources;
            if (sources.Count == 0) return Directory.GetCurrentDirectory();

            string last = RevExcelService.Full(sources[sources.Count - 1]);
            if (Directory.Exists(last)) return last;
            string dir = Path.GetDirectoryName(last);
            return !string.IsNullOrEmpty(dir) && Directory.Exists(dir) ? dir : Directory.GetCurrentDirectory();
        }

        /// <summary>仓库里自带的样例表（Revolution.Demo/ExcelTool/Excel）；不存在返回 null</summary>
        private static string DemoFolder()
        {
            string path = RevExcelService.Full("../Revolution.Demo/ExcelTool/Excel");
            return Directory.Exists(path) ? path : null;
        }

        // ============================================================
        // 导出
        // ============================================================

        private void DoExport(bool force, ExcelExportMode mode = ExcelExportMode.Full)
        {
            if (_read == null) return;

            // 导出的永远是"最新保存的 Excel"：指纹变了就先重读
            RevExcelUserSettings user = RevExcelUserSettings.instance;
            if (RevExcelService.Signature(RevExcelService.CollectFiles(user.sources, user.includeSubfolders)) != _read.Signature)
                Reload("导出前发现 Excel 有变化，已重新读取");

            RevExcelReport report = RevExcelService.Export(_read.Tables, force, ConfirmRemoval, mode);
            if (report.cancelled)
            {
                Log(0, "已取消导出");
                return;
            }

            ShowReport(report);
            Replan();

            ShowNotification(new GUIContent(!report.success ? "导出失败，详见下方结果"
                : report.written.Count == 0 ? "已是最新，没有文件需要更新"
                : $"导出完成：更新 {report.written.Count} 个文件"));
        }

        private void LogReport(RevExcelReport report)
        {
            if (report == null) return;

            if (!report.success)
            {
                Log(2, "导出失败：" + (report.errors.Count > 0 ? report.errors[0] : "未知原因") +
                       (report.errors.Count > 1 ? $"（共 {report.errors.Count} 个错误）" : ""));
                return;
            }

            Log(3, $"导出完成{(report.dataOnly ? "（仅数据，未生成代码）" : "")}：" +
                   $"{report.tableCount} 张表 / {report.rowCount} 条数据，写入 {report.written.Count} 个文件，" +
                   $"{report.unchangedCount} 个未变" + (report.codeChanged ? "（代码有变化，Unity 会重新编译）" : ""));

            foreach (string w in report.warnings) Log(1, w);
            if (report.unmarked.Count > 0) Log(1, $"{report.unmarked.Count} 个数据文件没有 AB 标记：AB 模式 / 真机读不到");
            if (report.mapStatus.Length > 0) Log(report.mapFailed ? 1 : 0, report.mapStatus);
            if (report.orphans.Count > 0) Log(1, $"数据目录里有 {report.orphans.Count} 个旧数据文件已经没有对应的表");
        }

        private void MarkDataDir()
        {
            ABGUI.Defer(() =>
            {
                string dir = RevExcelService.DataDir();
                string name = ABNamePrompt.Show("给数据目录标 AB 包",
                    $"把 {dir} 整个文件夹标进哪个包？（里面现在和以后导出的表都会跟着进这个包）", "data");
                if (name == null) return;

                name = ABBundleEditor.Normalize(name);
                if (!ABBundleEditor.IsLegalBundleName(name, out string why))
                {
                    EditorUtility.DisplayDialog("包名不合法", $"「{name}」：{why}", "好");
                    return;
                }

                if (!RevExcelService.MarkDataDir(name)) return;
                Log(3, $"数据目录 {dir} 已标进包「{name}」");

                if (_report != null)
                {
                    _report.unmarked.Clear();
                    RevExcelService.GenerateMap(_report, "标记数据目录后生成映射");
                    Log(_report.mapFailed ? 1 : 0, _report.mapStatus);
                }

                RefreshChecks();
                Repaint();
            });
        }

        private void DeleteOrphans()
        {
            List<string> files = _report?.orphans.ToList() ?? new List<string>();
            if (files.Count == 0) return;

            ABGUI.Defer(() =>
            {
                string list = string.Join("\n", files.Take(10).Select(RevExcelService.Display)) + (files.Count > 10 ? "\n…" : "");
                if (!EditorUtility.DisplayDialog("删除旧数据文件",
                        $"这 {files.Count} 个数据文件已经没有对应的表（表从 Excel 里删掉 / 改名了）：\n\n{list}\n\n删除它们（连同 .meta）？",
                        "删除", "取消"))
                    return;

                int deleted = RevExcelService.DeleteOrphans(files);
                Log(3, $"已删除 {deleted} 个旧数据文件");

                _report.orphans.Clear();
                if (RevExcelProjectSettings.instance.generateMapAfterExport)
                {
                    RevExcelService.GenerateMap(_report, "删除旧数据后生成映射");
                    Log(_report.mapFailed ? 1 : 0, _report.mapStatus);
                }

                Replan();
                Repaint();
            });
        }

        // ============================================================
        // 主绘制
        // ============================================================

        private void OnGUI()
        {
            EnsureControls();
            HandleKeys();
            HandleWindowDrop();

            bool hasSources = RevExcelUserSettings.instance.sources.Count > 0;

            // ---------- 顶部（GUILayout）：高度记下来，下面的区域按它排 ----------
            EditorGUILayout.BeginVertical();
            DrawToolbar();
            if (hasSources)
            {
                DrawHeader();
                DrawOutputLine();
                if (_showSettings) DrawSettings();
            }
            EditorGUILayout.EndVertical();

            if (Event.current.type == EventType.Repaint)
            {
                float top = GUILayoutUtility.GetLastRect().yMax + 2f;
                if (Mathf.Abs(top - _topHeight) > 0.5f)
                {
                    _topHeight = top;
                    Repaint();                              // 高度变了（展开设置 / 出现提示）→ 下一帧按新高度排
                }
            }

            var rest = new Rect(0f, _topHeight, position.width, Mathf.Max(0f, position.height - _topHeight));

            if (!hasSources) DrawEmpty(rest);
            else DrawBody(rest);

            DrawDropOverlay();
        }

        private void HandleKeys()
        {
            Event e = Event.current;
            if (e.type != EventType.KeyDown || EditorGUIUtility.editingTextField) return;

            if (e.keyCode == KeyCode.F5)
            {
                Reload("手动重新读取");
                e.Use();
            }
            else if ((e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter) && (e.control || e.command))
            {
                if (_plan != null && _plan.CanExport) ABGUI.Defer(() => DoExport(false));
                e.Use();
            }
        }

        // ---------------- 工具栏 ----------------

        private void DrawToolbar()
        {
            RevExcelUserSettings user = RevExcelUserSettings.instance;

            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                string label = user.sources.Count == 0 ? "来源：未选择"
                    : user.sources.Count == 1 ? "来源：" + Path.GetFileName(RevExcelService.Full(user.sources[0]).TrimEnd('/'))
                    : $"来源：{user.sources.Count} 个";

                var content = new GUIContent(label, ABGUI.Icon("TextAsset Icon"),
                    user.sources.Count == 0 ? "还没选 Excel" : string.Join("\n", user.sources.Select(RevExcelService.Display)));

                Rect sourceRect = GUILayoutUtility.GetRect(content, EditorStyles.toolbarDropDown, GUILayout.MaxWidth(260));
                if (EditorGUI.DropdownButton(sourceRect, content, FocusType.Passive, EditorStyles.toolbarDropDown))
                    ShowSourceMenu(sourceRect);

                if (GUILayout.Button(new GUIContent(" 添加文件", ABGUI.PlusIcon, "添加一个 .xlsx 工作簿"), EditorStyles.toolbarButton))
                    ABGUI.Defer(PickFiles);
                if (GUILayout.Button(new GUIContent(" 添加文件夹", ABGUI.FolderIcon, "添加一个装着 .xlsx 的文件夹（里面的工作簿都会读）"), EditorStyles.toolbarButton))
                    ABGUI.Defer(PickFolder);

                using (new EditorGUI.DisabledScope(user.sources.Count == 0))
                {
                    if (GUILayout.Button(new GUIContent(" 重新读取", ABGUI.RefreshIcon, "重新读一遍所有 Excel（F5）"), EditorStyles.toolbarButton))
                        Reload("手动重新读取");
                }

                GUILayout.FlexibleSpace();

                bool auto = GUILayout.Toggle(user.autoReload,
                    new GUIContent("自动重读", "Excel 保存后自动重新读取（窗口开着时每 1.5 秒看一次文件修改时间）"),
                    EditorStyles.toolbarButton);
                if (auto != user.autoReload)
                {
                    user.autoReload = auto;
                    user.SaveNow();
                    if (auto) _nextPoll = 0;
                }

                if (GUILayout.Button(new GUIContent("规则", "Excel 该怎么写"), EditorStyles.toolbarButton))
                    ABGUI.Defer(ShowRules);
            }
        }

        private void ShowSourceMenu(Rect rect)
        {
            RevExcelUserSettings user = RevExcelUserSettings.instance;
            var menu = new GenericMenu();

            for (int i = 0; i < user.sources.Count; i++)
            {
                int index = i;
                string full = RevExcelService.Full(user.sources[i]);
                // 菜单里 "/" 是子菜单分隔符：只显示名字，完整路径放在"在资源管理器中显示"里
                string name = $"{i + 1}. {Path.GetFileName(full.TrimEnd('/'))}{(Directory.Exists(full) ? "（文件夹）" : "")}";

                menu.AddItem(new GUIContent(name + "/在资源管理器中显示"), false, () => EditorUtility.RevealInFinder(full));
                if (File.Exists(full))
                    menu.AddItem(new GUIContent(name + "/在 Excel 中打开"), false, () => RevExcelTableList.OpenInExcel(full));
                menu.AddItem(new GUIContent(name + "/移除"), false, () => RemoveSource(index));
            }

            if (user.sources.Count > 0) menu.AddSeparator("");
            menu.AddItem(new GUIContent("添加 Excel 文件…"), false, () => ABGUI.Defer(PickFiles));
            menu.AddItem(new GUIContent("添加文件夹…"), false, () => ABGUI.Defer(PickFolder));
            menu.AddSeparator("");
            menu.AddItem(new GUIContent("文件夹：包含子文件夹"), user.includeSubfolders, () =>
            {
                user.includeSubfolders = !user.includeSubfolders;
                user.SaveNow();
                Reload(user.includeSubfolders ? "改为连子文件夹一起读" : "改为只读文件夹顶层");
            });

            if (user.sources.Count > 0)
            {
                menu.AddSeparator("");
                menu.AddItem(new GUIContent("清空全部来源"), false, () =>
                {
                    user.sources.Clear();
                    user.SaveNow();
                    Reload("已清空来源");
                });
            }

            menu.DropDown(rect);
        }

        private static void ShowRules()
        {
            EditorUtility.DisplayDialog("Excel 该怎么写",
                "一个工作表 = 一张表（工作表名 = 表名 = 类名）\n\n" +
                "第 1 行：字段名（要能当 C# 字段名）\n" +
                "第 2 行：类型 —— int / float / string / bool\n" +
                "第 3 行：描述（只进代码注释）\n" +
                "第 4 行起：数据，一行一条\n\n" +
                "· 第一个字段是主键：每行必填、不能重复\n" +
                "· 字段名 / 行首 / 工作表名以 # 开头 = 备注，忽略\n" +
                "· 第 1 行留空的列忽略；整行空的行跳过\n" +
                "· 空单元格 = 默认值（0 / false / 空串）\n\n" +
                "样例：Revolution.Demo/ExcelTool/Excel/DemoConfig.xlsx",
                "知道了");
        }

        // ---------------- 概览 + 导出按钮 ----------------

        private static GUIStyle _summaryStyle, _statusStyle;

        private static GUIStyle SummaryStyle => _summaryStyle ??= new GUIStyle(EditorStyles.boldLabel) { fontSize = 13 };
        private static GUIStyle StatusStyle => _statusStyle ??= new GUIStyle(EditorStyles.label) { richText = true, wordWrap = false };

        private void DrawHeader()
        {
            if (_stale)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.HelpBox("Excel 已经改过了（自动重读已关闭），下面显示的是旧数据。", MessageType.Warning);
                    if (GUILayout.Button("重新读取", GUILayout.Width(80), GUILayout.Height(38))) Reload("手动重新读取");
                }
            }

            using (new EditorGUILayout.HorizontalScope(GUILayout.Height(44)))
            {
                GUILayout.Space(6);
                using (new EditorGUILayout.VerticalScope())
                {
                    GUILayout.Space(3);
                    GUILayout.Label(SummaryText(), SummaryStyle);
                    GUILayout.Label(StatusText(), StatusStyle);
                }

                GUILayout.FlexibleSpace();

                using (new EditorGUILayout.VerticalScope(GUILayout.Width(310)))
                {
                    GUILayout.Space(5);
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        bool can = _plan != null && _plan.CanExport;
                        using (new EditorGUI.DisabledScope(!can))
                        {
                            // 主按钮：全量导出（代码 + 数据，只写内容真的变了的文件）
                            if (ABGUI.PrimaryButton(ExportButtonContent(), 32f, GUILayout.Width(150)))
                                ABGUI.Defer(() => DoExport(false));

                            // ★ 仅数据：只改了 Excel 数值、没动表结构时用 ——
                            //   一个代码文件都不碰，导完不触发全量脚本编译（那要十几秒起步）
                            if (GUILayout.Button(new GUIContent("仅数据", "只重新生成数据 txt，不生成代码。\n只改了数值、没动表结构时用它 —— 导完不会触发脚本编译"),
                                    GUILayout.Width(64), GUILayout.Height(32)))
                                ABGUI.Defer(() => DoExport(false, ExcelExportMode.DataOnly));
                        }

                        Rect more = GUILayoutUtility.GetRect(24, 32, GUILayout.Width(24), GUILayout.Height(32));
                        if (GUI.Button(more, new GUIContent("▾", "更多导出选项")))
                            ShowExportMenu(more);
                    }
                }
                GUILayout.Space(6);
            }
        }

        private string SummaryText()
        {
            if (_read == null) return "正在读取…";
            if (_read.Files.Count == 0) return "来源里没有 .xlsx";

            string text = $"{_read.Files.Count} 个工作簿 · {_validCount} 张表可导出";
            if (_errorCount > 0) text += $" · {_errorCount} 张有错误";
            if (_ignoredCount > 0) text += $" · {_ignoredCount} 张忽略";
            return text + $" · 共 {_rowCount} 条数据";
        }

        private string StatusText()
        {
            string Tint(string color, string text) => $"<color={color}>{text}</color>";
            string green = EditorGUIUtility.isProSkin ? "#7fd18b" : "#2e7d32";
            string red = EditorGUIUtility.isProSkin ? "#ff7b72" : "#c62828";
            string amber = EditorGUIUtility.isProSkin ? "#e8c26a" : "#a86b00";
            string blue = EditorGUIUtility.isProSkin ? "#79b8ff" : "#1f5fbf";

            if (_read == null) return "";
            if (_read.FileErrors.Count > 0 && _read.Tables.Count == 0) return Tint(red, "✖ 工作簿读取失败（详见日志）");
            if (_plan == null) return _read.Files.Count == 0 ? Tint(red, "✖ 检查来源路径是否还在") : "";

            if (!_plan.CanExport) return Tint(red, "✖ 无法导出：" + (_plan.Errors.Count > 0 ? _plan.Errors[0] : ""));

            string outputs = OutputProblem();
            if (outputs != null) return Tint(red, "✖ " + outputs);

            int changed = _plan.ChangedCount;
            string tail = _errorCount > 0 ? Tint(amber, $"   ·  {_errorCount} 张有错误的表会被跳过") : "";
            if (_plan.RemovedTables.Count > 0) tail += Tint(amber, $"   ·  会移除 {_plan.RemovedTables.Count} 张表");

            if (changed == 0) return Tint(green, "✔ 与已导出的完全一致，不需要导出") + tail;

            int dataChanged = _plan.Files.Count(f => f.Kind == ExcelFileKind.Data && f.Change != ExcelFileChange.Unchanged);
            bool codeChanged = _plan.Files.Any(f => f.Kind != ExcelFileKind.Data && f.Change != ExcelFileChange.Unchanged);

            return Tint(blue, $"● 有 {changed} 个文件待更新：{dataChanged} 张表的数据" + (codeChanged ? " + 代码（会触发一次脚本编译）" : "")) + tail;
        }

        /// <summary>输出位置里会阻止导出的问题（没有返回 null）</summary>
        private string OutputProblem()
        {
            if (_codeCheck.IsError) return "代码目录：" + _codeCheck.Message;
            if (_containerCheck.IsError) return "容器类目录：" + _containerCheck.Message;
            if (_dataCheck.IsError) return "数据目录：" + _dataCheck.Message;
            return null;
        }

        private GUIContent ExportButtonContent()
        {
            if (_plan == null || !_plan.CanExport) return new GUIContent("导出", "没有可导出的表");

            string tip = "生成代码 + 数据，直接写进工程（Ctrl/Cmd + Enter）";
            if (_plan.Skipped.Count > 0) tip += $"\n有错误的 {_plan.Skipped.Count} 张表会被跳过";

            return _plan.ChangedCount == 0
                ? new GUIContent("导出（已是最新）", tip + "\n所有文件都没变化：点了也不会写任何文件")
                : new GUIContent($"导出 {_plan.Tables.Count} 张表", tip);
        }

        private void ShowExportMenu(Rect rect)
        {
            var menu = new GenericMenu();
            bool can = _plan != null && _plan.CanExport;

            // ★ 全量生成 = 代码（数据结构类 + 容器类）+ 数据文件全部重写，即使内容没变
            if (can) menu.AddItem(new GUIContent("全量生成代码和数据（强制重写全部文件）"), false, () => ABGUI.Defer(() => DoExport(true)));
            else menu.AddDisabledItem(new GUIContent("全量生成代码和数据（强制重写全部文件）"));

            // ★ 仅数据（强制版）：连"内容没变"的数据 txt 也全部重写，代码仍然一个不碰
            if (can) menu.AddItem(new GUIContent("仅生成数据文件（强制重写全部 txt，不碰代码）"), false, () => ABGUI.Defer(() => DoExport(true, ExcelExportMode.DataOnly)));
            else menu.AddDisabledItem(new GUIContent("仅生成数据文件（强制重写全部 txt，不碰代码）"));

            menu.AddItem(new GUIContent("只生成资源映射（ResMap）"), false, () => ABGUI.Defer(() =>
            {
                var report = _report ?? new RevExcelReport { success = true, time = DateTime.Now.ToString("HH:mm:ss") };
                RevExcelService.GenerateMap(report, "导表工具手动生成映射");
                ShowReport(report);
            }));

            menu.AddSeparator("");

            string dataDir = RevExcelService.DataDir();
            if (AssetDatabase.IsValidFolder(dataDir)) menu.AddItem(new GUIContent("定位数据目录"), false, () => ABGUI.Ping(dataDir));
            else menu.AddDisabledItem(new GUIContent("定位数据目录（还没导出过）"));

            ExcelExportOptions options = RevExcelService.Options();
            string containers = options.ContainerDir + "/" + options.ContainerFileName;
            string structs = options.StructDir + "/" + options.StructFileName;
            if (File.Exists(containers)) menu.AddItem(new GUIContent("打开容器类 " + options.ContainerFileName), false,
                () => AssetDatabase.OpenAsset(AssetDatabase.LoadMainAssetAtPath(containers)));
            if (File.Exists(structs)) menu.AddItem(new GUIContent("打开数据结构类 " + options.StructFileName), false,
                () => AssetDatabase.OpenAsset(AssetDatabase.LoadMainAssetAtPath(structs)));

            menu.AddSeparator("");
            menu.AddItem(new GUIContent("打开 RevAB 打包工具"), false, ABBuildWindow.Open);

            menu.DropDown(rect);
        }

        // ---------------- 输出位置 ----------------

        private void DrawOutputLine()
        {
            RevExcelProjectSettings s = RevExcelProjectSettings.instance;
            bool follow = string.IsNullOrEmpty(RevExcelService.Clean(s.dataDir));
            string dataDir = RevExcelService.DataDir();

            string bundle = _autoByFolder ? "按目录自动分包" : string.IsNullOrEmpty(_bundle) ? "未标记" : _bundle;
            bool problem = OutputProblem() != null || _dataCheck.Type == MessageType.Warning
                           || (!_autoByFolder && string.IsNullOrEmpty(_bundle));

            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                var toggle = new GUIContent((_showSettings ? "▾ " : "▸ ") + "输出", problem ? ABGUI.WarnIcon : null,
                    "代码 / 数据写到哪（团队共享的设置，存在 ProjectSettings 里）");
                _showSettings = GUILayout.Toggle(_showSettings, toggle, EditorStyles.toolbarButton, GUILayout.Width(problem ? 70 : 54));

                string code = s.structDir == s.containerDir ? s.structDir : $"{s.structDir} | {s.containerDir}";
                GUILayout.Label(new GUIContent(
                        $"代码 → {code}      数据 → {(dataDir.Length > 0 ? dataDir : "（未设置）")}{(follow ? "（跟随资源根目录）" : "")}      AB 包 → {bundle}",
                        "点左边「输出」展开设置"),
                    EditorStyles.miniLabel);
                GUILayout.FlexibleSpace();
            }
        }

        private void DrawSettings()
        {
            RevExcelProjectSettings s = RevExcelProjectSettings.instance;
            RevExcelUserSettings user = RevExcelUserSettings.instance;

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUI.BeginChangeCheck();

                // ---------- 代码 ----------
                bool together = RevExcelService.Clean(s.structDir) == RevExcelService.Clean(s.containerDir);
                string code = PathField(new GUIContent(together ? "代码目录" : "数据结构类目录",
                        "生成的 C# 放哪（推荐框架自带的 Generation 程序集：已引用 Revolution.Runtime，业务直接能用）"),
                    s.structDir, "选择代码输出目录");
                if (code != s.structDir)
                {
                    s.structDir = code;
                    if (together) s.containerDir = code;
                }
                DrawCheck(_codeCheck);

                // ---------- 数据 ----------
                bool follow = string.IsNullOrEmpty(RevExcelService.Clean(s.dataDir));
                bool nowFollow = EditorGUILayout.ToggleLeft(new GUIContent("数据目录跟随资源根目录（推荐）",
                    "运行时按逻辑路径 Data/<表名> 从资源根目录读：数据放在 <资源根目录>/Data 才读得到"), follow);

                if (nowFollow != follow)
                {
                    string expected = RevExcelService.ExpectedDataDir();
                    s.dataDir = nowFollow ? "" : (expected.Length > 0 ? expected : "Assets/Data");
                }

                if (nowFollow)
                {
                    string dir = RevExcelService.DataDir();
                    EditorGUILayout.LabelField("数据目录",
                        dir.Length == 0 ? "（资源根目录还没设置）"
                        : dir + (AssetDatabase.IsValidFolder(dir) ? "" : "（还不存在，导出时自动创建）"));
                }
                else
                {
                    s.dataDir = PathField(new GUIContent("数据目录", "TXT 数据文件放哪（它是资源，走资源系统加载 / 打包）"),
                        s.dataDir, "选择数据输出目录");
                }

                DrawCheck(_dataCheck, RevExcelService.ResRoot().Length == 0);
                DrawBundleRow();

                s.generateMapAfterExport = EditorGUILayout.ToggleLeft(new GUIContent("新增 / 删除表后自动生成资源映射",
                    "不生成的话 AB 模式 / 真机读不到新表（编辑器直读不受影响）。等同于 RevAB 打包工具里的「仅生成映射」"),
                    s.generateMapAfterExport);

                // ---------- 高级 ----------
                _showAdvanced = EditorGUILayout.Foldout(_showAdvanced, "高级", true);
                if (_showAdvanced)
                {
                    EditorGUI.indentLevel++;

                    string container = PathField(new GUIContent("容器类目录", "和数据结构类放不同目录时才需要改"),
                        s.containerDir, "选择容器类输出目录");
                    if (container != s.containerDir) s.containerDir = container;
                    if (!together) DrawCheck(_containerCheck);

                    s.structFileName = EditorGUILayout.DelayedTextField("数据结构类文件名", s.structFileName);
                    s.containerFileName = EditorGUILayout.DelayedTextField("容器类文件名", s.containerFileName);

                    bool sub = EditorGUILayout.Toggle(new GUIContent("来源文件夹含子文件夹", "只影响你自己（存在 UserSettings 里）"),
                        user.includeSubfolders);
                    if (sub != user.includeSubfolders)
                    {
                        user.includeSubfolders = sub;
                        user.SaveNow();
                        ABGUI.Defer(() => Reload(sub ? "改为连子文件夹一起读" : "改为只读文件夹顶层"));
                    }

                    if (GUILayout.Button("恢复默认输出设置", GUILayout.Width(130)))
                    {
                        s.structDir = s.containerDir = RevExcelProjectSettings.DefaultCodeDir;
                        s.structFileName = RevExcelProjectSettings.DefaultStructFileName;
                        s.containerFileName = RevExcelProjectSettings.DefaultContainerFileName;
                        s.dataDir = "";
                        GUI.changed = true;
                    }

                    EditorGUI.indentLevel--;
                }

                if (EditorGUI.EndChangeCheck())
                {
                    s.SaveNow();
                    Replan();
                }
            }
        }

        private void DrawBundleRow()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.PrefixLabel(new GUIContent("AB 包", "数据文件打进哪个 AssetBundle（没有标记 = AB 模式 / 真机读不到）"));

                if (_autoByFolder)
                {
                    GUILayout.Label("按目录自动分包：打包 / 生成映射时自动标记", EditorStyles.miniLabel);
                }
                else if (!string.IsNullOrEmpty(_bundle))
                {
                    GUILayout.Label(_bundle + "（跟着数据目录的文件夹标记）", EditorStyles.miniLabel);
                }
                else
                {
                    GUILayout.Label(new GUIContent(" 未标记：编辑器直读正常，AB 模式 / 真机读不到", ABGUI.WarnIcon), EditorStyles.miniLabel);
                    if (GUILayout.Button("标记…", EditorStyles.miniButton, GUILayout.Width(60))) MarkDataDir();
                }

                GUILayout.FlexibleSpace();
            }
        }

        private static void DrawCheck(RevExcelCheck check, bool offerResRoot = false)
        {
            if (check.Type == MessageType.None)
            {
                if (!string.IsNullOrEmpty(check.Message))
                    EditorGUILayout.LabelField(" ", "✔ " + check.Message, EditorStyles.miniLabel);
                return;
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.HelpBox(check.Message, check.Type);
                if (offerResRoot && GUILayout.Button("打开 RevAB\n打包工具", GUILayout.Width(80), GUILayout.Height(38)))
                    ABBuildWindow.Open();
            }
        }

        /// <summary>
        /// 目录输入框：可以直接改文字、从 Project 拖文件夹进来、或点「选择…」。
        /// ★ 用文本框而不是 ObjectField：目录还不存在时（第一次导出前）ObjectField 只能显示 None，看不出会写到哪。
        /// </summary>
        private static string PathField(GUIContent label, string value, string title)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                string text = EditorGUILayout.DelayedTextField(label, value ?? "");
                Rect rect = GUILayoutUtility.GetLastRect();

                Event e = Event.current;
                if ((e.type == EventType.DragUpdated || e.type == EventType.DragPerform) && rect.Contains(e.mousePosition))
                {
                    string folder = DraggedAssetsFolder();
                    if (folder != null)
                    {
                        DragAndDrop.visualMode = DragAndDropVisualMode.Link;
                        if (e.type == EventType.DragPerform)
                        {
                            DragAndDrop.AcceptDrag();
                            text = folder;
                            GUI.changed = true;
                        }
                        e.Use();
                    }
                }

                if (GUILayout.Button("选择…", GUILayout.Width(60)))
                {
                    string start = AssetDatabase.IsValidFolder(text) ? text : "Assets";
                    string picked = EditorUtility.OpenFolderPanel(title, start, "");
                    if (!string.IsNullOrEmpty(picked))
                    {
                        string project = RevExcelService.ToProjectPath(picked);
                        if (RevExcelService.IsUnderAssets(project))
                        {
                            text = project;
                            GUI.changed = true;
                        }
                        else
                        {
                            EditorUtility.DisplayDialog("目录不在工程里", "请选工程 Assets 目录下的文件夹（Unity 才会导入 / 编译）。", "好");
                        }
                        GUIUtility.ExitGUI();              // 模态框之后布局已经乱了：结束本帧，下一帧重画
                    }
                }

                return RevExcelService.Clean(text);
            }
        }

        /// <summary>从 Project 窗口拖进来的文件夹（Assets 下）；不是文件夹返回 null</summary>
        private static string DraggedAssetsFolder()
        {
            foreach (string path in DragAndDrop.paths ?? Array.Empty<string>())
            {
                string p = RevExcelService.ToProjectPath(path);
                if (RevExcelService.IsUnderAssets(p) && AssetDatabase.IsValidFolder(p)) return p;
            }
            return null;
        }

        // ============================================================
        // 主体：表列表 | 详情 / 底部结果
        // ============================================================

        private void DrawBody(Rect rest)
        {
            const float collapsedBar = 22f;
            Rect main, bottom;

            if (_bottomOpen)
            {
                _bottomRatio = ABGUI.HorizontalSplitter(rest, _bottomRatio, 180f, 90f, ref _draggingBottom);
                float cut = rest.y + rest.height * _bottomRatio;
                main = new Rect(rest.x, rest.y, rest.width, cut - rest.y - 1f);
                bottom = new Rect(rest.x, cut + 2f, rest.width, rest.yMax - cut - 2f);
            }
            else
            {
                main = new Rect(rest.x, rest.y, rest.width, rest.height - collapsedBar);
                bottom = new Rect(rest.x, main.yMax, rest.width, collapsedBar);
            }

            DrawMain(main);
            DrawBottom(bottom);
        }

        private void DrawMain(Rect main)
        {
            _split = ABGUI.VerticalSplitter(main, _split, 200f, 340f, ref _draggingSplit);
            float x = main.x + main.width * _split;

            // ---------- 左：表列表 ----------
            var left = new Rect(main.x + 3f, main.y + 2f, x - main.x - 5f, main.height - 4f);
            _list.searchString = _listSearch.OnGUI(new Rect(left.x, left.y, left.width, 18f), _list.searchString);
            _list.OnGUI(new Rect(left.x, left.y + 20f, left.width, left.height - 20f));

            // ---------- 右：详情 ----------
            DrawDetail(new Rect(x + 4f, main.y + 2f, main.xMax - x - 7f, main.height - 4f));
        }

        private static GUIStyle _titleStyle;

        private static GUIStyle TitleStyle => _titleStyle ??= new GUIStyle(EditorStyles.boldLabel) { fontSize = 14 };

        private void DrawDetail(Rect rect)
        {
            ExcelTable t = _selected;

            if (t == null)
            {
                GUI.Label(new Rect(rect.x, rect.y + 8f, rect.width, 40f),
                    _read != null && _read.Tables.Count == 0 ? "来源里没有读到任何工作表。" : "在左边选一张表，这里显示它的数据和字段。",
                    EditorStyles.centeredGreyMiniLabel);
                return;
            }

            // ---------- ① 标题 ----------
            const float headerHeight = 42f;
            GUILayout.BeginArea(new Rect(rect.x, rect.y, rect.width, headerHeight));
            using (new EditorGUILayout.HorizontalScope())
            {
                string names = t.Ignored ? "" : $"   →   {ExcelCodeGenerator.Identifier(t.StructName())} / {ExcelCodeGenerator.Identifier(t.ContainerName())}";
                GUILayout.Label(t.Name + names, TitleStyle);
                GUILayout.FlexibleSpace();

                if (GUILayout.Button(new GUIContent("在 Excel 中打开", "用 Excel 打开所在的工作簿（导表工具读取时不需要关掉它）"), EditorStyles.miniButton))
                    RevExcelTableList.OpenInExcel(t.SourcePath);

                ExcelPlannedFile data = t.IsValid ? _plan?.DataFileOf(t) : null;
                using (new EditorGUI.DisabledScope(data == null || !File.Exists(data.Path)))
                {
                    if (GUILayout.Button(new GUIContent("定位数据文件", data?.Path), EditorStyles.miniButton) && data != null)
                        ABGUI.Ping(data.Path);
                }
            }
            GUILayout.Label(DetailLine(t), EditorStyles.miniLabel);
            GUILayout.EndArea();

            float y = rect.y + headerHeight;

            // ---------- ② 问题 ----------
            int issues = t.Errors.Count + t.Warnings.Count;
            if (t.Ignored) issues = 1;

            if (issues > 0)
            {
                int visibleLines = _showAllIssues ? issues : Mathf.Min(issues, 5);
                float height = Mathf.Min(visibleLines * 20f + (issues > 5 ? 20f : 0f) + 6f, rect.height * 0.4f);
                DrawIssues(new Rect(rect.x, y, rect.width, height), t);
                y += height + 4f;
            }

            // ---------- ③ 页签 + 内容 ----------
            if (t.Ignored || t.Fields.Count == 0) return;

            var tabs = new Rect(rect.x, y, rect.width, 20f);
            GUILayout.BeginArea(tabs);
            using (new EditorGUILayout.HorizontalScope())
            {
                _detailTab = GUILayout.Toolbar(_detailTab,
                    new[] { new GUIContent($"数据预览（{t.Rows.Count} 行）"), new GUIContent($"字段（{t.Fields.Count}）") },
                    EditorStyles.toolbarButton, GUILayout.Width(260));
                GUILayout.FlexibleSpace();
            }
            GUILayout.EndArea();

            var content = new Rect(rect.x, y + 22f, rect.width, rect.yMax - y - 22f);

            if (_detailTab == 0 && _preview != null)
            {
                var search = new Rect(tabs.xMax - 200f, tabs.y + 1f, 200f, 18f);
                _preview.searchString = _previewSearch.OnGUI(search, _preview.searchString);
                _preview.OnGUI(content);
            }
            else
            {
                DrawFields(content, t);
            }
        }

        private string DetailLine(ExcelTable t)
        {
            if (t.Ignored) return $"{t.SourceFile} · {t.IgnoreReason}";

            ExcelField key = t.KeyField;
            string line = $"{t.SourceFile}   ·   主键 {key?.Name}（{key?.TypeText.ToLowerInvariant()}）   ·   {t.Fields.Count} 个字段   ·   {t.Rows.Count} 行";

            ExcelPlannedFile data = t.IsValid ? _plan?.DataFileOf(t) : null;
            if (data != null)
            {
                string state = data.Change == ExcelFileChange.New ? "（还没导出过）"
                    : data.Change == ExcelFileChange.Changed ? "（有改动，待导出）"
                    : "（已是最新）";
                line += $"   ·   数据 → {data.Path}{state}";
            }
            else if (!t.IsValid)
            {
                line += "   ·   有错误，导出时跳过";
            }

            return line;
        }

        private void DrawIssues(Rect rect, ExcelTable t)
        {
            GUILayout.BeginArea(rect, EditorStyles.helpBox);
            _issueScroll = EditorGUILayout.BeginScrollView(_issueScroll);

            if (t.Ignored)
            {
                GUILayout.Label(new GUIContent(" " + t.IgnoreReason, ABGUI.InfoIcon), EditorStyles.label);
            }
            else
            {
                int shown = 0;
                int limit = _showAllIssues ? int.MaxValue : 5;

                foreach (ExcelIssue issue in t.Errors)
                    if (shown++ < limit) IssueRow(issue, ABGUI.ErrorIcon);
                foreach (ExcelIssue issue in t.Warnings)
                    if (shown++ < limit) IssueRow(issue, ABGUI.WarnIcon);

                int total = t.Errors.Count + t.Warnings.Count;
                if (total > 5)
                {
                    if (GUILayout.Button(_showAllIssues ? "收起" : $"显示全部 {total} 条", EditorStyles.miniButton, GUILayout.Width(120)))
                        _showAllIssues = !_showAllIssues;
                }
            }

            EditorGUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        private void IssueRow(ExcelIssue issue, Texture icon)
        {
            using (new EditorGUILayout.HorizontalScope(GUILayout.Height(18)))
            {
                GUILayout.Label(new GUIContent(" " + issue.Message, icon, issue.Message), EditorStyles.label, GUILayout.Height(18));
                GUILayout.FlexibleSpace();

                if (issue.Row > 3 && _preview != null &&
                    GUILayout.Button(new GUIContent("定位", $"在预览里跳到第 {issue.Row} 行"), EditorStyles.miniButton, GUILayout.Width(40)))
                {
                    _detailTab = 0;
                    if (!_preview.RevealRow(issue.Row))
                        ShowNotification(new GUIContent($"第 {issue.Row} 行没有进数据（被跳过了），请直接在 Excel 里看"));
                }
            }
        }

        private void DrawFields(Rect rect, ExcelTable t)
        {
            GUILayout.BeginArea(rect);
            _fieldScroll = EditorGUILayout.BeginScrollView(_fieldScroll);

            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                GUILayout.Label("", GUILayout.Width(18));
                GUILayout.Label("列", EditorStyles.miniBoldLabel, GUILayout.Width(34));
                GUILayout.Label("字段名", EditorStyles.miniBoldLabel, GUILayout.Width(140));
                GUILayout.Label("类型", EditorStyles.miniBoldLabel, GUILayout.Width(60));
                GUILayout.Label("C# 字段", EditorStyles.miniBoldLabel, GUILayout.Width(140));
                GUILayout.Label("描述", EditorStyles.miniBoldLabel);
            }

            foreach (ExcelField f in t.Fields)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label(new GUIContent(f.IsKey ? "★" : "", f.IsKey ? "主键（第一个字段）" : null), GUILayout.Width(18));
                    GUILayout.Label(f.ColumnName, EditorStyles.miniLabel, GUILayout.Width(34));
                    GUILayout.Label(f.Name, f.IsKey ? EditorStyles.boldLabel : EditorStyles.label, GUILayout.Width(140));
                    GUILayout.Label(f.TypeText.ToLowerInvariant(), GUILayout.Width(60));
                    EditorGUILayout.SelectableLabel($"{f.CSharpType()} {ExcelCodeGenerator.Identifier(f.CSharpName())}",
                        GUILayout.Width(140), GUILayout.Height(18));
                    GUILayout.Label(f.Desc, EditorStyles.wordWrappedLabel);
                }
            }

            EditorGUILayout.Space(6);
            ExcelField key = t.KeyField;
            if (key != null)
            {
                string container = ExcelCodeGenerator.Identifier(t.ContainerName());
                string row = ExcelCodeGenerator.Identifier(t.StructName());
                EditorGUILayout.LabelField("业务里这么读：", EditorStyles.miniBoldLabel);
                EditorGUILayout.SelectableLabel(
                    $"await RevDataTableManager.LoadAsync<{container}>();\n" +
                    $"if ({container}.Instance.FindByKey({(key.Type == FieldType.String ? "\"key\"" : "1001")}, out {row} row)) {{ … }}",
                    EditorStyles.textArea, GUILayout.Height(36));
            }

            EditorGUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        // ---------------- 底部：导出结果 / 日志 ----------------

        private void DrawBottom(Rect rect)
        {
            var bar = new Rect(rect.x, rect.y, rect.width, 21f);
            GUILayout.BeginArea(bar, EditorStyles.toolbar);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(_bottomOpen ? "▾" : "▸", EditorStyles.toolbarButton, GUILayout.Width(22)))
                    _bottomOpen = !_bottomOpen;

                int warnings = _log.Count(l => l.level == 1 || l.level == 2);
                var tabs = new[]
                {
                    new GUIContent(" 导出结果", _report == null ? null : !_report.success ? ABGUI.ErrorIcon
                        : NeedsAttention(_report) ? ABGUI.WarnIcon : ABGUI.Icon("TestPassed")),
                    new GUIContent($" 日志（{_log.Count}）", warnings > 0 ? ABGUI.WarnIcon : null),
                };

                int tab = GUILayout.Toolbar(_bottomTab, tabs, EditorStyles.toolbarButton, GUILayout.Width(220));
                if (tab != _bottomTab)
                {
                    _bottomTab = tab;
                    _bottomOpen = true;                    // 折叠着点页签 = 展开看它
                }

                GUILayout.FlexibleSpace();

                if (_bottomTab == 1 && _bottomOpen)
                {
                    if (GUILayout.Button("复制", EditorStyles.toolbarButton))
                        EditorGUIUtility.systemCopyBuffer = string.Join("\n", _log.Select(l => $"[{l.time}] {l.text}"));
                    if (GUILayout.Button("清空", EditorStyles.toolbarButton)) _log.Clear();
                }
            }
            GUILayout.EndArea();

            if (!_bottomOpen || rect.height < 30f) return;

            GUILayout.BeginArea(new Rect(rect.x + 4f, rect.y + 23f, rect.width - 8f, rect.height - 25f));
            _bottomScroll = EditorGUILayout.BeginScrollView(_bottomScroll);

            if (_bottomTab == 0) DrawReport();
            else DrawLog();

            EditorGUILayout.EndScrollView();
            GUILayout.EndArea();

            if (_bottomTab == 1 && _scrollLogToEnd && Event.current.type == EventType.Repaint)
            {
                _scrollLogToEnd = false;
                _bottomScroll.y = float.MaxValue;
                Repaint();
            }
        }

        private static bool NeedsAttention(RevExcelReport r)
            => r.unmarked.Count > 0 || r.mapFailed || r.orphans.Count > 0 || r.warnings.Count > 0;

        private void DrawReport()
        {
            RevExcelReport r = _report;
            if (r == null)
            {
                EditorGUILayout.LabelField("还没有导出过。左边确认数据没问题后，点右上角的「导出」。", EditorStyles.miniLabel);
                return;
            }

            if (!r.success)
            {
                EditorGUILayout.HelpBox($"{r.time}  导出失败，没有写入任何文件。", MessageType.Error);
                foreach (string e in r.errors) EditorGUILayout.HelpBox(e, MessageType.Error);
                return;
            }

            EditorGUILayout.HelpBox(
                $"{r.time}  导出完成：{r.tableCount} 张表 / {r.rowCount} 条数据 · 写入 {r.written.Count} 个文件，{r.unchangedCount} 个没变化（没有重写）" +
                (r.codeChanged ? "\n代码有变化：Unity 会重新编译一次脚本。" : r.written.Count > 0 ? "\n只有数据变了：不需要重新编译。" : ""),
                MessageType.Info);

            // ---------- 需要处理的事（带按钮） ----------
            if (r.unmarked.Count > 0)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.HelpBox($"{r.unmarked.Count} 个数据文件没有 AB 标记：编辑器直读正常，AB 模式 / 真机读不到。" +
                                            "给数据目录整个标一个包，以后新增的表也会自动跟着进包。", MessageType.Warning);
                    if (GUILayout.Button("标记数据\n目录…", GUILayout.Width(80), GUILayout.Height(38))) MarkDataDir();
                }
            }

            if (r.mapStatus.Length > 0)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.HelpBox(r.mapStatus, r.mapFailed ? MessageType.Warning : MessageType.Info);
                    if (r.mapFailed && GUILayout.Button("打开 RevAB\n打包工具", GUILayout.Width(80), GUILayout.Height(38)))
                        ABBuildWindow.Open();
                }
            }

            if (r.orphans.Count > 0)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.HelpBox($"数据目录里有 {r.orphans.Count} 个旧数据文件已经没有对应的表（表被删 / 改名了）：" +
                                            string.Join("、", r.orphans.Take(6).Select(Path.GetFileName)) + (r.orphans.Count > 6 ? " …" : ""),
                        MessageType.Warning);
                    if (GUILayout.Button("删除…", GUILayout.Width(80), GUILayout.Height(38))) DeleteOrphans();
                }
            }

            foreach (string w in r.warnings.Take(20)) EditorGUILayout.HelpBox(w, MessageType.Warning);
            if (r.warnings.Count > 20) EditorGUILayout.LabelField($"… 还有 {r.warnings.Count - 20} 条警告（见日志）", EditorStyles.miniLabel);

            // ---------- 写了哪些文件 ----------
            if (r.written.Count > 0)
            {
                EditorGUILayout.LabelField("写入的文件", EditorStyles.miniBoldLabel);
                foreach (string line in r.written)
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        string path = line.Substring(line.IndexOf("Assets", StringComparison.Ordinal) >= 0
                            ? line.IndexOf("Assets", StringComparison.Ordinal) : 0);
                        using (new EditorGUI.DisabledScope(!File.Exists(path)))
                        {
                            if (GUILayout.Button("定位", EditorStyles.miniButton, GUILayout.Width(40))) ABGUI.Ping(path);
                        }
                        EditorGUILayout.LabelField(line, EditorStyles.miniLabel);
                    }
                }
            }
        }

        private void DrawLog()
        {
            if (_log.Count == 0)
            {
                EditorGUILayout.LabelField("（空）", EditorStyles.miniLabel);
                return;
            }

            foreach (LogEntry entry in _log)
            {
                Texture icon = entry.level == 2 ? ABGUI.ErrorIcon
                    : entry.level == 1 ? ABGUI.WarnIcon
                    : entry.level == 3 ? ABGUI.Icon("TestPassed")
                    : ABGUI.InfoIcon;

                EditorGUILayout.LabelField(new GUIContent($" [{entry.time}] {entry.text}", icon, entry.text), EditorStyles.label);
            }
        }

        private void Log(int level, string text)
        {
            _log.Add(new LogEntry { level = level, time = DateTime.Now.ToString("HH:mm:ss"), text = text });
            if (_log.Count > MaxLog) _log.RemoveRange(0, _log.Count - MaxLog);
            _scrollLogToEnd = true;
        }

        // ============================================================
        // 空状态 / 拖放
        // ============================================================

        private static GUIStyle _bigTitle;

        private static GUIStyle BigTitle => _bigTitle ??= new GUIStyle(EditorStyles.boldLabel)
        {
            fontSize = 18,
            alignment = TextAnchor.MiddleCenter
        };

        private void DrawEmpty(Rect rest)
        {
            var box = new Rect(rest.center.x - 250f, rest.y + Mathf.Max(20f, rest.height * 0.5f - 170f), 500f, 330f);
            GUILayout.BeginArea(box, EditorStyles.helpBox);
            _emptyScroll = EditorGUILayout.BeginScrollView(_emptyScroll);

            GUILayout.Space(14);
            GUILayout.Label("把 Excel 拖到这里", BigTitle);
            GUILayout.Label("支持 .xlsx 工作簿，或装着 .xlsx 的文件夹（可以添加多个）", EditorStyles.centeredGreyMiniLabel);
            GUILayout.Space(12);

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                if (GUILayout.Button(new GUIContent(" 选择 Excel 文件…", ABGUI.PlusIcon), GUILayout.Width(150), GUILayout.Height(28)))
                    ABGUI.Defer(PickFiles);
                if (GUILayout.Button(new GUIContent(" 选择文件夹…", ABGUI.FolderIcon), GUILayout.Width(150), GUILayout.Height(28)))
                    ABGUI.Defer(PickFolder);
                GUILayout.FlexibleSpace();
            }

            string demo = DemoFolder();
            if (demo != null)
            {
                GUILayout.Space(4);
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button("用仓库里的样例表试试", EditorStyles.linkLabel)) AddSources(new[] { demo });
                    EditorGUIUtility.AddCursorRect(GUILayoutUtility.GetLastRect(), MouseCursor.Link);
                    GUILayout.FlexibleSpace();
                }
            }

            GUILayout.Space(14);
            EditorGUILayout.LabelField("Excel 怎么写", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                "一个工作表 = 一张表（工作表名就是表名）\n" +
                "第 1 行 字段名 · 第 2 行 类型（int / float / string / bool）· 第 3 行 描述 · 第 4 行起 数据\n" +
                "第一个字段是主键（必填、不重复）· 以 # 开头的列 / 行 / 工作表是备注，会被忽略\n\n" +
                "导出后：代码进 " + RevExcelProjectSettings.DefaultCodeDir + "，数据进 <资源根目录>/Data，业务直接\n" +
                "await RevDataTableManager.LoadAsync<HeroTable>() 读表。",
                EditorStyles.wordWrappedMiniLabel);

            EditorGUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        /// <summary>
        /// 整个窗口都能接 Excel：拖 .xlsx 或装着 .xlsx 的文件夹进来 = 添加来源。
        /// ★ 只认"里面真有 xlsx"的文件夹 —— 否则从 Project 拖文件夹到「输出」的目录框上时会被这里抢走。
        /// </summary>
        private void HandleWindowDrop()
        {
            Event e = Event.current;

            if (e.type == EventType.DragExited)
            {
                _dropHover = false;
                Repaint();
                return;
            }

            if (e.type != EventType.DragUpdated && e.type != EventType.DragPerform) return;

            string[] paths = DragAndDrop.paths;
            if (!DragHasExcel(paths))
            {
                _dropHover = false;
                return;
            }

            DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
            _dropHover = e.type == EventType.DragUpdated;

            if (e.type == EventType.DragPerform)
            {
                DragAndDrop.AcceptDrag();
                var accepted = paths.Where(p => File.Exists(p) ? RevExcelService.IsExcel(p) : Directory.Exists(p)).ToList();
                ABGUI.Defer(() => AddSources(accepted));
            }

            e.Use();
            Repaint();
        }

        private bool DragHasExcel(string[] paths)
        {
            if (paths == null || paths.Length == 0) return false;
            if (ReferenceEquals(paths, _dragProbePaths)) return _dragProbeResult;      // 同一次拖拽只判断一次

            bool result = false;
            foreach (string path in paths)
            {
                if (File.Exists(path) && RevExcelService.IsExcel(path)) { result = true; break; }
                if (!Directory.Exists(path)) continue;

                try
                {
                    if (Directory.EnumerateFiles(path, "*.xlsx", SearchOption.AllDirectories).Any(RevExcelService.IsExcel))
                    {
                        result = true;
                        break;
                    }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }

            _dragProbePaths = paths;
            _dragProbeResult = result;
            return result;
        }

        private void DrawDropOverlay()
        {
            if (!_dropHover || Event.current.type != EventType.Repaint) return;

            var rect = new Rect(0f, 0f, position.width, position.height);
            EditorGUI.DrawRect(rect, new Color(0.25f, 0.5f, 1f, 0.12f));
            GUI.Label(rect, "松手添加为 Excel 来源", BigTitle);
        }
    }
}
