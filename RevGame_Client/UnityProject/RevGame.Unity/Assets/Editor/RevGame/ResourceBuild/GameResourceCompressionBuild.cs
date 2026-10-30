// 游戏侧资源制作约定：保留原美术，制作可压缩的16:9背景副本，并统一战斗字体。
// 只在Editor运行，不改Revolution内核；资源加载、引用计数和动态Shader继续使用原路径。
using System;
using System.IO;
using System.Security.Cryptography;
using Revolution.Editor;
using UnityEditor;
using UnityEngine;

namespace RevGame.Editor
{
    /// <summary>五张背景和战斗字体的幂等制作门禁；输出保留原资产GUID，重跑不覆盖材质表现参数。</summary>
    public static class GameResourceCompressionBuild
    {
        private const string Root = "Assets/GameRes/ChapterBackground/";
        // 1408×792严格16:9且宽高均为4倍数，接近原1360/1376像素，避免拉伸或裁切画面。
        private const int Width = 1408;
        private const int Height = 792;
        private static readonly string[] Names = { "Chapter02_Village", "Chapter03_Lake", "Chapter04_Swamp", "Chapter05_Ice", "Chapter06_Temple" };
        private static readonly string[] Sources = { "棋盘_第5关_魔法村落_背景.png", "棋盘_第2关_魔法湖泊_湖面.png", "棋盘_第3关_死亡沼泽_湖面_v1.png", "棋盘_第4关_冰雪极地_背景.png", "棋盘_第6关_魔法神殿_背景_象牙金.png" };

