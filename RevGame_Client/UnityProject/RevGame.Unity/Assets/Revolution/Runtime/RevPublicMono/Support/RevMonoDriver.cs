// ============================================================
// RevMonoDriver.cs —— 公共 Mono 模块的 Unity 驱动（宿主适配层，零配置自动创建）
//
// 位置：Runtime\RevPublicMono\Support\
//
// 【它做三件事】
//   ① 把 MonoBehaviour 的三个帧回调转给内核：Update / LateUpdate / FixedUpdate → 对应的监听列表；
//   ② 当协程宿主：纯 C# 类不能 StartCoroutine，这里替它跑（并做异常隔离，见下）；
//   ③ **重复驱动防御**：场景里被手动多挂了一个驱动 → 关掉它并告警。
//      帧回调最怕同帧跑两次：所有监听者会每帧执行两遍（表现是"速度翻倍"，很难往"多了一个驱动"上想）。
//
// 【★ 协程的异常隔离】Unity 原生协程抛异常会静默中断那一条协程（且只报一次到 Console）。
//   这里用一层薄包装：捕获到异常 → 走 RevMono.OnException（默认进框架日志系统）→ 结束这条协程。
//   其余协程与监听者不受影响。
//
// 【本模块只有这个文件与 UnityHooks 需要 UnityEngine】内核与监听列表是纯 C#（可工程外断言）。
// ============================================================
using System;
using System.Collections;
using UnityEngine;

namespace Revolution
{
    /// <summary>驱动 <see cref="RevMonoCore"/> 的隐藏宿主 + 协程宿主（自动创建，无需配置）。</summary>
    [DisallowMultipleComponent]
    internal sealed class RevMonoDriver : MonoBehaviour
    {
        private static RevMonoDriver _instance;

        /// <summary>把"首次用到就建宿主"接到内核上（内核是纯 C#，不认 GameObject）。</summary>
        internal static void Install() => RevMonoCore.EnsureDriver = EnsureDefault;

        internal static void ResetForNewSession()
        {
            // 关闭 Domain Reload 时，Unity 不会重建静态字段；隐藏的 RevMono GameObject 因此可能从上一局 Play 留到下一局。
            // 旧协程必须停止，避免继续操作上一局对象；但有效的宿主仍然存在，所以不能把 DriverReady 错设为 false，
            // 否则新一局会误以为没有驱动，或重复创建第二个宿主。
            if (_instance != null)
            {
                _instance.StopAllCoroutines();
                RevMonoCore.DriverReady = true;
            }
            else RevMonoCore.DriverReady = false;
        }

        internal static void EnsureDefault()
        {
            if (!Application.isPlaying) return;                     // 编辑模式不偷偷建 GameObject
            if (_instance != null) return;

            GameObject host = new GameObject("[RevMono]");
            host.hideFlags = HideFlags.HideAndDontSave;
            UnityEngine.Object.DontDestroyOnLoad(host);      // 本文件同时 using System → Object 要写全限定
            _instance = host.AddComponent<RevMonoDriver>();
            RevMonoCore.DriverReady = true;
        }

        // ==================== 协程 ====================

        internal static Coroutine StartRoutine(IEnumerator routine)
        {
            if (routine == null) return null;

            if (!Application.isPlaying)
            {
                RevLog.Warn("[RevMono] 非运行状态不能启动协程（编辑器里没 Play）：本次调用已忽略。", "Mono");
                RevMono.Core.ReportFailure(RevMonoErrorReason.NotPlaying, "非运行状态启动协程");
                return null;
            }

            EnsureDefault();
            if (_instance == null) return null;

            return _instance.StartCoroutine(_instance.Guarded(routine));
        }

        internal static void StopRoutine(Coroutine routine)
        {
            if (routine == null || _instance == null) return;

            _instance.StopCoroutine(routine);
        }

        internal static void StopAllRoutines()
        {
            if (_instance == null) return;

            _instance.StopAllCoroutines();
        }

        /// <summary>包一层：捕获业务协程里的异常 → 报到统一出口 → 结束这条协程（其余不受影响）。</summary>
        private IEnumerator Guarded(IEnumerator routine)
        {
            while (true)
            {
                object yielded;

                try
                {
                    if (!routine.MoveNext()) yield break;
                    yielded = routine.Current;
                }
                catch (Exception e)
                {
                    RevMono.Core.ReportFailure(RevMonoErrorReason.CallbackThrew, "协程 " + Describe(routine));
                    RevMono.Core.ReportException(e,
                        $"[RevMono] 协程 {Describe(routine)} 抛异常（已隔离，这条协程结束；其余协程与监听者照常）。");
                    yield break;
                }

                // 把嵌套 IEnumerator 也过同一层守卫，否则异常发生在 Unity 执行子协程时会绕过本驱动。
                if (yielded is IEnumerator nested) yield return Guarded(nested);
                else yield return yielded;                         // ★ yield 必须在 try 之外（C# 语法限制）
            }
        }

        private static string Describe(IEnumerator routine)
        {
            Type type = routine.GetType();
            return type.DeclaringType != null ? type.DeclaringType.Name + "." + type.Name : type.Name;
        }

        // ==================== 生命周期 ====================

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                RevLog.Warn(
                    "[RevMono] 已经有一个公共 Mono 驱动在跑，这个多余的驱动已关闭。" +
                    "（帧回调最怕同一帧跑两次：所有监听者会每帧执行两遍。请删掉场景里手动挂的这个组件。）", "Mono");
                enabled = false;
                return;
            }

            _instance = this;
            RevMonoCore.DriverReady = true;
        }

        private void Update() => RevMono.Core.Tick(RevMonoPhase.Update);

        private void LateUpdate() => RevMono.Core.Tick(RevMonoPhase.LateUpdate);

        private void FixedUpdate() => RevMono.Core.Tick(RevMonoPhase.FixedUpdate);

        private void OnDestroy()
        {
            if (_instance == this)
            {
                _instance = null;
                RevMonoCore.DriverReady = false;
            }
        }
    }
}
