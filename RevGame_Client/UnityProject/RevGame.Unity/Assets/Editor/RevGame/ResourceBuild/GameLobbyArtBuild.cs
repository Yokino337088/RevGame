// 小屋原画、植物遮罩与Shader参数通过既有AB总入口制作；版本迁移只执行一次，保护美术后续调参。
using System;
using UnityEditor;
using UnityEngine;

namespace RevGame.Editor
{
    /// <summary>非Sprite的局部效果纹理归lobby_cottage包，运行期不生成像素或单独绕过Rev资源系统。</summary>
    public static class GameLobbyArtBuild
    {
        /// <summary>导入可再生成的自然遮罩并绑定材质，旧材质首次升级参数，之后保留Inspector设置。</summary>
        public static void Configure()
        {
            const string maskPath = "Assets/GameRes/Lobby/MagicCottageNatureMask.png";
            var texture = AssetImporter.GetAtPath(maskPath) as TextureImporter;
            if (texture == null)
            {
                throw new InvalidOperationException("自然遮罩未生成，请先执行tools/lobby-nature-mask.py");
            }
            if (texture.textureType != TextureImporterType.Default || texture.sRGBTexture || texture.mipmapEnabled || texture.isReadable)
            {
                texture.textureType = TextureImporterType.Default;
                texture.sRGBTexture = false;
                texture.mipmapEnabled = false;
                texture.isReadable = false;
                texture.wrapMode = TextureWrapMode.Clamp;
                texture.maxTextureSize = 2048;
                texture.SetPlatformTextureSettings(new TextureImporterPlatformSettings { name = "Standalone", overridden = true, maxTextureSize = 2048, format = TextureImporterFormat.DXT1 });
                texture.SetPlatformTextureSettings(new TextureImporterPlatformSettings { name = "Android", overridden = true, maxTextureSize = 2048, format = TextureImporterFormat.ETC2_RGB4 });
                texture.SaveAndReimport();
            }
            const string materialPath = "Assets/GameRes/Lobby/MagicCottageMaterial.mat";
            Material material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
            if (material == null)
            {
                throw new InvalidOperationException("缺少大厅小屋正式材质");
            }
            Texture2D mask = AssetDatabase.LoadAssetAtPath<Texture2D>(maskPath);
            if (material.GetTexture("_NatureMask") != mask)
            {
                material.SetTexture("_NatureMask", mask);
                EditorUtility.SetDirty(material);
            }
            AssetImporter importer = AssetImporter.GetAtPath(materialPath);
            const string revision = "RevGame.LobbyMagic.v2";
            if (!importer.userData.Contains(revision))
            {
                // 一次版本升级改善旧默认值；以后只调材质Inspector即可，重建AB不能覆盖调好的水波/风动。
                material.SetFloat("_WaterAmount", .006f);
                material.SetFloat("_WaterSpeed", .8f);
                material.SetFloat("_WaterLight", .32f);
                material.SetFloat("_WindAmount", .0045f);
                material.SetFloat("_WindSpeed", 1.2f);
                material.SetFloat("_MagicGlow", .55f);
                material.SetFloat("_CottageMagic", .38f);
                material.SetFloat("_SmokeAmount", .003f);
                importer.userData += "\n" + revision;
                importer.SaveAndReimport();
                EditorUtility.SetDirty(material);
            }
        }
    }
}
