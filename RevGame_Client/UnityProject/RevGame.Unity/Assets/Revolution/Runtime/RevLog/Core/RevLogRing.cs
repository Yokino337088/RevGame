// ============================================================
// RevLogRing.cs —— 最近的日志环形缓冲（纯 C#，零分配写入）
//
// 位置：Runtime\RevLog\Core\
//
// 【为什么要有它】
//   线上问题最需要的不是"从现在开始记"，而是"出事**之前**发生了什么"。
//   环形缓冲常驻最近 N 条，业务/将来的日志面板随时能取走一段快照 —— 不用开文件、不用等复现。
//
// 【★ 容量是特性，不是限制】写满就覆盖最旧的：
//   · 内存有硬顶（不会因为日志刷屏把内存吃光）；
//   · 写入 O(1) 且恒定（只写一个数组元素 + 动一个下标）；
//   · 日志的价值随时间衰减 —— 留最近的就够了。
//
// 【为什么 Write 零分配】只写数组元素与下标，不做任何拷贝/装箱；条目是 struct。
//   取快照（Snapshot）才会 new 数组 —— 那是"人主动要"，不是热路径。
// ============================================================
using System;

namespace Revolution
{
    /// <summary>定长环形缓冲：写满覆盖最旧的（容量固定 → 内存有硬顶）。</summary>
    internal sealed class RevLogRing
    {
        private RevLogEntry[] _entries;
        private int _next;
        private int _count;

        internal RevLogRing(int capacity)
        {
            _entries = new RevLogEntry[capacity < 1 ? 1 : capacity];
        }

        internal int Capacity => _entries.Length;

        internal int Count => _count;

        /// <summary>写入一条（O(1)、零分配）。</summary>
        internal void Write(in RevLogEntry entry)
        {
            _entries[_next] = entry;
            _next++;
            if (_next >= _entries.Length) _next = 0;
            if (_count < _entries.Length) _count++;
        }

        /// <summary>最近 <paramref name="count"/> 条的快照（旧的在前；count &lt;= 0 表示全部）。</summary>
        internal RevLogEntry[] Snapshot(int count)
        {
            int take = count <= 0 || count > _count ? _count : count;
            var result = new RevLogEntry[take];

            // 最旧那条的下标：从写指针往前推 count 条
            int start = _next - take;
            while (start < 0) start += _entries.Length;

            for (int i = 0; i < take; i++)
            {
                int index = start + i;
                if (index >= _entries.Length) index -= _entries.Length;
                result[i] = _entries[index];
            }

            return result;
        }

        /// <summary>把最近若干条拼成多行文本（一键复制现场用）。</summary>
        internal string Dump(int count)
        {
            RevLogEntry[] entries = Snapshot(count);
            if (entries.Length == 0) return "";

            var builder = new System.Text.StringBuilder(entries.Length * 64);
            for (int i = 0; i < entries.Length; i++) builder.AppendLine(entries[i].ToString());
            return builder.ToString();
        }

        /// <summary>清空（进 Play / 换账号时用；容量不变）。</summary>
        internal void Clear()
        {
            Array.Clear(_entries, 0, _entries.Length);
            _next = 0;
            _count = 0;
        }

        /// <summary>改容量（会丢历史；容量硬顶，不允许为 0）。</summary>
        internal void Resize(int capacity)
        {
            _entries = new RevLogEntry[capacity < 1 ? 1 : capacity];
            _next = 0;
            _count = 0;
        }
    }
}
