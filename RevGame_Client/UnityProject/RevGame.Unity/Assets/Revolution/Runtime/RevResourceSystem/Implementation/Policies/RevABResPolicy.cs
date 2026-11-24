using System;
using System.Collections.Generic;

namespace Revolution
{
    // ============================================================
    // RevABResPolicy.cs —— AB 策略（主方案：正式资源一律走 AB）
    //
    //
    // 【匹配规则】除 "Res/" 前缀的特殊资源外，其余路径**全部归它管** ——
    //   注意不是"只匹配映射表里有的"，而是"先接管，加载时再查表"。
    //   这样"没打进包的资源"也会先走到这里，失败后再兜底到 Resources，
    //   从而精确实现"只有加载不到的资源才走 Resources"。
    //
    // 【查表】内置来源使用已解析的包名与资源名，旧的公开构造器继续接受字典。
    // ============================================================
    /// <summary>加载由 AB 提供的资源。缺失时允许资源门面尝试后续策略。</summary>
    public class RevABResPolicy : IRevResPolicy
    {
        /// <summary>
        /// 公开构造器的旧映射实例。保留原字典和其匹配规则，便于现有宿主动态调整映射。
        /// </summary>
        private readonly Dictionary<string, string> _map;

        /// <summary>已验证且可整版替换的内置映射。启用这种来源时没有第二张文本字典。</summary>
        private readonly RevResMap _catalog;

        /// <summary>本策略绑定到的 AB 加载器。</summary>
        private readonly RevABLoader _loader;

        /// <summary>映射表条目数（窗口/排查用）</summary>
        public int MapCount => _catalog?.Count ?? _map.Count;

        /// <summary>只有框架装配的类型化策略走地址直传。第三方策略继续调用 IRevResPolicy。</summary>
        internal bool UsesResolvedMap => _catalog != null;

        /// <summary>
        /// 对外提供调用的ab包加载器属性
        /// </summary>
        public RevABLoader BundleLoader => _loader;
        /// <summary>兼容接受可变字典的项目与自定义策略。</summary>
        public RevABResPolicy(Dictionary<string, string> map, RevABLoader loader)
        {
            _map = map ?? new Dictionary<string, string>();
            _loader = loader;
        }
        /// <summary>框架装配首包或已验证的完整热更版本时使用。</summary>
        internal RevABResPolicy(RevResMap catalog, RevABLoader loader)
        {
            _catalog = catalog;
            _loader = loader;
        }

        /// <summary>AB 未命中或加载失败后允许继续尝试后续策略。</summary>
        public bool AllowFallback => true;

        /// <summary>返回本策略绑定的加载器，包引用由同一实例维护。</summary>
        public IRevResLoader CreateLoader()
        {
            return _loader;
        }

        /// <summary>为旧策略接口和诊断保留包名与资源名的文本表示。普通 AB 加载直接使用地址。</summary>
        public string MapPath(string standardPath, Type contentType)
        {
            if (_catalog != null)
            {
                return _catalog.TryGetAddress(standardPath, out RevABAddress address) ? address.ToPath() : null;
            }
            return _map.TryGetValue(standardPath, out string v) ? v : null;
        }

        /// <summary>将映射中已有的地址交给句柄，缺失时由门面继续后续策略。</summary>
        internal bool TryResolveAddress(RevResHandle handle)
        {
            if (!_catalog.TryGetAddress(handle.StandardPath, out RevABAddress address))
            {
                return false;
            }
            handle.SetABAddress(address);
            return true;
        }

        /// <summary>明确请求 Resources 的路径交给后续策略，其余由 AB 尝试。</summary>
        public bool Match(string standardPath)
        {
            return !standardPath.StartsWith(RevResourcesResPolicy.PREFIX);
        }

        /// <summary>兼容按逻辑路径直接归还包引用的调用。门面管理的资源由其句柄归还实际取得的包和依赖。</summary>
        public void ReleaseBundleOf(string standardPath)
        {
            if (_catalog != null)
            {
                if (_catalog.TryGetAddress(standardPath, out RevABAddress address))
                {
                    _loader.ReleaseBundle(address.BundleName);
                }
            }
            else if (_map.TryGetValue(standardPath, out string path))
            {
                int separator = path.IndexOf('|');
                _loader.ReleaseBundle(separator < 0 ? path : path.Substring(0, separator));
            }
        }
    }
}
