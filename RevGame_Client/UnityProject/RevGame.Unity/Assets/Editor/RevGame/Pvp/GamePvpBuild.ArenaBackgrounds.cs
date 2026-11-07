using System;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace RevGame.Editor
{
    /// <summary>在编辑器保存两张背景的引用。正式运行时直接随决斗场景或围棋棋盘资源加载。</summary>
    public static partial class GamePvpBuild
    {
        // 从无尽模式材质读取原图，与围棋共用同一份已分包的纹理。
        private const string EndlessBackgroundPath = "Assets/GameRes/Endless/EndlessBackground.mat";
        // 决斗场景直接持有完整矩形 Sprite，Shader 需要独立的 0~1 原画 UV。
        private const string BoxingRingPath = DuelArt + "BoxingRing.png";
        // 决斗底图是画布第一层，之后的角色和按钮依照现有层级绘制。
        private const string DuelBackdropName = "BoxingRingBackdrop";
        // 透过材质选择场景样式，两个背景复用同一个 Shader。
        private const string ArenaShaderPath = "Assets/GameShader/Pvp/PvpArenaBackdrop.shader";

        /// <summary>只更新背景资产及其绑定，保留决斗场景中已有的角色、招式、HUD 和控件引用。</summary>
        public static string BuildArenaBackgrounds()
        {
            if (EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode)
            {
                throw new InvalidOperationException("请先退出 Play 再更新竞技场背景。");
            }

            Scene existing = SceneManager.GetSceneByPath(DuelScenePath);
            bool wasLoaded = existing.isLoaded;
            if (wasLoaded && existing.isDirty)
            {
                throw new InvalidOperationException("决斗场景有未保存改动，请先保存。");
            }

            // 围棋 Prefab 已引用此材质，保留 GUID 即可更新显示，无需重新保存棋盘。
            GetTempleArenaMaterial();
            Scene prior = SceneManager.GetActiveScene();
            Scene duel = wasLoaded ? existing : EditorSceneManager.OpenScene(DuelScenePath, OpenSceneMode.Additive);
            try
            {
                Transform canvas = duel.GetRootGameObjects().Single(root => root.name == "Duel").transform;
                Transform backdrop = canvas.Find(DuelBackdropName) ?? canvas.Find("TempleBackdrop") ??
                    throw new InvalidOperationException("决斗场景缺少背景节点。");
                ApplyBoxingRing(backdrop.GetComponent<Image>());
                if (!EditorSceneManager.SaveScene(duel))
                {
                    throw new InvalidOperationException("决斗场景背景保存失败。");
                }
            }
            finally
            {
                SceneManager.SetActiveScene(prior);
                if (!wasLoaded)
                {
                    EditorSceneManager.CloseScene(duel, true);
                }
            }

            AssetDatabase.SaveAssets();
            return "已保存决斗拳击擂台，并为围棋的无尽神殿原画更新了专用光效。";
        }

        // 决斗新建场景与现有场景升级共用同一套绑定。背景不可接收点击，避免拦住格斗控件。
        private static void ApplyBoxingRing(Image image)
        {
            ImportBoxingRing();
            image.gameObject.name = DuelBackdropName;
            image.sprite = LoadDuelSprite("BoxingRing");
            image.material = SaveArenaMaterial(DuelArt + "BoxingRingBackdrop.mat", 0, null);
            image.raycastTarget = false;
        }

        // 继续使用原 PvpTemple 材质 GUID，使已有 Prefab 和 AB 逻辑路径平滑过渡。
        private static Material GetTempleArenaMaterial()
        {
            Material endless = AssetDatabase.LoadAssetAtPath<Material>(EndlessBackgroundPath);
            Texture art = endless != null ? endless.GetTexture("_ChapterTex") : null;
            if (art == null)
            {
                throw new InvalidOperationException("无尽背景材质未绑定神殿原画。");
            }

            return SaveArenaMaterial("Assets/GameRes/Pvp/PvpTemple.mat", 1, art);
        }

        // 擂台用整张 Sprite 的 UV。保留原分辨率并钳制 UV 边缘，避免边界采样串色。
        private static void ImportBoxingRing()
        {
            var importer = AssetImporter.GetAtPath(BoxingRingPath) as TextureImporter;
            if (importer == null)
            {
                throw new InvalidOperationException("拳击擂台原画未导入：" + BoxingRingPath);
            }

            var settings = new TextureImporterSettings();
            importer.ReadTextureSettings(settings);
            if (importer.textureType == TextureImporterType.Sprite && importer.wrapMode == TextureWrapMode.Clamp && !importer.mipmapEnabled
                && importer.npotScale == TextureImporterNPOTScale.None
                && importer.spriteImportMode == SpriteImportMode.Single && settings.spriteMeshType == SpriteMeshType.FullRect)
            {
                return;
            }

            settings.textureType = TextureImporterType.Sprite;
            settings.wrapMode = TextureWrapMode.Clamp;
            settings.mipmapEnabled = false;
            settings.npotScale = TextureImporterNPOTScale.None;
            settings.spriteMode = (int)SpriteImportMode.Single;
            settings.spriteMeshType = SpriteMeshType.FullRect;
            importer.SetTextureSettings(settings);
            importer.SaveAndReimport();
        }

        // style 与 Shader 的 _Style 对应。迁移材质时清理旧 Shader 的属性，只保留新 Shader 使用的采样槽。
        private static Material SaveArenaMaterial(string path, int style, Texture art)
        {
            Shader shader = AssetDatabase.LoadAssetAtPath<Shader>(ArenaShaderPath) ??
                throw new InvalidOperationException("竞技场背景 Shader 未导入。");
            Material material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                material = new Material(shader);
                AssetDatabase.CreateAsset(material, path);
            }
            else if (material.shader != shader)
            {
                var replacement = new Material(shader) { name = material.name };
                try
                {
                    EditorUtility.CopySerialized(replacement, material);
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(replacement);
                }
            }

            material.SetFloat("_Style", style);
            material.SetTexture("_MainTex", art);
            EditorUtility.SetDirty(material);
            return material;
        }
    }
}
