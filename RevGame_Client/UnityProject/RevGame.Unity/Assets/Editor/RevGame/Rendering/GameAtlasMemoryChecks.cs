using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.U2D;

namespace RevGame.Editor
{
    /// <summary>
    /// 编辑器离线AB内存对比夹具。允许从一个已导出的旧版本目录读取基线，不回滚当前工程。
    /// 直接AssetBundle API只用于编辑器核查二进制；正式游戏加载仍必须通过RevResManager。
    /// </summary>
    public static class GameAtlasMemoryChecks
    {
        /// <summary>前后使用完全相同的资源根；不通过把新版加载范围缩小来制造“内存优化”结果。</summary>
        private static readonly string[] RootBundles = { "ui_panels", "ui_items", "battle_board", "battle_units", "battle_feedback", "config_piece_art" };

        /// <summary>
        /// 比较旧、新PC AB，并输出原生纹理内存。此数据不包含进程其它对象，不宣称是驱动显存。
        /// baselineFolder是编辑器测试输入，要求包含PC主Manifest；它不进入业务资源路径。
        /// </summary>
        public static string Compare(string baselineFolder)
        {
            if (EditorApplication.isPlaying)
            {
                throw new InvalidOperationException("请退出Play后测量，避免混入正在使用的AB");
            }
            string report = Measure(baselineFolder) + "\n" + Measure("Assets/StreamingAssets/PC");
            File.WriteAllText("Temp/AtlasMemoryComparison.txt", report);
            return report;
        }
        /// <summary>加载前记录已有纹理实例ID，只统计本次根资源带来的新纹理；最后卸载本夹具打开的包。</summary>
        private static string Measure(string folder)
        {
            var initial = new HashSet<int>(Resources.FindObjectsOfTypeAll<Texture2D>().Select(t => t.GetInstanceID()));
            var bundles = new Dictionary<string, AssetBundle>(StringComparer.Ordinal);
            AssetBundle main = Open(folder, "PC");
            try
            {
                AssetBundleManifest manifest = main.LoadAsset<AssetBundleManifest>("AssetBundleManifest");

                if (manifest == null)
                {
                    throw new InvalidOperationException("主包缺少AssetBundleManifest：" + folder);
                }
                foreach (string root in RootBundles)
                {
                    foreach (string dependency in manifest.GetAllDependencies(root))
                    {
                        if (!bundles.ContainsKey(dependency))
                        {
                            bundles.Add(dependency, Open(folder, dependency));
                        }
                    }

                    if (!bundles.ContainsKey(root))
                    {
                        bundles.Add(root, Open(folder, root));
                    }
                    // 加载同一组预制体/配置会带入它们真正引用的素材；不能只比较压缩文件大小。
                    bundles[root].LoadAllAssets<UnityEngine.Object>();
                }
                foreach (var pair in bundles)
                {
                    if (pair.Key.StartsWith("atlas_", StringComparison.Ordinal))
                    {
                        pair.Value.LoadAllAssets<SpriteAtlas>();
                    }
                }
                Texture2D[] textures = Resources.FindObjectsOfTypeAll<Texture2D>().Where(t => !initial.Contains(t.GetInstanceID())).ToArray();
                return folder + "|textures=" + textures.Length + "|bytes=" + textures.Sum(UnityEngine.Profiling.Profiler.GetRuntimeMemorySizeLong) + "|readable=" + textures.Count(t => t.isReadable);
            }
            finally
            {
                // 本方法仅在非Play执行，且只卸载本方法成功打开的独立AB；不对全编辑器调用UnloadAllAssetBundles。
                foreach (AssetBundle bundle in bundles.Values)
                {
                    bundle.Unload(true);
                }
                main.Unload(true);
            }
        }
        /// <summary>打开失败就携带具体包名报错，不继续用null做“零内存”统计。</summary>
        private static AssetBundle Open(string folder, string name)
        {
            AssetBundle bundle = AssetBundle.LoadFromFile(Path.GetFullPath(Path.Combine(folder, name)));

            if (bundle == null)
            {
                throw new InvalidOperationException("测试AB打开失败：" + folder + "/" + name);
            }
            return bundle;
        }
    }
}
