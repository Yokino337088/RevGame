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
    /// 整次刷新共享 256 KiB / 128 行预算，轮转最近 64 个文件；每次合并即限制最近 500 条。
    /// </summary>
    public sealed class RevMCPActivityReader
    {
        public const int Capacity = 500;
        public const int PollByteBudget = 256 * 1024;
        public const int PollLineBudget = 128;
        private sealed class Cursor
        {
            public long Offset, ObservedLength = -1;
            public DateTime ObservedWrite;
            public bool SkipFirst;
        }
        private readonly string _directory;
        private readonly byte[] _buffer = new byte[PollByteBudget];
        private readonly Dictionary<string, Cursor> _offsets = new Dictionary<string, Cursor>(StringComparer.Ordinal);
        private readonly Dictionary<string, DateTimeOffset> _times = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        private int _nextFile;
        public int Version { get; private set; }
        public int LastPollBytes { get; private set; }
        public int LastPollLines { get; private set; }
        /// <summary>预算用尽后仍有未消费数据；半行已观察且未继续写入不算积压。</summary>
        public bool HasPendingData { get; private set; }
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
            LastPollBytes = 0; LastPollLines = 0; HasPendingData = false;
            if (!Directory.Exists(_directory)) { return false; }
            try
            {
                var files = new DirectoryInfo(_directory).GetFiles("*.jsonl")
                    .OrderByDescending(file => file.LastWriteTimeUtc).Take(64).ToArray();
                // 预算属于整个 Poll，而非每个文件；轮转起点避免忙流一直挤掉其它 Agent。
                int first = files.Length == 0 ? 0 : _nextFile % files.Length;
                for (int visited = 0; visited < files.Length; visited++)
                {
                    int index = (first + visited) % files.Length;
                    try { changed |= Read(files[index]); }
                    catch (IOException e) { LastError = e.Message; }
                    catch (UnauthorizedAccessException e) { LastError = e.Message; }
                    _nextFile = index + 1;
                    if (LastPollBytes >= PollByteBudget || LastPollLines >= PollLineBudget) { break; }
                }
                // 前64个是读取范围，不是文件生命周期。旧流再次活跃时必须保留清空视图后的尾游标。
                foreach (string gone in _offsets.Keys.Where(p => !File.Exists(p)).ToArray()) { _offsets.Remove(gone); }
                foreach (FileInfo file in files)
                {
                    if (file.Length == 0) { continue; }
                    if (!_offsets.TryGetValue(file.FullName, out Cursor cursor)
                        || cursor.Offset < file.Length && (cursor.ObservedLength != file.Length || cursor.ObservedWrite != file.LastWriteTimeUtc))
                    { HasPendingData = true; break; }
                }
                if (changed) { Version++; }
                return changed;
            }
            catch (IOException e) { LastError = e.Message; return changed; }
            catch (UnauthorizedAccessException e) { LastError = e.Message; return changed; }
        }

        private bool Read(FileInfo file)
        {
            if (!_offsets.TryGetValue(file.FullName, out Cursor cursor))
            {
                cursor = new Cursor();
                if (file.Length > PollByteBudget) { cursor.Offset = file.Length - PollByteBudget; cursor.SkipFirst = true; }
                _offsets.Add(file.FullName, cursor);
            }
            if (cursor.Offset > file.Length) { cursor.Offset = 0; cursor.SkipFirst = false; cursor.ObservedLength = -1; }
            if (cursor.ObservedLength == file.Length && cursor.ObservedWrite == file.LastWriteTimeUtc) { return false; }
            if (cursor.Offset == file.Length)
            { cursor.ObservedLength = file.Length; cursor.ObservedWrite = file.LastWriteTimeUtc; return false; }
            using (var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                long length = stream.Length;
                if (cursor.Offset > length) { cursor.Offset = 0; cursor.SkipFirst = false; }
                stream.Position = cursor.Offset;
                int capacity = Math.Min(PollByteBudget - LastPollBytes, (int)Math.Min(PollByteBudget, length - cursor.Offset));
                int count = stream.Read(_buffer, 0, capacity);
                LastPollBytes += count;
                int start = 0, consumed = 0;
                bool changed = false, limited = false;
                for (int i = 0; i < count; i++)
                {
                    if (_buffer[i] != (byte)'\n') { continue; }
                    LastPollLines++;
                    if (cursor.SkipFirst) { cursor.SkipFirst = false; }
                    else
                    {
                        string json = Encoding.UTF8.GetString(_buffer, start, i - start);
                        try
                        {
                            var entry = JsonUtility.FromJson<RevAutomationActivityEntry>(json);
                            if (entry == null || entry.schema != 1 || string.IsNullOrEmpty(entry.id) || string.IsNullOrEmpty(entry.action)
                                || !DateTimeOffset.TryParse(entry.utc, out DateTimeOffset utc)) { InvalidLines++; }
                            else { changed |= Merge(entry, utc); }
                        }
                        catch (ArgumentException) { InvalidLines++; }
                    }
                    start = i + 1; consumed = start;
                    if (LastPollLines >= PollLineBudget) { limited = true; break; }
                }
                // 普通半行从行首重读；超过整个缓冲的畸形行丢弃到换行，不能永久占满每次预算。
                if (!limited && consumed == 0 && count == PollByteBudget)
                { if (!cursor.SkipFirst) { InvalidLines++; } cursor.SkipFirst = true; consumed = count; }
                if (!limited && cursor.SkipFirst) { consumed = count; }
                long previous = cursor.Offset;
                cursor.Offset += consumed;
                if (!limited && previous + count >= length)
                { cursor.ObservedLength = file.Length; cursor.ObservedWrite = file.LastWriteTimeUtc; }
                return changed;
            }
        }

        private bool Merge(RevAutomationActivityEntry entry, DateTimeOffset utc)
        {
            if (_byId.TryGetValue(entry.id, out RevAutomationActivityEntry old))
            {
                // 结束不能倒退成开始；晚读到的旧结束事件也不能覆盖新结果。
                if (old.state != "Running" && entry.state == "Running" || utc < _times[entry.id]) { return false; }
                _entries.Remove(old);
            }
            _byId[entry.id] = entry; _times[entry.id] = utc;
            int low = 0, high = _entries.Count;
            while (low < high)
            {
                int middle = (low + high) / 2;
                if (_times[_entries[middle].id] <= utc) { low = middle + 1; } else { high = middle; }
            }
            _entries.Insert(low, entry);
            if (_entries.Count > Capacity)
            {
                string id = _entries[0].id;
                _entries.RemoveAt(0); _byId.Remove(id); _times.Remove(id);
            }
            return true;
        }

        /// <summary>清空当前视图，不删除证据；记住现有文件尾部，下次只收新事件。</summary>
        public void ClearView()
        {
            _entries.Clear(); _byId.Clear(); _times.Clear(); _offsets.Clear(); Version++;
            InvalidLines = 0; LastError = null; _nextFile = 0;
            try
            {
                if (!Directory.Exists(_directory)) { return; }
                foreach (FileInfo file in new DirectoryInfo(_directory).GetFiles("*.jsonl"))
                { _offsets[file.FullName] = new Cursor { Offset = file.Length, ObservedLength = file.Length, ObservedWrite = file.LastWriteTimeUtc }; }
            }
            catch (IOException e) { LastError = e.Message; }
            catch (UnauthorizedAccessException e) { LastError = e.Message; }
        }
    }
}
