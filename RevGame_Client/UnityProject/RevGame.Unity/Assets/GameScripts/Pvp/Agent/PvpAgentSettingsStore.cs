using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;

namespace RevGame.Pvp.Agent
{
    /// <summary>
    /// 玩家本地凭据存储。Windows 保留当前用户的 DPAPI 密文，Android 在应用数据目录保存明文 TXT。
    /// 两种格式均把 Key、模型和授权一起保存，棋谱与联机服务端无需持有这些配置。
    /// </summary>
    internal static class PvpAgentSettingsStore
    {
        internal const string PlayerSlot = "RevGame.DeepSeek.ProtectedSettings.v1";
        // 使用实际运行平台，避免 Device Simulator 改变 Windows 编辑器里玩家的凭据存储位置。
        internal static bool UsesTextStorage => Application.platform == RuntimePlatform.Android;
        private const int RecordSizeLimit = 16384;
        [Serializable]
        private sealed class Record
        {
            public int version = 1;
            public string key, model;
            public bool enabled;
        }

        internal static void Save(PvpAgentSettings settings, string slot = PlayerSlot)
        {
            if (!settings.Enabled)
            {
                Clear(slot);
                return;
            }
            if (UsesTextStorage)
            {
                SaveText(settings, slot);
                return;
            }
            byte[] plain = Encoding.UTF8.GetBytes(Encode(settings));
            try
            {
                string encrypted = Convert.ToBase64String(Transform(plain, true));
                PlayerPrefs.SetString(slot, encrypted);
                PlayerPrefs.Save();
            }
            finally
            {
                Array.Clear(plain, 0, plain.Length);
            }
        }

        /// <summary>损坏记录保留供玩家主动清除，失败不启用云端，也不把密文或异常正文写进日志。</summary>
        internal static bool Load(out PvpAgentSettings settings, out string reason, string slot = PlayerSlot)
        {
            if (UsesTextStorage)
            {
                return LoadText(out settings, out reason, slot);
            }
            settings = new PvpAgentSettings();
            reason = "";
            if (!PlayerPrefs.HasKey(slot))
            {
                return true;
            }
            byte[] plain = null;
            try
            {
                string saved = PlayerPrefs.GetString(slot);
                if (saved.Length > RecordSizeLimit)
                {
                    throw new InvalidOperationException();
                }
                plain = Transform(Convert.FromBase64String(saved), false);
                Decode(Encoding.UTF8.GetString(plain), settings);
                return true;
            }
            catch (Exception)
            {
                settings = new PvpAgentSettings();
                reason = "本地Agent配置无法解密或已损坏，请清除Key后重新保存";
                return false;
            }
            finally
            {
                if (plain != null)
                {
                    Array.Clear(plain, 0, plain.Length);
                }
            }
        }

        internal static void Clear(string slot = PlayerSlot)
        {
            if (UsesTextStorage)
            {
                ClearText(slot);
                return;
            }
            PlayerPrefs.DeleteKey(slot);
            PlayerPrefs.Save();
        }

        /// <summary>正式槽与自动化假 Key 槽各有文件，测试不会覆盖玩家自己的配置。</summary>
        internal static string TextPath(string slot)
        {
            string directory = Application.persistentDataPath;
            if (string.IsNullOrEmpty(directory))
            {
                throw new InvalidOperationException("应用存储目录不可用");
            }
            string filename = slot == PlayerSlot ? "DeepSeekAgentSettings.txt" : slot + ".txt";
            return Path.Combine(directory, filename);
        }

        /// <summary>先把新配置写入同目录的临时文件，再替换正式文件，写入失败时保留上一份记录。</summary>
        internal static void SaveText(PvpAgentSettings settings, string slot)
        {
            string path = TextPath(slot);
            string temporary = path + ".tmp";
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            try
            {
                File.WriteAllText(temporary, Encode(settings));
                if (File.Exists(path))
                {
                    File.Replace(temporary, path, null);
                }
                else
                {
                    File.Move(temporary, path);
                }
            }
            finally
            {
                File.Delete(temporary);
            }
        }

        /// <summary>只读取完整的正式 TXT。损坏或无法读取时保持云端关闭，留给玩家清除或重新保存。</summary>
        internal static bool LoadText(out PvpAgentSettings settings, out string reason, string slot)
        {
            settings = new PvpAgentSettings();
            reason = "";
            try
            {
                string path = TextPath(slot);
                if (!File.Exists(path))
                {
                    return true;
                }
                if (new FileInfo(path).Length > RecordSizeLimit)
                {
                    throw new InvalidOperationException();
                }
                Decode(File.ReadAllText(path), settings);
                return true;
            }
            catch (Exception)
            {
                settings = new PvpAgentSettings();
                reason = "本地Agent配置无法读取或已损坏，请清除Key后重新保存";
                return false;
            }
        }

        /// <summary>撤销授权时一起清除可能由异常退出留下的临时文件。</summary>
        internal static void ClearText(string slot)
        {
            string path = TextPath(slot);
            File.Delete(path + ".tmp");
            File.Delete(path);
        }

        private static string Encode(PvpAgentSettings settings)
        {
            return JsonUtility.ToJson(new Record { key = settings.Key, model = settings.Model, enabled = settings.Enabled });
        }

        private static void Decode(string json, PvpAgentSettings settings)
        {
            Record record = JsonUtility.FromJson<Record>(json);
            if (record == null || record.version != 1 || !settings.TrySet(record.key, record.model, record.enabled, out _))
            {
                throw new InvalidOperationException();
            }
        }

#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
        [StructLayout(LayoutKind.Sequential)]
        private struct Blob
        {
            public int Length;
            public IntPtr Data;
        }
        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CryptProtectData(ref Blob input, string description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
        [DllImport("crypt32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr memory);
#endif

        /// <summary>原生输入由本方法分配/清零/释放，系统输出必须LocalFree，不能混用FreeHGlobal。</summary>
        private static byte[] Transform(byte[] bytes, bool protect)
        {
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
            var input = new Blob { Length = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
            Blob output = default;
            try
            {
                Marshal.Copy(bytes, 0, input.Data, bytes.Length);
                // 仅禁止弹窗，不设置LOCAL_MACHINE，确保密文归当前用户而不是同机所有用户。
                bool ok = protect
                    ? CryptProtectData(ref input, "RevGame DeepSeek", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
                    : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
                if (!ok)
                {
                    throw new InvalidOperationException("Windows本地凭据保护失败");
                }
                var result = new byte[output.Length];
                Marshal.Copy(output.Data, result, 0, result.Length);
                return result;
            }
            finally
            {
                for (int i = 0; i < input.Length; i++)
                {
                    Marshal.WriteByte(input.Data, i, 0);
                }
                Marshal.FreeHGlobal(input.Data);
                if (output.Data != IntPtr.Zero)
                {
                    for (int i = 0; i < output.Length; i++)
                    {
                        Marshal.WriteByte(output.Data, i, 0);
                    }
                    LocalFree(output.Data);
                }
            }
#else
            throw new PlatformNotSupportedException("本地凭据保护当前仅支持Windows");
#endif
        }
    }
}
