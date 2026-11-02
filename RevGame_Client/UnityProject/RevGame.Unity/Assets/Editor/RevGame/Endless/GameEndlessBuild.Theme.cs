using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace RevGame.Editor
{
    /// <summary>
    /// 无尽模式的 UI 美术配方（新手先读这段）。
    ///
    /// 【规矩】UI 预制体绝对不能出现白膜：每个 Image 都必须有 Sprite。
    /// 面板、按钮、血条用九宫格（Sliced）：图被切成 9 块，四个角不缩放，边和中间才拉伸，
    /// 所以同一张图能撑满不同尺寸，边框也不变形。切割线就是 spriteBorder。
    /// 图标（齿轮、关闭叉、棋子头像）不需要拉伸，用 Simple 并保持比例。
    ///
    /// 【素材来源】全部用项目 Art/UI 里的原图，它们已经属于 UIBattle / UIMenus 图集，
    /// 不再单独裁一份，这样无尽和六章风格一致，也不会多占一张图集。
    /// </summary>
    public static partial class GameEndlessBuild
    {
        /// <summary>项目 UI 原图目录。</summary>
        private const string TempleArt = "Assets/GameRes/Art/UI/";
        /// <summary>神殿背景原图。它走材质采样，不进 Sprite 图集，所以单独配置。</summary>
        private const string TempleTexture = "Assets/GameRes/ChapterBackground/EndlessTemple.png";

        /// <summary>需要补九宫格切割线的原图（左、下、右、上，单位像素）。已有正确切割线的图不在这里。</summary>
        private static readonly Dictionary<string, Vector4> Borders = new Dictionary<string, Vector4>
        {
            // 藤蔓都在四角和上边，切割线要把藤蔓整个留在“不拉伸”的角里。
            { "UI_Pvp_imgBK", new Vector4(130, 95, 130, 95) },
            { "UI_Game_imgMobilityBK", new Vector4(26, 0, 26, 0) },
            { "UI_Game_imgEnemyMobility", new Vector4(26, 0, 26, 0) },
            // 2026-11-02 HUD 重做用到的用户指定原图：奶油长条、深色面板、木纹竖卡（棕色按钮原本就有切割线）。
            // 切割线只包住圆角描边，中间纹理才拉伸；六章里这些图是 Simple 模式，不受影响。
            { "UI_achieveItem_imgBK", new Vector4(36, 30, 36, 30) },
            { "UI_Tip_imgBK", new Vector4(34, 34, 34, 34) },
            { "UI_Tavern_imgBK", new Vector4(64, 64, 64, 64) },
        };

        /// <summary>
        /// 设置 Sprite 的切割线。这些图也被六章面板使用，但那些 Image 是 Simple 模式，
        /// 切割线只对 Sliced 生效，所以不会影响六章的显示。
        /// FullRect 让网格保持完整矩形，九宫格拉伸才正确。
        /// </summary>
        private static void ConfigureTempleArt()
        {
            foreach (KeyValuePair<string, Vector4> pair in Borders)
            {
                string path = TempleArt + pair.Key + ".png";
                var importer = AssetImporter.GetAtPath(path) as TextureImporter;
                if (importer == null)
                {
                    throw new InvalidOperationException("无尽UI原图缺失：" + path);
                }
                var settings = new TextureImporterSettings();
                importer.ReadTextureSettings(settings);
                if (settings.spriteBorder != pair.Value || settings.spriteMeshType != SpriteMeshType.FullRect)
                {
                    // 切割线要写在 settings 上再整体写回：先改 importer.spriteBorder 再 SetTextureSettings，
                    // 会被 settings 里的旧值（0）覆盖掉，meta 里看起来就像从没设置过。
                    settings.spriteBorder = pair.Value;
                    settings.spriteMeshType = SpriteMeshType.FullRect;
                    importer.SetTextureSettings(settings);
                    importer.SaveAndReimport();
                }
            }
            var background = (TextureImporter)AssetImporter.GetAtPath(TempleTexture);
            background.textureType = TextureImporterType.Default;
            background.mipmapEnabled = false;
            background.isReadable = false;
            background.npotScale = TextureImporterNPOTScale.None;
            background.maxTextureSize = 2048;
            background.wrapMode = TextureWrapMode.Clamp;
            background.textureCompression = TextureImporterCompression.CompressedHQ;
            background.SaveAndReimport();
        }

        /// <summary>剑气刃贴图（Kenney Particle Pack 的 twirl_03，CC0），已缩到 256×256。</summary>
        private const string SwordAuraTexture = "Assets/GameRes/Endless/Textures/EndlessSwordAuraBlade.png";

        /// <summary>盾牌剪影（Kenney Board Game Icons 的 shield.png，CC0）与突刺速度残影（Kenney Particle Pack 的 trace_01，CC0，已转横向）。</summary>
        private const string ShieldIconTexture = "Assets/GameRes/Endless/Textures/EndlessShieldIcon.png";
        private const string ThrustTrailTexture = "Assets/GameRes/Endless/Textures/EndlessThrustTrail.png";

        /// <summary>
        /// 通用“非 Sprite 采样纹理 + 共享材质”配方：Shader 需要完整 0~1 UV（旋转/拉伸采样），所以按 Default 纹理导入，
        /// 钳制寻址、关 mipmap、不可读，随 endless_arena 包走（与剑气刃贴图同一规则）。
        /// </summary>
        private static Material SampledMaterial(string name, string shader, string property, string texturePath)
        {
            var importer = (TextureImporter)AssetImporter.GetAtPath(texturePath);
            if (importer == null)
            {
                throw new InvalidOperationException("无尽贴图缺失：" + texturePath);
            }
            importer.textureType = TextureImporterType.Default;
            importer.alphaSource = TextureImporterAlphaSource.FromInput;
            importer.alphaIsTransparency = true;
            importer.mipmapEnabled = false;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.maxTextureSize = 256;
            importer.npotScale = TextureImporterNPOTScale.None;
            importer.isReadable = false;
            importer.SaveAndReimport();
            Material material = Material(name, shader);
            material.SetTexture(property, AssetDatabase.LoadAssetAtPath<Texture2D>(texturePath));
            EditorUtility.SetDirty(material);
            return material;
        }

        /// <summary>
        /// 剑气材质。刃贴图被 Shader 旋转采样，所以不能进 Sprite 图集（图集里的 UV 不是 0~1，旋转会采到邻图）：
        /// 按“非 Sprite 采样纹理”导入，钳制寻址、关 mipmap、只保留 256 尺寸，跟着 endless_arena 包走。
        /// </summary>
        private static Material SwordAuraMaterial()
        {
            var importer = (TextureImporter)AssetImporter.GetAtPath(SwordAuraTexture);
            if (importer == null)
            {
                throw new InvalidOperationException("剑气贴图缺失：" + SwordAuraTexture);
            }
            importer.textureType = TextureImporterType.Default;
            importer.alphaSource = TextureImporterAlphaSource.FromInput;
            importer.alphaIsTransparency = true;
            importer.mipmapEnabled = false;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.maxTextureSize = 256;
            importer.npotScale = TextureImporterNPOTScale.None;
            importer.isReadable = false;
            importer.SaveAndReimport();
            Material material = Material("EndlessSwordAura", "RevGame/Endless/SwordAura");
            material.SetTexture("_BladeTex", AssetDatabase.LoadAssetAtPath<Texture2D>(SwordAuraTexture));
            EditorUtility.SetDirty(material);
            return material;
        }

        /// <summary>
        /// 无尽音频导入设置：三首 BGM 走流式加载（不一次解压进内存，配合 EndlessMusicPlaylist 的“快播完才加载下一首”），
        /// 打击音效体积很小、播放频繁，加载时解压，播放零延迟。
        /// </summary>
        private static void ConfigureEndlessAudio()
        {
            foreach (string guid in AssetDatabase.FindAssets("t:AudioClip", new[] { "Assets/GameRes/Audio/Bgm/Endless", "Assets/GameRes/Audio/Sfx/Endless" }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var importer = (AudioImporter)AssetImporter.GetAtPath(path);
                bool music = path.Contains("/Bgm/");
                AudioImporterSampleSettings settings = importer.defaultSampleSettings;
                settings.loadType = music ? AudioClipLoadType.Streaming : AudioClipLoadType.DecompressOnLoad;
                settings.compressionFormat = AudioCompressionFormat.Vorbis;
                settings.quality = music ? .5f : .7f;
                settings.preloadAudioData = !music;
                importer.defaultSampleSettings = settings;
                importer.loadInBackground = music;
                importer.forceToMono = !music;
                importer.SaveAndReimport();
            }
        }

        /// <summary>图标类节点：不拉伸，保持原比例。</summary>
        private static bool IsIcon(string name)
        {
            return name == "pause" || name == "close" || name == "icon" || name == "modalClose";
        }

        /// <summary>按节点名字决定用哪张图。名字约定写在这一处，生成器其余代码不用关心美术。</summary>
        private static string SpriteFor(string name)
        {
            switch (name)
            {
                case "pause": return "UI_Pvp_btnSetting";
                case "close": case "modalClose": return "UI_Buy_btnClose";
                case "icon": return "UI_Setting_Background";
                case "entry": return "UI_Pvp_imgBK";
                case "top": return "UI_achieveItem_imgBK";
                case "modal": case "tip": case "boss": case "banner": case "boon": return "UI_Tip_imgBK";
                case "heartTrack": case "bossTrack": case "xpTrack": return "UI_Game_imgMobilityBK";
                case "heartFill": case "bossFill": case "xpFill": return "UI_Game_imgEnemyMobility";
                case "start": case "level": case "resume": case "reroll": return "UI_Tavern_btnClose";
            }
            if (name.StartsWith("card", StringComparison.Ordinal))
            {
                return "UI_roleItem_imgBK";
            }
            if (name.StartsWith("choice", StringComparison.Ordinal))
            {
                return "UI_Tavern_imgBK";
            }
            if (name.StartsWith("formation", StringComparison.Ordinal))
            {
                return "UI_roleItem_roleItem";
            }
            return "UI_Tavern_Background";
        }

        /// <summary>给 Image 配图。找不到图或没配切割线就直接报错，绝不退回白膜。</summary>
        private static void ApplyTempleImage(Image image, string name)
        {
            string spriteName = SpriteFor(name);
            image.sprite = AssetDatabase.LoadAssetAtPath<Sprite>(TempleArt + spriteName + ".png");
            if (image.sprite == null)
            {
                throw new InvalidOperationException("无尽UI缺图：" + spriteName);
            }
            image.color = Color.white;
            if (IsIcon(name))
            {
                image.type = Image.Type.Simple;
                image.preserveAspect = true;
                return;
            }
            // 血条这类细长图只切左右（上下为 0）也算有效切割，所以只要求至少有一条切割线。
            if (image.sprite.border == Vector4.zero)
            {
                throw new InvalidOperationException("无尽UI缺九宫格切割线：" + spriteName);
            }
            image.type = Image.Type.Sliced;
            // 木纹竖卡原图 828×1454、圆角 64 像素；缩到手牌这么小时，原尺寸的圆角会把整张卡挤成胶囊形。
            // pixelsPerUnitMultiplier 把九宫格的角按比例缩小（4 倍 → 角约 16 像素），卡片形状才和原图一致。
            if (name.StartsWith("choice", StringComparison.Ordinal))
            {
                image.pixelsPerUnitMultiplier = 2.5f;
            }
        }

        /// <summary>
        /// 白膜门禁：生成结束后扫描无尽的所有面板，发现没配图的 Image 就让构建失败。
        /// 比靠人眼检查可靠，以后谁改生成器漏了图，这里会立刻拦下。
        /// </summary>
        private static void AssertNoWhiteImages()
        {
            foreach (string guid in AssetDatabase.FindAssets("t:Prefab", new[] { "Assets/GameRes/UI/Panel/Endless" }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                foreach (Image image in prefab.GetComponentsInChildren<Image>(true))
                {
                    if (image.sprite == null)
                    {
                        throw new InvalidOperationException("无尽UI出现白膜：" + path + " / " + image.name);
                    }
                    if (!IsIcon(image.name) && (image.type != Image.Type.Sliced || image.sprite.border == Vector4.zero))
                    {
                        throw new InvalidOperationException("无尽UI未使用九宫格：" + path + " / " + image.name);
                    }
                }
            }
        }

        /// <summary>
        /// 神殿背景材质：氛围参数都不高，因为背景是衬托，太亮会抢棋盘的视线。
        /// BoardProtection 让棋盘周围的光效收敛，棋子始终是画面主角。
        /// </summary>
        private static Material TempleBackground()
        {
            Material material = Material("EndlessBackground", "RevGame/Endless/StarField");
            material.SetTexture("_ChapterTex", AssetDatabase.LoadAssetAtPath<Texture2D>(TempleTexture));
            material.SetFloat("_Strength", .62f);
            material.SetFloat("_Glow", .72f);
            material.SetFloat("_GodRays", .95f);
            material.SetFloat("_RuneStrength", .55f);
            material.SetFloat("_Particles", .32f);
            material.SetFloat("_BoardProtection", .85f);
            material.SetFloat("_Aspect", 16f / 9f);
            EditorUtility.SetDirty(material);
            return material;
        }
    }
}
