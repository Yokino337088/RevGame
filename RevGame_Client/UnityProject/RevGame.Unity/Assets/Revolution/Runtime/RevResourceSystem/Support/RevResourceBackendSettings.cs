using System;
using System.Collections.Generic;
using UnityEngine;

namespace Revolution
{
    /// <summary>项目选择的资源后端。空值表示使用框架自带策略。</summary>
    public sealed class RevResourceBackendSettings : ScriptableObject
    {
        /// <summary>资源引导只在装配时读取设置。</summary>
        public const string ResourcePath = "Revolution/ResourceBackend";

        /// <summary>与扩展包注册时使用的标识一致。</summary>
        public string BackendId;
    }

    /// <summary>将可选资源后端装配进框架的句柄、调度和域回收系统。</summary>
    public sealed class RevResourceBackend
    {
        /// <summary>扩展提供的首选资源策略。</summary>
        internal readonly IRevResPolicy Policy;

        /// <summary>这次装配的原生包加载器，供切换时释放本代包。</summary>
        internal readonly RevABLoader Loader;

        /// <summary>绑定加载策略和负责归还原生包的加载器实例。</summary>
        private RevResourceBackend(IRevResPolicy policy, RevABLoader loader)
        {
            Policy = policy ?? throw new ArgumentNullException(nameof(policy));
            Loader = loader;
        }

        /// <summary>用已验证的地址和依赖快照建立原生 AB 后端。</summary>
        public static RevResourceBackend FromBundles(RevResMap map, IRevBundleSource source)
        {
            if (map == null)
            {
                throw new ArgumentNullException(nameof(map));
            }
            var loader = new RevABLoader(source ?? throw new ArgumentNullException(nameof(source)));
            return new RevResourceBackend(new RevABResPolicy(map, loader), loader);
        }

        /// <summary>建立使用自定义加载策略的后端，例如编辑器模拟加载。</summary>
        public static RevResourceBackend FromPolicy(IRevResPolicy policy)
        {
            return new RevResourceBackend(policy, null);
        }
    }

    /// <summary>一代 AB 的读取位置和依赖。实现方在本代结束前保持查询结果稳定。</summary>
    public interface IRevBundleSource
    {
        /// <summary>返回批准使用的本地文件或当前平台可读的 URL。</summary>
        string GetPath(string bundleName);

        /// <summary>返回去重的依赖闭包，深层依赖在前。数组由快照持有并保持只读。</summary>
        string[] GetDependencies(string bundleName);

        /// <summary>提供 URL 加载所需的 Unity 缓存标识及 CRC。</summary>
        RevBundleCacheKey GetCacheKey(string bundleName);
    }

    /// <summary>登记可选资源后端，框架统一完成选择、装配和退代。</summary>
    public static class RevResourceBackends
    {
        /// <summary>工厂仅在资源初始化和切换时执行。</summary>
        private static readonly Dictionary<string, Func<RevResourceBackend>> Factories =
            new Dictionary<string, Func<RevResourceBackend>>(StringComparer.Ordinal);

        /// <summary>当前装配成功的后端标识，空值为框架自带模式。</summary>
        public static string ActiveId { get; internal set; }

        /// <summary>登记扩展工厂。Player 在程序集加载后、场景初始化前登记。</summary>
        public static void Register(string id, Func<RevResourceBackend> create)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                throw new ArgumentException("资源后端标识不能为空", nameof(id));
            }
            Factories[id] = create ?? throw new ArgumentNullException(nameof(create));
        }

        /// <summary>先验证候选装配。移除扩展后可恢复框架自带后端。</summary>
        internal static RevResourceBackend CreateSelected(out string id)
        {
            var settings = Resources.Load<RevResourceBackendSettings>(RevResourceBackendSettings.ResourcePath);
            id = settings == null ? string.Empty : settings.BackendId ?? string.Empty;
            if (id.Length == 0)
            {
                return null;
            }
            if (!Factories.TryGetValue(id, out Func<RevResourceBackend> create))
            {
                RevLog.Warn("已选择的资源扩展未安装，恢复内置资源后端：" + id, "Res");
                id = string.Empty;
                return null;
            }
            return create() ?? throw new InvalidOperationException("资源后端未返回有效绑定：" + id);
        }

        /// <summary>清除关闭域重载时仍留在上一轮 Play 的工厂。</summary>
        internal static void Reset()
        {
            Factories.Clear();
            ActiveId = string.Empty;
        }
    }
}
