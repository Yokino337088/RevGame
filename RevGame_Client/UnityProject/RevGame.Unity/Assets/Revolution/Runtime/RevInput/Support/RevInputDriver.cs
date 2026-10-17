// ============================================================
// RevInputDriver.cs —— 隐藏宿主：每帧采集一次输入（零配置）
//
// 位置：Runtime\RevInput\Support\
//
// 【职责】
//   ① 第一次用到 `RevInput` 时自动建一个 `[RevInput]` 宿主（DontDestroyOnLoad、不显示在 Hierarchy）；
//   ② 每帧把 `Time.*` 交给内核（内核是纯 C#，不认识 Time）；
//   ③ **失去焦点 / 切后台时自动复位** —— 这是"切回来按键卡住"的正解：
//      抬起事件已经在系统切换时丢了，状态必须当成"全部松手"，否则角色会一直跑。
//
// 【两条注意】
//   · 手动驱动（`RevInput.ManualDriven = true`）时宿主**让位**，不推帧（自动化测试用）；
//   · 场景里不小心多挂了一个宿主 → 只保留一个，另一个自己停用（不报错、不双推）。
// ============================================================

using UnityEngine;

namespace Revolution
{
    /// <summary>输入的隐藏宿主（业务永远不需要认识它）。</summary>
    internal sealed class RevInputDriver : MonoBehaviour
    {
        private static RevInputDriver _instance;

        /// <summary>把"首次用到就建宿主"接到纯 C# 内核上（由 <see cref="RevInputUnityHooks"/> 调用）。</summary>
        internal static void Install() => RevInputCore.EnsureDriver = EnsureDefault;

        /// <summary>建宿主（编辑器未运行时什么都不做：不在编辑模式偷偷塞物体）。</summary>
        internal static void EnsureDefault()
        {
            if (!Application.isPlaying) return;
            if (_instance != null) return;

            GameObject host = new GameObject("[RevInput]");
            host.hideFlags = HideFlags.HideAndDontSave;
            Object.DontDestroyOnLoad(host);
            _instance = host.AddComponent<RevInputDriver>();
            RevInputLog.V("[RevInput] 隐藏宿主已创建");
        }

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                enabled = false;
                RevInputLog.W("[RevInput] 场景里出现了第二个驱动宿主，已停用它（可能是场景里手挂的）");
                return;
            }
            _instance = this;
        }

        private void Update()
        {
            RevInputCore core = RevInput.Core;
            if (core.ManualDriven) return;      // 业务要自己驱动：让位
            core.Tick(Time.deltaTime, Time.unscaledDeltaTime, Time.realtimeSinceStartup, Time.frameCount);
        }

        private void OnApplicationFocus(bool hasFocus)
        {
            if (!hasFocus) RevInput.ResetAll("失去焦点");
        }

        private void OnApplicationPause(bool pause)
        {
            if (pause) RevInput.ResetAll("切后台");
        }

        private void OnDestroy()
        {
            if (_instance == this) _instance = null;
        }
    }
}
