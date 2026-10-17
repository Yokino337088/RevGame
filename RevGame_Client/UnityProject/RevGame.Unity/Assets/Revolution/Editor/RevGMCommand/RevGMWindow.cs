// RevGMWindow.cs —— GM 指令面板（编辑器窗口，IMGUI；不依赖任何运行时 UI 系统）
//
// 【打开】菜单 Revolution.Tools/GM 指令面板（快捷键 Ctrl+Shift+G）
//
// 【怎么用（3 步，全程不用记键位）】
//   ① 上面输入框：打关键词 → 左栏立刻出搜索结果（点一条 = 自动填入并带一个空格）
//   ② 需要参数就在输入框里接着打；右侧同步显示说明，枚举参数点候选值即可
//   ③ 点「执行」或回车 —— 结果与耗时显示在输入框下方；左栏也可以直接按分组浏览
//
// 【编辑模式 vs Play 模式】
//   · 编辑模式：只查看与联想（命令清单来自 [RevGMEntry] 注册入口的快照）—— 不执行
//   · Play 模式：读运行期注册表，可真执行（被测环境就是游戏本身，结果真实）
//
// 【为什么不用 UGUI】面板完全活在编辑器里：EditorWindow + IMGUI，不占运行时、不进包体。
//   与工程既有的 Editor\RevResourceSystem\ABTool 保持同一套写法（EditorWindow + EditorStyles）。

