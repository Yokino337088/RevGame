// ============================================================
// ABSnapshotView.cs —— 「快照」页签：分包布局的前后对比
//
// 位置：Editor\RevResourceSystem\ABTool\Window\
//
// 【这个页签回答的问题】
//   "我这一通改，到底动了什么？" —— 现状（分包浏览）看得见，但看不出**变化**。
//
// 【两种对比】
//   · 与记录基准比：点「记录当前布局」把现在存下来当基准，之后随便改，再点「与记录的布局对比」；
//     → 适合"我准备开始调分包，先把现在存一份"。
//   · 与上次打包比：每次「仅生成映射」/「打包」成功后自动留底（见 ABLayoutSnapshotStore），
//     所以"上次出包到现在多/少了什么"是零操作的。
//
// 【报告怎么读（按重要程度排）】
//   ① 概览：包数 / 资源数 / 体积 各涨跌多少；
//   ② 改名：内容完全一致、只是包名变了 —— 单独列出来，否则会被误读成"删一个 + 加一个"；
//   ③ 新增 / 删除的包；
//   ④ 资源换包（A 包 → B 包）；
//   ⑤ 变动明细：每个包资源的增删 + 体积变化（变化大的排前面）。
//
// 【数据从哪来】
//   当前布局 = ABCollectCache 的同一份只读扫描（和另外四个页签共用，标记一变就失效）；
//   ★ 快照只在"缓存换代"时重建一次（字节数要读文件大小，不能每帧算）。
// ============================================================
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Revolution.Editor
{
    /// <summary>快照页签（记录 / 对比 / 看报告）</summary>
    internal sealed class ABSnapshotView
    {
        /// <summary>报告里每节最多列多少条（再多只给个"还有 N 条"，避免一屏塞满）</summary>
        private const int MaxRowsPerSection = 12;

        /// <summary>变动明细里每个包最多列几条资源</summary>
        private const int MaxAssetsPerBundle = 6;

        private Vector2 _scroll;

        /// <summary>当前布局快照（跟着扫描缓存换代，别每帧重建：要读每个文件的字节数）</summary>
        private ABCollectResult _forCollect;
        private ABLayoutSnapshot _current;

        private ABLayoutDiff _diff;
        private string _diffTitle = "";
        private int _diffRevision = -1;
        private string _status = "";

        public void Draw(float availableHeight)
        {
            ABCollectResult collect = ABCollectCache.Get();
            ABLayoutSnapshot current = Current(collect);

            DrawTitle(current);
            DrawButtons(collect, current);
            DrawReport(availableHeight);
        }

        /// <summary>当前布局：同一份扫描只算一次（与 ABDependencyCache 同一个套路）</summary>
        private ABLayoutSnapshot Current(ABCollectResult collect)
        {
            if (_current == null || !ReferenceEquals(_forCollect, collect))
            {
                _forCollect = collect;
                _current = ABLayoutSnapshotStore.From(collect, "当前布局");
            }
            return _current;
        }

        // ==================== 顶栏 ====================

        private void DrawTitle(ABLayoutSnapshot current)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("刷新", EditorStyles.miniButton, GUILayout.Width(48)))
                    ABCollectCache.Invalidate();

                EditorGUILayout.LabelField("分包布局快照", EditorStyles.boldLabel, GUILayout.Width(120));

                EditorGUILayout.LabelField(
                    $"当前：{current.BundleCount} 个包 · {current.AssetCount} 个资源 · {FormatSize(current.TotalBytes)}",
                    EditorStyles.miniLabel);
            }

            EditorGUILayout.LabelField(
                "记录一份布局基准 → 之后随便改分包 → 再点对比，看清到底动了哪些包、哪些资源",
                EditorStyles.miniLabel);
        }

        private void DrawButtons(ABCollectResult collect, ABLayoutSnapshot current)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("记录当前布局（作对比基准）", GUILayout.Height(26)))
                {
                    ABLayoutSnapshotStore.Save(ABLayoutSnapshotStore.BaselinePath, current);

                    _diff = null;
                    _status = $"已记录基准：{FormatTime(current.timeUtc)}（{current.BundleCount} 个包 · {current.AssetCount} 个资源）";
                }

                using (new EditorGUI.DisabledScope(!ABLayoutSnapshotStore.HasBaseline))
                {
                    if (GUILayout.Button("与记录的布局对比", GUILayout.Height(26)))
                        DoCompare(ABLayoutSnapshotStore.Load(ABLayoutSnapshotStore.BaselinePath), "与记录基准对比");
                }

                using (new EditorGUI.DisabledScope(!ABLayoutSnapshotStore.HasLastBuild))
                {
                    if (GUILayout.Button("与上次打包对比", GUILayout.Height(26)))
                        DoCompare(ABLayoutSnapshotStore.Load(ABLayoutSnapshotStore.LastBuildPath), "与上次打包对比");
                }
            }

            // 两个基准各自的时间：不看这个，报告的"before"是谁就说不清了
            EditorGUILayout.LabelField(
                $"基准：{Stamp(ABLayoutSnapshotStore.BaselinePath)}      上次打包：{Stamp(ABLayoutSnapshotStore.LastBuildPath)}",
                EditorStyles.miniLabel);

            if (_status.Length > 0)
                EditorGUILayout.LabelField(_status, EditorStyles.miniLabel);
        }

        /// <summary>基准的记录时间（按文件修改时间缓存，不再每帧读两次 JSON）</summary>
        private static string Stamp(string path)
        {
            string stamp = ABLayoutSnapshotStore.StampOf(path);
            return stamp == null ? "（还没记录）" : FormatTime(stamp);
        }

        private void DoCompare(ABLayoutSnapshot baseline, string title)
        {
            if (baseline == null)
            {
                _diff = null;
                _status = "没有可用的基准快照 —— 先点「记录当前布局」";
                return;
            }

            _diff = ABLayoutDiffer.Compare(baseline, Current(ABCollectCache.Get()));
            _diffTitle = title;
            _diffRevision = ABMarkerWatcher.Revision;          // 记下"这份报告对应哪一刻"
            _status = "";
            _scroll = Vector2.zero;
        }

        // ==================== 报告 ====================

        private void DrawReport(float availableHeight)
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll, GUILayout.Height(availableHeight - 92f));

            if (_diff == null)
            {
                EditorGUILayout.HelpBox(
                    "还没有对比结果。\n\n" +
                    "· 「记录当前布局（作对比基准）」：把现在的分包存成基准，之后改完再点「与记录的布局对比」；\n" +
                    "· 「与上次打包对比」：用的是每次生成映射 / 打包时自动留底的那份（不用手动记录）。",
                    MessageType.Info);
                EditorGUILayout.EndScrollView();
                return;
            }

            EditorGUILayout.LabelField(
                $"{_diffTitle}    {FormatTime(_diff.beforeTime)} → {FormatTime(_diff.afterTime)}",
                EditorStyles.boldLabel);

            // 这份报告属于"点对比那一刻"：之后项目里又改过分包，就该重算（和打包页签同一套提示）
            if (ABMarkerWatcher.Revision != _diffRevision)
                EditorGUILayout.HelpBox(
                    "注意：这之后项目里的分包标记又变过，下面这份报告是旧数据 —— 再点一次对比。",
                    MessageType.Warning);

            // ---------- ① 概览 ----------
            EditorGUILayout.HelpBox(
                $"包　{_diff.bundlesBefore} → {_diff.bundlesAfter}（{Signed(_diff.BundleDelta)}）\n" +
                $"资源 {_diff.assetsBefore} → {_diff.assetsAfter}（{Signed(_diff.AssetDelta)}）\n" +
                $"体积 {FormatSize(_diff.bytesBefore)} → {FormatSize(_diff.bytesAfter)}（{SignedSize(_diff.ByteDelta)}）",
                MessageType.Info);

            if (_diff.IsEmpty)
            {
                EditorGUILayout.HelpBox("分包布局与基准完全一致：没有任何包或资源的增减。", MessageType.Info);
                EditorGUILayout.EndScrollView();
                return;
            }

            // ---------- ② 改名（先看：最容易被误读成"删一个 + 加一个"）----------
            if (_diff.renamed.Count > 0)
            {
                Section($"改名（内容一致，只有包名变了）：{_diff.renamed.Count} 个");
                for (int i = 0; i < _diff.renamed.Count; i++)
                {
                    ABBundleRename rename = _diff.renamed[i];
                    Row($"{rename.before}  →  {rename.after}", $"{rename.count} 个资源（内容未变）");
                }
                EditorGUILayout.Space(6);
            }

            // ---------- ③ 新增 / 删除的包 ----------
            if (_diff.added.Count > 0)
            {
                Section($"新增的包：{_diff.added.Count} 个");
                Rows(_diff.added, b => $"{b.name}", b => $"{b.count} 个资源 · {FormatSize(b.bytes)}");
                EditorGUILayout.Space(6);
            }

            if (_diff.removed.Count > 0)
            {
                Section($"删除的包：{_diff.removed.Count} 个");
                Rows(_diff.removed, b => $"{b.name}", b => $"{b.count} 个资源 · {FormatSize(b.bytes)}");
                EditorGUILayout.Space(6);
            }

            // ---------- ④ 资源换包 ----------
            if (_diff.moved.Count > 0)
            {
                Section($"资源换包：{_diff.moved.Count} 条");
                for (int i = 0; i < Math.Min(MaxRowsPerSection, _diff.moved.Count); i++)
                {
                    ABAssetMove move = _diff.moved[i];
                    Row(Short(move.asset), $"{move.from}  →  {move.to}");
                }
                More(_diff.moved.Count);
                EditorGUILayout.Space(6);
            }

            // ---------- ⑤ 变动明细（变化大的排前面）----------
            if (_diff.changed.Count > 0)
            {
                Section($"变动的包：{_diff.changed.Count} 个");

                for (int i = 0; i < _diff.changed.Count; i++)
                {
                    ABBundleDelta delta = _diff.changed[i];

                    Row(delta.name,
                        $"+{delta.added.Count} / -{delta.removed.Count}    " +
                        $"{FormatSize(delta.bytesBefore)} → {FormatSize(delta.bytesAfter)}（{SignedSize(delta.ByteDelta)}）");

                    ListAll(delta.added, "+ ");
                    ListAll(delta.removed, "- ");
                }
            }

            EditorGUILayout.EndScrollView();
        }

        private static void Section(string title)
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
        }

        private void Rows(List<ABLayoutBundle> list, Func<ABLayoutBundle, string> left,
            Func<ABLayoutBundle, string> right)
        {
            for (int i = 0; i < Math.Min(MaxRowsPerSection, list.Count); i++)
                Row(left(list[i]), right(list[i]));

            More(list.Count);
        }

        private void ListAll(List<string> assets, string prefix)
        {
            for (int i = 0; i < Math.Min(MaxAssetsPerBundle, assets.Count); i++)
                EditorGUILayout.LabelField("    " + prefix + Short(assets[i]), EditorStyles.miniLabel);

            if (assets.Count > MaxAssetsPerBundle)
                EditorGUILayout.LabelField($"    … 还有 {assets.Count - MaxAssetsPerBundle} 条", EditorStyles.miniLabel);
        }

        private static void More(int total)
        {
            if (total > MaxRowsPerSection)
                EditorGUILayout.LabelField($"… 还有 {total - MaxRowsPerSection} 条", EditorStyles.miniLabel);
        }

        private static void Row(string left, string right)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(new GUIContent(left, left), GUILayout.Width(260f));
                EditorGUILayout.LabelField(right, EditorStyles.miniLabel);
            }
        }

        // ==================== 小工具 ====================

        /// <summary>"Assets/GameRes/Hero/1001.prefab" → "GameRes/Hero/1001.prefab"（少一截，报告更短）</summary>
        private static string Short(string assetPath)
            => assetPath != null && assetPath.StartsWith("Assets/")
                ? assetPath.Substring("Assets/".Length)
                : assetPath;

        private static string Signed(int value) => value >= 0 ? "+" + value : value.ToString();

        private static string SignedSize(long bytes)
            => (bytes >= 0 ? "+" : "-") + FormatSize(Math.Abs(bytes));

        private static string FormatSize(long bytes)
        {
            if (bytes >= 1024L * 1024L * 1024L) return $"{bytes / 1024f / 1024f / 1024f:F2} GB";
            if (bytes >= 1024L * 1024L) return $"{bytes / 1024f / 1024f:F2} MB";
            return $"{bytes / 1024f:F1} KB";
        }

        /// <summary>ISO(UTC) → 本地时间文字（看不懂"Z"结尾的时间，直接转成本机时区）</summary>
        private static string FormatTime(string utc)
        {
            if (string.IsNullOrEmpty(utc)) return "—";

            if (!DateTime.TryParse(utc, null,
                    System.Globalization.DateTimeStyles.RoundtripKind, out DateTime time))
                return utc;

            return time.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        }
    }
}
