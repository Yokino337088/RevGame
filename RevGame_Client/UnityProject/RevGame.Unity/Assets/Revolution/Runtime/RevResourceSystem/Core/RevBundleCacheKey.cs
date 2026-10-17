// ============================================================
// RevBundleCacheKey.cs —— AB 包的"缓存标识"（hash + crc）
//
// 位置：Runtime\RevResourceSystem\Core\
//
// 【为什么需要它】
//   WebGL / 微信小游戏 / 抖音小游戏拿 AB 的方式是"按 URL 下载"，而引擎判断
//   "这份包本地缓存过没有、要不要重新下载"靠的正是调用方给出的 hash / crc：
//     UnityWebRequestAssetBundle.GetAssetBundle(url, hash, crc)
//   不传 → 每次进游戏都把全部 AB 重新下载一遍（流量与首屏时间双输）。
//
//   hash / crc 是打包时 Unity 算好的（构建出的主包清单里有），热更清单也把它们抄了下来。
//   但 RevABLoader 属于框架运行时程序集，不能直接引用热更包的类型 ——
//   所以用这个"纯数据 + 可选钩子"的形状过桥：
//     热更包查到之后设置 RevABLoader.BundleCacheKeyResolver，框架侧只管用。
//
// 【不装热更包会怎样】
//   钩子保持 null → 退回"无缓存标识"的老行为，功能完全正常（只是引擎缓存不生效）。
// ============================================================
using UnityEngine;

namespace Revolution
{
    /// <summary>
    /// 一个 AB 包的缓存标识（引擎缓存 + 加载期校验用）。
    /// <para>★ <see cref="IsValid"/> = false 表示"没有标识"：加载照常进行，但引擎不会启用本地缓存。</para>
    /// <para>★ 用结构体而不是 class：它是每次加载请求都要问一次的热路径数据，
    ///   值类型不产生堆分配（小游戏上 GC 是要命的）。</para>
    /// </summary>
    public struct RevBundleCacheKey
    {
        /// <summary>包内容哈希（Unity 构建时算出，写在主包清单与热更清单里）。</summary>
        public Hash128 Hash;

        /// <summary>包内容 CRC（同上）。</summary>
        public uint Crc;

        /// <summary>标识是否有效（false = 调用方没提供，按"无缓存标识"处理）。</summary>
        public bool IsValid;
    }
}
