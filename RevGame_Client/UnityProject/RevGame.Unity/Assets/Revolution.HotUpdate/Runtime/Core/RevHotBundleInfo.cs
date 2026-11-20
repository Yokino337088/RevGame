// ============================================================
// RevHotBundleInfo.cs —— 清单里的"一个包"：它叫什么、内容是什么、依赖谁
//
// 位置：Assets\Revolution.HotUpdate\Runtime\Core\
//
// 【它对应清单里的一行】
//   @bundle|包名|sha256|字节|依赖(逗号分隔)|标签(逗号分隔)|unityHash|unityCrc
//
// 【两个 hash 各干什么用（别混）】
//   · Sha256 ：我们自己算的内容指纹，用于"变没变"的比对与下载后的完整性校验；
//   · UnityHash / UnityCrc ：Unity 构建时给每个包算的值（在 .manifest 文本里），
//     小游戏 / WebGL 上要交给 UnityWebRequestAssetBundle.GetAssetBundle(url, hash, crc)，
//     让"引擎的缓存与校验"用它们 —— 这是引擎认的版本号，不是我们能随便换的。
// ============================================================
using System;
using System.Collections.Generic;

namespace Revolution.HotUpdate
{
    /// <summary>清单里的一个 AB 包条目。</summary>
    public sealed class RevHotBundleInfo
    {
        /// <summary>包名（= 文件名，与打包产物的文件名一致，例如 hero）。</summary>
        public string Name = "";

        /// <summary>内容哈希（清单 @hashAlgo 指定的算法，默认 sha256 的十六进制小写）。</summary>
        public string Sha256 = "";

        /// <summary>包体字节数（用于：磁盘预检 / 尺寸校验 / 进度合计）。</summary>
        public long Size;

        /// <summary>依赖的包名列表（加载时框架会按 Unity 的 Manifest 再核对一遍，这里用于"下载前算依赖闭包"）。</summary>
        public readonly List<string> Dependencies = new List<string>();

        /// <summary>标签（builtin = 首包内置；hot = 可热更；optional = 按需下载）。</summary>
        public readonly List<string> Tags = new List<string>();

        /// <summary>Unity 的包 hash（Hash128 文本，仅 WebGL / 小游戏需要；文件型平台可为空）。</summary>
        public string UnityHash = "";

        /// <summary>Unity 的包 CRC（.manifest 里的那个数字；可为空 = 不做加载期 CRC 校验）。</summary>
        public string UnityCrc = "";

        /// <summary>是否打了 builtin 标签（首包内置：StreamingAssets 里有一份，热更时允许被覆盖）。</summary>
        public bool HasBuiltinTag()
        {
            return HasTag("builtin");
        }

        /// <summary>是否打了某个标签（大小写不敏感）。</summary>
        public bool HasTag(string tag)
        {
            for (int i = 0; i < Tags.Count; i++)
            {
                if (string.Equals(Tags[i], tag, StringComparison.OrdinalIgnoreCase)) return true;
            }

            return false;
        }

        /// <summary>把依赖串（"a,b,c"）解析进列表（空的依赖不产生条目）。</summary>
        public void SetDependencies(string joined)
        {
            Dependencies.Clear();
            if (string.IsNullOrEmpty(joined)) return;
            string[] parts = joined.Split(',');
            for (int i = 0; i < parts.Length; i++)
            {
                string p = parts[i].Trim();
                if (p.Length > 0) Dependencies.Add(p);
            }
        }

        /// <summary>把标签串（"builtin,hot"）解析进列表。</summary>
        public void SetTags(string joined)
        {
            Tags.Clear();
            if (string.IsNullOrEmpty(joined)) return;
            string[] parts = joined.Split(',');
            for (int i = 0; i < parts.Length; i++)
            {
                string p = parts[i].Trim();
                if (p.Length > 0) Tags.Add(p);
            }
        }

        /// <summary>调试显示。</summary>
        public override string ToString()
        {
            return Name + "(" + Size + " B)";
        }
    }

    /// <summary>
    /// 清单里"不是 AB 包"的附加文件（目前只有 ResMap.txt —— 映射表本身也要热更，
    /// 否则"新增资源"永远加载不到：技术方案 3.2 那条硬事实）。
    /// </summary>
    public sealed class RevHotFileInfo
    {
        /// <summary>文件名（相对版本目录，例如 ResMap.txt）。</summary>
        public string Name = "";

        /// <summary>内容哈希（与清单 @hashAlgo 一致）。</summary>
        public string Sha256 = "";

        /// <summary>字节数。</summary>
        public long Size;
    }
}
