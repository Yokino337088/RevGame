using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Revolution
{
    /// <summary>打包端与运行时共用的映射表格式。压缩层校验完整性，文本层逐条验证逻辑路径与定位结果。</summary>
    public static class RevResMapCodec
    {
        /// <summary>压缩首包映射表的 Resources 路径。固定路径用于在 AB 之前完成引导。</summary>
        public const string PackedResourcePath = "ResourceSystem/ResMapPacked";

        /// <summary>旧 Player 的内置文本路径。新版 Player 由构建检查禁止携带这份文本。</summary>
        public const string LegacyResourcePath = "ResourceSystem/ResMap";

        /// <summary>UTF-8 映射文本上限，单位字节。读取文件和分配解压缓冲前都检查此限制。</summary>
        public const int MaxTextBytes = 32 * 1024 * 1024;

        /// <summary>固定标记 RVMP，按小端序写入。</summary>
        private const uint Magic = 0x504D5652;

        /// <summary>首版格式保存版本号、原文长度、gzip 正文长度与正文摘要。</summary>
        private const int Version = 1;

        /// <summary>SHA-256 摘要在头部占用的字节数。</summary>
        private const int DigestLength = 32;

        /// <summary>头部由四个 32 位整数和一个 SHA-256 摘要组成。</summary>
        private const int HeaderLength = sizeof(int) * 4 + DigestLength;

        /// <summary>拒绝截断多字节字符和无效序列。允许旧文件开头有 UTF-8 BOM。</summary>
        private static readonly Encoding Utf8 = new UTF8Encoding(false, true);

        /// <summary>用相同编码解析首包与旧热更文本。超限或格式损坏时报告具体原因。</summary>
        public static string DecodeText(byte[] bytes)
        {
            if (bytes == null || bytes.Length > MaxTextBytes)
            {
                throw new InvalidDataException("ResMap 文本缺失或超过大小上限");
            }

            try
            {
                string text = Utf8.GetString(bytes);
                return text.Length != 0 && text[0] == '\uFEFF' ? text.Substring(1) : text;
            }
            catch (DecoderFallbackException exception)
            {
                throw new InvalidDataException("ResMap 含无效 UTF-8", exception);
            }
        }

        /// <summary>完整验证后才返回映射。重复键、非法行和失控路径都会拒绝整表。</summary>
        public static Dictionary<string, string> ParseText(string text)
        {
            if (text == null || Utf8.GetByteCount(text) > MaxTextBytes)
            {
                throw new InvalidDataException("ResMap 文本缺失或超过大小上限");
            }
            if (text.Length != 0 && text[0] == '\uFEFF')
            {
                text = text.Substring(1);
            }

            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            // 逐行读取，避免在启动阶段额外持有全量行数组和每行的字段数组。
            using (var reader = new StringReader(text))
            {
                int lineNumber = 0;
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    lineNumber++;
                    line = line.Trim();
                    if (line.Length == 0 || line[0] == '#')
                    {
                        continue;
                    }

                    int first = line.IndexOf('|');
                    int second = first < 0 ? -1 : line.IndexOf('|', first + 1);
                    if (first < 1 || second <= first + 1 || second == line.Length - 1 || line.IndexOf('|', second + 1) >= 0)
                    {
                        throw InvalidRow(lineNumber, "需提供逻辑路径、包名和资源名");
                    }
                    string logicalPath = line.Substring(0, first);
                    string bundle = line.Substring(first + 1, second - first - 1);
                    string asset = line.Substring(second + 1);
                    if (!IsRelativeName(logicalPath) || !IsRelativeName(bundle) || !IsRelativeName(asset))
                    {
                        throw InvalidRow(lineNumber, "路径或包内名称含非法字符、空段或目录跳转");
                    }
                    if (!map.TryAdd(logicalPath, line.Substring(first + 1)))
                    {
                        throw InvalidRow(lineNumber, "重复逻辑路径 " + logicalPath);
                    }
                }
            }
            return map;
        }

        /// <summary>生成确定的 UTF-8 派生副本，并在写入前检测映射格式和重复键。</summary>
        public static byte[] Pack(string text)
        {
            ParseText(text);
            string canonical = CanonicalText(text);
            byte[] data = Utf8.GetBytes(canonical);
            if (data.Length == 0)
            {
                throw new InvalidDataException("ResMap 压缩副本至少需要一行文本");
            }

            // 摘要覆盖整个压缩正文。即使解压库能容忍被截断的 gzip 尾部，此处也会拒绝。
            using (var output = new MemoryStream())
            {
                output.Position = HeaderLength;
                using (var compressor = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
                {
                    compressor.Write(data, 0, data.Length);
                }

                int compressedLength = checked((int)output.Length - HeaderLength);
                using (var sha = SHA256.Create())
                using (var header = new BinaryWriter(output, Utf8, leaveOpen: true))
                {
                    byte[] digest = sha.ComputeHash(output.GetBuffer(), HeaderLength, compressedLength);
                    output.Position = 0;
                    header.Write(Magic);
                    header.Write(Version);
                    header.Write(data.Length);
                    header.Write(compressedLength);
                    header.Write(digest);
                }
                return output.ToArray();
            }
        }

        /// <summary>验证长度和摘要，再以声明上限解压并严格解码 UTF-8。</summary>
        public static string Unpack(byte[] packed)
        {
            if (packed == null || packed.Length <= HeaderLength || packed.Length > MaxTextBytes + HeaderLength + 65536)
            {
                throw new InvalidDataException("ResMap 压缩副本大小无效");
            }

            using (var input = new MemoryStream(packed, writable: false))
            using (var header = new BinaryReader(input, Utf8, leaveOpen: true))
            {
                if (header.ReadUInt32() != Magic || header.ReadInt32() != Version)
                {
                    throw new InvalidDataException("ResMap 压缩格式或版本不受支持");
                }

                int textLength = header.ReadInt32();
                int compressedLength = header.ReadInt32();
                if (textLength < 0 || textLength > MaxTextBytes || compressedLength != packed.Length - HeaderLength)
                {
                    throw new InvalidDataException("ResMap 压缩正文或声明长度不一致");
                }
                byte[] expected = header.ReadBytes(DigestLength);
                using (var sha = SHA256.Create())
                {
                    byte[] digest = sha.ComputeHash(packed, HeaderLength, compressedLength);
                    if (!SameDigest(expected, digest))
                    {
                        throw new InvalidDataException("ResMap 压缩正文 SHA-256 不匹配");
                    }
                }

                var data = new byte[textLength];
                using (var decompressor = new GZipStream(input, CompressionMode.Decompress, leaveOpen: true))
                {
                    int offset = 0;
                    while (offset < data.Length)
                    {
                        int read = decompressor.Read(data, offset, data.Length - offset);
                        if (read == 0)
                        {
                            throw new InvalidDataException("ResMap 解压结果被截断");
                        }
                        offset += read;
                    }
                    if (decompressor.ReadByte() != -1)
                    {
                        throw new InvalidDataException("ResMap 解压结果超过声明长度");
                    }
                }
                return DecodeText(data);
            }
        }

        /// <summary>把可读文本统一为无 BOM、LF 换行，方便比较版本与重建一致性。</summary>
        public static string CanonicalText(string text)
        {
            if (text.Length != 0 && text[0] == '\uFEFF')
            {
                text = text.Substring(1);
            }
            return text.Replace("\r\n", "\n").Replace('\r', '\n');
        }

        /// <summary>比较同长度摘要。此处摘要用于检测损坏，热更来源由签名清单验证。</summary>
        private static bool SameDigest(byte[] expected, byte[] actual)
        {
            int difference = 0;
            for (int i = 0; i < DigestLength; i++)
            {
                difference |= expected[i] ^ actual[i];
            }
            return difference == 0;
        }

        /// <summary>只接受非空的相对路径。允许多级目录与 Unicode 名称。</summary>
        private static bool IsRelativeName(string value)
        {
            if (char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[value.Length - 1]))
            {
                return false;
            }

            int start = 0;
            for (int i = 0; i <= value.Length; i++)
            {
                if (i == value.Length || value[i] == '/')
                {
                    int length = i - start;
                    if (length == 0 || (length == 1 && value[start] == '.') ||
                        (length == 2 && value[start] == '.' && value[start + 1] == '.'))
                    {
                        return false;
                    }
                    start = i + 1;
                    continue;
                }

                char c = value[i];
                if (char.IsControl(c) || c == '\\' || c == ':' || c == '%' || c == '?' || c == '#' || c == '\uFEFF')
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>将失败定位到原表行号，调用方可以直接回到构建产物排查。</summary>
        private static InvalidDataException InvalidRow(int lineNumber, string reason)
        {
            return new InvalidDataException("ResMap 第 " + lineNumber + " 行无效：" + reason);
        }
    }
}
