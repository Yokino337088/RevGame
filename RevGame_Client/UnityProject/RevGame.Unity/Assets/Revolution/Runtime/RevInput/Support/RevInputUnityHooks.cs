// ============================================================
// RevInputUnityHooks.cs —— Unity 生命周期接线（进 Play 复位 / 日志出口 / UI 命中判定）
//
// 位置：Runtime\RevInput\Support\
//
// 【为什么要这个文件】
//   纯 C# 内核不该认识 Unity，但"进 Play 时要清掉上一局的静态残留""日志要接到 RevLog"
//   "指针在不在 UI 上要问 EventSystem" —— 这三件事必须由引擎侧有人来接线，就是这里。
//
// 【三条铁律】
//   ① **进 Play 一定复位**：关掉 Domain Reload 后静态字段不会自己清，
//      不复位就会出现"上一局按着的键，这一局开局还在按"（最难查的那类鬼故事）。
//   ② **不改 UI 系统**：这里只**读** `EventSystem`（判断指针是否落在 UI 上），
//      不建、不改、不接管（EventSystem 由 UI 系统负责）。
//   ③ **日志只影响可见性**：接口线到 `RevLog` 的 "Input" 标签，`RevInput.VerboseLog` 关着时一条都不打。
// ============================================================

using UnityEngine;
using UnityEngine.EventSystems;

namespace Revolution
{
    /// <summary>输入模块的 Unity 接线（业务不需要认识它）。</summary>
    internal static class RevInputUnityHooks
    {
#if UNITY_EDITOR
        [UnityEditor.InitializeOnLoadMethod]
        private static void InstallInEditor() => Install();
#endif

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void InstallInPlayer()
        {
            Install();
            RevInput.ResetForNewSession();
        }

        /// <summary>装驱动、接日志出口、装默认设备、接 UI 命中判定（幂等，可以反复调）。</summary>
        private static void Install()
        {
            RevInputDriver.Install();

            RevInputLog.Log = message => RevLog.Info(message, "Input");
            RevInputLog.Warn = message => RevLog.Warn(message, "Input");
            RevInputLog.OnException = (e, what) => RevLog.Exception(e, what, "Input");

            RevInput.WorldPointerOverUI = PointerOverUI;

            RevInputCore core = RevInput.Core;
            if (core.Poll == null)
            {
                var device = new RevInputUnityDevice();
                core.Poll = device.Poll;
                core.ResetDeviceState = device.ResetForNewSession;
                core.SourceName = device.Name;
                core.AxisProvider = device.ReadAxis;
                RevInputLog.V("[RevInput] 采集口已接上：" + device.Name);
            }
        }

        /// <summary>
        /// 指针是否落在 UI 上。鼠标问 <c>IsPointerOverGameObject()</c>，手指要带 fingerId（否则永远判 false）。
        /// 场景里没有 EventSystem 时返回 false（纯 3D 场景 / 没接 UI 的工程）。
        /// </summary>
        private static bool PointerOverUI(int pointerId)
        {
            EventSystem es = EventSystem.current;
            if (es == null) return false;
            return pointerId == RevInputSnapshot.MousePointerId
                ? es.IsPointerOverGameObject()
                : es.IsPointerOverGameObject(pointerId);
        }
    }
}
