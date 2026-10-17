// RevServiceBuilder.cs —— 【第 2 个要看的文件】注册服务（只有组合根该干这件事）
//
//   static readonly RevServiceLocator Services = RevServiceLocator.Create()
//       .AddSingleton<ISoundService, SoundService>()      // 接口 → 实现（零反射，启动期不创建）
//       .AddSingleton<IConfigService>(Config.Load())      // 已有实例（业务自己的对象，容器不释放它）
//       .AddScoped<ICombatContext, CombatContext>()       // 每局一份（在作用域里取）
//       .Build();
//
// 【为什么要"注册期"和"运行期"分开】
//   王者的纪律是"组合根之外禁止赋值"，但它只是口头纪律（一批谁都能改的静态字段，任何代码都能偷偷改写）。
//   这里把纪律变成类型：注册只能在这个 Builder 上做，Build() 之后连改的机会都没有。
//
// 【重复注册】直接报错，不静默覆盖（静默覆盖和老单例各处自己 new，都属于"事故温床"——这是参考实现交过学费的教训）。
//
// 【为什么没有 [Inject] / 构造注入（和王者同一个选择）】
//   DI 容器的核心动作是运行时反射构造 —— 在 IL2CPP 热路径上是性能与 AOT 裁剪的双重风险，
//   而且依赖被藏进特性里，"这个系统依赖谁"要翻容器配置才看得见。
//   这里延续王者的哲学：显式注册（本文件）+ 显式获取（RevServiceLocator）+ 零反射（泛型 new() 工厂）。
//   完整利弊论证见《服务定位器使用说明》第五章。

using System;
using System.Collections.Generic;

namespace Revolution
{
    /// <summary>
    /// 服务注册表（构建期）：链式声明"有哪些服务、怎么造、活多久"，最后 <see cref="Build"/> 成只读容器。
    /// <para>构建器是一次性的：<see cref="Build"/> 之后再调用 Add 会抛异常（免得你以为改了装配其实没改）。</para>
    /// </summary>
    public sealed class RevServiceBuilder
    {
        private readonly Dictionary<Type, RevServiceDescriptor> _descriptors;
        private bool _built;

        internal RevServiceBuilder(int capacity) => _descriptors = new Dictionary<Type, RevServiceDescriptor>(capacity);

        // ============================================================
        // 注册：整个应用一份（Singleton）
        // ============================================================

        /// <summary>
        /// 注册单例：<typeparamref name="TService"/> 是业务取服务用的类型（写接口），
        /// <typeparamref name="TImpl"/> 是实现（必须有公开无参构造 —— 需要参数请用工厂重载）。
        /// <code>.AddSingleton&lt;ISoundService, SoundService&gt;()</code>
        /// </summary>
        public RevServiceBuilder AddSingleton<TService, TImpl>()
            where TService : class
            where TImpl : class, TService, new()
            => Add(typeof(TService), RevServiceLifetime.Singleton, _ => new TImpl(), ownsInstance: true,
                   origin: $"AddSingleton<{typeof(TService).Name}, {typeof(TImpl).Name}>()");

        /// <summary>
        /// 注册单例（已有实例）：适合"宿主自己创建的对象"（配置、MonoBehaviour 适配器……）。
        /// <para>★ 这个实例<b>由业务自己负责释放</b> —— 容器只持有它、不会 Dispose 它。</para>
        /// </summary>
        public RevServiceBuilder AddSingleton<TService>(TService instance) where TService : class
        {
            if (instance == null) throw new ArgumentNullException(nameof(instance), $"AddSingleton<{typeof(TService).Name}>(instance) 收到 null");

            return Add(typeof(TService), RevServiceLifetime.Singleton, _ => instance, ownsInstance: false,
                       origin: $"AddSingleton<{typeof(TService).Name}>(实例)");
        }

