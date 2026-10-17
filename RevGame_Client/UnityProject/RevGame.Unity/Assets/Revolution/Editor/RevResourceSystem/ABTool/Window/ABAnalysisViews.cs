// ============================================================
// ABAnalysisViews.cs —— 三个"检查类"页签：依赖 / 体积 / 检查
//
// 位置：Editor\RevResourceSystem\ABTool\Window\
//
// 【为什么三个放一个文件】共用同一份只读扫描（ABCollectCache）、同一个依赖分析（ABDependencyCache）、
//   同一套小件（ABGUI）—— 拆开只会把公共部分复制三遍。
//
// 【和以前比，改了什么（易用性）】
//   · 不再"只看不能改"：包名点一下 = 跳到「分包」页签选中它；
//     "被多个包共享又没分包"的资源可以**一键移进一个共享包**；空包名一键清理；漏标资源一键在 Project 里全选；
//   · 依赖分析不在绘制里同步算（大工程会卡住窗口）：先显示"正在分析"，下一帧带可取消进度条算完自动刷新；
//   · 每一节都能折叠、长列表只先显示前 N 条，不会一屏塞满。
// ============================================================
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Revolution.Editor
{
    /// <summary>检查类视图的公共部分</summary>
    internal abstract class ABAnalysisView
    {
        /// <summary>长列表先显示多少条</summary>
        protected const int PageSize = 20;

        private readonly HashSet<string> _expanded = new HashSet<string>();

        public abstract void Draw(float availableHeight);

        /// <summary>顶栏：刷新 + 标题 + 一句摘要；返回本帧的扫描结果</summary>
        protected static ABCollectResult DrawTitle(string title, string summary = null)
        {
            ABCollectResult collect = ABCollectCache.Get();

            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                if (GUILayout.Button(new GUIContent(" 刷新", ABGUI.RefreshIcon, "立刻重新扫描（依赖也会重算）"),
                        EditorStyles.toolbarButton, GUILayout.Width(58)))
                {
                    ABDependencyCache.ClearCancelled();
                    ABMarkerWatcher.RefreshNow("手动刷新");
                }

                GUILayout.Label(title, EditorStyles.boldLabel, GUILayout.Width(80));
                if (summary != null) GUILayout.Label(summary, EditorStyles.miniLabel);
                GUILayout.FlexibleSpace();
            }

            return collect;
        }

        /// <summary>
        /// 取依赖分析结果；还没算好就显示"正在分析 / 已取消"并返回 null（调用方直接 return）。
        /// </summary>
        protected static ABDependencyReport DependencyOrPlaceholder(ABCollectResult collect)
        {
            ABDependencyReport report = ABDependencyCache.GetOrSchedule(collect);
            if (report != null) return report;

            if (ABDependencyCache.WasCancelled(collect))
            {
                EditorGUILayout.HelpBox("依赖分析已取消。", MessageType.Info);
                if (GUILayout.Button("重新分析", GUILayout.Width(90)))
                {
                    ABDependencyCache.ClearCancelled();
                    ABGUI.Defer(() => ABDependencyCache.Get(ABCollectCache.Get()));
                }
            }
            else
            {
                EditorGUILayout.HelpBox("正在分析依赖（资源多时会弹出可取消的进度条）…", MessageType.Info);
            }

            return null;
        }

        /// <summary>包名做成可点的链接：点了跳到「分包」页签并选中它</summary>
        protected static void BundleLink(string bundle, params GUILayoutOption[] options)
        {
            if (GUILayout.Button(new GUIContent(bundle, "在「分包」页签里选中这个包"), EditorStyles.linkLabel, options))
                ABBuildWindow.RevealBundle(bundle);

            EditorGUIUtility.AddCursorRect(GUILayoutUtility.GetLastRect(), MouseCursor.Link);
        }

        /// <summary>一行：[定位] + 文字</summary>
        protected static void RowWithPing(string assetPath, string text)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("定位", EditorStyles.miniButton, GUILayout.Width(38))) ABGUI.Ping(assetPath);
                EditorGUILayout.LabelField(new GUIContent(text, assetPath), EditorStyles.miniLabel);
            }
        }

        /// <summary>长列表分页：先显示 PageSize 条，下面给"显示全部 / 收起"</summary>
        protected int VisibleCount(string key, int total)
            => _expanded.Contains(key) ? total : Math.Min(PageSize, total);

        protected void MoreToggle(string key, int total)
        {
            if (total <= PageSize) return;

            bool all = _expanded.Contains(key);
            if (GUILayout.Button(all ? "收起" : $"显示全部 {total} 条（还有 {total - PageSize} 条）",
                    EditorStyles.miniButton, GUILayout.Width(200)))
            {
                if (all) _expanded.Remove(key);
                else _expanded.Add(key);
            }
        }

        protected static List<KeyValuePair<string, T>> Sorted<T>(Dictionary<string, T> map)
        {
            var list = new List<KeyValuePair<string, T>>(map);
            list.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
            return list;
        }

        /// <summary>
        /// 把"被多个包共享、又没分包"的资源移进一个共享包（先问包名）。
        /// ★ 这是 AssetBundle Browser 里"把共享依赖拎成单独的包"的同款操作：
        ///   它们从此只打一份，引用它的包改成依赖这个共享包。
        /// </summary>
        protected static void MoveSharedIntoBundle(ICollection<string> assetPaths)
        {
            if (assetPaths.Count == 0) return;

            ABGUI.Defer(() =>
            {
                string name = ABNamePrompt.Show("移进共享包",
                    $"把 {assetPaths.Count} 个共享资源移进哪个包？（已有的包名 = 加进去；没有 = 新建）", "shared");
                if (name == null) return;

                name = ABBundleEditor.Normalize(name);
                if (!ABBundleEditor.IsLegalBundleName(name, out string why))
                {
                    EditorUtility.DisplayDialog("包名不合法", $"「{name}」：{why}", "好");
                    return;
                }

                int added = ABBundleEditor.AddAssets(name, assetPaths, out _, out _);
                RevABLog.Info($"[RevAB] 已把 {added} 个共享资源移进包「{name}」");
                ABMarkerWatcher.RefreshNow("移进共享包");
            });
        }
    }

    // ============================================================
    // ① 依赖
    // ============================================================
    /// <summary>包 → 包 的依赖关系、会被复制多份的资源、循环依赖</summary>
    internal sealed class ABDependencyView : ABAnalysisView
    {
        private Vector2 _scroll;
        private string _filter = "";

        public override void Draw(float availableHeight)
        {
            ABCollectResult collect = ABCollectCache.Get();
            ABDependencyReport report = ABDependencyCache.Peek(collect);

            int edges = 0;
            if (report != null) foreach (var pair in report.directDeps) edges += pair.Value.Length;

            DrawTitle("依赖", report == null ? null
                : $"{report.directDeps.Count} 个包 · {edges} 条包间依赖 · {report.sharedAssets.Count} 个会被复制多份的资源" +
                  "（编辑器侧估算，不用先打包）");

            report = DependencyOrPlaceholder(collect);
            if (report == null) return;

            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            // ---------- ① 循环依赖：最严重，放最前面 ----------
            if (report.circularBundles.Count > 0)
                EditorGUILayout.HelpBox("发现循环依赖（加载顺序无解，必须拆开）：" + string.Join("、", report.circularBundles),
                    MessageType.Error);
            else if (edges == 0 && report.sharedAssets.Count == 0)
                EditorGUILayout.HelpBox("没有任何包间依赖、也没有会被复制多份的资源 —— 每个包都能独立加载。", MessageType.Info);

            // ---------- ② 会被复制多份的资源：体积膨胀的真正来源（有操作，放前面） ----------
            DrawShared(report);

            // ---------- ③ 包依赖 ----------
            if (ABGUI.Foldout("dep.edges", "包 → 它依赖的包", true, $"{edges} 条"))
            {
                _filter = EditorGUILayout.TextField(new GUIContent("过滤包名"), _filter, EditorStyles.toolbarSearchField);

                int shown = 0;
                foreach (var pair in Sorted(report.directDeps))
                {
                    if (_filter.Length > 0 && pair.Key.IndexOf(_filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    shown++;

                    using (new EditorGUILayout.HorizontalScope())
                    {
                        BundleLink(pair.Key, GUILayout.Width(200));
                        EditorGUILayout.LabelField(
                            pair.Value.Length == 0 ? "—（不依赖别的包）" : string.Join("、", pair.Value),
                            EditorStyles.wordWrappedMiniLabel);
                    }
                }

                if (shown == 0) EditorGUILayout.LabelField("（没有匹配的包）", EditorStyles.miniLabel);
            }

            EditorGUILayout.EndScrollView();
        }

        private void DrawShared(ABDependencyReport report)
        {
            if (report.sharedAssets.Count == 0) return;

            long wasted = 0;
            List<KeyValuePair<string, List<string>>> shared = Sorted(report.sharedAssets);
            foreach (var pair in shared) wasted += ABSizeCache.OfAsset(pair.Key) * (pair.Value.Count - 1);

            if (!ABGUI.Foldout("dep.shared", "被多个包引用、又没分包（会被复制多份）", true,
                    $"{shared.Count} 个 · 多占约 {ABGUI.FormatSize(wasted)}")) return;

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.HelpBox("这些资源会被复制进每一个引用它的包。把它们单独打成一个共享包通常是最省体积的一步。",
                    MessageType.Warning);

                if (GUILayout.Button("全部移进共享包…", GUILayout.Width(120), GUILayout.Height(38)))
                    MoveSharedIntoBundle(report.sharedAssets.Keys);
            }

            int visible = VisibleCount("dep.shared", shared.Count);
            for (int i = 0; i < visible; i++)
            {
                var pair = shared[i];
                long size = ABSizeCache.OfAsset(pair.Key);
                RowWithPing(pair.Key,
                    $"{ABGUI.Short(pair.Key)}    {ABGUI.FormatSize(size)} × {pair.Value.Count} 份    被 {string.Join("、", pair.Value)} 引用");
            }
            MoreToggle("dep.shared", shared.Count);

            EditorGUILayout.Space(6);
        }
    }

    // ============================================================
    // ② 体积
    // ============================================================
    /// <summary>每个包多大、含依赖多大，以及最大的那些资源</summary>
    internal sealed class ABSizeView : ABAnalysisView
    {
        private struct Row
        {
            public string bundle;
            public int count;
            public long own;          // 包内资源自身
            public long withDeps;     // 自身 + 会被复制进来的未分包资源
        }

        private Vector2 _scroll;
        private bool _bySize = true;

        // 按扫描代数缓存排好序的结果（以前每帧都重算 + 重新 stat 每个文件）
        private int _version = -1;
        private bool _builtWithDeps;
        private readonly List<Row> _rows = new List<Row>();
        private readonly List<KeyValuePair<string, long>> _largest = new List<KeyValuePair<string, long>>();
        private long _total, _max;

        public override void Draw(float availableHeight)
        {
            ABCollectResult collect = ABCollectCache.Get();
            ABDependencyReport report = ABDependencyCache.GetOrSchedule(collect);     // 没算好先显示"自身体积"
            Rebuild(collect, report);

            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                if (GUILayout.Button(new GUIContent(" 刷新", ABGUI.RefreshIcon), EditorStyles.toolbarButton, GUILayout.Width(58)))
                    ABMarkerWatcher.RefreshNow("手动刷新");

                GUILayout.Label("体积", EditorStyles.boldLabel, GUILayout.Width(80));
                GUILayout.Label($"{_rows.Count} 个包 · 合计约 {ABGUI.FormatSize(_total)}（源文件体积，不是压缩后的 AB 体积）",
                    EditorStyles.miniLabel);
                GUILayout.FlexibleSpace();

                if (GUILayout.Toggle(_bySize, "按体积", EditorStyles.toolbarButton, GUILayout.Width(56)) && !_bySize)
                {
                    _bySize = true;
                    _version = -1;
                }

                if (GUILayout.Toggle(!_bySize, "按名字", EditorStyles.toolbarButton, GUILayout.Width(56)) && _bySize)
                {
                    _bySize = false;
                    _version = -1;
                }
            }

            EditorGUILayout.LabelField(report == null
                ? "深色 = 包自身；依赖还在分析，分析完会补上浅色部分（含会被复制进来的未分包资源）"
                : "深色 = 包自身，浅色 = 含会被复制进来的未分包资源 · 点包名跳到「分包」页签", EditorStyles.miniLabel);

            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            if (_rows.Count == 0) EditorGUILayout.LabelField("（还没有任何包）", EditorStyles.miniLabel);

            foreach (Row row in _rows) DrawBar(row);

            EditorGUILayout.Space(10);
            if (ABGUI.Foldout("size.largest", "最大的 20 个资源"))
            {
                if (_largest.Count == 0) EditorGUILayout.LabelField("（还没有任何资源）", EditorStyles.miniLabel);
                foreach (var pair in _largest)
                    RowWithPing(pair.Key, $"{ABGUI.FormatSize(pair.Value)}    {ABGUI.Short(pair.Key)}");
            }

            EditorGUILayout.EndScrollView();
        }

        private void DrawBar(Row row)
        {
            Rect line = EditorGUILayout.GetControlRect(false, 22f);

            var nameRect = new Rect(line.x, line.y + 2f, 180f, 18f);
            if (GUI.Button(nameRect, new GUIContent(row.bundle, "在「分包」页签里选中这个包"), EditorStyles.linkLabel))
                ABBuildWindow.RevealBundle(row.bundle);
            EditorGUIUtility.AddCursorRect(nameRect, MouseCursor.Link);

            float barWidth = Mathf.Max(40f, line.width - 182f - 250f);
            var track = new Rect(line.x + 182f, line.y + 4f, barWidth, 14f);
            EditorGUI.DrawRect(track, new Color(0.5f, 0.5f, 0.5f, 0.12f));

            float kWith = Mathf.Clamp01(row.withDeps / (float)_max);
            float kOwn = Mathf.Clamp01(row.own / (float)_max);
            EditorGUI.DrawRect(new Rect(track.x, track.y, track.width * kWith, track.height), new Color(0.35f, 0.60f, 1f, 0.35f));
            EditorGUI.DrawRect(new Rect(track.x, track.y, track.width * kOwn, track.height), new Color(0.35f, 0.60f, 1f, 0.85f));

            string text = row.withDeps > row.own
                ? $"{ABGUI.FormatSize(row.own)}   含依赖 {ABGUI.FormatSize(row.withDeps)}   {row.count} 个"
                : $"{ABGUI.FormatSize(row.own)}   {row.count} 个资源";
            GUI.Label(new Rect(line.xMax - 246f, line.y + 2f, 246f, 18f), text, EditorStyles.miniLabel);
        }

        private void Rebuild(ABCollectResult collect, ABDependencyReport report)
        {
            if (_version == ABCollectCache.Version && _builtWithDeps == (report != null)) return;
            _version = ABCollectCache.Version;
            _builtWithDeps = report != null;

            _rows.Clear();
            _total = 0;
            _max = 1;

            foreach (var pair in collect.bundleToAssets)
            {
                var row = new Row { bundle = pair.Key, count = pair.Value.Count, own = ABSizeCache.OfBundle(collect, pair.Key) };

                long extra = 0;
                if (report != null) report.extraBytes.TryGetValue(pair.Key, out extra);
                row.withDeps = row.own + extra;

                _rows.Add(row);
                _total += row.withDeps;
                _max = Math.Max(_max, row.withDeps);
            }

            _rows.Sort(_bySize
                ? (Comparison<Row>)((a, b) => b.withDeps.CompareTo(a.withDeps))
                : ((a, b) => string.CompareOrdinal(a.bundle, b.bundle)));

            _largest.Clear();
            foreach (var pair in collect.logicToAssetPath)
                _largest.Add(new KeyValuePair<string, long>(pair.Value, ABSizeCache.OfAsset(pair.Value)));
            _largest.Sort((a, b) => b.Value.CompareTo(a.Value));
            if (_largest.Count > 20) _largest.RemoveRange(20, _largest.Count - 20);
        }
    }

    // ============================================================
    // ③ 检查
    // ============================================================
    /// <summary>
    /// 问题检查：同包重名 / 逻辑路径冲突 / 空包 / 漏标 / 会被复制多份的资源。
    /// 这几类都是"编辑器里看不出、运行时才炸"的问题，集中在一页，每一条都能定位或一键处理。
    /// </summary>
    internal sealed class ABDuplicateView : ABAnalysisView
    {
        private Vector2 _scroll;
        private ABResMapCheckReport _mapReport;
        private bool _showAllMapIssues;

        // 同包重名的分组也按扫描代数缓存
        private int _version = -1;
        private readonly List<KeyValuePair<string, List<string>>> _sameName = new List<KeyValuePair<string, List<string>>>();

        public override void Draw(float availableHeight)
        {
            ABCollectResult collect = ABCollectCache.Get();
            Rebuild(collect);

            ABBuildConfig cfg = ABBuildConfig.Instance;
            int unmarked = cfg.checkUnmarkedAssets ? collect.unmarkedAssets.Count : 0;
            int blocking = _sameName.Count + collect.duplicateLogicDetail.Count + collect.emptyBundles.Count + unmarked;

            DrawTitle("检查", blocking == 0 ? "没有会阻止打包的问题" : $"{blocking} 个会阻止打包的问题（红色）");

            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            if (blocking == 0)
                EditorGUILayout.HelpBox("当前分包标记没有会阻止打包的问题。产物一致性需要在下方单独检查。", MessageType.Info);

            DrawResMapCheck();
            DrawUnmarked(collect, cfg);
            DrawSameName(collect);
            DrawLogicClash(collect);
            DrawEmpty(collect);
            DrawShared(collect);

            EditorGUILayout.EndScrollView();
        }

        /// <summary>磁盘上的表与产物不随标记缓存更新；只在用户点击时读取，结果标明平台和检查时间。</summary>
        private void DrawResMapCheck()
        {
            if (!ABGUI.Foldout("check.resmap", "ResMap 映射表 / AB 产物一致性（打包后检查）", true)) return;

            BuildTarget target = ABBuildSetting.ResolveBuildTarget();
            string outputDir = ABBuildSetting.GetOutputDir(target).Replace('\\', '/');
            EditorGUILayout.LabelField("映射表：" + ABBuildSetting.MapAssetPath, EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.LabelField("目标产物：" + outputDir + "（打包页的目标平台）", EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.HelpBox("只读检查：错误格式、重复逻辑名、映射指向不存在的包、与当前资源标记不一致。" +
                "不会改资源或自动修复；先打包再检查，改过分包后请重新生成映射 / 打包。", MessageType.None);

            if (GUILayout.Button("检查映射表与该平台 AB", GUILayout.Width(200)))
            {
                // 读取工程标记 / 磁盘在用户主动点击后进行；不在每帧 OnGUI 内扫描产物。
                ABGUI.Defer(() =>
                {
                    _mapReport = ABResMapChecker.Check(target);
                    _showAllMapIssues = false;
                    EditorWindow.GetWindow<ABBuildWindow>().Repaint();
                });
            }

            if (_mapReport == null) return;
            if (_mapReport.Target != target || _mapReport.MarkerVersion != ABCollectCache.Version)
                EditorGUILayout.HelpBox("平台或当前资源标记已变化，下面是旧结果；请重新检查。", MessageType.Warning);

            EditorGUILayout.HelpBox($"{_mapReport.Platform} · {_mapReport.CheckedAt:HH:mm:ss} · {_mapReport.EntryCount} 条映射 · " +
                $"{_mapReport.Errors.Count} 个错误 / {_mapReport.Warnings.Count} 个提醒" +
                (_mapReport.Errors.Count == 0 ? "（仅说明已执行所列检查，不代表包内容一定正确）" : ""),
                _mapReport.Errors.Count > 0 ? MessageType.Error : MessageType.Info);

            int limit = _showAllMapIssues ? int.MaxValue : PageSize;
            DrawMapIssues(_mapReport.Errors, MessageType.Error, limit);
            DrawMapIssues(_mapReport.Warnings, MessageType.Warning, limit);
            if (_mapReport.Errors.Count > limit || _mapReport.Warnings.Count > limit || _showAllMapIssues)
                _showAllMapIssues = GUILayout.Toggle(_showAllMapIssues, "显示全部", EditorStyles.miniButton, GUILayout.Width(80));
            EditorGUILayout.Space(8);
        }

        private static void DrawMapIssues(List<string> issues, MessageType type, int limit)
        {
            for (int i = 0; i < Math.Min(issues.Count, limit); i++) EditorGUILayout.HelpBox(issues[i], type);
            if (issues.Count > limit) EditorGUILayout.LabelField($"…还有 {issues.Count - limit} 条", EditorStyles.miniLabel);
        }

        private void Rebuild(ABCollectResult collect)
        {
            if (_version == ABCollectCache.Version) return;
            _version = ABCollectCache.Version;
            _sameName.Clear();

            // 同包内重名：按 (包, 资源名) 分组，组内 ≥ 2 就是重名
            var groups = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (ResMapEntry entry in collect.entries)
            {
                string key = entry.bundle + " → " + entry.asset;
                if (!groups.TryGetValue(key, out List<string> logics)) groups[key] = logics = new List<string>();
                logics.Add(entry.logic);
            }

            foreach (var pair in groups)
                if (pair.Value.Count >= 2) _sameName.Add(pair);
        }

        // ---------------- ① 漏标（最常见，放最前） ----------------
        private void DrawUnmarked(ABCollectResult collect, ABBuildConfig cfg)
        {
            List<string> list = collect.unmarkedAssets;
            if (list.Count == 0) return;

            string badge = cfg.checkUnmarkedAssets ? $"✖ {list.Count} 个" : $"{list.Count} 个（已关闭检查）";
            if (!ABGUI.Foldout("check.unmarked", "没有 AB 标记的资源（不会进包）", true, badge)) return;

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.HelpBox("资源根目录下却没被任何包收走：编辑器直读照样能读到，一出真机就是加载失败。\n" +
                                        "给它们（或它们的文件夹）分包；确实不该进包的，到「打包」页签的「排除的目录名 / 后缀」里排除。",
                    cfg.checkUnmarkedAssets ? MessageType.Error : MessageType.Warning);

                using (new EditorGUILayout.VerticalScope(GUILayout.Width(120)))
                {
                    if (GUILayout.Button("全部加入一个包…")) AddUnmarkedToBundle(list);
                    if (GUILayout.Button("在 Project 里全选")) ABGUI.SelectInProject(list);
                }
            }

            int visible = VisibleCount("check.unmarked", list.Count);
            for (int i = 0; i < visible; i++) RowWithPing(list[i], "    " + ABGUI.Short(list[i]));
            MoreToggle("check.unmarked", list.Count);

            EditorGUILayout.Space(6);
        }

        private static void AddUnmarkedToBundle(List<string> assets)
        {
            var copy = new List<string>(assets);
            ABGUI.Defer(() =>
            {
                string name = ABNamePrompt.Show("加入包", $"把 {copy.Count} 个没有标记的资源加进哪个包？", "default");
                if (name == null) return;

                name = ABBundleEditor.Normalize(name);
                if (!ABBundleEditor.IsLegalBundleName(name, out string why))
                {
                    EditorUtility.DisplayDialog("包名不合法", $"「{name}」：{why}", "好");
                    return;
                }

                int added = ABBundleEditor.AddAssets(name, copy, out _, out _);
                RevABLog.Info($"[RevAB] 已把 {added} 个漏标资源加进包「{name}」");
                ABMarkerWatcher.RefreshNow("漏标资源加入包");
            });
        }

        // ---------------- ② 同包重名 ----------------
        private void DrawSameName(ABCollectResult collect)
        {
            if (_sameName.Count == 0) return;
            if (!ABGUI.Foldout("check.same", "同一个包里资源重名", true, $"✖ {_sameName.Count} 处")) return;

            EditorGUILayout.HelpBox("同一个包里两个同名资源 → LoadAsset(\"名字\") 取到哪个不确定：改名，或拆到不同的包。", MessageType.Error);

            foreach (var group in _sameName)
            {
                string bundle = group.Key.Substring(0, group.Key.IndexOf(" → ", StringComparison.Ordinal));
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(group.Key.Substring(bundle.Length + 3) + $"（{group.Value.Count} 个）", EditorStyles.boldLabel,
                        GUILayout.Width(220));
                    EditorGUILayout.LabelField("在包", EditorStyles.miniLabel, GUILayout.Width(28));
                    BundleLink(bundle);
                }

                foreach (string logic in group.Value)
                    if (collect.logicToAssetPath.TryGetValue(logic, out string assetPath))
                        RowWithPing(assetPath, "    " + ABGUI.Short(assetPath));
            }

            EditorGUILayout.Space(6);
        }

        // ---------------- ③ 逻辑路径冲突 ----------------
        private static void DrawLogicClash(ABCollectResult collect)
        {
            if (collect.duplicateLogicDetail.Count == 0) return;
            if (!ABGUI.Foldout("check.logic", "逻辑路径冲突（同名不同扩展名）", true, $"✖ {collect.duplicateLogicDetail.Count} 处")) return;

            EditorGUILayout.HelpBox("逻辑路径 = 相对资源根目录的路径去掉扩展名：Hero/1001.prefab 和 Hero/1001.png 会撞成同一个名字，其中一个会加载不到。",
                MessageType.Error);

            foreach (var pair in Sorted(collect.duplicateLogicDetail))
            {
                EditorGUILayout.LabelField($"「{pair.Key}」被这几个文件撞了：", EditorStyles.boldLabel);
                foreach (string assetPath in pair.Value) RowWithPing(assetPath, "    " + ABGUI.Short(assetPath));
            }

            EditorGUILayout.Space(6);
        }

        // ---------------- ④ 空包 ----------------
        private static void DrawEmpty(ABCollectResult collect)
        {
            if (collect.emptyBundles.Count == 0) return;
            if (!ABGUI.Foldout("check.empty", "空包（包名还在，但没有任何资源）", true, $"✖ {collect.emptyBundles.Count} 个")) return;

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.HelpBox("通常是资源被删 / 改名后留下的旧包名：" + string.Join("、", collect.emptyBundles), MessageType.Error);

                if (GUILayout.Button("一键清理", GUILayout.Width(80), GUILayout.Height(38)))
                {
                    int removed = ABBundleEditor.RemoveUnusedNames();
                    RevABLog.Info($"[RevAB] 已清理 {removed} 个未使用的包名");
                    ABMarkerWatcher.RefreshNow("清理未使用包名");
                }
            }

            EditorGUILayout.Space(6);
        }

        // ---------------- ⑤ 会被复制多份（不阻止打包，只提醒） ----------------
        private void DrawShared(ABCollectResult collect)
        {
            ABDependencyReport report = ABDependencyCache.GetOrSchedule(collect);
            if (report == null || report.sharedAssets.Count == 0) return;

            if (!ABGUI.Foldout("check.shared", "被多个包引用、又没分包（会被复制多份）", false,
                    $"⚠ {report.sharedAssets.Count} 个")) return;

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.HelpBox("不会阻止打包，但同一份内容会在多个包里各存一份。", MessageType.Warning);
                if (GUILayout.Button("全部移进共享包…", GUILayout.Width(120), GUILayout.Height(38)))
                    MoveSharedIntoBundle(report.sharedAssets.Keys);
            }

            List<KeyValuePair<string, List<string>>> shared = Sorted(report.sharedAssets);
            int visible = VisibleCount("check.shared", shared.Count);
            for (int i = 0; i < visible; i++)
                RowWithPing(shared[i].Key, $"    {ABGUI.Short(shared[i].Key)}    被 {string.Join("、", shared[i].Value)} 引用");
            MoreToggle("check.shared", shared.Count);
        }
    }

    /// <summary>点击按钮那一刻的只读快照；与打包前的 ABValidator 报告互不混淆。</summary>
    internal sealed class ABResMapCheckReport
    {
        public BuildTarget Target;
        public string Platform;
        public DateTime CheckedAt;
        public int MarkerVersion;
        public int EntryCount;
        public readonly List<string> Errors = new List<string>();
        public readonly List<string> Warnings = new List<string>();
    }

    /// <summary>
    /// 比对运行时实际读取的 Resources/ResMap.txt、当前资源标记和选定平台的磁盘 AB。
    /// 只做诊断：不生成映射、不修改标记、不加载 AB（加载 AB 会占内存且可能要求先释放）。
    /// </summary>
    internal static class ABResMapChecker
    {
        public static ABResMapCheckReport Check(BuildTarget target)
        {
            ABCollectResult collect = ABCollectCache.Get();
            string outputDir = ABBuildSetting.GetOutputDir(target);
            var report = new ABResMapCheckReport
            {
                Target = target,
                Platform = ABBuildSetting.GetPlatformName(target),
                CheckedAt = DateTime.Now,
                MarkerVersion = ABCollectCache.Version
            };

            try
            {
                if (!File.Exists(ABBuildSetting.MapAssetPath))
                {
                    report.Errors.Add("映射表不存在：" + ABBuildSetting.MapAssetPath + "。请在「打包」页点击「仅生成映射」或「打包」。");
                    return report;
                }

                // 大小写严格匹配：安卓 / WebGL 的远端路径区分大小写，Windows 的 File.Exists 不区分。
                HashSet<string> diskBundles = null;
                if (!Directory.Exists(outputDir))
                    report.Errors.Add("该平台还没有 AB 产物：" + outputDir + "。请先打包，再检查表里的包是否真的存在。");
                else
                {
                    diskBundles = new HashSet<string>(StringComparer.Ordinal);
                    foreach (string path in Directory.GetFiles(outputDir)) diskBundles.Add(Path.GetFileName(path));
                }

                var expected = new Dictionary<string, ResMapEntry>(StringComparer.Ordinal);
                foreach (ResMapEntry entry in collect.entries) expected[entry.logic] = entry;
                var seen = new HashSet<string>(StringComparer.Ordinal);
                var checkedBundles = new HashSet<string>(StringComparer.Ordinal);
                var seenAssets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                string[] lines = File.ReadAllLines(ABBuildSetting.MapAssetPath);
                for (int i = 0; i < lines.Length; i++)
                {
                    // 与 RevResBootstrap.LoadResMap 一致：空行和 # 注释跳过；其他行必须是三列。
                    string line = lines[i].Trim().TrimStart('\uFEFF');
                    if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
                    int number = i + 1;
                    string[] parts = line.Split('|');
                    if (parts.Length != 3 || string.IsNullOrWhiteSpace(parts[0]) ||
                        string.IsNullOrWhiteSpace(parts[1]) || string.IsNullOrWhiteSpace(parts[2]) ||
                        parts[0] != parts[0].Trim() || parts[1] != parts[1].Trim() || parts[2] != parts[2].Trim())
                    {
                        report.Errors.Add($"第 {number} 行格式错误：需要「逻辑名|包名|资源名」三列，字段不能为空或带首尾空格。");
                        continue;
                    }

                    report.EntryCount++;
                    string logic = parts[0], bundle = parts[1], asset = parts[2];
                    if (!seen.Add(logic))
                        report.Errors.Add($"第 {number} 行逻辑名重复：{logic}（运行时后面的记录会悄悄覆盖前面的）。");
                    if (!seenAssets.Add(bundle + "|" + asset))
                        report.Errors.Add($"第 {number} 行包内资源名重复：{bundle}|{asset}；LoadAsset 无法区分同名资源。");
                    if (diskBundles != null && checkedBundles.Add(bundle) && !diskBundles.Contains(bundle))
                        report.Errors.Add($"第 {number} 行指向的 AB 文件不存在：{outputDir}/{bundle}（请确认平台及大小写）。");

                    if (!expected.TryGetValue(logic, out ResMapEntry current))
                        report.Warnings.Add($"第 {number} 行的 {logic} 在当前资源标记中找不到；可能是资源已删、改名或映射未重新生成。");
                    else if (current.bundle != bundle || current.asset != asset)
                        report.Warnings.Add($"第 {number} 行 {logic} 与当前标记不一致：表={bundle}|{asset}，标记={current.bundle}|{current.asset}；请重新生成映射并打包。");
                }

                foreach (ResMapEntry entry in collect.entries)
                    if (!seen.Contains(entry.logic))
                        report.Warnings.Add($"当前资源 {entry.logic}（{entry.bundle}|{entry.asset}）不在映射表中；新增资源可能无法在 AB 模式下加载。");

                if (report.EntryCount == 0)
                    report.Errors.Add("映射表没有有效条目；请先在「打包」页生成映射。");
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                report.Errors.Add("读取映射表或产物目录失败：" + e.Message);
            }

            return report;
        }
    }

    /// <summary>
    /// 一个极小的"输入名字"模态框（EditorUtility 没有现成的输入框）。
    /// ★ 用 ShowModalUtility：调用方拿到返回值再往下走，写法和 DisplayDialog 一样顺。
    /// </summary>
    internal sealed class ABNamePrompt : EditorWindow
    {
        private string _message;
        private string _value;
        private bool _ok;
        private bool _focused;

        /// <summary>弹框输入名字；取消返回 null</summary>
        public static string Show(string title, string message, string defaultValue)
        {
            var win = CreateInstance<ABNamePrompt>();
            win.titleContent = new GUIContent(title);
            win._message = message;
            win._value = defaultValue ?? "";

            var size = new Vector2(380, 110);
            Rect main = EditorGUIUtility.GetMainWindowPosition();
            win.position = new Rect(main.center - size / 2f, size);
            win.minSize = win.maxSize = size;

            win.ShowModalUtility();
            return win._ok ? win._value : null;
        }

        private void OnGUI()
        {
            Event e = Event.current;
            if (e.type == EventType.KeyDown && (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter)) Confirm();
            if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape) Close();

            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField(_message, EditorStyles.wordWrappedLabel);

            GUI.SetNextControlName("RevAB.NameField");
            _value = EditorGUILayout.TextField(_value);
            if (!_focused) { EditorGUI.FocusTextInControl("RevAB.NameField"); _focused = true; }

            EditorGUILayout.Space(4);
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("确定", GUILayout.Width(80))) Confirm();
                if (GUILayout.Button("取消", GUILayout.Width(80))) Close();
            }
        }

        private void Confirm()
        {
            _ok = !string.IsNullOrWhiteSpace(_value);
            Close();
        }
    }
}
