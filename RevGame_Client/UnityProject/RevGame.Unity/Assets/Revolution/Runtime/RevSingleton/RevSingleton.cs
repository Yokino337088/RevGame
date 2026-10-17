// ============================================================
// RevSingleton.cs —— 单例基类（框架核心公共设施）
// 【用法】什么都不用写，直接继承即可：
//     public class RevResBootstrap : RevSingleton<RevResBootstrap>
//     {
//     }
//     RevResBootstrap.Instance.Init();
//
//   若想防止外部 new 出第二个实例，可以自己补一个私有构造（可选、非必需）：
//     private RevResBootstrap() { }
//
// 【设计要点】
//   ① 用 Lazy<T> 懒加载：首次访问 Instance 才创建；
//   ② LazyThreadSafetyMode.ExecutionAndPublication 语义等价于"双重检查锁"，
//      保证多线程下也只创建一次；
//   ③ 构造函数可见性不限，写不写都行：
//      · 不写     → C# 自动生成隐式 public 无参构造，照样能实例化；
//      · 写 private → 防外部 new，推荐但不强制。
//      反射查找同时包含 public 与 nonPublic，两种情况都能命中。
//
// 【注意】本类是纯 C# 类，不能用于 MonoBehaviour。
//         要"挂在物体上的单例"请用同目录的：
//           · RevSingletonMono<T>      —— 自己摆一个（Inspector 配参数 / 需要 Unity 生命周期）
//           · RevSingletonAutoMono<T>  —— 不摆也行，首次访问 Instance 自动建隐藏宿主
//         资源系统里只有 RevResBootstrap 需要它；RevResManager / RevAsyncLoadPump 是静态类。
// ============================================================
using System;
using System.Reflection;
using System.Threading;

namespace Revolution
{
    /// <summary>单例基类：C# 标准写法（Lazy 懒加载 + 线程安全 + 反射创建实例）</summary>
    public abstract class RevSingleton<T> where T : class
    {
        // Lazy<T>：ExecutionAndPublication = 双重检查锁语义，多线程只创建一次
        private static readonly Lazy<T> _lazy = new Lazy<T>(CreateInstance, LazyThreadSafetyMode.ExecutionAndPublication);

        /// <summary>全局唯一实例（首次访问时创建）</summary>
        public static T Instance => _lazy.Value;

        /// <summary>创建实例：只会在首次访问 Instance 时被 Lazy 调用一次</summary>
        private static T CreateInstance()
        {
            // BindingFlags.Instance   ：实例成员
            // BindingFlags.Public     ：public 构造 —— 不写构造函数时，编译器生成的隐式 public 无参构造靠它命中
            // BindingFlags.NonPublic  ：private / protected / internal 构造
            // types: Type.EmptyTypes  ：只匹配"无参"构造函数
            ConstructorInfo ctor = typeof(T).GetConstructor(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                types: Type.EmptyTypes,
                modifiers: null);

            if (ctor == null)
            {
                throw new InvalidOperationException(
                    $"[RevSingleton] {typeof(T).Name} 没有无参构造函数。" +
                    $"提示：一旦显式声明了带参构造函数，编译器就不再自动生成无参构造，" +
                    $"此时补一个 private {typeof(T).Name}() {{ }} 即可。");
            }

            return (T)ctor.Invoke(null);
        }
    }
}
