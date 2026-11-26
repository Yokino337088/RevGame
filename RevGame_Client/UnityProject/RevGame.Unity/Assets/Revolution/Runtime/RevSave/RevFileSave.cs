// RevFileSave.cs — 在已声明的文件槽提交完整快照并保留可验证备份。
// 调用方负责业务格式及版本校验。每个路径在进程内串行，损坏时逐份验证后才交付。
using System;
using System.IO;
using System.Text;
#if UNITY_WEBGL && !UNITY_EDITOR && !UNITY_WEIXINMINIGAME && !UNITY_BYTEDANCE_MINIGAME && !UNITY_BYTEDANCEMINIGAME
using System.Runtime.InteropServices;
using UnityEngine;
#endif

namespace Revolution
{
    /// <summary>使用本机文件系统的存档门面。所有权交给提供槽路径的业务宿主。</summary>
    public static class RevFileSave
    {
        /// <summary>有限数量的槽锁。不同路径允许并行，槽键碰撞仅增加串行等待。</summary>
        private static readonly object[] Gates = CreateGates();

        /// <summary>主档或备份存在。可用性仍以读取和业务校验为准。</summary>
        public static bool Exists(string path)
        {
            return File.Exists(path) || File.Exists(path + ".bak");
        }

        /// <summary>首先校验主档，再校验备份。读取和校验都不改写原文件。</summary>
        public static T Read<T>(string path, Func<string, T> validate, out bool fromBackup)
        {
            string full = Path.GetFullPath(path);
            if (validate == null)
            {
                throw new ArgumentNullException(nameof(validate));
            }
            lock (GetGate(full))
            {
                fromBackup = false;
                try
                {
                    return validate(File.ReadAllText(full, Encoding.UTF8));
                }
                catch (Exception primary) when (IsReadFailure(primary))
                {
                    try
                    {
                        T result = validate(File.ReadAllText(full + ".bak", Encoding.UTF8));
                        fromBackup = true;
                        return result;
                    }
                    catch (Exception backup) when (IsReadFailure(backup))
                    {
                        throw new InvalidDataException("主档与备份均不可用", new AggregateException(primary, backup));
                    }
                }
            }
        }

        /// <summary>
        /// 同目录写入、刷新和读回校验后原子提交。恢复后的首次写入可保留上次可用备份。
        /// 浏览器在虚拟文件提交后请求异步写回，返回时 IndexedDB 事务仍可能在进行。
        /// </summary>
        public static void Write(string path, string data, Action<string> validate, bool preserveBackup = false)
        {
            Write(path, data, validate, preserveBackup, null);
        }

        /// <summary>
        /// 提交完整快照，临时文件清理失败时由 onCleanupError 接收原异常，空回调沿用框架日志。
        /// 原生平台的工作线程必须提供线程安全的校验与清理回调。网页持久目录写回由主线程发起。
        /// </summary>
        public static void Write(string path, string data, Action<string> validate, bool preserveBackup, Action<Exception> onCleanupError)
        {
            if (data == null || validate == null)
            {
                throw new ArgumentException("保存需要编码后的快照及其校验器。");
            }
            string full = Path.GetFullPath(path);
            lock (GetGate(full))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(full));
                string temporary = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
                bool created = false;
                try
                {
                    using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
                    {
                        created = true;
                        using (var writer = new StreamWriter(stream, Encoding.UTF8, 1024, leaveOpen: true))
                        {
                            writer.Write(data);
                        }
                        stream.Flush(true);
                    }
                    validate(File.ReadAllText(temporary, Encoding.UTF8));
                    if (File.Exists(full))
                    {
                        File.Replace(temporary, full, preserveBackup ? null : full + ".bak");
                    }
                    else
                    {
                        File.Move(temporary, full);
                    }
                    created = false;
                }
                finally
                {
                    if (created)
                    {
                        // 只清除本次拥有的临时槽。可用主档和备份由操作系统继续保护。
                        try
                        {
                            File.Delete(temporary);
                        }
                        catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
                        {
                            // 原生工作线程将错误交回宿主，避免在文件线程读取 Unity 帧号或操作日志通道。
                            if (onCleanupError != null)
                            {
                                onCleanupError(error);
                            }
                            else
                            {
                                RevLog.Exception(error, "删除临时存档失败", "RevSave");
                            }
                        }
                    }
                }
            }
#if UNITY_WEBGL && !UNITY_EDITOR && !UNITY_WEIXINMINIGAME && !UNITY_BYTEDANCE_MINIGAME && !UNITY_BYTEDANCEMINIGAME
            // 先完成主档和备份的变更，再同步持久目录，避免把临时文件或失败写入提交到 IndexedDB。
            string persistentRoot = Application.persistentDataPath;
            if (!string.IsNullOrEmpty(persistentRoot))
            {
                string prefix = Path.GetFullPath(persistentRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (full.StartsWith(prefix, StringComparison.Ordinal))
                {
                    RevWebFileSave_Queue();
                }
            }
#endif
        }

#if UNITY_WEBGL && !UNITY_EDITOR && !UNITY_WEIXINMINIGAME && !UNITY_BYTEDANCE_MINIGAME && !UNITY_BYTEDANCEMINIGAME
        /// <summary>通知网页插件写回 IDBFS，由插件管理跨帧同步。微信与抖音小游戏使用自身的文件宿主。</summary>
        [DllImport("__Internal")]
        private static extern void RevWebFileSave_Queue();
#endif

        /// <summary>业务校验错误和读取错误可走备份。程序错误继续传给调用方排查。</summary>
        private static bool IsReadFailure(Exception error)
        {
            return error is IOException || error is UnauthorizedAccessException || error is InvalidDataException || error is ArgumentException;
        }

        /// <summary>用完整路径选择槽锁，忽略大小写以兼容 Windows 常用路径。</summary>
        private static object GetGate(string path)
        {
            return Gates[(StringComparer.OrdinalIgnoreCase.GetHashCode(path) & int.MaxValue) % Gates.Length];
        }

        /// <summary>一次性创建固定容量的槽锁，避免随存档数量累计。</summary>
        private static object[] CreateGates()
        {
            var slots = new object[32];
            for (int i = 0; i < slots.Length; i++)
            {
                slots[i] = new object();
            }
            return slots;
        }
    }
}
