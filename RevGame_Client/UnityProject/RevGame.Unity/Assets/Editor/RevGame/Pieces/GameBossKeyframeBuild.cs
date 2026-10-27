// 六章Boss关键帧制作：帧选择由清单记录，Unity只导入48张制作副本，原97帧动画保留在美术目录。
using System;
using System.IO;
using RevGame.Core;
using RevGame.View;
using UnityEditor;
using UnityEngine;

namespace RevGame.Editor
{
    public static class GameBossKeyframeBuild
    {
        private static readonly string[] Roles = { "Swordman", "Archer", "MageWater", "Assassin", "Shield", "Priest" };
        /// <summary>只写Boss独立美术配置，普通PieceArtConfig不引用新帧，避免进入战斗时六章全加载。</summary>
        public static string Configure()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) { throw new InvalidOperationException("Boss关键帧制作只能在编辑态执行"); }
            if (!AssetDatabase.IsValidFolder("Assets/GameRes/Config/Boss")) { AssetDatabase.CreateFolder("Assets/GameRes/Config", "Boss"); }
            for (int chapter = 1; chapter <= 6; chapter++)
            {
                string role = Roles[chapter - 1];
                var frames = new Sprite[8];
                for (int frame = 0; frame < frames.Length; frame++)
                {
                    string name = "Boss" + chapter.ToString("D2") + "_" + role + "_Idle_" + (frame + 1).ToString("D2");
                    string path = "Assets/GameRes/Art/Boss/Chapter" + chapter.ToString("D2") + "_" + role + "/" + name + ".png";
                    var importer = AssetImporter.GetAtPath(path) as TextureImporter;
                    if (importer == null) { throw new InvalidOperationException("Boss关键帧缺失：" + path); }
                    importer.textureType = TextureImporterType.Sprite;
                    importer.spriteImportMode = SpriteImportMode.Single;
                    importer.spritePixelsPerUnit = 384f;
                    importer.spritePivot = new Vector2(.5f, .5f);
                    var spriteSettings = new TextureImporterSettings();
                    importer.ReadTextureSettings(spriteSettings);
                    spriteSettings.spriteAlignment = (int)SpriteAlignment.Center;
                    spriteSettings.spriteGenerateFallbackPhysicsShape = false;
                    // 光晕沿帧外透明区展开；完整矩形保留Shader绘制区域，紧密网格会裁掉环形光效。
                    spriteSettings.spriteMeshType = SpriteMeshType.FullRect;
                    importer.SetTextureSettings(spriteSettings);
                    importer.mipmapEnabled = false;
                    importer.isReadable = false;
                    importer.alphaIsTransparency = true;
                    importer.maxTextureSize = 512;
                    importer.SaveAndReimport();
                    frames[frame] = AssetDatabase.LoadAssetAtPath<Sprite>(path);
                }
                string assetPath = "Assets/GameRes/Config/Boss/" + BossChapterArtConfig.ResourceName(chapter) + ".asset";
                BossChapterArtConfig config = AssetDatabase.LoadAssetAtPath<BossChapterArtConfig>(assetPath);
                if (config == null)
                {
                    config = ScriptableObject.CreateInstance<BossChapterArtConfig>();
                    config.FrameSeconds = .18f;
                    config.BodyScale = 1;
                    config.PingPong = true;
                    config.BuiltInDecorations = true;
                    AssetDatabase.CreateAsset(config, assetPath);
                }
                config.Chapter = chapter;
                config.Appearance = (PieceKind)Enum.Parse(typeof(PieceKind), role);
                config.Frames = frames;
                config.Validate(chapter);
                EditorUtility.SetDirty(config);
                AssetDatabase.SaveAssetIfDirty(config);
            }
            return "六章Boss各8张关键帧已装配；384px、统一中心、往返播放，普通棋子配置不变";
        }

        public static string Check()
        {
            int count = 0;
            for (int chapter = 1; chapter <= 6; chapter++)
            {
                string path = "Assets/GameRes/Config/Boss/" + BossChapterArtConfig.ResourceName(chapter) + ".asset";
                BossChapterArtConfig config = AssetDatabase.LoadAssetAtPath<BossChapterArtConfig>(path);
                if (config == null) { throw new InvalidOperationException("Boss配置缺失：" + path); }
                config.Validate(chapter);
                if (config.Appearance.ToString() != Roles[chapter - 1]) { throw new InvalidOperationException("Boss章节职业美术映射错误"); }
                foreach (Sprite sprite in config.Frames)
                {
                    string framePath = AssetDatabase.GetAssetPath(sprite);
                    string expected = "atlas_" + BossChapterArtConfig.AtlasName(chapter).ToLowerInvariant();
                    if (AssetDatabase.GetImplicitAssetBundleName(framePath) != expected || Mathf.Abs(sprite.pixelsPerUnit - 384) > .001f)
                    { throw new InvalidOperationException("Boss帧分包/尺寸异常：" + framePath); }
                    count++;
                }
                if (AssetDatabase.GetImplicitAssetBundleName(path) != "atlas_" + BossChapterArtConfig.AtlasName(chapter).ToLowerInvariant())
                { throw new InvalidOperationException("Boss配置与其图集没有同包"); }
            }
            return count + "帧、六章职业映射、配置/图集同包检查通过";
        }
    }
}
