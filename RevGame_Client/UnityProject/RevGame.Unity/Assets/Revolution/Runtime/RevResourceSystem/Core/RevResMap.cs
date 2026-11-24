using System;
using System.Collections.Generic;

namespace Revolution
{
    /// <summary>已完整验证的运行时 AB 映射。每个逻辑路径直接命中包名和包内名称，可随资源版本整体替换。</summary>
    public sealed class RevResMap
    {
        /// <summary>序号与大小写匹配规则沿用原映射。发布后只读，旧请求仍可使用曾取得的地址。</summary>
        private readonly Dictionary<string, RevABAddress> _entries;

        /// <summary>逻辑路径数，供资源策略和诊断使用。</summary>
        public int Count => _entries.Count;

        /// <summary>接管已经解析完成的条目。用于将候选与当前版本的生命周期分开。</summary>
        private RevResMap(Dictionary<string, RevABAddress> entries)
        {
            _entries = entries;
        }

        /// <summary>严格校验三列表并归并包名。版本发布方可在建表时同时限制允许引用的包。</summary>
        public static RevResMap Parse(string text, Predicate<string> isKnownBundle = null)
        {
            var entries = new Dictionary<string, RevABAddress>(StringComparer.Ordinal);
            // 包名通常由大量资源共用，归并表只在构造时存在。每行资源名已解析，直接交给地址。
            var bundles = new Dictionary<string, string>(StringComparer.Ordinal);
            RevResMapCodec.ReadRows(text, (path, bundle, asset, line) =>
            {
                if (isKnownBundle != null && !isKnownBundle(bundle))
                {
                    throw RevResMapCodec.InvalidRow(line, "映射指向清单外的包 " + bundle);
                }
                var address = new RevABAddress(ShareBundleName(bundles, bundle), asset);
                if (!entries.TryAdd(path, address))
                {
                    throw RevResMapCodec.InvalidRow(line, "重复逻辑路径 " + path);
                }
            });
            return new RevResMap(entries);
        }

        /// <summary>按现有逻辑路径返回包名与包内资源名。缺失时返回 false，两个输出都为空。</summary>
        public bool TryGetLocation(string logicalPath, out string bundleName, out string assetName)
        {
            if (TryGetAddress(logicalPath, out RevABAddress address))
            {
                bundleName = address.BundleName;
                assetName = address.AssetName;
                return true;
            }
            bundleName = null;
            assetName = null;
            return false;
        }

        /// <summary>框架内部返回同一份地址对象，使同步、异步和句柄复用都不重新分配字段。</summary>
        internal bool TryGetAddress(string logicalPath, out RevABAddress address)
        {
            if (logicalPath == null)
            {
                address = null;
                return false;
            }
            return _entries.TryGetValue(logicalPath, out address);
        }

        /// <summary>按当前索引共用相同包名。返回实际索引保留的实例。</summary>
        private static string ShareBundleName(Dictionary<string, string> names, string name)
        {
            if (names.TryGetValue(name, out string shared))
            {
                return shared;
            }
            names.Add(name, name);
            return name;
        }
    }

    /// <summary>一个已验证的 AB 地址。创建后不变，映射条目与使用它的句柄共享同一个对象。</summary>
    internal sealed class RevABAddress
    {
        /// <summary>用于获取目标包与依赖的实际包名，可带变体。</summary>
        internal string BundleName { get; }

        /// <summary>传给 Unity AssetBundle 加载入口的包内名称。</summary>
        internal string AssetName { get; }

        /// <summary>以映射中的名称或兼容策略提供的两段文本建立地址。</summary>
        internal RevABAddress(string bundleName, string assetName)
        {
            BundleName = bundleName;
            AssetName = assetName;
        }

        /// <summary>诊断或旧接口请求文本时生成原来的两段式地址。</summary>
        internal string ToPath()
        {
            return BundleName + "|" + AssetName;
        }
    }
}
