// ============================================================
// RevHotSource.cs —— 下载源（主源 + 备用源）
//
// 位置：Assets\Revolution.HotUpdate\Runtime\Download\
//
// 【为什么抽出来】
//   "换源重试"需要知道：一共有几个源、现在用的是第几个、失败日志里打"源 #几"。
//   源的真正用途差别在平台上：
//     · Android / iOS / PC：主源失败 → 换备用源，继续 Range 续传（换的只是 URL 前缀，.part 仍然有效）；
//     · 小程序（WebGL）：合法域名数量有限（公众平台名额），备用源基本配不了 →
//       把"多源容灾"配置到 CDN 的多源站/回源上，客户端只用一个域名。
// ============================================================
using System.Collections.Generic;

namespace Revolution.HotUpdate
{
    /// <summary>一个下载源（就是"远端根地址" + 它排在第几位）。</summary>
    public readonly struct RevHotSource
    {
        /// <summary>第几个源（从 1 开始；日志里显示"源 #1 / 源 #2"）。</summary>
        public readonly int Index;

        /// <summary>源根地址（已去尾斜杠）。</summary>
        public readonly string Root;

        public RevHotSource(int index, string root)
        {
            Index = index;
            Root = root;
        }

        public override string ToString()
        {
            return "源 #" + Index + "（" + Root + "）";
        }
    }

    /// <summary>源列表装配（配置里的 RemoteRoot + FallbackRoots → 有序的源列表）。</summary>
    internal static class RevHotSources
    {
        /// <summary>按配置顺序装配源（第一个是主源）；配置非法（Root 为空）时返回空列表。</summary>
        public static List<RevHotSource> Build(RevHotConfig config)
        {
            List<string> roots = config.BuildSourceRoots();
            var sources = new List<RevHotSource>(roots.Count);
            for (int i = 0; i < roots.Count; i++)
            {
                sources.Add(new RevHotSource(i + 1, roots[i]));
            }

            return sources;
        }
    }
}
