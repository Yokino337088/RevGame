// ============================================================
// RevAppLifecycleDriver.cs —— 生命周期的隐藏宿主（零配置：进游戏自动就位）
//
// 位置：Runtime\RevAppLifecycle\Support\
//
// 【职责】
//   把 Unity 的应用级回调转成 RevAppLifecycle 的派发：
//     OnApplicationPause / OnApplicationFocus → 前后台状态（重复通知的去重在门面里做）
//     OnApplicationQuit                       → Quitting
//
// 【为什么用"隐藏宿主 + RuntimeInitializeOnLoadMethod"】
//   业务不该为了收一个"回到前台"的事件，就往场景里挂一个 MonoBehaviour ——
//   那是每做一个项目都要重来一遍的体力活，还容易漏（漏了就成了"偶现 bug"）。
//   宿主由框架自己建：不用摆、不用配、不显示在 Hierarchy，Play 起来就已经在工作。
//
// 【为什么编辑器里失焦也会派发】
//   点击 Unity 之外的窗口 → 编辑器同样按"失焦"处理。这是**故意的**：
//   它让"切后台 → 恢复"这条链路在编辑器里就能被试到，而不必等到真机。
//   不想要这个效果的业务，自己判断 Application.isEditor 即可。
// ============================================================
using UnityEngine;

namespace Revolution
{
    /// <summary>生命周期隐藏宿主（业务永远不需要认识它）。</summary>
    internal sealed class RevAppLifecycleDriver : MonoBehaviour
    {
        private static RevAppLifecycleDriver _instance;

        /// <summary>进 Play 时自动就位（早于首个场景加载，业务在 Awake 里订阅也不会漏事件）。</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void AutoInstall()
        {
            if (!Application.isPlaying) { return; }
            if (_instance != null) { return; }                  // 关闭 Domain Reload 时可能还留着上一局的宿主

            RevAppLifecycle.ResetForNewSession();

            GameObject host = new GameObject("[RevAppLifecycle]");
            host.hideFlags = HideFlags.HideAndDontSave;
            Object.DontDestroyOnLoad(host);
            _instance = host.AddComponent<RevAppLifecycleDriver>();
        }

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                enabled = false;                                // 场景里被手挂了一份：只留一个，不双推
                return;
            }

            _instance = this;
        }

        private void OnApplicationPause(bool pause)
        {
            RevAppLifecycle.SetBackground(pause);
        }

        private void OnApplicationFocus(bool hasFocus)
        {
            // ★ 不做方向判断、也不判断"是不是和 Pause 重复"：统一交给门面的状态去重，
            //   这样"Pause 先到还是 Focus 先到"都不会漏派发、也不会派发两次。
            RevAppLifecycle.SetBackground(!hasFocus);
        }

        private void OnApplicationQuit()
        {
            RevAppLifecycle.NotifyQuitting();
        }

        private void OnDestroy()
        {
            if (_instance == this) { _instance = null; }
        }
    }
}
