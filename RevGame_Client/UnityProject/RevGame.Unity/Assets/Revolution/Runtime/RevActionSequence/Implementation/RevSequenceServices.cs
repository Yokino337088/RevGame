// RevSequenceServices.cs —— 服务容器（把业务依赖挡在框架外）
// 【用法】runner.Services.Add<ISoundService>(实现) → 步骤里 ctx.Get<ISoundService>() / ctx.Require<ISoundService>()。
// 【不怕写错泛型】Add(new SoundImpl()) 注册成了具体类型，Get<ISoundService>() 也能取到（按"能不能转成 T"兜底查找）。

using System;
using System.Collections.Generic;

namespace Revolution
{
    /// <summary>
    /// 动作序列的服务容器：按"类型"注入/取出业务服务（具体是什么服务由业务自己决定，框架不预设）。
    /// <para>框架本身不认识任何业务接口；步骤通过 <c>context.Get&lt;T&gt;()</c> 取得需要的服务。</para>
    /// </summary>
    public sealed class RevSequenceServices
    {
        private readonly Dictionary<Type, object> _map;

        // 兜底查找的缓存：Get<IFoo>() 第一次按"可转换"找到后记下来，之后 O(1)。Add / Remove 时清空（可能换了实现）。
        private readonly Dictionary<Type, object> _aliases = new Dictionary<Type, object>();

        /// <summary>建一个服务容器</summary>
        /// <param name="capacity">预估服务数量（只是初始容量，不影响功能）</param>
        public RevSequenceServices(int capacity = 8)
            => _map = new Dictionary<Type, object>(capacity);

        /// <summary>已注册服务数量（框架内部 / 调试用）</summary>
        internal int Count => _map.Count;

        /// <summary>
        /// 注册服务（键 = 泛型参数 T；写接口类型最清楚：<c>services.Add&lt;ISoundService&gt;(mySound)</c>，
        /// 写成具体类型也行 —— 取的时候会按接口兜底找到它）。
        /// </summary>
        /// <returns>返回自身，支持链式注册</returns>
        public RevSequenceServices Add<T>(T service) where T : class
        {
            if (service == null) throw new ArgumentNullException(nameof(service));
            _map[typeof(T)] = service;      // 后注册覆盖先注册（便于测试时用 Fake 覆盖真实现）
            _aliases.Clear();
            return this;
        }

        /// <summary>取服务；没注册返回 null（必需的服务请用 <c>ctx.Require&lt;T&gt;()</c>，取不到会直接告诉你）</summary>
        public T Get<T>() where T : class => Find(typeof(T)) as T;

        /// <summary>尝试取服务</summary>
        public bool TryGet<T>(out T service) where T : class
        {
            service = Find(typeof(T)) as T;
            return service != null;
        }

        /// <summary>精确类型优先；找不到再找"能转成该类型"的已注册服务（按注册顺序取第一个）</summary>
        private object Find(Type type)
        {
            if (_map.TryGetValue(type, out object exact)) return exact;
            if (_aliases.TryGetValue(type, out object cached)) return cached;

            object hit = null;
            foreach (KeyValuePair<Type, object> kv in _map)
            {
                if (!type.IsInstanceOfType(kv.Value)) continue;
                hit = kv.Value;
                break;
            }

            if (hit != null) _aliases[type] = hit;     // 循环外再写：遍历字典时不能改它
            return hit;
        }

        /// <summary>移除服务（框架内部：测试替换实现、宿主销毁时清理）</summary>
        internal bool Remove<T>() where T : class
        {
            _aliases.Clear();
            return _map.Remove(typeof(T));
        }

        /// <summary>清空所有服务（框架内部：宿主销毁时调用，避免持有已销毁的业务对象）</summary>
        internal void Clear()
        {
            _map.Clear();
            _aliases.Clear();
        }
    }
}
