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
            internal static readonly RevPoolPrefabAttribute Definition =
                (RevPoolPrefabAttribute)Attribute.GetCustomAttribute(typeof(T), typeof(RevPoolPrefabAttribute));
            internal static readonly string Name = string.IsNullOrEmpty(Definition?.Name) ? typeof(T).Name : Definition.Name;
        }

        /// <summary>已加载资源的同步快速路径，不发起新加载；调用者在false时必须走冷异步入口。</summary>
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
