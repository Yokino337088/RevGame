// ============================================================
// GameArcaneArtBuilder.cs —— 通过Unity MCP创建材质并保存UI预制体引用。
// 幂等装配：重复执行不会新建几十份材质，也不保存或覆盖用户正在编辑的场景和背景图片。
// 运行时靠预制体直接引用；场景背景则由GamePresentation通过RevResManager加载。
// ============================================================
using System;
using RevGame.UI;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace RevGame.Editor
{
    public static class GameArcaneArtBuilder
    {
        private const string MaterialRoot = "Assets/GameRes/Material/";

        /// <summary>仅非Play调用；先验证Shader，再保存引用，避免给玩家留下粉色控件。</summary>
        public static string Apply()
        {
            if (EditorApplication.isPlaying) { throw new InvalidOperationException("请先退出Play再保存动态美术资源"); }
            Material card = MaterialOf("CardGleam", "RevGame/UI/CardGleam", 0.30f, 0.18f, 1f);
            Material panel = MaterialOf("ArcanePanel", "RevGame/UI/ArcanePanel", 0.38f, 0.28f, 1f);
            Material backdrop = MaterialOf("ArcaneBackdrop", "RevGame/Background/ArcaneBackdrop", 0.7f, 0.28f, 0f);
            MaterialOf("ArcaneScene", "RevGame/Background/Chapter01_Tutorial", 0.85f, 0.38f, 1f);
            int images = 0;
            foreach (string guid in AssetDatabase.FindAssets("t:Prefab", new[] { "Assets/GameRes/UI", "Assets/GameRes/Effects" }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                GameObject root = PrefabUtility.LoadPrefabContents(path);
                try
                {
                    foreach (Image image in root.GetComponentsInChildren<Image>(true))
                    {
                        string spritePath = AssetDatabase.GetAssetPath(image.sprite);
                        if (image.name == "screenBackground" || spritePath.StartsWith("Assets/ArtRes/背景/", StringComparison.Ordinal)
                            || spritePath.StartsWith("Assets/GameRes/Art/Background/", StringComparison.Ordinal))
                        {
                            StyleBackdrop(image, backdrop);
                        }
                        // PreparePanel中已经保存了六个卡片槽位，不能只处理独立CardItem.prefab的根节点。
                        else if (image.GetComponent<CardItem>() != null)
                        {
                            image.material = card;
                            Coordinates(image);
                        }
                        else if (IsSurface(spritePath))
                        {
                            image.material = panel;
                            Coordinates(image);
                        }
                        else { continue; }
                        images++;
                    }
                    PrefabUtility.SaveAsPrefabAsset(root, path);
                }
                finally { PrefabUtility.UnloadPrefabContents(root); }
            }
            AssetDatabase.SaveAssets();
            return "已保存4个共享材质、" + images + "个UI底图引用；UI大地图背景已替换为程序星雾，场景文件未被覆盖。";
        }

        /// <summary>生成器重建大厅时同样使用无地图背景；不能只修改这一次预制体而让下次生成又回到旧外观。</summary>
        public static void StyleBackdrop(Image image, Material material = null)
        {
            if (material == null) { material = MaterialOf("ArcaneBackdrop", "RevGame/Background/ArcaneBackdrop", 0.7f, 0.28f, 0f); }
            AspectRatioFitter fitter = image.GetComponent<AspectRatioFitter>();
            if (fitter != null) { UnityEngine.Object.DestroyImmediate(fitter); }
            image.sprite = null;
            image.overrideSprite = null;
            image.color = Color.white;
            image.type = Image.Type.Simple;
            image.preserveAspect = false;
            image.raycastTarget = false;
            image.material = material;
            RectTransform rect = image.rectTransform;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = rect.offsetMax = Vector2.zero;
            Coordinates(image);
        }

        /// <summary>只给底图加特效坐标，肖像、字体、血条和图标保留原渲染。</summary>
        private static void Coordinates(Image image)
        {
            if (image.GetComponent<ArcaneImageCoordinates>() == null) { image.gameObject.AddComponent<ArcaneImageCoordinates>(); }
        }

        private static bool IsSurface(string path)
        {
            return path.Contains("UI_Pvp_imgBK") || path.Contains("UI_Tavern_Background") || path.Contains("UI_Tavern_btnClose") ||
                path.Contains("UI_Tip_imgBK") || path.Contains("UI_roleItem_") || path.Contains("UI_Begin_btnAdventure");
        }

        /// <summary>一个效果一个共享资产。调整参数直接改材质；不会访问renderer.material导致运行时复制材质。</summary>
        private static Material MaterialOf(string name, string shaderName, float strength, float speed, float artBlend)
        {
            Shader shader = Shader.Find(shaderName);
            if (shader == null || ShaderUtil.ShaderHasError(shader)) { throw new InvalidOperationException("Shader缺失或编译失败：" + shaderName); }
            // 材质按用途分文件夹；生成器和运行时共用相同组织方式，重建时不能又在根目录造出重复资产。
            string folder = name == "CardGleam" ? "Cards" : (name == "ArcanePanel" ? "UI" : "Background");
            string path = MaterialRoot + folder + "/" + name + ".mat";
            Material material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                material = new Material(shader);
                AssetDatabase.CreateAsset(material, path);
                material.SetFloat("_Strength", strength);
                material.SetFloat("_Speed", speed);
                material.SetFloat("_ArtBlend", artBlend);
            }
            // 已有资产保留用户调好的强度和速度；只核对Shader归属，不每次执行都重置美术调参。
            material.shader = shader;
            EditorUtility.SetDirty(material);
            return material;
        }
    }
}