        /// <summary>制作先于分包；原图位于Unity工程之外，运行时只加载GameRes下的RGB副本。</summary>
        public static void Configure()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                throw new InvalidOperationException("资源瘦身仅可在编辑态执行");
            }
            string sourceRoot = Path.GetFullPath("../../美术/AI生成");
            for (int i = 0; i < Names.Length; i++)
            {
                PrepareBackground(Path.Combine(sourceRoot, Sources[i]), Root + Names[i] + ".png");
            }
            ConfigureCombatFont();
        }
        /// <summary>源图SHA标识生成版本，画面RGB双线性采样、Alpha不参与背景Shader；不裁透明留边，归一化UV保持不变。</summary>
        public static void PrepareBackground(string source, string destination)
        {
            if (!File.Exists(source))
            {
                throw new FileNotFoundException("背景原图缺失", source);
            }
            byte[] bytes = File.ReadAllBytes(source);
            string digest;
            using (SHA256 sha = SHA256.Create())
            {
                digest = Convert.ToBase64String(sha.ComputeHash(bytes));
            }
            string marker = "RevGameRGB1408x792-v1:" + digest;
            var importer = AssetImporter.GetAtPath(destination) as TextureImporter;

            // 重建AB只核对已有制作结果，不无条件触发五次SaveAndReimport；源图或导入配置变化时才重做。
            if (importer != null && importer.userData == marker && IsConfigured(importer))
            {
                Texture2D prepared = AssetDatabase.LoadAssetAtPath<Texture2D>(destination);

                if (prepared != null && prepared.width == Width && prepared.height == Height)
                {
                    return;
                }
            }

            if (importer == null || importer.userData != marker)
            {
                Texture2D original = null;
                Texture2D output = null;
                try
                {
                    original = new Texture2D(2, 2, TextureFormat.RGBA32, false);

                    if (!original.LoadImage(bytes, false))
                    {
                        throw new InvalidOperationException("无法解码背景原图：" + source);
                    }
                    Color32[] pixels = original.GetPixels32();
                    var resized = new Color32[Width * Height];
                    for (int y = 0; y < Height; y++)
                    {
                        float sourceY = Mathf.Clamp((y + 0.5f) * original.height / Height - 0.5f, 0, original.height - 1);
                        int y0 = (int)sourceY;
                        int y1 = Mathf.Min(y0 + 1, original.height - 1);
                        for (int x = 0; x < Width; x++)
                        {
                            float sourceX = Mathf.Clamp((x + 0.5f) * original.width / Width - 0.5f, 0, original.width - 1);
                            int x0 = (int)sourceX;
                            int x1 = Mathf.Min(x0 + 1, original.width - 1);
                            Color lower = Color.Lerp(pixels[y0 * original.width + x0], pixels[y0 * original.width + x1], sourceX - x0);
                            Color upper = Color.Lerp(pixels[y1 * original.width + x0], pixels[y1 * original.width + x1], sourceX - x0);
                            resized[y * Width + x] = Color.Lerp(lower, upper, sourceY - y0);
                        }
                    }
                    // RGB24移除背景Shader从未读取的Alpha；原图仍保留，透明留边的RGB及有效UV均不裁掉。
                    output = new Texture2D(Width, Height, TextureFormat.RGB24, false);
                    output.SetPixels32(resized);
                    output.Apply(false, false);
                    File.WriteAllBytes(destination, output.EncodeToPNG());
                    AssetDatabase.ImportAsset(destination, ImportAssetOptions.ForceSynchronousImport);
                }
                finally
                {
                    if (original != null)
                    {
                        UnityEngine.Object.DestroyImmediate(original);
                    }
                    if (output != null)
                    {
                        UnityEngine.Object.DestroyImmediate(output);
                    }
                }
                importer = (TextureImporter)AssetImporter.GetAtPath(destination);
            }
            importer.userData = marker;
            importer.textureType = TextureImporterType.Default;
            importer.alphaSource = TextureImporterAlphaSource.None;
            importer.mipmapEnabled = false;
            importer.isReadable = false;
            importer.npotScale = TextureImporterNPOTScale.None;
            importer.sRGBTexture = true;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.filterMode = FilterMode.Bilinear;
            importer.SetPlatformTextureSettings(new TextureImporterPlatformSettings { name = "Standalone", overridden = true, maxTextureSize = 2048, format = TextureImporterFormat.DXT1 });
            importer.SetPlatformTextureSettings(new TextureImporterPlatformSettings { name = "Android", overridden = true, maxTextureSize = 2048, format = TextureImporterFormat.ETC2_RGB4 });
            importer.SaveAndReimport();
        }
        /// <summary>判断会影响色彩、UV、尺寸和压缩的制作设置是否一致，不用PNG源文件体积代替导入格式。</summary>
        private static bool IsConfigured(TextureImporter importer)
        {
            TextureImporterPlatformSettings pc = importer.GetPlatformTextureSettings("Standalone");
            TextureImporterPlatformSettings android = importer.GetPlatformTextureSettings("Android");
            return importer.textureType == TextureImporterType.Default && importer.alphaSource == TextureImporterAlphaSource.None
                && !importer.mipmapEnabled && !importer.isReadable && importer.sRGBTexture
                && importer.npotScale == TextureImporterNPOTScale.None && importer.wrapMode == TextureWrapMode.Clamp
                && importer.filterMode == FilterMode.Bilinear && pc.overridden && android.overridden
                && pc.maxTextureSize == 2048 && android.maxTextureSize == 2048
                && pc.format == TextureImporterFormat.DXT1 && android.format == TextureImporterFormat.ETC2_RGB4;
        }
        /// <summary>更新TextMesh字体和MeshRenderer的配套字体材质；只保存发生变化的预制体，避免旧字体隐式入包。</summary>
        private static void ConfigureCombatFont()
        {
            const string path = "Assets/GameRes/Effects/Combat/BattleEffect.prefab";
            Font font = AssetDatabase.LoadAssetAtPath<Font>("Assets/GameRes/Fonts/CJK/NotoSansCJKsc-Bold.otf");

            if (font == null || font.material == null)
            {
                throw new InvalidOperationException("统一战斗字体或材质缺失");
            }
            GameObject root = PrefabUtility.LoadPrefabContents(path);
            try
            {
                bool changed = false;
                foreach (TextMesh text in root.GetComponentsInChildren<TextMesh>(true))
                {
                    MeshRenderer renderer = text.GetComponent<MeshRenderer>();

                    if (renderer == null)
                    {
                        throw new InvalidOperationException("飘字缺少MeshRenderer：" + text.name);
                    }
                    if (text.font == font && renderer.sharedMaterial == font.material)
                    {
                        continue;
                    }
                    text.font = font;
                    renderer.sharedMaterial = font.material;
                    changed = true;
                }
                if (changed)
                {
                    PrefabUtility.SaveAsPrefabAsset(root, path);
                }
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
        }
        /// <summary>检查源图标识、实际导入格式和旧字体依赖；真实跨平台AB仍要在构建后核对。</summary>
        public static void ValidateBackground(string source, string destination)
        {
            string digest;
            using (SHA256 sha = SHA256.Create())
            {
                digest = Convert.ToBase64String(sha.ComputeHash(File.ReadAllBytes(source)));
            }
            var importer = (TextureImporter)AssetImporter.GetAtPath(destination);

            if (importer.userData != "RevGameRGB1408x792-v1:" + digest || !IsConfigured(importer))
            {
                throw new InvalidOperationException("背景制作版本与原图不一致：" + destination);
            }
            Texture2D texture = AssetDatabase.LoadAssetAtPath<Texture2D>(destination);

            if (texture == null || texture.width != Width || texture.height != Height)
            {
                throw new InvalidOperationException("背景没有使用可压缩的16:9尺寸：" + destination);
            }
            if (importer.GetPlatformTextureSettings("Android").format != TextureImporterFormat.ETC2_RGB4
                || importer.GetPlatformTextureSettings("Standalone").format != TextureImporterFormat.DXT1)
            {
                throw new InvalidOperationException("背景的平台压缩格式错误：" + destination);
            }
        }

        [RevAutomationAction("game.resources.compression-check", Environment = "edit", Keys = new string[0], Description = "检查五章背景制作版本和统一战斗字体，不切场景")]
        public static object Check(RevAutomationRequest request)
        {
            string sourceRoot = Path.GetFullPath("../../美术/AI生成");
            for (int i = 0; i < Names.Length; i++)
            {
                ValidateBackground(Path.Combine(sourceRoot, Sources[i]), Root + Names[i] + ".png");
            }
            string oldFont = AssetDatabase.GUIDToAssetPath("870596c7b091cbf4993bf97518a6a16a");
            foreach (string dependency in AssetDatabase.GetDependencies("Assets/GameRes/Effects/Combat/BattleEffect.prefab", true))
            {
                if (!string.IsNullOrEmpty(oldFont) && dependency == oldFont)
                {
                    throw new InvalidOperationException("战斗飘字仍引用明朝体");
                }
            }
            Font font = AssetDatabase.LoadAssetAtPath<Font>("Assets/GameRes/Fonts/CJK/NotoSansCJKsc-Bold.otf");
            GameObject root = PrefabUtility.LoadPrefabContents("Assets/GameRes/Effects/Combat/BattleEffect.prefab");
            try
            {
                TextMesh[] texts = root.GetComponentsInChildren<TextMesh>(true);

                if (texts.Length != 5)
                {
                    throw new InvalidOperationException("伤害正文和四处描边数量异常");
                }
                foreach (TextMesh text in texts)
                {
                    if (text.font != font || text.GetComponent<MeshRenderer>().sharedMaterial != font.material)
                    {
                        throw new InvalidOperationException("伤害正文/描边没有同步统一字体材质：" + text.name);
                    }
                }
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
            return new Result { text = "五张RGB背景1408×792、源图SHA标识一致；战斗飘字无明朝体依赖" };
        }
        [RevAutomationAction("game.resources.compression-preview", Environment = "edit", Keys = new string[0], Description = "隔离加载真实PC AB绘制五章动态背景与Noto飘字，不切场景")]
        public static object Preview(RevAutomationRequest request)
        {
            Type type = Type.GetType("RevGame.Editor.GameResourceCompressionPreview, Assembly-CSharp-Editor", true);
            try
            {
                return new Result { text = (string)type.GetMethod("Run").Invoke(null, null) };
            }
            catch (System.Reflection.TargetInvocationException error) { throw error.InnerException ?? error; }
        }
        [Serializable]
        private sealed class Result
        {
            public string text;
        }
    }
}
