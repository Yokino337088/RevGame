// ============================================================
// RevPoolUnityHooks.cs —— 对象池的 Unity 生命周期钩子
//
// 位置：Runtime\ObjectPool\Support\
//
// 【为什么要这个文件】
//   池的核心（Core\ 下的 RevPoolCore / RevRefPools / RevGameObjectPool…）是**纯 C#**
//   （RevGameObjectPool 之外），不碰 UnityEngine，好处是能在工程外跑断言。
//   代价是"日志往哪打、进 Play 要清什么"这类 Unity 的事没人做 —— 由这个薄文件补上。
//
// 【它做两件事】
//   ① 装日志出口：没有出口时池的告警会写到标准错误，在 Unity 里不够显眼，
//      这里接到 Debug.LogWarning。★ 业务自己设过就不覆盖。
//   ② 进 Play 时清空上一局的池：关闭 Domain Reload 时静态字段不会自动清，
//      上一次运行的池（以及一批已销毁的对象引用）会原封不动留到这一次。
// ============================================================
using UnityEngine;

namespace Revolution
{
    /// <summary>把对象池接到 Unity 的生命周期上（日志出口 + 进 Play 清理）。</summary>
    internal static class RevPoolUnityHooks
    {
#if UNITY_EDITOR
        // 编辑器里（没运行游戏时）编排工具也会用到池，所以额外接一次
        [UnityEditor.InitializeOnLoadMethod]
        private static void InstallInEditor() => Install();
#endif

        // SubsystemRegistration：进入 Play 后最早的阶段之一（早于场景加载、早于业务代码）
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void InstallInPlayer()
        {
            Install();

            // 关闭 Domain Reload 时静态注册表会残留 → 手动清一次
            RevPool.DestroyAll();
        }

        private static void Install()
        {
            // 池的告警统一走框架日志系统（tag = Pool）；业务之后想换出口再赋值覆盖即可
            RevPoolLog.Sink = message => RevLog.Warn(message, "Pool");
        }
    }
}
