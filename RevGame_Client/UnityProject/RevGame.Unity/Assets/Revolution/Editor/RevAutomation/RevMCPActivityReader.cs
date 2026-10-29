// RevMCPActivityReader —— 按文件偏移增量读取活动账本，不在每次 OnGUI 中重扫全部工单。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

namespace Revolution.Editor
{
    /// <summary>
    /// Unity 和 Python 写入同一活动目录的不同 JSONL 文件，本类负责合并开始/结束记录。
    /// 只读完整换行，外部恰好写到半行时等下一次刷新；同一 id 更新同一行。
    /// 一次最多读每文件 256 KiB、最近 64 个文件，内存只保留最近 500 条，避免长测拖慢编辑器。
    /// </summary>
    public sealed class RevMCPActivityReader
    {
        public const int Capacity = 500;
        private const int ChunkBytes = 256 * 1024;
        private readonly string _directory;
        private readonly Dictionary<string, long> _offsets = new Dictionary<string, long>(StringComparer.Ordinal);
        private readonly Dictionary<string, RevAutomationActivityEntry> _byId = new Dictionary<string, RevAutomationActivityEntry>(StringComparer.Ordinal);
        private readonly List<RevAutomationActivityEntry> _entries = new List<RevAutomationActivityEntry>();
        public IReadOnlyList<RevAutomationActivityEntry> Entries => _entries;
        public string LastError { get; private set; }
        public int InvalidLines { get; private set; }

        public RevMCPActivityReader(string directory) { _directory = Path.GetFullPath(directory); }

        /// <summary>由窗口每 0.25 秒调用；日志刷新不发 MCP 请求，不产生“读取日志又写一条日志”的循环。</summary>
        public bool Poll()
        {
            bool changed = false;
            if (!Directory.Exists(_directory)) { return false; }
            try
            {
                var files = new DirectoryInfo(_directory).GetFiles("*.jsonl")
                    .OrderByDescending(file => file.LastWriteTimeUtc).Take(64).ToArray();
                foreach (FileInfo file in files)
                {
                    try { changed |= Read(file); }
                    catch (IOException e) { LastError = e.Message; }
                    catch (UnauthorizedAccessException e) { LastError = e.Message; }
                }
                var activePaths = new HashSet<string>(files.Select(f => f.FullName), StringComparer.Ordinal);
                foreach (string gone in _offsets.Keys.Where(p => !activePaths.Contains(p)).ToArray()) { _offsets.Remove(gone); }
                if (changed)
                {
                    _entries.Sort((a, b) => DateTimeOffset.Parse(a.utc).CompareTo(DateTimeOffset.Parse(b.utc)));
                    while (_entries.Count > Capacity)
                    { _byId.Remove(_entries[0].id); _entries.RemoveAt(0); }
                }
                return changed;
            }
            catch (IOException e) { LastError = e.Message; return changed; }
            catch (UnauthorizedAccessException e) { LastError = e.Message; return changed; }
        }

        private bool Read(FileInfo file)
        {
            bool first = !_offsets.TryGetValue(file.FullName, out long offset);
            using (var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                long length = stream.Length;
                if (offset > length) { offset = 0; first = true; } // 文件被外部轮换/截断时从当前文件重读。
                bool skipFirst = first && length > ChunkBytes;
                if (skipFirst) { offset = length - ChunkBytes; }
                stream.Position = offset;
                var bytes = new byte[(int)Math.Min(ChunkBytes, length - offset)];
                int count = stream.Read(bytes, 0, bytes.Length);
                int start = 0, consumed = 0;
                bool changed = false;
                for (int i = 0; i < count; i++)
                {
                    if (bytes[i] != (byte)'\n') { continue; }
                    if (skipFirst) { skipFirst = false; }
                    else
                    {
                        string json = Encoding.UTF8.GetString(bytes, start, i - start);
                        try
                        {
                            var entry = JsonUtility.FromJson<RevAutomationActivityEntry>(json);
                            if (entry == null || entry.schema != 1 || string.IsNullOrEmpty(entry.id) || string.IsNullOrEmpty(entry.action)
                                || !DateTimeOffset.TryParse(entry.utc, out _)) { InvalidLines++; }
                            else { Merge(entry); changed = true; }
                        }
                        catch (ArgumentException) { InvalidLines++; }
                    }
                    start = i + 1; consumed = start;
                }
                // 未结束的一行连同可能只写了一半的中文 UTF-8 字节保留，下次从行首重读。
                _offsets[file.FullName] = offset + consumed;
                return changed;
            }
        }

        private void Merge(RevAutomationActivityEntry entry)
        {
            if (_byId.TryGetValue(entry.id, out RevAutomationActivityEntry old))
            {
                // 文件枚举顺序不等于事件顺序；结束记录不能被随后读到的开始事件倒退覆盖。
                if (old.state != "Running" && entry.state == "Running") { return; }
                _entries.Remove(old);
            }
            _byId[entry.id] = entry;
            _entries.Add(entry);
        }

        /// <summary>清空当前视图，不删除证据；记住现有文件尾部，下次只收新事件。</summary>
        public void ClearView()
        {
            _entries.Clear(); _byId.Clear(); InvalidLines = 0; LastError = null;
            try
            {
                if (!Directory.Exists(_directory)) { return; }
                foreach (FileInfo file in new DirectoryInfo(_directory).GetFiles("*.jsonl")) { _offsets[file.FullName] = file.Length; }
            }
            catch (IOException e) { LastError = e.Message; }
            catch (UnauthorizedAccessException e) { LastError = e.Message; }
        }
    }
}
