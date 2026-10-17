// ============================================================
// ExcelZipReader.cs —— 只读 zip（只为读 xlsx 用，零第三方依赖）
//
// 位置：Editor\RevExcelTool\Core\
//
// 【为什么不用 System.IO.Compression.ZipArchive】
//   ① Unity 的编辑器程序集默认不一定引用 System.IO.Compression.dll（随 API 兼容级别变化），
//      用它就得额外配 csc.rsp —— 换个工程就可能编译不过；
//      这里只用 DeflateStream（在 System.dll 里，哪个配置下都有）+ 自己读 zip 目录，任何配置都能编。
//   ② 能用 FileShare.ReadWrite 打开：**Excel 正开着这个文件也能读**。
//      ZipFile.OpenRead 只允许共享读，而 Excel 打开时持有写句柄 → 直接报"文件被占用"——
//      "改完表先关 Excel 再导"是 WPF 版最常见的抱怨，编辑器版里必须没有这一步。
//
// 【只支持什么】存储（0）与 Deflate（8）两种压缩方式、非 ZIP64 —— xlsx 就是这两种。
//   .xls（97-2003）和设置了打开密码的 xlsx 都是 OLE 复合文档而不是 zip，这里会给出明确的提示。
// ============================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Revolution.Editor.ExcelTool
{
    /// <summary>只读 zip（按部件名取数据流）</summary>
    public sealed class ExcelZipReader : IDisposable
    {
        private struct Entry
        {
            public int Method;
            public long CompressedSize;
            public long LocalOffset;
        }

        private readonly Stream _stream;

        // xlsx 部件名在规范里大小写不敏感（个别生成器会写成 xl/SharedStrings.xml）
        private readonly Dictionary<string, Entry> _entries =
            new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);

        /// <summary>打开文件（共享读写：Excel 开着也能读）</summary>
        public static ExcelZipReader Open(string path)
        {
            var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            try
            {
                return new ExcelZipReader(fs);
            }
            catch
            {
                fs.Dispose();
                throw;
            }
        }

        public ExcelZipReader(Stream stream)
        {
            _stream = stream ?? throw new ArgumentNullException(nameof(stream));
            ReadCentralDirectory();
        }

        public bool Contains(string name) => _entries.ContainsKey(name);

        /// <summary>取某个部件的数据流；不存在返回 null</summary>
        public Stream OpenEntry(string name)
        {
            if (string.IsNullOrEmpty(name) || !_entries.TryGetValue(name, out Entry entry)) return null;

            var header = new byte[30];
            _stream.Position = entry.LocalOffset;
            ReadExactly(header, header.Length);
            if (U32(header, 0) != 0x04034b50) throw new InvalidDataException($"zip 部件头损坏：{name}");

            long dataStart = entry.LocalOffset + 30 + U16(header, 26) + U16(header, 28);
            if (entry.CompressedSize > int.MaxValue) throw new InvalidDataException($"部件过大：{name}");

            var data = new byte[entry.CompressedSize];
            _stream.Position = dataStart;
            ReadExactly(data, data.Length);

            var raw = new MemoryStream(data, false);
            switch (entry.Method)
            {
                case 0: return raw;                                                    // 存储（不压缩）
                case 8: return new DeflateStream(raw, CompressionMode.Decompress);     // Deflate
                default: throw new InvalidDataException($"不支持的压缩方式 {entry.Method}：{name}");
            }
        }

        public void Dispose() => _stream.Dispose();

        // ============================================================
        // zip 目录
        // ============================================================

        private void ReadCentralDirectory()
        {
            long length = _stream.Length;
            if (length < 22) throw new InvalidDataException("文件太小，不是有效的 .xlsx");

            // ---------- 先认文件头：给"不是 xlsx"一个能看懂的原因 ----------
            var head = new byte[4];
            _stream.Position = 0;
            ReadExactly(head, head.Length);

            if (head[0] == 0xD0 && head[1] == 0xCF && head[2] == 0x11 && head[3] == 0xE0)
                throw new InvalidDataException("这是 .xls（Excel 97-2003）或设置了打开密码的工作簿：请另存为不加密的 .xlsx");
            if (U32(head, 0) != 0x04034b50)
                throw new InvalidDataException("不是 .xlsx 文件（xlsx 本质是 zip 包，文件头不对）");

            // ---------- 从尾部找"目录结束记录"（后面最多跟 64KB 注释） ----------
            int tailSize = (int)Math.Min(length, 22 + 65535);
            var tail = new byte[tailSize];
            _stream.Position = length - tailSize;
            ReadExactly(tail, tail.Length);

            int eocd = -1;
            for (int i = tailSize - 22; i >= 0; i--)
            {
                if (U32(tail, i) != 0x06054b50) continue;
                eocd = i;
                break;
            }
            if (eocd < 0) throw new InvalidDataException("找不到 zip 目录：文件可能损坏，或 Excel 还没保存完");

            int count = U16(tail, eocd + 10);
            long directorySize = U32(tail, eocd + 12);
            long directoryOffset = U32(tail, eocd + 16);

            if (count == 0xFFFF || directoryOffset == 0xFFFFFFFF)
                throw new InvalidDataException("不支持 ZIP64 格式（超过 4GB 或部件过多的工作簿）");
            if (directoryOffset + directorySize > length || directorySize > int.MaxValue)
                throw new InvalidDataException("zip 目录越界：文件可能损坏");

            var directory = new byte[directorySize];
            _stream.Position = directoryOffset;
            ReadExactly(directory, directory.Length);

            // ---------- 逐条读目录 ----------
            int p = 0;
            for (int n = 0; n < count; n++)
            {
                if (p + 46 > directory.Length || U32(directory, p) != 0x02014b50)
                    throw new InvalidDataException("zip 目录损坏");

                int flags = U16(directory, p + 8);
                if ((flags & 0x1) != 0) throw new InvalidDataException("工作簿部件被加密，无法读取");

                int nameLength = U16(directory, p + 28);
                int extraLength = U16(directory, p + 30);
                int commentLength = U16(directory, p + 32);

                string name = Encoding.UTF8.GetString(directory, p + 46, nameLength).Replace('\\', '/');

                _entries[name] = new Entry
                {
                    Method = U16(directory, p + 10),
                    CompressedSize = U32(directory, p + 20),
                    LocalOffset = U32(directory, p + 42),
                };

                p += 46 + nameLength + extraLength + commentLength;
            }
        }

        private void ReadExactly(byte[] buffer, int count)
        {
            int read = 0;
            while (read < count)
            {
                int n = _stream.Read(buffer, read, count - read);
                if (n <= 0) throw new EndOfStreamException("文件被截断（Excel 可能还没保存完）");
                read += n;
            }
        }

        private static int U16(byte[] b, int i) => b[i] | (b[i + 1] << 8);

        private static long U32(byte[] b, int i)
            => (uint)(b[i] | (b[i + 1] << 8) | (b[i + 2] << 16) | (b[i + 3] << 24));
    }
}
