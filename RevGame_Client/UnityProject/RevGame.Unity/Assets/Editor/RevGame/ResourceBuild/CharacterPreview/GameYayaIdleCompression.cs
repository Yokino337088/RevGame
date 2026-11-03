// 鸦鸦24帧压缩实验：隔离构建二进制，保留用户场景和正式AB配置。
using System;
using System.IO;
using System.Linq;
using Revolution.Editor;
using UnityEditor;
using UnityEditor.U2D;
using UnityEngine;
using UnityEngine.U2D;

namespace RevGame.Editor
{
    public static class GameYayaIdleCompression
    {
        private const string Folder = "Assets/ArtPreviews/YayaIdle/Frames24";
        private const string AtlasPath = Folder + "/YayaIdle24.spriteatlasv2";

        [RevAutomationAction("game.yaya-idle.compress", Environment = "edit", Keys = new string[0], Description = "隔离比较RGBA32和DXT5图集实际PC AB，保留24帧尺寸")]
        public static object Compress(RevAutomationRequest request)
        {
            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.StandaloneWindows64)
            {
                throw new InvalidOperationException("本实验只在当前Windows64目标执行，避免自动切换平台");
            }
            string[] paths = AssetDatabase.FindAssets("t:Texture2D", new[] { Folder }).Select(AssetDatabase.GUIDToAssetPath).Where(p => p.EndsWith(".png", StringComparison.Ordinal)).OrderBy(p => p).ToArray();
            if (paths.Length != 24)
            {
                throw new InvalidOperationException("输入必须恰好24张PNG");
            }
            EditorSettings.spritePackerMode = SpritePackerMode.SpriteAtlasV2;
            if (!File.Exists(AtlasPath))
            {
                SpriteAtlasAsset source = new SpriteAtlasAsset();
                try
                {
                    source.Add(paths.Select(p => AssetDatabase.LoadAssetAtPath<Texture2D>(p)).Cast<UnityEngine.Object>().ToArray());
                    SpriteAtlasAsset.Save(source, AtlasPath);
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(source);
                }
                AssetDatabase.ImportAsset(AtlasPath, ImportAssetOptions.ForceSynchronousImport);
            }
            long before = Build(paths, false);
            long after = Build(paths, true);
            Report result = new Report { platform = "StandaloneWindows64", beforeBytes = before, afterBytes = after, reduction = 1.0 - after / (double)before, frames = 24, width = 834, height = 1112 };
            string evidence = Path.GetFullPath("../../../../.codex/work/yaya-idle-compression");
            Directory.CreateDirectory(evidence);
            File.WriteAllText(Path.Combine(evidence, "result.json"), JsonUtility.ToJson(result, true));
            return result;
        }

        // 两轮使用相同4096页、相同资源闭包和LZ4选项，只改变纹理格式。
        private static long Build(string[] paths, bool compressed)
        {
            foreach (string path in paths)
            {
                TextureImporter texture = (TextureImporter)AssetImporter.GetAtPath(path);
                texture.textureCompression = compressed ? TextureImporterCompression.CompressedHQ : TextureImporterCompression.Uncompressed;
                texture.isReadable = false;
                texture.mipmapEnabled = false;
                texture.SetPlatformTextureSettings(new TextureImporterPlatformSettings { name = "Standalone", overridden = true, maxTextureSize = 2048, format = compressed ? TextureImporterFormat.DXT5 : TextureImporterFormat.RGBA32, compressionQuality = 100 });
                texture.SetPlatformTextureSettings(new TextureImporterPlatformSettings { name = "Android", overridden = true, maxTextureSize = 2048, format = TextureImporterFormat.ETC2_RGBA8, compressionQuality = 100 });
                AssetDatabase.WriteImportSettingsIfDirty(path);
            }
            // 集中刷新，避免每张图片触发一次整个图集重建。
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            SpriteAtlasImporter importer = (SpriteAtlasImporter)AssetImporter.GetAtPath(AtlasPath);
            importer.includeInBuild = false;
            importer.packingSettings = new SpriteAtlasPackingSettings { enableRotation = false, enableTightPacking = false, padding = 4 };
            importer.textureSettings = new SpriteAtlasTextureSettings { readable = false, generateMipMaps = false, sRGB = true, filterMode = FilterMode.Bilinear };
            importer.SetPlatformSettings(new TextureImporterPlatformSettings { name = "Standalone", overridden = true, maxTextureSize = 4096, format = compressed ? TextureImporterFormat.DXT5 : TextureImporterFormat.RGBA32, compressionQuality = 100 });
            importer.SetPlatformSettings(new TextureImporterPlatformSettings { name = "Android", overridden = true, maxTextureSize = 4096, format = TextureImporterFormat.ETC2_RGBA8, compressionQuality = 100 });
            importer.SaveAndReimport();
            AssetDatabase.SaveAssets();
            string output = Path.GetFullPath("Library/YayaIdleCompression/" + (compressed ? "compressed" : "baseline"));
            Directory.CreateDirectory(output);
            string[] assets = paths.Concat(new[] { AtlasPath, Folder + "/YayaIdle24.anim" }).ToArray();
            AssetBundleManifest manifest = BuildPipeline.BuildAssetBundles(output, new[] { new AssetBundleBuild { assetBundleName = "yaya_idle24", assetNames = assets } }, BuildAssetBundleOptions.ChunkBasedCompression | BuildAssetBundleOptions.ForceRebuildAssetBundle, BuildTarget.StandaloneWindows64);
            if (manifest == null || manifest.GetAllDependencies("yaya_idle24").Length != 0)
            {
                throw new InvalidOperationException("构建失败或产生额外依赖");
            }
            string bundlePath = Path.Combine(output, "yaya_idle24");
            AssetBundle bundle = AssetBundle.LoadFromFile(bundlePath);
            if (bundle == null)
            {
                throw new InvalidOperationException("真实AB无法加载");
            }
            try
            {
                SpriteAtlas atlas = bundle.LoadAsset<SpriteAtlas>(AtlasPath);
                if (atlas == null || atlas.spriteCount != 24)
                {
                    throw new InvalidOperationException("真实AB图集缺失或不是24帧");
                }
                Sprite[] sprites = new Sprite[24];
                atlas.GetSprites(sprites);
                if (sprites.Any(s => s == null || !s.packed))
                {
                    throw new InvalidOperationException("真实AB Sprite未绑定图集");
                }
                // 从真实AB中的图集页读回像素，留作透明边缘与块压缩视觉检查。
                Texture2D page = sprites[0].texture;
                RenderTexture rt = RenderTexture.GetTemporary(page.width, page.height, 0, RenderTextureFormat.ARGB32);
                RenderTexture previous = RenderTexture.active;
                Texture2D capture = new Texture2D(page.width, page.height, TextureFormat.RGBA32, false);
                try
                {
                    Graphics.Blit(page, rt);
                    RenderTexture.active = rt;
                    capture.ReadPixels(new Rect(0, 0, page.width, page.height), 0, 0);
                    capture.Apply();
                    File.WriteAllBytes(Path.Combine(output, "atlas-page.png"), capture.EncodeToPNG());
                }
                finally
                {
                    RenderTexture.active = previous;
                    RenderTexture.ReleaseTemporary(rt);
                    UnityEngine.Object.DestroyImmediate(capture);
                    foreach (Sprite sprite in sprites)
                    {
                        UnityEngine.Object.DestroyImmediate(sprite);
                    }
                }
            }
            finally
            {
                bundle.Unload(true);
            }
            return new FileInfo(bundlePath).Length;
        }

