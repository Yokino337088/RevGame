using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using RevGame.Data;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace RevGame.Editor
{
    /// <summary>导出与导入策划可编辑的剧情文档，保留原 StoryBook 资产身份。</summary>
    public static class GameStoryMarkdown
    {
        private const string BookPath = "Assets/GameRes/Config/Story/StoryBook.asset";
        private const string Marker = "<!-- story-book v1 -->";
        private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        private static string DocumentPath => Path.GetFullPath(Path.Combine(Application.dataPath, "../../../剧情配置/剧情配置.md"));

        [Serializable]
        private sealed class Options
        {
            public bool checkOnly;
            public string book;
        }

        [Serializable]
        private sealed class Result
        {
            public string document, book;
            public int episodes, lines;
            public bool changed, checkOnly;
        }

        public static object Export()
        {
            StoryBook book = LoadBook(BookPath);
            book.Validate();
            string text = Format(book);
            string path = DocumentPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            // 独占创建保护策划已经编辑过的文档。
            using (var file = new FileStream(path, FileMode.CreateNew))
            using (var writer = new StreamWriter(file, Utf8))
            {
                writer.Write(text);
            }
            return Describe(book, true, false, BookPath);
        }

        public static object Sync(string payload)
        {
            var options = JsonUtility.FromJson<Options>(payload) ?? new Options();
            StoryBook book = LoadBook(string.IsNullOrWhiteSpace(options.book) ? BookPath : options.book);
            if (!options.checkOnly && EditorUtility.IsDirty(book))
            {
                throw new InvalidOperationException("StoryBook 有未保存的 Inspector 修改，请先保存后再同步");
            }

            // 默认立绘、表情映射和迁移版本保留在原资产。全部校验通过后才写业务字段。
            StoryBook candidate = Object.Instantiate(book);
            try
            {
                Parse(File.ReadAllText(DocumentPath, Utf8), candidate);
                GameStoryPortraitAssets.UpdateBook(candidate);
                bool changed = Format(book) != Format(candidate) ||
                    !book.Episodes.Zip(candidate.Episodes, (existing, next) =>
                        (existing.PortraitAtlasNames ?? Array.Empty<string>()).SequenceEqual(next.PortraitAtlasNames)).All(equal => equal);

                if (!options.checkOnly && changed)
                {
                    Undo.RecordObject(book, "同步剧情 Markdown");
                    book.CharactersPerSecond = candidate.CharactersPerSecond;
                    book.Episodes = candidate.Episodes;
                    EditorUtility.SetDirty(book);
                    AssetDatabase.SaveAssetIfDirty(book);
                }
                return Describe(candidate, changed, options.checkOnly, AssetDatabase.GetAssetPath(book));
            }
            finally
            {
                Object.DestroyImmediate(candidate);
            }
        }

        private static StoryBook LoadBook(string path)
        {
            StoryBook book = path.StartsWith("Assets/", StringComparison.Ordinal) && path.EndsWith(".asset", StringComparison.Ordinal)
                ? AssetDatabase.LoadAssetAtPath<StoryBook>(path)
                : null;
            if (book == null)
            {
                throw new InvalidOperationException("找不到 StoryBook 资产：" + path);
            }
            return book;
        }

        private static Result Describe(StoryBook book, bool changed, bool checkOnly, string assetPath)
        {
            return new Result
            {
                document = DocumentPath, book = assetPath, episodes = book.Episodes.Length,
                lines = book.Episodes.Sum(episode => episode.Lines.Length), changed = changed, checkOnly = checkOnly
            };
        }

        private static string Format(StoryBook book)
        {
            var text = new StringBuilder();
            text.Append("# 剧情配置\n\n")
                .Append("修改本文后，运行同目录的 **同步剧情.cmd** 写回 Unity 的 StoryBook。")
                .Append("运行 **同步剧情.cmd --check** 可先只校验。编辑器处于 Play 时请先退出 Play。\n\n")
                .Append("- 二级标题包含篇章 ID 和游戏内标题。保留已有 ID，整块移动对白即可改变播放顺序。教学说明 lesson 保留五句。\n")
                .Append("- 三级标题是说话人。围栏内是游戏原文，保留标点与换行。复制整段可增添对白。\n")
                .Append("- 背景 0 是大厅小屋，1 到 6 对应关卡。表情 0 使用默认立绘。当前表情编号为 ")
                .Append(string.Join("、", book.ExpressionIds)).Append("。\n")
                .Append("- 立绘覆盖填「无」、已打入图集的唯一 Sprite 资产路径，或导出中的 GUID:本地ID。默认立绘及表情映射继续在 Inspector 编辑。\n")
                .Append("- 同步完成会保存 SO。游戏使用 AssetBundle 时，先在仓库根运行 python tools/font-subset.py 更新字库，再按项目流程重建对应平台资源。\n\n")
                .Append(Marker).Append("\n\n")
                .Append("- 打字速度：").Append(book.CharactersPerSecond.ToString("R", Invariant)).Append('\n');

            foreach (StoryBook.Episode episode in book.Episodes)
            {
                text.Append("\n## ").Append(episode.Id).Append(" · ").Append(episode.Title).Append("\n\n")
                    .Append("- 背景章节：").Append(episode.BackgroundChapter.ToString(Invariant)).Append('\n')
                    .Append("- 篇章立绘：").Append(SpriteKey(StoryBook.EditorSprite(episode.PortraitReference, episode.Portrait))).Append('\n');
                foreach (StoryBook.Line line in episode.Lines)
                {
                    string spoken = Normalize(line.Text);
                    int fenceLength = Regex.Matches(spoken, @"(?m)^ {0,3}~+").Cast<Match>()
                        .Select(match => match.Value.TrimStart().Length + 1).DefaultIfEmpty(3).Max();
                    string fence = new string('~', Math.Max(3, fenceLength));
                    text.Append("\n### ").Append(line.Speaker).Append("\n\n")
                        .Append("- 表情：").Append(line.Expression.ToString(Invariant)).Append('\n')
                        .Append("- 本句立绘：").Append(SpriteKey(StoryBook.EditorSprite(line.PortraitReference, line.Portrait))).Append("\n\n")
                        .Append(fence).Append("text\n").Append(spoken).Append('\n').Append(fence).Append('\n');
                }
            }
            return text.ToString();
        }

        private static string SpriteKey(Sprite sprite)
        {
            if (sprite == null)
            {
                return "无";
            }
            if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(sprite, out string guid, out long localId))
            {
                throw new InvalidOperationException("无法导出立绘引用：" + sprite.name);
            }
            return guid + ":" + localId.ToString(Invariant);
        }

        private static string Normalize(string text)
        {
            return (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n');
        }

        private static void Parse(string document, StoryBook candidate)
        {
            var reader = new MarkdownReader(Normalize(document).Split('\n'));
            reader.SeekMarker();
            if (!float.TryParse(reader.Take("- 打字速度："), NumberStyles.Float, Invariant, out float speed) ||
                float.IsNaN(speed) || float.IsInfinity(speed) || speed <= 0)
            {
                throw reader.Error("打字速度必须为有限的正数");
            }

            var episodes = new List<StoryBook.Episode>();
            while (reader.HasContent())
            {
                string heading = reader.Take("## ");
                Match title = Regex.Match(heading, @"^([A-Za-z0-9_-]+) · (.+)$");
                if (!title.Success || string.IsNullOrWhiteSpace(title.Groups[2].Value))
                {
                    throw reader.Error("章节标题格式应为「## 篇章ID · 章节标题」");
                }
                var episode = new StoryBook.Episode
                {
                    Id = title.Groups[1].Value, Title = title.Groups[2].Value,
                    BackgroundChapter = reader.TakeInteger("- 背景章节："),
                    Portrait = reader.TakeSprite("- 篇章立绘：")
                };

                var lines = new List<StoryBook.Line>();
                while (reader.StartsWith("### "))
                {
                    lines.Add(new StoryBook.Line
                    {
                        Speaker = reader.Take("### "),
                        Expression = reader.TakeInteger("- 表情："),
                        Portrait = reader.TakeSprite("- 本句立绘："),
                        Text = reader.TakeDialogue()
                    });
                }
                episode.Lines = lines.ToArray();
                episodes.Add(episode);
            }
            candidate.CharactersPerSecond = speed;
            candidate.Episodes = episodes.ToArray();
        }

        /// <summary>只解释固定格式，围栏内的标记和空行都属于原始台词。</summary>
        private sealed class MarkdownReader
        {
            private readonly string[] _lines;
            private int _next;
            private int _last;

            internal MarkdownReader(string[] lines)
            {
                _lines = lines;
            }

            internal bool HasContent()
            {
                while (_next < _lines.Length && string.IsNullOrWhiteSpace(_lines[_next]))
                {
                    _next++;
                }
                return _next < _lines.Length;
            }

            internal bool StartsWith(string prefix)
            {
                return HasContent() && _lines[_next].StartsWith(prefix, StringComparison.Ordinal);
            }

            internal void SeekMarker()
            {
                _next = Array.IndexOf(_lines, Marker) + 1;
                if (_next == 0)
                {
                    throw Error("缺少文档版本标记 " + Marker);
                }
            }

            internal string Take(string prefix)
            {
                if (!StartsWith(prefix))
                {
                    _last = _next;
                    throw Error("应以「" + prefix + "」开头");
                }
                _last = _next;
                return _lines[_next++].Substring(prefix.Length);
            }

            internal int TakeInteger(string prefix)
            {
                if (!int.TryParse(Take(prefix), NumberStyles.Integer, Invariant, out int value))
                {
                    throw Error(prefix + "需要整数");
                }
                return value;
            }

            internal Sprite TakeSprite(string prefix)
            {
                string key = Take(prefix).Trim();
                if (key == "无")
                {
                    return null;
                }

                Match reference = Regex.Match(key, @"^([0-9a-fA-F]{32}):(-?[0-9]+)$");
                if (!reference.Success && !key.StartsWith("Assets/", StringComparison.Ordinal))
                {
                    throw Error(prefix + "请填「无」、Assets 下的 Sprite 路径或 GUID:本地ID");
                }

                string path = reference.Success ? AssetDatabase.GUIDToAssetPath(reference.Groups[1].Value) : key;
                var sprites = string.IsNullOrEmpty(path) ? Array.Empty<Sprite>() : AssetDatabase.LoadAllAssetsAtPath(path).OfType<Sprite>().ToArray();
                if (reference.Success)
                {
                    if (!long.TryParse(reference.Groups[2].Value, NumberStyles.Integer, Invariant, out long localId))
                    {
                        throw Error("立绘的本地 ID 无效：" + key);
                    }
                    sprites = sprites.Where(sprite => AssetDatabase.TryGetGUIDAndLocalFileIdentifier(sprite, out string _, out long id) && id == localId).ToArray();
                }
                if (sprites.Length != 1)
                {
                    throw Error("立绘找不到或包含多张 Sprite，请用有效的 GUID:本地ID：" + key);
                }
                return sprites[0];
            }

            internal string TakeDialogue()
            {
                string fence = Regex.Match("~" + Take("~"), @"^(~{3,})text$").Groups[1].Value;
                if (fence.Length == 0)
                {
                    throw Error("对白内容以 ~~~text 开始");
                }
                int end = Array.IndexOf(_lines, fence, _next);
                if (end < 0)
                {
                    throw Error("对白缺少结束围栏 " + fence);
                }

                string dialogue = string.Join("\n", _lines, _next, end - _next);
                _next = end + 1;
                return dialogue;
            }

            internal FormatException Error(string message)
            {
                return new FormatException("剧情配置.md 第 " + (_last + 1) + " 行：" + message);
            }
        }
    }
}
