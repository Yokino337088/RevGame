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
    // 【映射表格式】逻辑名 → "包名|资源名"
    //   由打包工具生成的 ResMap.txt 解析而来（见 RevResBootstrap.LoadResMap）。
    // ============================================================
    public class RevABResPolicy : IRevResPolicy
    {
        /// <summary>
        ///逻辑名 → "包名|资源名"的映射字典
        ///键（Key）  ：业务写的逻辑路径，如 "Hero/1001"（也就是 handle.StandardPath）。
        ///值（Value）：AB 包里真正的坐标，格式 "包名|资源名"，
        ///如 "hero_1001.ab|Hero_1001"，用竖线 | 分隔：
        /// </summary>
        private readonly Dictionary<string,string> _map;

        //对应的ab包加载器
        private readonly RevABLoader _loader;

        /// <summary>映射表条目数（窗口/排查用）</summary>
        public int MapCount => _map.Count;

        /// <summary>
        /// 对外提供调用的ab包加载器属性
        /// </summary>
        public RevABLoader BundleLoader => _loader;
        public RevABResPolicy(Dictionary<string, string> map, RevABLoader loader)
        {
            _map = map ?? new Dictionary<string, string>();
            _loader = loader;
        }
        // ★ AB 里没有 / 包坏了 → 允许继续用 Resources 兜底
        public bool AllowFallback => true;

        public IRevResLoader CreateLoader()
        {
            return _loader;
        }

        // 查表：查到 → "包名|资源名"；查不到 → null（门面据此触发兜底）
        public string MapPath(string standardPath, Type contentType)
        {
            return _map.TryGetValue(standardPath, out string v) ? v : null;
        }

        // 除显式指定走 Resources 的资源外，全部由 AB 接管
        public bool Match(string standardPath)
        {
            return !standardPath.StartsWith(RevResourcesResPolicy.PREFIX);
        }

        /// <summary>
        /// 资源级引用归零 / 被强制移除时，把对应的 AB 包引用也还掉（包引用归零会自动 Unload）。
        /// 调用方：RevResManager 的 FlushUnused / UnloadGroup / ForceRemove / UnloadAll。
        ///
        /// 【为什么要用逻辑路径反查】加载时走的是"包名|资源名"，而卸载时手上只有逻辑路径，
        /// 所以拿同一张映射表反查一次，再从值里切出包名。
        /// </summary>
        public void ReleaseBundleOf(string standardPath)
        {
            if (_map.TryGetValue(standardPath, out string v))
                _loader.ReleaseBundle(v.Split('|')[0]);
        }
    }
}