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
    /// 【规矩】UI 预制体绝对不能出现白膜：每个 Image 都必须有 Sprite，并使用九宫格（Sliced）。
    /// 九宫格的意思是把图切成 9 块：四个角不缩放、四条边单向拉伸、中间双向拉伸，
    /// 这样同一张小图能撑满任意大小的面板，边框也不会变形。切割线就是 spriteBorder。
    ///
    /// 【差异化】不同用途用不同的图，而不是一张图改颜色，玩家一眼能分清主次：
    /// 面板/弹窗/按钮/手牌/血条各有自己的图。
    /// </summary>
    public static partial class GameEndlessBuild
    {
        /// <summary>无尽 UI 素材目录。放在这里的图会被 GameSpriteAtlasBuild 打进 UIEndless 图集。</summary>
        private const string TempleArt = "Assets/GameRes/Art/UI/Endless/";
        /// <summary>神殿背景原图。它走材质采样，不进 Sprite 图集，所以单独配置。</summary>
        private const string TempleTexture = "Assets/GameRes/ChapterBackground/EndlessTemple.png";

        /// <summary>每张图的九宫格切割线（左、下、右、上，单位像素），和 tools/make-endless-ui.py 的裁剪一一对应。</summary>
        private static readonly Dictionary<string, Vector4> Borders = new Dictionary<string, Vector4>
        {
            { "EndlessFrame", new Vector4(54, 54, 54, 54) },
            { "EndlessVine", new Vector4(110, 100, 110, 100) },
            { "EndlessBar", new Vector4(48, 26, 48, 26) },
            { "EndlessButton", new Vector4(44, 44, 44, 44) },
            { "EndlessPrimary", new Vector4(44, 40, 44, 40) },
            { "EndlessField", new Vector4(12, 12, 12, 12) },
            { "EndlessCard", new Vector4(24, 24, 24, 24) },
            { "EndlessTrack", new Vector4(30, 6, 30, 6) },
            { "EndlessFill", new Vector4(20, 4, 20, 4) },
        };

        /// <summary>
        /// 设置 Sprite 的导入参数。FullRect 让网格保持完整矩形，九宫格拉伸才正确；
        /// 关掉 Mipmap 和 Readable，UI 用不到，开着只会白占内存。
        /// </summary>
        private static void ConfigureTempleArt()
        {
            foreach (KeyValuePair<string, Vector4> pair in Borders)
            {
                string path = TempleArt + pair.Key + ".png";
                AssetDatabase.ImportAsset(path);
                var importer = AssetImporter.GetAtPath(path) as TextureImporter;
                if (importer == null)
                {
                    throw new InvalidOperationException("无尽UI原图缺失，请先运行 tools/make-endless-ui.py：" + path);
                }
                importer.textureType = TextureImporterType.Sprite;
                importer.spriteImportMode = SpriteImportMode.Single;
                importer.spriteBorder = pair.Value;
                importer.spritePixelsPerUnit = 100;
                importer.mipmapEnabled = false;
                importer.isReadable = false;
                importer.alphaIsTransparency = true;
                var settings = new TextureImporterSettings();
                importer.ReadTextureSettings(settings);
                settings.spriteMeshType = SpriteMeshType.FullRect;
                importer.SetTextureSettings(settings);
                importer.SaveAndReimport();
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

        /// <summary>按节点名字决定用哪张图。名字约定写在这一处，生成器其余代码不用关心美术。</summary>
        private static string SpriteFor(string name, bool button)
        {
            if (name == "heartTrack")
            {
                return "EndlessTrack";
            }
            if (name == "heartFill")
            {
                return "EndlessFill";
            }
            if (name == "top" || name == "bottom")
            {
                return "EndlessBar";
            }
            if (name == "modal" || name == "entry")
            {
                return "EndlessVine";
            }
            if (name.StartsWith("card", StringComparison.Ordinal) || name.StartsWith("choice", StringComparison.Ordinal)
                || name.StartsWith("formation", StringComparison.Ordinal))
            {
                return "EndlessCard";
            }
            if (!button)
            {
                return "EndlessFrame";
            }
            if (name == "start" || name == "resume" || name == "btnEndless")
            {
                return "EndlessPrimary";
            }
            if (name.StartsWith("route", StringComparison.Ordinal) || name.StartsWith("banish", StringComparison.Ordinal)
                || name == "effects" || name == "flash" || name == "shake")
            {
                return "EndlessField";
            }
            return "EndlessButton";
        }

        /// <summary>给 Image 配图。找不到图或没配切割线就直接报错，绝不退回白膜。</summary>
        private static void ApplyTempleImage(Image image, string name, bool button = false)
        {
            string spriteName = SpriteFor(name, button);
            image.sprite = AssetDatabase.LoadAssetAtPath<Sprite>(TempleArt + spriteName + ".png");
            if (image.sprite == null || image.sprite.border == Vector4.zero)
            {
                throw new InvalidOperationException("无尽UI缺图或九宫格边界：" + spriteName);
            }
            image.type = Image.Type.Sliced;
            image.color = Color.white;
            // 染色只做色相提示：主操作偏金，设置类偏淡紫。原图已经区分主次，这里不再改变明暗结构。
            if (spriteName == "EndlessPrimary")
            {
                image.color = new Color(1f, .90f, .58f);
            }
            else if (name == "effects" || name == "flash" || name == "shake")
            {
                image.color = new Color(.92f, .86f, .98f);
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
                    if (image.type != Image.Type.Sliced || image.sprite.border == Vector4.zero)
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
