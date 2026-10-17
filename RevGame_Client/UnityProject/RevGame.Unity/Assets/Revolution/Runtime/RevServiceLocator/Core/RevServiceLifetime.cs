// RevServiceLifetime.cs —— 生命周期：只有两级，刻意的
// 【Singleton】整个应用一份（配置、音频、网络、任务系统……）
// 【Scoped】每个作用域一份（一局战斗 / 一个场景 / 一个房间）—— CreateScope() 出来的容器各持一份
//
// 【为什么没有 Transient（每次 new 一个）】
//   服务定位器的价值是"共享同一份实现"，而 Transient 的本质是"不要共享" —— 那直接自己 new 就行了，
//   不需要容器。少一个生命周期 = 少一层心智负担（王者三套容器 + 老单例体系的心智负担是它的实际痛点之一）。

namespace Revolution
{
    /// <summary>
    /// 服务的生命周期（服务被创建几次、活多久）。
    /// </summary>
    public enum RevServiceLifetime
    {
        /// <summary>整个应用一份：第一次被取用时创建，一直活到根容器 <see cref="RevServiceLocator.Dispose"/>。</summary>
        Singleton = 0,

        /// <summary>每个作用域一份：每个 <see cref="RevServiceLocator.CreateScope"/> 出来的容器各有一份，随该作用域释放。</summary>
        Scoped = 1,
    }
}
