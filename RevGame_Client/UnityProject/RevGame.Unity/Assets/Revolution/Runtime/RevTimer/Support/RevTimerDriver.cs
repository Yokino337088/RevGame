// ============================================================
// RevTimerDriver.cs —— 计时器的 Unity 驱动（宿主适配层，零配置自动创建）
//
// 位置：Runtime\RevTimer\Support\
//
// 【它做三件事】
//   ① 每帧推进内核：Update 推进 Scaled / Unscaled / Server，FixedUpdate 推进 Fixed
//      —— 两个入口是刻意的，对齐王者"渲染帧 + 战斗逻辑帧"双驱动（60fps 与 30fps 手机推进一致）。
//   ② 自动创建：第一次用到计时器时挂一个隐藏宿主（DontDestroyOnLoad），业务不摆物体、不挂脚本。
//   ③ **重复驱动防御**：场景里被手动多挂了一个驱动 → 关掉它并告警。
//      计时器最怕同帧推进两次：所有倒计时都会走快一倍，而且现象很难往"多了一个驱动"上想。
//
// 【业务想自己驱动？】调一次 <see cref="RevTimer.Tick"/> 即可，本驱动会自动让位（见门面注释）。
//
// 【本模块只有这个文件、UnityHooks 需要 UnityEngine】内核、句柄、槽位表、服务器时钟、秒表都是纯 C#，
//   可以在普通 .NET 工程里直接跑断言。
// ============================================================
using UnityEngine;

namespace Revolution
{
    /// <summary>每帧驱动 <see cref="RevTimerCore"/> 的隐藏宿主（自动创建，无需配置）。</summary>
    [DisallowMultipleComponent]
    internal sealed class RevTimerDriver : MonoBehaviour
    {
        private static RevTimerDriver _instance;

        /// <summary>把"首次用到就建宿主"这件事接到内核上（内核是纯 C#，不认 GameObject）。</summary>
        internal static void Install()
        {
            RevTimerCore.EnsureDriver = EnsureDefault;

            // 把"此刻真实时间"接给内核：SyncServerTime 在第一帧 Tick 之前被调用时，也能拿到正确锚点。
            RevTimer.Core.RealtimeProvider = () => Time.realtimeSinceStartupAsDouble;
        }

        internal static void EnsureDefault()
        {
            if (!Application.isPlaying) return;                 // 编辑模式不偷偷建 GameObject
            if (_instance != null) return;

            GameObject host = new GameObject("[RevTimer]");
            host.hideFlags = HideFlags.HideAndDontSave;
            Object.DontDestroyOnLoad(host);
            _instance = host.AddComponent<RevTimerDriver>();
        }

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                RevLog.Warn(
                    "[RevTimer] 已经有一个计时器驱动在跑，这个多余的驱动已关闭。" +
                    "（计时器最怕同一帧被推进两次：所有倒计时都会走快一倍。请删掉场景里手动挂的这个组件。）", "Timer");
                enabled = false;
                return;
            }

            _instance = this;
        }

        private void Update()
        {
            // 只有"渲染帧被手动驱动"时才让位。以前 Update 与 FixedUpdate 共用一个标记：
            // 业务只在自己的 FixedUpdate 里调 TickFixed，会把这里也关掉，UI 倒计时/After/At 全部静默停摆。
            if (RevTimer.Core.ManualRenderDriven) return;
            RevTimer.Core.Tick(Time.deltaTime, Time.unscaledDeltaTime, Time.realtimeSinceStartupAsDouble);
        }

        private void FixedUpdate()
        {
            // 同理：只有"逻辑帧被手动驱动"才让 FixedUpdate 让位，Fixed 域不受渲染帧手动驱动影响。
            if (RevTimer.Core.ManualFixedDriven) return;
            RevTimer.Core.TickFixed(Time.fixedDeltaTime);
        }

        private void OnDestroy()
        {
            if (_instance == this) _instance = null;
        }
    }
}
