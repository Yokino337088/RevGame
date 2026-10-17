// ============================================================
// RevPoolDriver.cs —— 对象池的 Unity 驱动
//
// 位置：Runtime\ObjectPool\Support\
//
// 【它只做两件事】
//   ① 每帧推进"延迟回收"（RevPool.Tick）—— 延迟回收需要有人数帧，王者靠引擎主循环，
//      这边用一个常驻 MonoBehaviour 顶上（不用协程、不用 RevTask，成本最低）。
//   ② 提供池根节点 [RevObjectPool]：所有池节点挂在它下面，再往下的空闲实例都处于失活状态。
//      根节点 DontDestroyOnLoad —— 切场景不会把池里的对象连带销毁。
//
// 【生命周期】第一次用到对象池时自动创建（懒加载），业务不需要手动挂载。
//   ★ 关掉 Domain Reload 的"快速进入 Play 模式"下，静态数据会被
//     Support\RevPoolUnityHooks.cs 在 SubsystemRegistration 阶段清一次，
//     不会出现"上一局的池还活着"的鬼故事。
//
// 【注意】这是框架内部驱动，业务不要自己挂载/销毁它。
// ============================================================
using UnityEngine;

namespace Revolution
{
    /// <summary>对象池的每帧驱动与池根节点（框架内部，业务不必关心）。</summary>
    [DisallowMultipleComponent]
    public sealed class RevPoolDriver : MonoBehaviour
    {
        private static RevPoolDriver _instance;
        private static Transform _root;

        /// <summary>池根节点（所有池节点都挂在它下面）。</summary>
        internal static Transform Root
        {
            get
            {
                EnsureRunning();
                return _root;
            }
        }

        /// <summary>确保驱动与根节点存在（第一次用到池时自动调用）。</summary>
        internal static void EnsureRunning()
        {
            if (_instance != null) return;

            var holder = new GameObject("[RevObjectPool]");
            // 只有运行时才需要跨场景保留；编辑器非运行状态下调用 DontDestroyOnLoad 会告警
            if (Application.isPlaying) DontDestroyOnLoad(holder);

            _root = holder.transform;
            _instance = holder.AddComponent<RevPoolDriver>();
        }

        private void Update()
        {
            // 延迟回收的推进：两条注册表各自 tick
            RevRefPools.Tick();
            RevGameObjectPools.Tick();
        }
    }
}
