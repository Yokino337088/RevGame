// ============================================================
// RevSingletonAutoMono.cs —— 自动创建的组件单例基类（★ 最省事，推荐）
//
// 位置：Runtime\RevSingleton\
//
// 【它解决什么】"我需要一个全局管理器，但不想每开一个场景都记得把它摆进去。"
//   首次访问 Instance 时自动创建一个隐藏宿主（DontDestroyOnLoad），此后全游戏常驻。
//
// 【零配置用法】连场景都不用碰：
// <code>
// public sealed class AudioDirector : RevSingletonAutoMono&lt;AudioDirector&gt;
// {
//     protected override void OnInit() { /* 只初始化一次 */ }
//     public void PlayBgm(string name) { ... }      // 业务直接写公开方法
// }
// AudioDirector.Instance.PlayBgm("login");          // 第一次访问时自动创建宿主
// </code>
//
// 【★ 相比旧框架 SingletonAutoMono 多做的四件事】
//   ① **编辑态不偷偷建物体**：非运行状态访问 Instance 只告警、不创建
//      （旧实现会在编辑器里 new GameObject —— 可能被存进场景，变成"莫名的空物体"）；
//   ② **退出期不再创建**：应用正在退出时创建物体会留下关不掉的尾巴，这里明确拒绝并告警；
//   ③ **重复实例有明确归属**：先 Awake 的生效，后来者销毁自己并告警（不再静默）；
//   ④ **销毁后自动修复**：OnDestroy 清掉静态引用 —— 下次访问会重新创建（而不是拿到已销毁对象）。
//
// 【★ 两条使用规则】
//   1. **同一个类型只用一种方式**：要么用 Auto（不摆），要么用 RevSingletonMono（摆一个）。
//      混用会出现"两个实例"的告警（谁先 Awake 谁生效）—— 规则简单点，别给自己找麻烦。
//   2. 自动创建的宿主**在 Hierarchy 里是隐藏的**（HideAndDontSave）。
//      想让它可见/想在 Inspector 里配参数 → 改成 RevSingletonMono，自己摆一个。
// ============================================================
using UnityEngine;

namespace Revolution
{
    /// <summary>
    /// 自动创建的组件单例基类：不摆物体也行，首次访问 Instance 时自动建隐藏宿主（跨场景常驻）。
    /// <code>public sealed class GameBoot : RevSingletonAutoMono&lt;GameBoot&gt; { }</code>
    /// </summary>
    /// <typeparam name="T">自己的类型（CRTP：让 Instance 直接是 T，不用转型）</typeparam>
    public abstract class RevSingletonAutoMono<T> : MonoBehaviour where T : RevSingletonAutoMono<T>
    {
        private static T _instance;

        /// <summary>
        /// 全局唯一实例：没有就自动创建（运行期）。
        /// ★ 非运行状态（编辑器里没 Play）或应用正在退出时**不会创建**，返回 null 并告警 —— 绝不偷偷建物体。
        /// </summary>
        public static T Instance
        {
            get
            {
                if (_instance != null) return _instance;          // Unity 的 == 能识别"已销毁"，所以这里天然自愈

                if (!Application.isPlaying)
                {
                    RevLog.Warn($"[{typeof(T).Name}] 在非运行状态访问了 Instance：未创建自动宿主（运行时访问会自动创建）。", "Singleton");
                    return null;
                }

                if (RevSingletonRuntimeState.Quitting)
                {
                    RevLog.Warn($"[{typeof(T).Name}] 应用正在退出：不再创建单例实例（退出期创建物体会留下关不掉的尾巴）。", "Singleton");
                    return null;
                }

                return Create();
            }
        }

        /// <summary>有没有活着的实例（不触发创建）。</summary>
        public static bool HasInstance => _instance != null;

        /// <summary>子类初始化钩子：创建（或场景里那个 Awake）之后会调一次。</summary>
        protected virtual void OnInit()
        {
        }

        private static T Create()
        {
            var host = new GameObject($"[{typeof(T).Name}]");
            host.hideFlags = HideFlags.HideAndDontSave;    // 框架内部宿主：不进场景、不显示在 Hierarchy
            Object.DontDestroyOnLoad(host);

            // AddComponent 会触发 Awake → 走和"手动摆"完全相同的赋值路径（不写第二份赋值逻辑）
            return host.AddComponent<T>();
        }

        // ★ 私有 Awake：子类无法覆盖，单例赋值不会被跳过（初始化写 OnInit）
        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                RevLog.Warn(
                    $"[{typeof(T).Name}] 已经有一个实例了：保留先 Awake 的那个，本组件已销毁。" +
                    "★ 同一个类型请只用一种方式（要么用 Auto 不摆，要么用 RevSingletonMono 摆一个），不要混用。", "Singleton");

                Destroy(this);
                return;
            }

            _instance = (T)this;
            OnInit();
        }

        private void OnDestroy()
        {
            if (_instance == this) _instance = null;       // 清掉 → 下次访问会重新创建（而不是拿到已销毁对象）
        }
    }

    /// <summary>单例基类共用的运行期状态（放这里是为了让泛型基类不膨胀）。</summary>
    internal static class RevSingletonRuntimeState
    {
        /// <summary>应用是否正在退出（退出期禁止自动创建单例宿主）。</summary>
        internal static bool Quitting { get; private set; }

        // 静态构造：第一次用到时挂上退出通知（一个 Application.quitting 就够，所有泛型类型共用）
        static RevSingletonRuntimeState()
        {
            Application.quitting += () => Quitting = true;
        }

        // SubsystemRegistration：进 Play 最先执行的阶段之一。关掉 Domain Reload 时静态字段会残留，
        // 上一局的 Quitting=true 必须清掉，否则"这一次进 Play 再也创建不出单例"（最难查的那种鬼故事）。
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetForNewSession() => Quitting = false;
    }
}
