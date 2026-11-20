// ============================================================
// RevHotVerifier.cs —— 下载后的完整性校验（尺寸 + SHA-256）
//
// 位置：Assets\Revolution.HotUpdate\Runtime\Pipeline\
//
// 【为什么"校验失败要整包重下，而不是从断点继续"】
//   断点续传的前提是"前半截肯定是对的"。校验失败说明内容已经坏了，
//   这个时候"从 .part 继续"只会把错误固化 —— 必须删掉 .part、从头再来（调用方负责）。
//
// 【为什么分帧算哈希】
//   几百 MB 的包一次性算完，主线程会卡几秒（loading 条直接冻结）。
//   做法：1 MB 一块流式喂给 SHA256，每累计 8 MB 让出一帧 —— 玩家看到的是"校验中 37%"，
//   游戏其余逻辑照常跑。代价是总耗时略增（可忽略）。
// ============================================================
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using Revolution;

// ★ 把"配套的编辑器程序集"设为友元程序集（friend assembly）：
//   编辑器工具生成清单时要调 ComputeSha256，而 RevHotVerifier 是 internal（本程序集的实现细节，
//   不想让包外的人看到）—— 跨程序集访问 internal 会报 CS0122，所以在这里显式开个口子。
//   · 字符串必须与 Editor/Revolution.HotUpdate.Editor.asmdef 里的 "name" 完全一致；
//     若改了那个 asmdef 的名字（或把 Editor 目录挪进别的程序集），这里要同步改，否则又会 CS0122。
//   · 只影响编译期可见性：运行时零开销，包外使用者依然看不到这些 internal 类型。
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Revolution.HotUpdate.Editor")]

namespace Revolution.HotUpdate
{
    /// <summary>校验器（文件型平台专用；小游戏没有本地文件，校验交给引擎的 hash/crc）。</summary>
    internal static class RevHotVerifier
    {
        /// <summary>哈希缓冲：1 MB（太小会拖慢，太大占内存 —— 1MB 是移动端的甜点值）。</summary>
        private const int BufferBytes = 1 << 20;

        /// <summary>每算多少字节让出一帧（8MB ≈ 手机上 30~80ms 一帧，肉眼无感）。</summary>
        private const long YieldEveryBytes = 8L << 20;

        /// <summary>
        /// 校验一个已下载/已落地的文件：先比尺寸（最便宜），再按需算哈希。
        /// 失败抛 RevHotException(VerifyFailed) —— 调用方捕获后负责删 .part 重下。
        /// </summary>
        public static async RevTask VerifyAsync(string path, long expectedSize, string expectedSha256, RevHotVerifyMode mode, RevHotConfig config, RevCancellationToken token)
        {
            if (File.Exists(path) == false)
            {
                throw new RevHotException(RevHotError.Of(RevHotErrorCode.VerifyFailed, "要校验的文件不存在"), false);
            }

            // ---------- ① 尺寸：最便宜的校验，先做 ----------
            long actualSize = new FileInfo(path).Length;
            if (actualSize != expectedSize)
            {
                string reason = "文件大小不符（期望 " + RevHotProgress.FormatBytes(expectedSize) + "，实际 " + RevHotProgress.FormatBytes(actualSize) + "）";
                throw new RevHotException(RevHotError.Of(RevHotErrorCode.VerifyFailed, reason, path), false);
            }

            // ---------- ② 只验尺寸的模式到此为止 ----------
            if (mode == RevHotVerifyMode.SizeOnly || string.IsNullOrEmpty(expectedSha256))
            {
                return;
            }

            // ---------- ③ SHA-256：分帧流式 ----------
            token.ThrowIfCancelled();

            byte[] buffer = new byte[BufferBytes];
            using (SHA256 sha = SHA256.Create())
            {
                using (FileStream stream = File.OpenRead(path))
                {
                    long sinceYield = 0;
                    int read = 0;
                    while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        token.ThrowIfCancelled();
                        sha.TransformBlock(buffer, 0, read, null, 0);
                        sinceYield += read;

                        if (sinceYield >= YieldEveryBytes)
                        {
                            sinceYield = 0;
                            await RevTask.Yield();                       // 让出一帧：loading 条能刷、游戏不冻结
                        }
                    }

                    sha.TransformFinalBlock(buffer, 0, 0);
                    string hex = BytesToHex(sha.Hash);

                    if (string.Equals(hex, expectedSha256, StringComparison.OrdinalIgnoreCase) == false)
                    {
                        string reason = "文件内容与清单不符（哈希不一致）—— 下载不完整或源文件被改动";
                        throw new RevHotException(RevHotError.Of(RevHotErrorCode.VerifyFailed, reason, path), false);
                    }
                }
            }
        }

        /// <summary>
        /// 计算一个文件的 SHA-256（十六进制小写）。
        /// ★ public 是给编辑器工具用的（生成清单时算每个包的哈希）；运行时走上面的异步分帧版。
        /// </summary>
        public static string ComputeSha256(string path)
        {
            using (SHA256 sha = SHA256.Create())
            {
                using (FileStream stream = File.OpenRead(path))
                {
                    byte[] hash = sha.ComputeHash(stream);
                    return BytesToHex(hash);
                }
            }
        }

        /// <summary>字节转十六进制小写（清单与本地计算必须同一种写法，否则永远"校验失败"）。</summary>
        private static string BytesToHex(byte[] bytes)
        {
            var sb = new System.Text.StringBuilder(bytes.Length * 2);
            for (int i = 0; i < bytes.Length; i++)
            {
                sb.Append(bytes[i].ToString("x2", CultureInfo.InvariantCulture));
            }

            return sb.ToString();
        }
    }
}
