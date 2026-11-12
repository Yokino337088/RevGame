using System;
using RevGame.Endless;
using UnityEditor;
using UnityEngine;

namespace RevGame.Editor
{
    public static partial class GameEndlessBuild
    {
        /// <summary>新增素材在Assets内按固定配方导入；修改预制体时保留GUID，不重建当前场景。</summary>
        public static string BuildInteraction()
        {
            if (EditorApplication.isPlaying)
            {
                throw new InvalidOperationException("交互素材配方需要编辑态");
            }
            ConfigureArrowSprite("CardArrowHead");
            ConfigureArrowSprite("CardArrowRibbon");
            Material thrust = SampledMaterial("EndlessThrust", "RevGame/Endless/Thrust", "_TrailTex", ThrustTrailTexture);
            var bladeImporter = (TextureImporter)AssetImporter.GetAtPath("Assets/GameRes/Endless/Textures/EndlessThrustBlade.png");
            bladeImporter.textureType = TextureImporterType.Default;
            bladeImporter.mipmapEnabled = false;
            bladeImporter.isReadable = false;
            bladeImporter.wrapMode = TextureWrapMode.Clamp;
            bladeImporter.SaveAndReimport();
            thrust.SetTexture("_BladeTex", AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/GameRes/Endless/Textures/EndlessThrustBlade.png"));
            EditorUtility.SetDirty(thrust);
            PatchInteractionPrefab();
            const string arenaPath = "Assets/GameRes/Endless/EndlessArena.prefab";
            GameObject arena = PrefabUtility.LoadPrefabContents(arenaPath);
            try
            {
                var serialized = new SerializedObject(arena.GetComponent<EndlessArena>());
                var horde = (MeshRenderer)serialized.FindProperty("hordeRenderer").objectReferenceValue;
                horde.sortingOrder = 8;
                var renderer = (MeshRenderer)serialized.FindProperty("thrustRenderer").objectReferenceValue;
                renderer.sortingOrder = 20;
                InstallUpgradeFeedback(arena);
                PrefabUtility.SaveAsPrefabAsset(arena, arenaPath);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(arena);
            }
            AssetDatabase.SaveAssets();
            return "曲线箭头/拖曳/刺客长剑气已装配；杂兵8层、火焰7层、弹道原30层";
        }
        /// <summary>固定项目配方装配升级共享材质和网格；仅加载既有竞技场内容，不改当前场景，不更换Prefab GUID。</summary>
        private static void InstallUpgradeFeedback(GameObject arena)
        {
            const string path = "Assets/GameRes/Material/Endless/EndlessCardUpgrade.mat";
            Material material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                Shader shader = Shader.Find("RevGame/Endless/CardUpgrade");
                if (shader == null)
                {
                    throw new InvalidOperationException("升级Shader尚未导入");
                }
                material = new Material(shader);
                AssetDatabase.CreateAsset(material, path);
            }
            Transform existing = arena.transform.Find("CardUpgradeFeedback");
            GameObject node = existing != null ? existing.gameObject : new GameObject("CardUpgradeFeedback");
            node.transform.SetParent(arena.transform, false);
            if (node.GetComponent<MeshFilter>() == null)
            {
                node.AddComponent<MeshFilter>();
            }
            MeshRenderer renderer = node.GetComponent<MeshRenderer>();
            if (renderer == null)
            {
                renderer = node.AddComponent<MeshRenderer>();
            }
            renderer.sharedMaterial = material;
            renderer.sortingOrder = 21;
            renderer.enabled = false;
            var serialized = new SerializedObject(arena.GetComponent<EndlessArena>());
            serialized.FindProperty("upgradeRenderer").objectReferenceValue = renderer;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }
        /// <summary>源图已经裁到独立箭头/丝带区域；以Single Sprite导入供唯一V2图集收纳。</summary>
        private static void ConfigureArrowSprite(string name)
        {
            string path = "Assets/GameRes/Art/Endless/CardArrow/" + name + ".png";
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            importer.textureType = TextureImporterType.Sprite;
            importer.spriteImportMode = SpriteImportMode.Single;
            importer.spritePixelsPerUnit = 100;
            importer.mipmapEnabled = false;
            importer.isReadable = false;
            importer.alphaIsTransparency = true;
            importer.SaveAndReimport();
        }
        /// <summary>加载现有HUD内容后装配组件并保存，finally归还编辑器临时对象，原Prefab GUID不变。</summary>
        private static void PatchInteractionPrefab()
        {
            const string path = "Assets/GameRes/UI/Panel/Endless/EndlessHudPanel.prefab";
            GameObject root = PrefabUtility.LoadPrefabContents(path);
            try
            {
                InstallCardDrag(root);
                PrefabUtility.SaveAsPrefabAsset(root, path);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }
        /// <summary>独立配方和原HUD生成器共用装配方法，下一次重建也不会丢掉拖放节点。</summary>
        private static void InstallCardDrag(GameObject root)
        {
            for (int i = 0; i < 4; i++)
            {
                GameObject card = root.transform.Find("bottom/card" + i).gameObject;
                if (card.GetComponent<EndlessCardDrag>() == null)
                {
                    card.AddComponent<EndlessCardDrag>();
                }
            }
            Transform existing = root.transform.Find("cardArrow");
            GameObject arrow = existing != null ? existing.gameObject : Rect("cardArrow", root.transform, 0, 0, 1, 1);
            if (arrow.GetComponent<CanvasRenderer>() == null)
            {
                arrow.AddComponent<CanvasRenderer>();
            }
            EndlessCurveArrow graphic = arrow.GetComponent<EndlessCurveArrow>();
            if (graphic == null)
            {
                graphic = arrow.AddComponent<EndlessCurveArrow>();
            }
            graphic.raycastTarget = false;
            graphic.enabled = false;
            var fields = new SerializedObject(graphic);
            fields.FindProperty("ribbon").objectReferenceValue = RequireAsset<Sprite>("Assets/GameRes/Art/Endless/CardArrow/CardArrowRibbon.png");
            fields.FindProperty("arrowHead").objectReferenceValue = RequireAsset<Sprite>("Assets/GameRes/Art/Endless/CardArrow/CardArrowHead.png");
            fields.ApplyModifiedPropertiesWithoutUndo();
            var hud = new SerializedObject(root.GetComponent<EndlessHudPanel>());
            hud.FindProperty("cardArrow").objectReferenceValue = graphic;
            hud.ApplyModifiedPropertiesWithoutUndo();
        }
    }
}