        // 从已经构建的压缩包读回24帧，恢复透明裁切偏移，保证预览不会逐帧抖动。
        [RevAutomationAction("game.yaya-idle.export-compressed", Environment = "edit", Keys = new string[0], Description = "从真实压缩AB读回24帧用于动画预览")]
        public static object ExportCompressed(RevAutomationRequest request)
        {
            string output = Path.GetFullPath("Library/YayaIdleCompression/decoded24");
            Directory.CreateDirectory(output);
            AssetBundle bundle = AssetBundle.LoadFromFile(Path.GetFullPath("Library/YayaIdleCompression/compressed/yaya_idle24"));
            if (bundle == null)
            {
                throw new InvalidOperationException("压缩AB加载失败");
            }
            try
            {
                SpriteAtlas atlas = bundle.LoadAsset<SpriteAtlas>(AtlasPath);
                for (int i = 0; i < 24; i++)
                {
                    Sprite sprite = atlas.GetSprite("frame_" + i.ToString("D3"));
                    if (sprite == null)
                    {
                        throw new InvalidOperationException("缺少帧：" + i);
                    }
                    Texture2D page = sprite.texture;
                    RenderTexture rt = RenderTexture.GetTemporary(page.width, page.height, 0, RenderTextureFormat.ARGB32);
                    RenderTexture previous = RenderTexture.active;
                    Texture2D decoded = new Texture2D(page.width, page.height, TextureFormat.RGBA32, false);
                    Texture2D frame = new Texture2D(834, 1112, TextureFormat.RGBA32, false);
                    try
                    {
                        Graphics.Blit(page, rt);
                        RenderTexture.active = rt;
                        decoded.ReadPixels(new Rect(0, 0, page.width, page.height), 0, 0);
                        decoded.Apply();
                        frame.SetPixels32(new Color32[834 * 1112]);
                        Rect rect = sprite.textureRect;
                        Vector2 offset = sprite.textureRectOffset;
                        frame.SetPixels(Mathf.RoundToInt(offset.x), Mathf.RoundToInt(offset.y), Mathf.RoundToInt(rect.width), Mathf.RoundToInt(rect.height), decoded.GetPixels(Mathf.RoundToInt(rect.x), Mathf.RoundToInt(rect.y), Mathf.RoundToInt(rect.width), Mathf.RoundToInt(rect.height)));
                        frame.Apply();
                        File.WriteAllBytes(Path.Combine(output, "frame_" + i.ToString("D3") + ".png"), frame.EncodeToPNG());
                    }
                    finally
                    {
                        RenderTexture.active = previous;
                        RenderTexture.ReleaseTemporary(rt);
                        UnityEngine.Object.DestroyImmediate(decoded);
                        UnityEngine.Object.DestroyImmediate(frame);
                        UnityEngine.Object.DestroyImmediate(sprite);
                    }
                }
            }
            finally
            {
                bundle.Unload(true);
            }
            return new { frames = 24, directory = output };
        }

        [Serializable]
        private sealed class Report
        {
            public string platform;
            public long beforeBytes;
            public long afterBytes;
            public double reduction;
            public int frames;
            public int width;
            public int height;
        }
    }
}
