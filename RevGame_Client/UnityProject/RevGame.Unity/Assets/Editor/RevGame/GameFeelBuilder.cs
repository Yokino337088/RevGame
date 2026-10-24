// ============================================================
// GameFeelBuilder.cs —— 由 Unity MCP 执行的表现资源装配。
// 只保存本项目的预制体和场景；不在运行时找磁盘美术，也不修改 Revolution 快照。
// ============================================================
using RevGame.View;
using RevGame.Core;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace RevGame.Editor
{
    public static class GameFeelBuilder
    {
        public static string Apply()
        {
            if (EditorApplication.isPlaying)
            {
                throw new System.InvalidOperationException("表现预制体需要在非 Play 状态保存");
            }
            BuildNotice();
            GameArtMigration.ApplyAll();
            ApplyProjectileStyle();
            AssetDatabase.SaveAssets();
            return "DOTween 增益提示预制体、手机布局、1.15 单位拼格和点击区域已保存。\n" + GameArtMigration.Audit();
        }

        /// <summary>可单独由MCP调用：只更新弹道预制体，不重建场景或其他UI；重复执行复用原节点。</summary>
        public static string ApplyProjectileStyle()
        {
            if (EditorApplication.isPlaying) { throw new System.InvalidOperationException("请先退出Play再保存弹道预制体"); }
            ImportProjectileArt();
            const string path = "Assets/GameRes/Effects/Combat/BattleEffect.prefab";
            GameObject root = PrefabUtility.LoadPrefabContents(path);
            try
            {
                BattleEffectView view = root.GetComponent<BattleEffectView>();
                var so = new SerializedObject(view);
                string[] artNames = { "ArcherArrow", "CrossbowBolt", "WaterOrb", "LavaFireball", "HolyOrb" };
                PieceKind[] kinds = { PieceKind.Archer, PieceKind.Crossbow, PieceKind.MageWater, PieceKind.MageFire, PieceKind.Priest };
                Vector2[] sizes = { new Vector2(.92f,.17f), new Vector2(.72f,.22f), new Vector2(.56f,.56f), new Vector2(.92f,.58f), new Vector2(.58f,.58f) };
                Color[] colors = { new Color(.75f,.95f,1), new Color(1,.67f,.24f), new Color(.2f,.8f,1), new Color(1,.24f,.025f), new Color(1,.88f,.36f) };
                SerializedProperty styles = so.FindProperty("projectileStyles");
                styles.arraySize = kinds.Length;
                for (int i = 0; i < kinds.Length; i++)
                {
                    SerializedProperty style = styles.GetArrayElementAtIndex(i);
                    style.FindPropertyRelative("Kind").intValue = (int)kinds[i];
                    style.FindPropertyRelative("Sprite").objectReferenceValue = AssetDatabase.LoadAssetAtPath<Sprite>("Assets/GameRes/Art/Combat/Projectiles/" + artNames[i] + ".png");
                    style.FindPropertyRelative("BodyMaterial").objectReferenceValue = ProjectileMaterial(artNames[i], i, false, colors[i]);
                    style.FindPropertyRelative("TrailMaterial").objectReferenceValue = ProjectileMaterial(artNames[i], i, true, colors[i]);
                    style.FindPropertyRelative("WorldSize").vector2Value = sizes[i];
                    style.FindPropertyRelative("TrailSize").vector2Value = new Vector2(i < 2 ? 2.2f : 1.65f, i < 2 ? .46f : .7f);
                    style.FindPropertyRelative("TrailColor").colorValue = colors[i];
                    style.FindPropertyRelative("AuraMaterial").objectReferenceValue = i < 2 ? null : ArcaneAuraMaterial(artNames[i], i, colors[i]);
                }
                Sprite arrow = AssetDatabase.LoadAssetAtPath<Sprite>("Assets/GameRes/Art/Combat/Projectiles/ArcherArrow.png");
                so.FindProperty("projectileDuration").floatValue = .36f;
                so.FindProperty("projectileOutline").objectReferenceValue = ProjectileLayer(root.transform, "projectileOutline", arrow);
                so.FindProperty("projectileTrail").objectReferenceValue = ProjectileLayer(root.transform, "projectileTrail", arrow);
                so.FindProperty("swordSlash").objectReferenceValue = AssetDatabase.LoadAssetAtPath<Sprite>("Assets/GameRes/Art/Combat/Melee/SwordSlash.png");
                so.FindProperty("shieldImpact").objectReferenceValue = AssetDatabase.LoadAssetAtPath<Sprite>("Assets/GameRes/Art/Combat/Melee/ShieldImpact.png");
                so.FindProperty("swordMaterial").objectReferenceValue = MeleeMaterial("SwordSlash", false, new Color(.18f,.78f,1));
                so.FindProperty("shieldMaterial").objectReferenceValue = MeleeMaterial("ShieldBash", true, new Color(1,.62f,.08f));
                so.FindProperty("assassinMaterial").objectReferenceValue = MeleeMaterial("AssassinSlash", false, new Color(.8f,.18f,1));
                SpriteRenderer body = (SpriteRenderer)so.FindProperty("spriteRenderer").objectReferenceValue;
                body.sprite = arrow;
                body.sharedMaterial = ProjectileMaterial("ArcherArrow", 0, false, colors[0]);
                so.ApplyModifiedPropertiesWithoutUndo();
                PrefabUtility.SaveAsPrefabAsset(root, path);
                AssetDatabase.SaveAssets();
                ConfigureRegion();
                return "五职业长尾/魔法环、近战原画与区域魔法地面共享材质已保存。";
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        /// <summary>仅设置本次五张原图的导入规则；图集归属仍由GameSpriteAtlasBuild统一配置。</summary>
        private static void ImportProjectileArt()
        {
            var files = new System.Collections.Generic.List<string>(System.IO.Directory.GetFiles("Assets/GameRes/Art/Combat/Projectiles", "*.png"));
            files.AddRange(System.IO.Directory.GetFiles("Assets/GameRes/Art/Combat/Melee", "*.png"));
            files.Add("Assets/GameRes/Art/Combat/RegionHazard.png");
            foreach (string file in files)
            {
                string path = file.Replace('\\', '/');
                AssetDatabase.ImportAsset(path);
                var importer = (TextureImporter)AssetImporter.GetAtPath(path);
                importer.textureType = TextureImporterType.Sprite;
                importer.spriteImportMode = SpriteImportMode.Single;
                importer.spritePixelsPerUnit = 100;
                importer.alphaIsTransparency = true;
                importer.mipmapEnabled = false;
                importer.isReadable = false;
                importer.filterMode = FilterMode.Bilinear;
                importer.textureCompression = TextureImporterCompression.Uncompressed;
                var settings = new TextureImporterSettings();
                importer.ReadTextureSettings(settings);
                settings.spriteMeshType = SpriteMeshType.FullRect;
                importer.SetTextureSettings(settings);
                importer.SaveAndReimport();
            }
        }

        private static Material ArcaneAuraMaterial(string name, int style, Color color)
        {
            Material material = SharedMaterial(name + "Aura", "RevGame/Combat/RangedProjectile");
            material.SetFloat("_Style", style); material.SetFloat("_Trail", 2); material.SetColor("_Glow", color);
            EditorUtility.SetDirty(material); return material;
        }

        private static Material MeleeMaterial(string name, bool shield, Color color)
        {
            Material material = SharedMaterial(name, "RevGame/Combat/MeleeImpact");
            material.SetFloat("_Shield", shield ? 1 : 0); material.SetColor("_Glow", color);
            EditorUtility.SetDirty(material); return material;
        }

        /// <summary>编辑器创建共享资产，运行时只引用；不能以renderer.material克隆来调职业颜色。</summary>
        private static Material SharedMaterial(string name, string shaderName)
        {
            string path = "Assets/GameRes/Material/Combat/" + name + ".mat";
            Shader shader = Shader.Find(shaderName);
            if (shader == null) { throw new System.InvalidOperationException("缺少Shader：" + shaderName); }
            Material material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null) { material = new Material(shader); AssetDatabase.CreateAsset(material,path); }
            material.shader = shader;
            return material;
        }

        private static void ConfigureRegion()
        {
            const string path = "Assets/GameRes/Board/Tile/BoardCell.prefab";
            GameObject root = PrefabUtility.LoadPrefabContents(path);
            try
            {
                var so = new SerializedObject(root.GetComponent<BoardCellView>());
                so.FindProperty("hazardMaterial").objectReferenceValue = SharedMaterial("RegionRift", "RevGame/Combat/RegionRift");
                so.ApplyModifiedPropertiesWithoutUndo();
                PrefabUtility.SaveAsPrefabAsset(root,path);
                AssetDatabase.SaveAssets();
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
        }

        /// <summary>材质由预制体直接引用；每种职业共享一对，运行时禁止逐弹道克隆。</summary>
        private static Material ProjectileMaterial(string name, int style, bool trail, Color glow)
        {
            const string directory = "Assets/GameRes/Material/Combat";
            System.IO.Directory.CreateDirectory(directory);
            string path = directory + "/" + name + (trail ? "Trail" : "Body") + ".mat";
            Material material = AssetDatabase.LoadAssetAtPath<Material>(path);
            Shader shader = Shader.Find("RevGame/Combat/RangedProjectile");
            if (shader == null) { throw new System.InvalidOperationException("远程弹道Shader尚未导入"); }
            if (material == null)
            {
                material = new Material(shader);
                AssetDatabase.CreateAsset(material, path);
            }
            material.shader = shader;
            material.SetFloat("_Style", style);
            material.SetFloat("_Trail", trail ? 1f : 0f);
            material.SetColor("_Glow", glow);
            EditorUtility.SetDirty(material);
            return material;
        }

        /// <summary>编辑器装配时创建一次，运行时只启用/隐藏；不会每次开火都new一个拖尾物体。</summary>
        private static SpriteRenderer ProjectileLayer(Transform parent, string name, Sprite sprite)
        {
            Transform child = parent.Find(name);
            if (child == null)
            {
                child = new GameObject(name).transform;
                child.SetParent(parent, false);
            }
            SpriteRenderer renderer = child.GetComponent<SpriteRenderer>();
            if (renderer == null) { renderer = child.gameObject.AddComponent<SpriteRenderer>(); }
            renderer.sprite = sprite;
            renderer.drawMode = SpriteDrawMode.Simple;
            renderer.enabled = false;
            return renderer;
        }

        private static void BuildNotice()
        {
            GameObject root = GameAssetBuilder.Rect("BattleEventNotice", null, new Vector2(920, 148), Vector2.zero);
            try
            {
                CanvasGroup group = root.AddComponent<CanvasGroup>();
                group.blocksRaycasts = false;
                Image background = root.AddComponent<Image>();
                background.sprite = GameAssetBuilder.Sprite("界面/新版/UI_Tip_imgBK.png");
                background.raycastTarget = false;
                // 金色侧条、粗体大字和黑描边增加冲击感，仍只引用用户指定的新版图片。
                Image icon = GameAssetBuilder.Picture("icon", root.transform, GameAssetBuilder.Sprite("界面/新版/UI_Setting_Checkmark.png"), new Vector2(72, 72), new Vector2(-340, 0));
                Text title = GameAssetBuilder.Label("title", root.transform, "战斗支援", new Vector2(620, 65), new Vector2(0, 30), 48);
                title.fontStyle = FontStyle.Bold;
                title.alignment = TextAnchor.MiddleCenter;
                title.color = new Color(1f, 0.88f, 0.2f);
                Outline outline = title.gameObject.AddComponent<Outline>();
                outline.effectColor = Color.black;
                outline.effectDistance = new Vector2(2, -2);
                Text detail = GameAssetBuilder.Label("detail", root.transform, "增益效果已生效", new Vector2(640, 55), new Vector2(0, -32), 27);
                detail.fontStyle = FontStyle.Bold;
                detail.alignment = TextAnchor.MiddleCenter;
                detail.color = Color.white;
                BattleEventNoticeView view = root.AddComponent<BattleEventNoticeView>();
                GameAssetBuilder.SetReference(view, "group", group);
                GameAssetBuilder.SetReference(view, "titleText", title);
                GameAssetBuilder.SetReference(view, "detailText", detail);
                GameAssetBuilder.SetReference(view, "icon", icon);
                PrefabUtility.SaveAsPrefabAsset(root, "Assets/GameRes/UI/Item/Notice/BattleEventNotice.prefab");
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }
    }
}
