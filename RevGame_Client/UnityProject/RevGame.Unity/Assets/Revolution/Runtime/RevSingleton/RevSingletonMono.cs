// ============================================================
// RevSingletonMono.cs —— 组件单例基类（你**自己摆**在场景/预制体上）
//
// 位置：Runtime\RevSingleton\
//
// 【三个单例基类怎么选】
//   RevSingleton<T>          纯 C# 单例：不挂物体、可脱离 Unity 单测（工具类、管理器内核用它）
//   RevSingletonMono<T>      **你要摆一个**的组件单例（本文件）：需要在 Inspector 里配参数、或被 Unity 调生命周期时用
//   RevSingletonAutoMono<T>  不摆也行：首次访问 Instance 时自动建隐藏宿主（最省事，推荐）
//
// 【相比"到处写 static Instance"多做的三件事】
//   ① **重复实例不再静默**：场景里被复制出两份时，只有先 Awake 的那个生效，
//      多余的那份被销毁并明确告警（旧实现是 `Destroy(this)` 一声不吭 —— 你根本不知道在用哪一个）；
//   ② **销毁后不留悬空引用**：OnDestroy 里清静态字段。
//      旧实现只赋值不清 → 物体销毁后 Instance 返回一个"假 null"的已销毁对象，
//      调用它的方法会 MissingReferenceException，而报错现场指向的往往是无辜的调用方；
//   ③ **子类不可能漏掉单例赋值**：基类 Awake 是私有的（子类无法覆盖），要初始化请写 OnInit()。
//
// 【★ 想用 Update 之类的生命周期？】直接写在子类里，不受影响 —— 只有 Awake/OnDestroy 归基类管。
//
// 【不自动跨场景】摆在哪就在哪（随场景销毁而销毁）。
//   要"一次创建、全游戏常驻"请用 RevSingletonAutoMono；要手动常驻就在子类 Awake 后自己 DontDestroyOnLoad。
// ============================================================
using UnityEngine;

namespace Revolution
{
    /// <summary>
    /// 场景里的组件单例基类：实例由你摆；重复摆放会被销毁并告警，销毁后自动清引用。
    /// <code>public sealed class GameManager : RevSingletonMono&lt;GameManager&gt; { protected override void OnInit() { ... } }</code>
    /// </summary>
    /// <typeparam name="T">自己的类型（CRTP：让 Instance 直接是 T，不用转型）</typeparam>
    public abstract class RevSingletonMono<T> : MonoBehaviour where T : RevSingletonMono<T>
    {
        private static T _instance;

        /// <summary>
        /// 全局唯一实例。**场景里没摆时返回 null**（不自动创建 —— 要自动创建请用 <see cref="RevSingletonAutoMono{T}"/>）。
        /// </summary>
        public static T Instance => _instance;

        /// <summary>有没有活着的实例（摆了、且还没销毁）。</summary>
        public static bool HasInstance => _instance != null;

        /// <summary>子类初始化钩子：基类 Awake 是私有的，所以初始化写这里（不要试图覆盖 Awake）。</summary>
        protected virtual void OnInit()
        {
        }

        // ★ 私有 Awake：Unity 会调用它，但子类无法"覆盖掉"——单例赋值不可能被跳过
        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                RevLog.Warn(
                    $"[{typeof(T).Name}] 场景里存在多个实例：保留先 Awake 的那个，本组件已销毁。" +
                    "请删掉重复摆放的这一份（或在编辑器里搜索该组件名确认）。", "Singleton");

                Destroy(this);          // 只销毁这个组件：所在物体上可能还挂着别的东西，不连坐销毁
                return;
            }

            _instance = (T)this;
            OnInit();
        }

        private void OnDestroy()
        {
            if (_instance == this) _instance = null;   // ★ 清掉静态引用：不留"假 null"的已销毁对象
        }
    }
}
