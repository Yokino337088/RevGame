using System;
using Revolution;
using UnityEngine;

namespace RevGame.View
{
    /// <summary>
    /// 项目异步取池对象的短租约边界。只组合公开门面，不另造对象池。
    /// 冷加载请求的引用在交付后归还，池自身持有预制体直到场景清池；避免每次进战斗多留一份请求引用。
    /// </summary>
    public static class GamePoolRequests
    {
        /// <summary>特性每种组件只解析一次；路径仍由组件的RevPoolPrefab声明，禁止复制磁盘路径。</summary>
        private static class Resource<T> where T : Component
        {
            // 每种组件的资源特性只解析一次；Root/Name/Group来自原声明，不复制一套硬编码磁盘路径。
            internal static readonly RevPoolPrefabAttribute Definition =
                (RevPoolPrefabAttribute)Attribute.GetCustomAttribute(typeof(T), typeof(RevPoolPrefabAttribute));
            // 资源逻辑名称，声明为空时按原约定用组件名。它是路径的一段，不是StreamingAssets文件地址。
            internal static readonly string Name = string.IsNullOrEmpty(Definition?.Name) ? typeof(T).Name : Definition.Name;
        }

        /// <summary>已加载资源的同步快速路径，不发起新加载；调用者在false时必须走冷异步入口。</summary>
        // 只尝试已经加载的资源；false表示必须走GetAsync，不会为了快同步加载冷AB。
        // true表示已执行池取用流程，消费者仍须检查item和阶段代次，资源已加载不保证玩法前置仍有效。
        public static bool TryGetReady<T>(out T item, Transform parent = null) where T : Component
        {
            RevPoolPrefabAttribute definition = Resource<T>.Definition;
            if (definition == null) { throw new InvalidOperationException(typeof(T).Name + "缺少RevPoolPrefab声明"); }
            if (!RevResManager.Get(definition.Root, Resource<T>.Name).IsLoaded) { item = null; return false; }
            item = RevPool.Get<T>(definition.Root, Resource<T>.Name, parent, definition.Group);
            return true;
        }

        /// <summary>命中缓存同步交付，冷资源异步加载。消费者仍负责过期检查与归池；失败交付null，临时资源引用总会归还。</summary>
        public static void GetAsync<T>(Action<T> completed, Transform parent = null) where T : Component
        {
            RevPoolPrefabAttribute definition = Resource<T>.Definition;
            if (definition == null) { throw new InvalidOperationException(typeof(T).Name + "缺少RevPoolPrefab声明"); }
            string name = Resource<T>.Name;
            if (RevResManager.Get(definition.Root, name).IsLoaded)
            {
                completed(RevPool.Get<T>(definition.Root, name, parent, definition.Group));
                return;
            }
            LoadCold(completed,parent,definition,name);
        }

        /// <summary>
        /// 捕获参数的加载回调只在冷资源路径创建。不能把lambda留在GetAsync方法里，
        /// C#编译器会在进入方法时先建闭包，导致已缓存的每发弹道也付出一次短命分配。
        /// </summary>
        // 冷加载独立方法保存completed/parent/definition/name的闭包。
        // 若闭包仍写在GetAsync内部，编译器可能进入热方法时就先分配，即使随后直接命中缓存返回。
        private static void LoadCold<T>(Action<T> completed,Transform parent,RevPoolPrefabAttribute definition,string name) where T : Component
        {
            RevResManager.LoadAsync(definition.Root, name, typeof(GameObject), handle =>
            {
                try
                {
                    completed(handle.IsLoaded ? RevPool.Get<T>(definition.Root, name, parent, definition.Group) : null);
                }
                finally
                {
                    // 回调可能触发退出；缓存身份已变化时不能归还同路径的新一代资源。
                    RevResHandle current = RevResManager.Get(definition.Root, name);
                    if (current.Key == handle.Key && current.Content == handle.Content && current.RefCount > 0)
                    {
                        RevResManager.Release(definition.Root, name);
                    }
                }
            }, definition.Group);
        }
    }
}
