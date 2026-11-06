using System;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;

namespace RevGame.Pvp.Agent
{
    /// <summary>
    /// 玩家本地凭据存储：PlayerPrefs只保存Windows DPAPI密文，不保存明文Key或自制固定加密密钥。
    /// DPAPI使用当前Windows登录用户保护数据；复制到其他用户/设备不保证可解密。它不能防同用户恶意进程。
    /// 当前发布目标为Windows；其它平台明确拒绝持久化，不能静默降级为明文。
    /// </summary>
    internal static class PvpAgentSettingsStore
    {
        internal const string PlayerSlot = "RevGame.DeepSeek.ProtectedSettings.v1";
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
            string json = JsonUtility.ToJson(new Record { key = settings.Key, model = settings.Model, enabled = settings.Enabled });
            byte[] plain = Encoding.UTF8.GetBytes(json);
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
                if (saved.Length > 16384)
                {
                    throw new InvalidOperationException();
                }
                plain = Transform(Convert.FromBase64String(saved), false);
                Record record = JsonUtility.FromJson<Record>(Encoding.UTF8.GetString(plain));
                if (record == null || record.version != 1 || !settings.TrySet(record.key, record.model, record.enabled, out _))
                {
                    throw new InvalidOperationException();
                }
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
            PlayerPrefs.DeleteKey(slot);
            PlayerPrefs.Save();
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