using System;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Revolution.Editor
{
    /// <summary>GM 指令面板。</summary>
    public sealed class RevGMWindow : EditorWindow
    {
        private const string SearchControl = "RevGMSearchField";
        private const int MaxSuggestions = 8;
        private const int MaxHistory = 50;
        private const float PaneGap = 8f;
        private static readonly char[] ArgSeparators = { ' ', '\t', '\u3000' };   // 命令名与参数的分隔（半角/Tab/全角空格）

        // ── 样式（首次 OnGUI 时创建） ────────────────────────────────
        private static GUIStyle _richLabel;
        private static GUIStyle _richMini;
        private static GUIStyle _iconLabel;
        private static GUIStyle _commandRow;

        // ── 状态 ────────────────────────────────────────────────────
        private string _input = string.Empty;
        private string _suggestedFor;                                           // 上次算联想用的输入（变了才重算）
        private bool _suggestedWhilePlaying;                                    // 上次算联想时的模式（进出 Play 时强制重算）
        private IReadOnlyList<RevGMCommand> _suggestions = Array.Empty<RevGMCommand>();
        private int _selectedIndex;
        private RevGMCommand _detail;                                           // 右侧详情
        private RevGMResult? _lastResult;
        private string _lastExecuted = string.Empty;
        private bool _showEntries;
        private bool _showHistory;
        private bool _focusSearch = true;
        private Vector2 _treeScroll, _detailScroll, _historyScroll;
        private readonly List<HistoryItem> _history = new List<HistoryItem>(16);
        private readonly Dictionary<string, bool> _foldouts = new Dictionary<string, bool>(16);
        private readonly Dictionary<string, List<RevGMCommand>> _groups = new Dictionary<string, List<RevGMCommand>>(16);

        private struct HistoryItem
        {
            public string Text;
            public RevGMResult Result;
            public string Time;
        }

        // ============================================================
        // 打开 / 初始化
        // ============================================================

        /// <summary>打开面板</summary>
        [MenuItem("Revolution.Tools/GM 指令面板 %#g", false, 2)]
        public static void Open()
        {
            RevGMWindow window = GetWindow<RevGMWindow>("GM 指令");
            window.minSize = new Vector2(660f, 440f);
            window.Show();
        }

        private void OnEnable()
        {
            titleContent = new GUIContent("GM 指令");
            _focusSearch = true;
        }

        // ============================================================
        // 绘制
        // ============================================================

        private void OnGUI()
        {
            EnsureStyles();
            HandleKeyboard();                       // ★ 先消费键盘事件，再画搜索框（否则方向键会被输入框吃掉）

            DrawToolbar();
            EditorGUILayout.Space(10f);
            DrawSearchRow();
            UpdateSuggestions();
            DrawResult();
            EditorGUILayout.Space(6f);
            DrawBody();
            DrawHistory();
        }

        private static void EnsureStyles()
        {
            if (_richLabel != null) return;

            _richLabel = new GUIStyle(EditorStyles.label) { richText = true, alignment = TextAnchor.MiddleLeft };
            _richMini = new GUIStyle(EditorStyles.miniLabel) { richText = true };
            _iconLabel = new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.MiddleCenter };
            _commandRow = new GUIStyle(EditorStyles.label)
            {
                alignment = TextAnchor.MiddleLeft,
                padding = new RectOffset(10, 6, 3, 3),
                fixedHeight = 24f
            };
        }

        // ── 工具条 ──────────────────────────────────────────────────
        private void DrawToolbar()
        {
            bool playing = RevGMEditorCatalog.IsPlaying;
            IReadOnlyList<RevGMCommand> commands = RevGMEditorCatalog.Commands;

            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            GUILayout.Label(playing ? "● 运行中 · 可执行" : "○ 编辑模式 · 仅预览",
                            EditorStyles.miniLabel, GUILayout.Width(135f));
            GUILayout.Label($"{commands.Count} 条命令", EditorStyles.miniLabel, GUILayout.Width(75f));
            GUILayout.FlexibleSpace();

            _showEntries = GUILayout.Toggle(_showEntries, "来源", EditorStyles.toolbarButton, GUILayout.Width(44f));

            if (GUILayout.Button("刷新", EditorStyles.toolbarButton, GUILayout.Width(46f)))
            {
                RevGMEditorCatalog.Invalidate();
                _suggestedFor = null;
                Repaint();
            }

            if (GUILayout.Button("帮助", EditorStyles.toolbarButton, GUILayout.Width(46f))) ShowHelp();

            EditorGUILayout.EndHorizontal();

            if (_showEntries) DrawEntries();
        }

        private void DrawEntries()
        {
            IReadOnlyList<RevGMEditorCatalog.EntryInfo> entries = RevGMEditorCatalog.Entries;

            if (entries.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    "没有找到 [RevGMEntry] 注册入口。\n" +
                    "· 编辑模式：给注册方法加上 [RevGMEntry] 就能在这里看到命令清单（方法里只做 RevGM.Register）\n" +
                    "· Play 模式：命令来自游戏自己的注册，不需要这个标记",
                    MessageType.Info);
                return;
            }

            for (int i = 0; i < entries.Count; i++)
            {
                RevGMEditorCatalog.EntryInfo entry = entries[i];

                if (string.IsNullOrEmpty(entry.Error))
                    EditorGUILayout.LabelField("· " + entry.Display, EditorStyles.miniLabel);
                else
                    EditorGUILayout.HelpBox("· " + entry.Display + "\n  调用失败：" + entry.Error, MessageType.Error);
            }
        }

        // ── 输入：搜索与执行共用一行 ────────────────────────────────
        private void DrawSearchRow()
        {
            EditorGUILayout.LabelField("搜索或输入命令", EditorStyles.boldLabel);
            EditorGUILayout.BeginHorizontal();

            GUI.SetNextControlName(SearchControl);
            EditorGUI.BeginChangeCheck();
            string typed = EditorGUILayout.TextField(_input, GUILayout.Height(26f));
            if (EditorGUI.EndChangeCheck())
            {
                _input = typed;
                _selectedIndex = 0;
                _suggestedFor = null;
                _treeScroll = Vector2.zero;
            }

            if (_focusSearch)
            {
                EditorGUI.FocusTextInControl(SearchControl);
                _focusSearch = false;
            }

            using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(_input)))
            {
                if (GUILayout.Button("清空", GUILayout.Width(48f), GUILayout.Height(26f)))
                {
                    _input = string.Empty;
                    _selectedIndex = 0;
                    _suggestedFor = null;
                    _focusSearch = true;
                }
            }

            bool playing = RevGMEditorCatalog.IsPlaying;
            using (new EditorGUI.DisabledScope(!playing || string.IsNullOrWhiteSpace(_input)))
            {
                if (GUILayout.Button("执行", GUILayout.Width(72f), GUILayout.Height(26f))) ExecuteInput(false);
            }
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.LabelField(playing
                ? "输入关键词查找，点选后可补参数；Enter 执行"
                : "编辑模式可查找命令；进入 Play 后才能执行", EditorStyles.miniLabel);
        }

        private bool IsSearching => !string.IsNullOrWhiteSpace(_input) && _input.Trim().IndexOfAny(new[] { ' ', '\t', '\u3000' }) < 0;

        private void UpdateSuggestions()
        {
            bool playing = RevGMEditorCatalog.IsPlaying;
            if (_suggestedFor == _input && _suggestedWhilePlaying == playing) return;

            _suggestions = IsSearching
                ? RevGMEditorCatalog.Suggest(_input.Trim(), MaxSuggestions)
                : Array.Empty<RevGMCommand>();
            _suggestedFor = _input;
            _suggestedWhilePlaying = playing;
            if (_selectedIndex >= _suggestions.Count) _selectedIndex = 0;

            // 直接输入完整命令名与参数时，仍同步显示该命令的说明。
            string line = _input.TrimStart();
            int end = line.IndexOfAny(ArgSeparators);
            if (end > 0)
            {
                string name = line.Substring(0, end);
                IReadOnlyList<RevGMCommand> commands = RevGMEditorCatalog.Commands;
                for (int i = 0; i < commands.Count; i++)
                    if (string.Equals(commands[i].Name, name, StringComparison.OrdinalIgnoreCase))
                    {
                        _detail = commands[i];
                        break;
                    }
            }
        }

        private void DrawSuggestions()
        {
            if (_suggestions.Count == 0)
            {
                EditorGUILayout.LabelField("没有匹配的命令，请换个关键词", EditorStyles.wordWrappedMiniLabel);
                return;
            }

            for (int i = 0; i < _suggestions.Count; i++)
            {
                RevGMCommand command = _suggestions[i];
                Rect row = EditorGUILayout.GetControlRect(false, 40f);
                if (i == _selectedIndex) EditorGUI.DrawRect(row, new Color(0.24f, 0.48f, 0.90f, 0.17f));
                GUI.Label(new Rect(row.x + 8f, row.y + 2f, row.width - 16f, 19f),
                    Highlight(command.Name, _input.Trim()), _richLabel);
                string subtitle = ArgsHint(command) + command.Description;
                if (command.IsHighRisk) subtitle = "高危 · " + subtitle;
                GUI.Label(new Rect(row.x + 8f, row.y + 21f, row.width - 16f, 16f), subtitle, _richMini);

                if (Event.current.type == EventType.MouseDown && row.Contains(Event.current.mousePosition))
                {
                    _detail = command;
                    FillInput(command.Name + " ");
                    Event.current.Use();
                }
            }
        }

        // ── 主体：左边查找 / 浏览，右边看说明 ────────────────────
        private void DrawBody()
        {
            EditorGUILayout.BeginHorizontal(GUILayout.ExpandHeight(true));
            float listWidth = Mathf.Clamp(position.width * 0.36f, 218f, 310f);

            EditorGUILayout.BeginVertical(EditorStyles.helpBox, GUILayout.Width(listWidth), GUILayout.ExpandHeight(true));
            EditorGUILayout.LabelField(IsSearching ? "搜索结果" : "浏览命令", EditorStyles.boldLabel);
            _treeScroll = EditorGUILayout.BeginScrollView(_treeScroll, GUILayout.MinHeight(160f), GUILayout.ExpandHeight(true));
            if (IsSearching) DrawSuggestions();
            else DrawTree();
            EditorGUILayout.EndScrollView();
            EditorGUILayout.EndVertical();

            GUILayout.Space(PaneGap);

            EditorGUILayout.BeginVertical(EditorStyles.helpBox, GUILayout.ExpandHeight(true));
            EditorGUILayout.LabelField("命令说明", EditorStyles.boldLabel);
            _detailScroll = EditorGUILayout.BeginScrollView(_detailScroll, GUILayout.MinHeight(160f), GUILayout.ExpandHeight(true));
            DrawDetail();
            EditorGUILayout.EndScrollView();
            EditorGUILayout.EndVertical();
            EditorGUILayout.EndHorizontal();
        }

        private void DrawTree()
        {
            IReadOnlyList<RevGMCommand> commands = RevGMEditorCatalog.Commands;

            if (commands.Count == 0)
            {
                EditorGUILayout.HelpBox(EmptyHint(), MessageType.Info);
                return;
            }

            BuildGroups(commands);

            List<string> groups = new List<string>(_groups.Keys);
            groups.Sort(StringComparer.Ordinal);

            for (int i = 0; i < groups.Count; i++)
            {
                string group = groups[i];
                string title = (string.IsNullOrEmpty(group) ? "通用" : group) + "  (" + _groups[group].Count + ")";

                if (!_foldouts.TryGetValue(group, out bool open)) open = true;

                _foldouts[group] = EditorGUILayout.Foldout(open, title, true);

                if (!_foldouts[group]) continue;

                EditorGUI.indentLevel++;

                List<RevGMCommand> list = _groups[group];
                for (int j = 0; j < list.Count; j++)
                {
                    RevGMCommand command = list[j];

                    string mark = command.IsHighRisk ? "⚠ " : command.IsHidden ? "（隐藏）" : string.Empty;
                    if (GUILayout.Button(new GUIContent(mark + command.BaseName, command.Description), _commandRow,
                            GUILayout.ExpandWidth(true)))
                    {
                        _detail = command;
                        FillInput(command.Name + " ");
                    }
                    if (Event.current.type == EventType.Repaint && _detail != null && _detail.Name == command.Name)
                        EditorGUI.DrawRect(GUILayoutUtility.GetLastRect(), new Color(0.24f, 0.48f, 0.90f, 0.13f));
                }

                EditorGUI.indentLevel--;
            }
        }

        private void DrawDetail()
        {
            if (_detail == null)
            {
                EditorGUILayout.Space(12f);
                EditorGUILayout.LabelField("在左侧查找或选择一条命令", EditorStyles.wordWrappedLabel);
                EditorGUILayout.LabelField("点选即可填入；输入参数后点「执行」", EditorStyles.wordWrappedMiniLabel);
                return;
            }

            EditorGUILayout.LabelField(_detail.Name, EditorStyles.boldLabel);
            if (!string.IsNullOrEmpty(_detail.Description))
                EditorGUILayout.LabelField(_detail.Description, EditorStyles.wordWrappedLabel);

            if (_detail.IsHighRisk) EditorGUILayout.HelpBox("高危命令：执行前会再次确认", MessageType.Warning);
            if (_detail.IsHidden) EditorGUILayout.LabelField("隐藏命令 · 不出现在搜索结果中", EditorStyles.miniLabel);

            EditorGUILayout.Space(8f);
            EditorGUILayout.LabelField("参数说明", EditorStyles.boldLabel);
            if (_detail.Args.Length == 0)
            {
                EditorGUILayout.LabelField("无需参数，选中后直接执行", EditorStyles.miniLabel);
            }
            else
            {
                for (int i = 0; i < _detail.Args.Length; i++)
                {
                    RevGMArg arg = _detail.Args[i];
                    EditorGUILayout.LabelField((i + 1) + ". " + arg.Describe(), EditorStyles.wordWrappedLabel);
                    if (arg.Candidates == null || arg.Candidates.Length == 0) continue;

                    int columns = Mathf.Max(1, Mathf.FloorToInt((position.width - Mathf.Clamp(position.width * 0.36f, 218f, 310f) - 60f) / 105f));
                    int picked = GUILayout.SelectionGrid(-1, arg.Candidates, columns, EditorStyles.miniButton);
                    if (picked >= 0) FillInput(_detail.Name + " " + arg.Candidates[picked] + " ");
                }
            }

            EditorGUILayout.Space(10f);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("填入命令", GUILayout.Width(90f))) FillInput(_detail.Name + " ");
            if (GUILayout.Button("复制名称", GUILayout.Width(80f)))
            {
                EditorGUIUtility.systemCopyBuffer = _detail.Name;
                ShowNotification(new GUIContent("已复制：" + _detail.Name));
            }
            EditorGUILayout.EndHorizontal();
        }

        // ── 结果 / 历史 ─────────────────────────────────────────────
        private void DrawResult()
        {
            if (!_lastResult.HasValue) return;

            RevGMResult result = _lastResult.Value;

            string head = (result.Success ? "✔ 执行成功" : "✘ 执行失败") + $"（{result.ElapsedMs:F1} ms）· " + _lastExecuted;
            EditorGUILayout.HelpBox(head + "\n" + result.Message, result.Success ? MessageType.Info : MessageType.Error);
        }

        private void DrawHistory()
        {
            if (_history.Count == 0) return;

            HistoryItem latest = _history[0];
            bool playing = RevGMEditorCatalog.IsPlaying;

            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            _showHistory = GUILayout.Toggle(_showHistory, $"历史 {_history.Count}", EditorStyles.toolbarButton, GUILayout.Width(78f));

            GUILayout.Label(new GUIContent((latest.Result.Success ? "✔ " : "✘ ") + latest.Text, "最近一次执行，点「重新执行」再来一次"),
                            EditorStyles.miniLabel);

            GUILayout.FlexibleSpace();

            using (new EditorGUI.DisabledScope(!playing))
            {
                if (GUILayout.Button("重新执行", EditorStyles.toolbarButton, GUILayout.Width(62f)))
                {
                    _input = latest.Text;
                    _suggestedFor = null;
                    ExecuteInput(false);
                }
            }

            if (GUILayout.Button("清空", EditorStyles.toolbarButton, GUILayout.Width(40f))) _history.Clear();

            EditorGUILayout.EndHorizontal();

            if (!_showHistory) return;

            _historyScroll = EditorGUILayout.BeginScrollView(_historyScroll, GUILayout.Height(96f));

            for (int i = 0; i < _history.Count; i++)
            {
                HistoryItem item = _history[i];

                EditorGUILayout.BeginHorizontal();
                GUILayout.Label(item.Time, EditorStyles.miniLabel, GUILayout.Width(58f));
                GUILayout.Label(item.Result.Success ? "✔" : "✘", _iconLabel, GUILayout.Width(16f));

                if (GUILayout.Button(item.Text, EditorStyles.miniLabel)) FillInput(item.Text);

                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.EndScrollView();
        }

        // ============================================================
        // 键盘 / 执行
        // ============================================================

        private void HandleKeyboard()
        {
            Event current = Event.current;
            if (current == null || current.type != EventType.KeyDown) return;
            if (GUI.GetNameOfFocusedControl() != SearchControl) return;

            switch (current.keyCode)
            {
                case KeyCode.DownArrow:
                    if (_suggestions.Count > 0) _selectedIndex = (_selectedIndex + 1) % _suggestions.Count;
                    current.Use();
                    Repaint();
                    break;

                case KeyCode.UpArrow:
                    if (_suggestions.Count > 0) _selectedIndex = (_selectedIndex - 1 + _suggestions.Count) % _suggestions.Count;
                    current.Use();
                    Repaint();
                    break;

                case KeyCode.Tab:
                    if (_suggestions.Count > 0 && _selectedIndex < _suggestions.Count)
                    {
                        _detail = _suggestions[_selectedIndex];
                        FillInput(_detail.Name + " ");
                    }

                    current.Use();
                    break;

                case KeyCode.Return:
                case KeyCode.KeypadEnter:
                    // 正在搜索且还没选好命令时，第一次回车 = 补全（防止执行到半截命令）
                    if (IsSearching && _selectedIndex < _suggestions.Count
                        && !string.Equals(_suggestions[_selectedIndex].Name, _input.Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        _detail = _suggestions[_selectedIndex];
                        FillInput(_detail.Name + " ");
                    }
                    else
                    {
                        ExecuteInput(current.control || current.command);
                    }

                    current.Use();
                    break;

                case KeyCode.Escape:
                    _input = string.Empty;
                    _suggestedFor = null;
                    _selectedIndex = 0;
                    current.Use();
                    Repaint();
                    break;
            }
        }

        private void ExecuteInput(bool force)
        {
            string line = _input?.Trim();

            if (string.IsNullOrEmpty(line))
            {
                ShowNotification(new GUIContent("先输入一条命令"));
                return;
            }

            if (!force && IsHighRisk(line))
            {
                if (!EditorUtility.DisplayDialog("确认执行高危 GM 命令", line + "\n\n这条命令被标记为高危（RevGMFlags.HighRisk）。确定执行？",
                                                 "执行", "取消"))
                    return;
            }

            RevGMResult result = RevGMEditorCatalog.Execute(line);

            _lastResult = result;
            _lastExecuted = line;

            _history.Insert(0, new HistoryItem { Text = line, Result = result, Time = DateTime.Now.ToString("HH:mm:ss") });
            if (_history.Count > MaxHistory) _history.RemoveAt(_history.Count - 1);

            Repaint();
        }

        private static bool IsHighRisk(string line)
        {
            // ★ 必须用与解析器/联想同一套分隔符（半角空格 / Tab / 中文全角空格）：
            //   只认半角空格时，用 Tab 或全角空格把命令名与参数分开就能绕过二次确认 ——
            //   高危命令被直接执行，而"高危"这道唯一的提醒形同虚设。
            int space = line.IndexOfAny(ArgSeparators);
            string name = space < 0 ? line : line.Substring(0, space);

            IReadOnlyList<RevGMCommand> commands = RevGMEditorCatalog.Commands;

            // 完整名优先
            for (int i = 0; i < commands.Count; i++)
                if (string.Equals(commands[i].Name, name, StringComparison.OrdinalIgnoreCase))
                    return commands[i].IsHighRisk;

            // 退化为"末段名唯一命中"
            for (int i = 0; i < commands.Count; i++)
                if (string.Equals(commands[i].BaseName, name, StringComparison.OrdinalIgnoreCase))
                    return commands[i].IsHighRisk;

            return false;
        }

        private void FillInput(string text)
        {
            _input = text;
            _suggestedFor = null;
            _selectedIndex = 0;
            _focusSearch = true;
            Repaint();
        }

        // ============================================================
        // 小工具
        // ============================================================

        private void BuildGroups(IReadOnlyList<RevGMCommand> commands)
        {
            // 每次重绘重建分组（命令数是几十~几百条，成本可忽略；少一份状态就少一处不一致）
            _groups.Clear();

            for (int i = 0; i < commands.Count; i++)
            {
                RevGMCommand command = commands[i];
                if (!_groups.TryGetValue(command.Group, out List<RevGMCommand> list))
                {
                    list = new List<RevGMCommand>(8);
                    _groups[command.Group] = list;
                }

                list.Add(command);
            }
        }

        private static string ArgsHint(RevGMCommand command)
        {
            if (command.Args.Length == 0) return string.Empty;

            StringBuilder builder = new StringBuilder(24);
            for (int i = 0; i < command.Args.Length; i++)
            {
                if (i > 0) builder.Append(' ');
                builder.Append('<').Append(command.Args[i].Name).Append('>');
            }

            return builder.Append("   ").ToString();
        }

        private static string Highlight(string text, string query)
        {
            if (string.IsNullOrEmpty(query) || !RevGMMatcher.TryMatch(text, query, out int[] positions) || positions == null)
                return text;

            StringBuilder builder = new StringBuilder(text.Length + positions.Length * 26);
            int current = 0;

            for (int i = 0; i < text.Length; i++)
            {
                if (current < positions.Length && positions[current] == i)
                {
                    builder.Append("<b><color=#FFB300>").Append(text[i]).Append("</color></b>");
                    current++;
                }
                else
                {
                    builder.Append(text[i]);
                }
            }

            return builder.ToString();
        }

        private static string EmptyHint()
        {
            if (RevGMEditorCatalog.IsPlaying)
                return "还没有注册任何 GM 命令。\n在启动期用一行注册：\nRevGM.Register(\"经济/加金币\", \"给玩家加金币\", args => AddGold(args.Int(0)), RevGMArg.Int(\"数量\", 1000));";

            return "编辑模式下暂无命令清单。\n给注册方法加 [RevGMEntry]（方法里只做 RevGM.Register），面板就能在编辑期读到命令；\n或者按 Play 进游戏后回来看（那时读的是运行期真实注册表）。";
        }

        private static void ShowHelp()
        {
            EditorUtility.DisplayDialog(
                "GM 指令面板 · 用法",
                "三步用法：\n" +
                "① 输入框打关键词（前缀 / 连续子串 / 字符缩写都能命中）→ 左栏出搜索结果，点一条自动填入\n" +
                "② 需要参数就在输入框接着打；右侧同步显示说明，枚举参数点候选值即可\n" +
                "③ 点「执行」或回车；结果与耗时显示在输入框下方\n\n" +
                "更多：\n" +
                "· 左栏也能按分组浏览（命令名里的 / 自动分组），点一条即填入\n" +
                "· 高危命令执行前会二次确认（Ctrl+回车跳过）；Esc 清空输入\n" +
                "· 底部「历史」可展开，点条目重新填入，「重新执行」一键重跑上一条\n\n" +
                "编辑模式只查看与联想；进 Play 后才能真的执行（那时被测环境就是游戏本身）。",
                "知道了");
        }
    }
}