        /// <summary>
        /// 注册单例（工厂）：需要构造参数、或要先读配置时用它。
        /// <code>.AddSingleton&lt;ISoundService&gt;(services =&gt; new SoundService(services.GetRequired&lt;IConfigService&gt;().SoundPath))</code>
        /// <para>工厂里可以取其它服务（框架会检测循环依赖并报错），但<b>不要</b>在这里取自己。</para>
        /// </summary>
        public RevServiceBuilder AddSingleton<TService>(Func<RevIServiceLocator, TService> factory) where TService : class
        {
            if (factory == null) throw new ArgumentNullException(nameof(factory));

            return Add(typeof(TService), RevServiceLifetime.Singleton,
                       services => factory(services), ownsInstance: true,
                       origin: $"AddSingleton<{typeof(TService).Name}>(工厂)");
        }

        // ============================================================
        // 注册：每个作用域一份（Scoped）
        // ============================================================

        /// <summary>
        /// 注册作用域服务：每个 <see cref="RevServiceLocator.CreateScope"/> 出来的容器各有一份。
        /// <code>.AddScoped&lt;ICombatContext, CombatContext&gt;()</code>
        /// <para>★ Scoped 服务<b>不能在根容器上取</b>（根没有"一轮"的语义）—— 必须在作用域里取，框架会明确报错提醒。</para>
        /// </summary>
        public RevServiceBuilder AddScoped<TService, TImpl>()
            where TService : class
            where TImpl : class, TService, new()
            => Add(typeof(TService), RevServiceLifetime.Scoped, _ => new TImpl(), ownsInstance: true,
                   origin: $"AddScoped<{typeof(TService).Name}, {typeof(TImpl).Name}>()");

        /// <summary>注册作用域服务（工厂）：需要构造参数时用它（写法同 <see cref="AddSingleton{TService}(Func{RevIServiceLocator, TService})"/>）。</summary>
        public RevServiceBuilder AddScoped<TService>(Func<RevIServiceLocator, TService> factory) where TService : class
        {
            if (factory == null) throw new ArgumentNullException(nameof(factory));

            return Add(typeof(TService), RevServiceLifetime.Scoped,
                       services => factory(services), ownsInstance: true,
                       origin: $"AddScoped<{typeof(TService).Name}>(工厂)");
        }

        // ============================================================
        // 构建
        // ============================================================

        /// <summary>
        /// 冻结装配并生成只读容器。
        /// <para>建议在启动期调用一次，把结果缓存到 <c>static readonly</c> 字段（容器本身可以跨场景复用）。</para>
        /// </summary>
        /// <exception cref="InvalidOperationException">重复 Build</exception>
        public RevServiceLocator Build()
        {
            if (_built)
                throw new InvalidOperationException("这个 RevServiceBuilder 已经 Build 过了 —— 容器是不可变的，请把它缓存起来复用，不要重复 Build");

            _built = true;
            return new RevServiceLocator(_descriptors);
        }

        // ============================================================
        // 内部
        // ============================================================

        private RevServiceBuilder Add(Type serviceType, RevServiceLifetime lifetime,
                                      Func<RevIServiceLocator, object> factory, bool ownsInstance, string origin)
        {
            if (_built)
                throw new InvalidOperationException($"已经 Build 过了，不能再注册 {serviceType.Name}（要改装配请改构建期代码，然后重新 Build 一个容器）");

            if (_descriptors.ContainsKey(serviceType))
                throw new InvalidOperationException(
                    $"服务 {serviceType.Name} 已经注册过了（第二次来自 {origin}）。" +
                    "装配只应该在一处写清楚：请合并这两处注册，或删掉多余的那处（框架不提供静默覆盖）。");

            _descriptors[serviceType] = new RevServiceDescriptor(serviceType, lifetime, factory, ownsInstance, origin);
            return this;
        }

        /// <summary>调试显示：例如 <c>RevServiceBuilder(已注册 3 个，未构建)</c></summary>
        public override string ToString() => $"RevServiceBuilder(已注册 {_descriptors.Count} 个{(_built ? "，已构建" : string.Empty)})";
    }
}
