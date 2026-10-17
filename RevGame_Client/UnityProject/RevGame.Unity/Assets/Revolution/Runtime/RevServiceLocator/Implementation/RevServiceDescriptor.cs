// RevServiceDescriptor.cs —— 一条注册记录（框架内部）
// 【内容】服务类型 + 生命周期 + 创建方式（工厂）+ 是否由容器负责释放 + 出错信息里怎么称呼它
// 【为什么用"工厂"而不是"类型反射"】零反射：泛型 new() 在编译期就把构造调用固定下来
//   （王者为了 IL2CPP/AOT 安全，宁可手写 22 个 switch 分支；这里用泛型闭包达到同样效果但不啰嗦）。

using System;

namespace Revolution
{
    /// <summary>一条注册记录：某个服务类型"怎么造、活多久"。</summary>
    internal sealed class RevServiceDescriptor
    {
        /// <summary>注册时用的键（业务取服务用的类型，通常是接口）</summary>
        internal readonly Type ServiceType;

        /// <summary>生命周期</summary>
        internal readonly RevServiceLifetime Lifetime;

        /// <summary>创建方式：参数是"拥有该实例的容器"（Singleton 传根容器、Scoped 传作用域）</summary>
        internal readonly Func<RevIServiceLocator, object> Factory;

        /// <summary>是否由容器负责释放：工厂创建的为 true；业务自己传进来的实例为 false</summary>
        internal readonly bool OwnsInstance;

        /// <summary>这条注册是怎么来的（写进错误信息，便于定位装配代码）</summary>
        internal readonly string Origin;

        internal RevServiceDescriptor(Type serviceType,
                                      RevServiceLifetime lifetime,
                                      Func<RevIServiceLocator, object> factory,
                                      bool ownsInstance,
                                      string origin)
        {
            ServiceType = serviceType;
            Lifetime = lifetime;
            Factory = factory;
            OwnsInstance = ownsInstance;
            Origin = origin;
        }

        /// <summary>调试显示：例如 <c>ISoundService ← SoundService（Singleton）</c></summary>
        public override string ToString() => $"{ServiceType.Name}（{Lifetime}，来自 {Origin}）";
    }
}
