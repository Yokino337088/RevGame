// RevSoundDriver.cs —— 宿主适配：每帧推一次内核（零配置）
//
// 【小白不用管它】第一次调用 RevSound.Play 时会自动创建一个隐藏宿主（DontDestroyOnLoad）来驱动内核 ——
//   不需要摆任何场景物体、不需要挂脚本、不需要写 Update。
//
// 【想自己驱动？】在你的 Update 里调 RevSound.Tick(Time.deltaTime) 即可 —— 之后本驱动会自动让位
//   （内核一旦被手动 Tick 就不再接受自动驱动，避免"同一帧推进两次"导致淡入淡出与结束判定都不准）。
//
// 【本框架其余部分不碰引擎】只有这个文件与 RevSoundAssets / RevSoundCore 需要 UnityEngine；
//   句柄、分类表、槽位表都是纯 C#，可以脱离引擎单测。

using UnityEngine;

namespace Revolution
{
    /// <summary>每帧驱动 <see cref="RevSoundCore"/> 的隐藏宿主（自动创建，无需配置）。</summary>
    [DisallowMultipleComponent]
    internal sealed class RevSoundDriver : MonoBehaviour
    {
        private static RevSoundDriver _instance;
        private RevSoundCore _core;

        internal static void EnsureDefault(RevSoundCore core)
        {
            if (!Application.isPlaying) return;                 // 编辑模式不偷偷建 GameObject
            if (_instance != null && _instance._core == core) return;

            GameObject host = new GameObject("[RevSoundDriver]");
            host.hideFlags = HideFlags.HideAndDontSave;
            Object.DontDestroyOnLoad(host);
            _instance = host.AddComponent<RevSoundDriver>();
            _instance._core = core;
        }

        private void Update()
        {
            if (_core == null || _core.ManualDriven) return;    // 被手动驱动接管后自动让位
            _core.Tick(Time.deltaTime);
        }

        private void OnDestroy()
        {
            if (_instance == this) _instance = null;
        }
    }
}
