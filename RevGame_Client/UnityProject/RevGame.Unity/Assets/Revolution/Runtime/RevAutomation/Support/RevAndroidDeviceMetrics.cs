#if UNITY_ANDROID && !UNITY_EDITOR && (DEVELOPMENT_BUILD || REV_AUTOMATION)
using System.Collections.Generic;
using UnityEngine;

namespace Revolution
{
    /// <summary>由安卓 Player 在压测边界读取自身进程及设备的系统指标，JNI 引用只在查询期间有效。</summary>
    internal static class RevAndroidDeviceMetrics
    {
        /// <summary>按公开 Android API 读取 PSS（KiB）、热等级和电池温度（摄氏度），设备未提供的项返回空值。</summary>
        internal static Dictionary<string, object> Read()
        {
            using var version = new AndroidJavaClass("android.os.Build$VERSION");
            using var process = new AndroidJavaClass("android.os.Process");
            using var debug = new AndroidJavaClass("android.os.Debug");
            using var unity = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
            using var activity = unity.GetStatic<AndroidJavaObject>("currentActivity");
            int sdk = version.GetStatic<int>("SDK_INT");
            long pssKiB = debug.CallStatic<long>("getPss");
            var data = new Dictionary<string, object>
            {
                ["applicationId"] = Application.identifier,
                ["buildId"] = Application.buildGUID,
                ["deviceModel"] = SystemInfo.deviceModel,
                ["sdkInt"] = sdk,
                ["pid"] = process.CallStatic<int>("myPid"),
                ["processPssKiB"] = pssKiB > 0 ? (object)pssKiB : null,
                ["thermalStatus"] = null,
                ["batteryCelsius"] = null
            };
            if (activity == null)
            {
                return data;
            }

            // Android 10 起才有系统热等级；零是真实的无热压力，与未采到分开。
            if (sdk >= 29)
            {
                using var power = activity.Call<AndroidJavaObject>("getSystemService", "power");
                if (power != null)
                {
                    data["thermalStatus"] = power.Call<int>("getCurrentThermalStatus");
                }
            }

            // 空接收器仅读取系统最近的电池广播，不注册跨帧回调，也不保留 Activity。
            using var filter = new AndroidJavaObject("android.content.IntentFilter", "android.intent.action.BATTERY_CHANGED");
            using var intent = activity.Call<AndroidJavaObject>("registerReceiver", (AndroidJavaObject)null, filter);
            if (intent != null && intent.Call<bool>("hasExtra", "temperature"))
            {
                data["batteryCelsius"] = intent.Call<int>("getIntExtra", "temperature", 0) / 10.0;
            }
            return data;
        }
    }
}
#endif
