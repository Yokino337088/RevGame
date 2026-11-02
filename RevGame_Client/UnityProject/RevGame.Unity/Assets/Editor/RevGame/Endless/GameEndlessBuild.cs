using System;
using System.Collections.Generic;
using System.IO;
using RevGame.Endless;
using RevGame.View;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
namespace RevGame.Editor
{
    /// <summary>固定资产配方，由RevCLI注册动作调用；所有Prefab/材质/场景通过Unity API保存，保留旧GUID。</summary>
    public static partial class GameEndlessBuild
    {
        /// <summary>复用项目已审核的发布方法，只复制已构建PC资源，不重新构建。</summary>
        public static string Publish()
        {
            return GameResourceBuild.PublishBuilt(BuildTarget.StandaloneWindows64);
        }
        private static Font _font;
        private static readonly Color Ink = new Color(0.025f, 0.065f, 0.12f, 0.96f);
        private static readonly Color Teal = new Color(0.025f, 0.32f, 0.36f, 0.98f);
        public static string Build()
        {
            if (EditorApplication.isPlaying)
            {
                throw new InvalidOperationException("无尽资产制作需要编辑态");
            }
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                if (SceneManager.GetSceneAt(i).isDirty)
                {
                    throw new InvalidOperationException("当前场景有未保存修改，已保留现场");
                }
            }
            Directory.CreateDirectory("Assets/GameRes/Endless");
            Directory.CreateDirectory("Assets/GameRes/UI/Panel/Endless");
            AssetDatabase.Refresh();
            _font = GameAssetBuilder.Font();
            ConfigureTempleArt();
            ConfigureEndlessAudio();
            BuildArena();
            BuildHud();
            BuildEntry();
            AddHomeButton();
            AssertNoWhiteImages();
            SceneSetup[] setup = EditorSceneManager.GetSceneManagerSetup();
            try
            {
                Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                var camera = new GameObject("Main Camera", typeof(Camera), typeof(AudioListener));
                camera.tag = "MainCamera";
                Camera c = camera.GetComponent<Camera>();
                c.orthographic = true;
                c.orthographicSize = 6.4f;
                c.clearFlags = CameraClearFlags.SolidColor;
                c.backgroundColor = new Color(0.01f, 0.02f, 0.05f);
                camera.transform.position = new Vector3(4, 4, -10);
                EditorSceneManager.SaveScene(scene, "Assets/Scenes/EndlessScene.unity");
                var scenes = new List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
                if (!scenes.Exists(s => s.path == "Assets/Scenes/EndlessScene.unity"))
                {
                    scenes.Add(new EditorBuildSettingsScene("Assets/Scenes/EndlessScene.unity", true));
                }
                EditorBuildSettings.scenes = scenes.ToArray();
            }
            finally
            {
                EditorSceneManager.RestoreSceneManagerSetup(setup);
            }
            AssetDatabase.SaveAssets();
            return "无尽资产制作完成；下一步生成字库，再执行资源门禁、AB构建和发布";
        }
        private static Material Material(string name, string shader)
        {
            string path = "Assets/GameRes/Endless/" + name + ".mat";
            Material material = AssetDatabase.LoadAssetAtPath<Material>(path);
            Shader found = Shader.Find(shader);
            if (found == null)
            {
                throw new InvalidOperationException("无尽Shader缺失：" + shader);
            }
            if (material == null)
            {
                material = new Material(found);
                AssetDatabase.CreateAsset(material, path);
            }
            material.shader = found;
            EditorUtility.SetDirty(material);
            return material;
        }
        private static MeshRenderer Quad(Transform parent, string name, Material material, Vector3 position, Vector3 scale)
        {
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Quad);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = position;
            go.transform.localScale = scale;
            UnityEngine.Object.DestroyImmediate(go.GetComponent<Collider>());
            MeshRenderer renderer = go.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            return renderer;
        }
        private static void BuildArena()
        {
            var root = new GameObject("EndlessArena", typeof(EndlessArena));
            try
            {
                var arena = new SerializedObject(root.GetComponent<EndlessArena>());
                string[] configs = AssetDatabase.FindAssets("t:PieceArtConfig");
                arena.FindProperty("art").objectReferenceValue = AssetDatabase.LoadAssetAtPath<PieceArtConfig>(AssetDatabase.GUIDToAssetPath(configs[0]));
                arena.FindProperty("backgroundRenderer").objectReferenceValue = Quad(root.transform, "古希腊神殿", TempleBackground(), new Vector3(4, 4, 5), new Vector3(25.6f, 14.4f, 1));
                arena.FindProperty("gridRenderer").objectReferenceValue = Quad(root.transform, "象牙金圣坛棋盘", Material("EndlessGrid", "RevGame/Endless/StarGrid"), new Vector3(4, 4, 2), new Vector3(9, 9, 1));
                arena.FindProperty("hordeRenderer").objectReferenceValue = Quad(root.transform, "外围敌潮批次", Material("EndlessHorde", "RevGame/Endless/Horde"), Vector3.zero, Vector3.one);
                // 战斗反馈不再用自制的“攻击脉冲批次”网格，统一复用六章 BattleEffect / PieceView（见 EndlessArena.CombatFx）。
                // 落子提示复用六章 BoardCell 高亮层的原图和 PlacementMagic 共享材质（见 EndlessArena.Placement）。
                arena.FindProperty("placementSprite").objectReferenceValue = RequireAsset<Sprite>(PlacementSpritePath);
                arena.FindProperty("placementMaterial").objectReferenceValue = RequireAsset<Material>(PlacementMaterialPath);
                // 近战剑气与外围血条：各一张动态批量网格，整场各 1 次绘制调用（见 EndlessArena.Feel）。
                arena.FindProperty("auraRenderer").objectReferenceValue = Quad(root.transform, "近战剑气批量", SwordAuraMaterial(), Vector3.zero, Vector3.one);
                arena.FindProperty("hpBarRenderer").objectReferenceValue = Quad(root.transform, "外围血条批量", Material("EndlessHpBar", "RevGame/Endless/HpBar"), Vector3.zero, Vector3.one);
                arena.ApplyModifiedPropertiesWithoutUndo();
                PrefabUtility.SaveAsPrefabAsset(root, "Assets/GameRes/Endless/EndlessArena.prefab");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }
        /// <summary>六章 BoardCell 预制体高亮层（highlightRenderer）使用的原图与材质，两边必须是同一份资产。</summary>
        private const string PlacementSpritePath = "Assets/GameRes/Art/Board/BoardSprite_736665ce.png";
        private const string PlacementMaterialPath = "Assets/GameRes/Material/Board/PlacementMagic.mat";
        /// <summary>读取必需资产，缺了就让生成失败，不生成引用为空的预制体。</summary>
        private static T RequireAsset<T>(string path) where T : UnityEngine.Object
        {
            T asset = AssetDatabase.LoadAssetAtPath<T>(path);
            if (asset == null)
            {
                throw new InvalidOperationException("无尽复用的六章资产缺失：" + path);
            }
            return asset;
        }
        private static GameObject Rect(string name, Transform parent, float x0, float y0, float x1, float y1)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var r = (RectTransform)go.transform;
            r.anchorMin = new Vector2(x0, y0);
            r.anchorMax = new Vector2(x1, y1);
            r.offsetMin = r.offsetMax = Vector2.zero;
            return go;
        }
        private static GameObject Panel(string name, Transform parent, float x0, float y0, float x1, float y1)
        {
            GameObject go = Rect(name, parent, x0, y0, x1, y1);
            ApplyTempleImage(go.AddComponent<Image>(), name);
            return go;
        }
        private static Text Label(string name, Transform parent, string text, float x0, float y0, float x1, float y1, int size = 22)
        {
            var go = Rect(name, parent, x0, y0, x1, y1);
            Text label = go.AddComponent<Text>();
            label.font = _font;
            label.text = text;
            label.fontSize = size;
            label.color = new Color(0.24f, 0.17f, 0.12f);
            label.alignment = TextAnchor.MiddleCenter;
            label.raycastTarget = false;
            return label;
        }
        private static void Button(string name, Transform parent, string text, float x0, float y0, float x1, float y1, int size = 22)
        {
            GameObject go = Panel(name, parent, x0, y0, x1, y1);
            Image image = go.GetComponent<Image>();
            Button button = go.AddComponent<Button>();
            button.targetGraphic = image;
            if (text.Length > 0)
            {
                Label("label", go.transform, text, 0.06f, 0.06f, 0.94f, 0.94f, size);
            }
        }
        private static GameObject Root(string name, Type component)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasGroup), component);
            ((RectTransform)go.transform).sizeDelta = new Vector2(1920, 1080);
            return go;
        }
        /// <summary>
        /// 战斗 HUD（2026-11-02 重做，沿用六章“顶部标题条 + 角落信息 + 棋盘零遮挡”的思路）。
        ///
        /// 【版面】参考分辨率 1920×1080：棋盘占屏幕横向 30%~70%、纵向 17%~85%，左右两侧 13%~30%/70%~87% 是敌潮通道。
        /// 所以 HUD 只放在三个地方，绝不压到棋盘和敌潮：
        ///   顶部条（y≥87%）：中间奶油色长条 = 棋心血量 + 时间 + 击杀，下方细线 = 经验条，左侧小木牌 = 等级，右上角齿轮 = 暂停；
        ///   左上角（领主在场时）：深色小面板 = 领主名字 + 血条；
        ///   底部条（y≤15%）：中间一排木纹手牌，左下角深色面板 = 一句提示，右下角棕色按钮 = 开战。
        /// 弹窗（升级/事件/暂停/结算）出现时战斗本来就暂停，可以居中显示：深色底板 + 三张木纹大卡 + 关闭叉。
        ///
        /// 【素材】全部是用户指定的项目原图：UI_achieveItem_imgBK（奶油长条）、UI_Buy_btnClose（圆形叉）、
        /// UI_Tip_imgBK（深色面板）、UI_Tavern_imgBK（木纹竖卡）、UI_Tavern_btnClose（棕色圆角按钮）。
        /// </summary>
        private static void BuildHud()
        {
            GameObject root = Root("EndlessHudPanel", typeof(EndlessHudPanel));
            try
            {
                // 顶部：棋心条。文字在上半，血条嵌在长条下半。
                Transform top = Panel("top", root.transform, .34f, .912f, .66f, .99f).transform;
                Label("status", top, "棋心", .06f, .44f, .94f, .94f, 28);
                Transform heart = Panel("heartTrack", top, .07f, .16f, .93f, .42f).transform;
                Panel("heartFill", heart, 0, 0, 1, 1);
                // 经验条：棋心条正下方一根细线，和六章标题下的进度线同一位置感。
                Transform xp = Panel("xpTrack", root.transform, .3f, .884f, .7f, .904f).transform;
                Panel("xpFill", xp, 0, 0, 1, 1);
                Transform level = Panel("level", root.transform, .255f, .912f, .332f, .99f).transform;
                Label("label", level, "Lv 1", .08f, .1f, .92f, .9f, 30).color = Cream;
                Button("pause", root.transform, "", .945f, .905f, .99f, .99f);
                // 左上角领主面板：只在领主在场时显示。
                Transform boss = Panel("boss", root.transform, .012f, .875f, .235f, .99f).transform;
                Label("name", boss, "领主", .08f, .5f, .92f, .92f, 26).color = Cream;
                Transform bossTrack = Panel("bossTrack", boss, .08f, .16f, .92f, .44f).transform;
                Panel("bossFill", bossTrack, 0, 0, 1, 1).GetComponent<Image>().color = new Color(1f, .42f, .3f);
                // 底部：三张手牌（UI_roleItem_imgBK，选中换 UI_roleItem_roleItem），居中在棋盘正下方的空白里。
                Transform bottom = Rect("bottom", root.transform, .392f, .008f, .608f, .148f).transform;
                for (int i = 0; i < 3; i++)
                {
                    float x0 = i / 3f + .012f;
                    GameObject card = Panel("card" + i, bottom, x0, 0, x0 + 1 / 3f - .024f, 1);
                    UnityEngine.UI.Button button = card.AddComponent<UnityEngine.UI.Button>();
                    button.targetGraphic = card.GetComponent<Image>();
                    Panel("icon", card.transform, .16f, .3f, .84f, .92f).GetComponent<Image>().raycastTarget = false;
                    Label("label", card.transform, "手牌", .04f, .04f, .96f, .3f, 20);
                }
                var hud = new SerializedObject(root.GetComponent<EndlessHudPanel>());
                hud.FindProperty("cardNormal").objectReferenceValue = RequireAsset<Sprite>(TempleArt + "UI_roleItem_imgBK.png");
                hud.FindProperty("cardSelected").objectReferenceValue = RequireAsset<Sprite>(TempleArt + "UI_roleItem_roleItem.png");
                hud.ApplyModifiedPropertiesWithoutUndo();
                // 右上角横幅（棋心条和齿轮之间的空位）：自动升级/自动事件的一句话提示，默认隐藏。
                Transform banner = Panel("banner", root.transform, .672f, .912f, .935f, .99f).transform;
                Label("text", banner, "", .05f, .08f, .95f, .92f, 22).color = Cream;
                banner.gameObject.SetActive(false);
                Transform tip = Panel("tip", root.transform, .012f, .015f, .235f, .13f).transform;
                Label("text", tip, "", .07f, .1f, .93f, .9f, 22).color = Cream;
                Button("start", root.transform, "开战", .84f, .02f, .985f, .12f, 36);
                // 弹窗：深色底板 + 三张木纹大卡 + 右上角关闭叉（暂停时用作“继续”）。
                Transform modal = Panel("modal", root.transform, .2f, .17f, .8f, .83f).transform;
                Label("title", modal, "升级！选一个", .08f, .76f, .92f, .95f, 38).color = Cream;
                for (int i = 0; i < 3; i++)
                {
                    Button("choice" + i, modal, "选择", .1f + i * .28f, .2f, .34f + i * .28f, .74f, 26);
                }
                Button("reroll", modal, "换一批", .38f, .04f, .62f, .15f, 26);
                Button("modalClose", modal, "", .925f, .87f, .985f, .975f);
                // 棕色按钮上的字用奶油色，深棕字压在棕底上看不清。
                root.transform.Find("start/label").GetComponent<Text>().color = Cream;
                modal.Find("reroll/label").GetComponent<Text>().color = Cream;
                // 与六章面板同一套共享材质：底板/按钮流光（ArcanePanel）、手牌与升级卡光泽（CardGleam）。
                GameArcaneArtBuilder.StyleGeneratedPanel(root);
                PrefabUtility.SaveAsPrefabAsset(root, "Assets/GameRes/UI/Panel/Endless/EndlessHudPanel.prefab");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }
        /// <summary>深色面板上的文字颜色（奶油色），浅色底上沿用 Label 默认的深棕色。</summary>
        private static readonly Color Cream = new Color(.96f, .9f, .78f);
        /// <summary>入口：选棋阵、开始。其余一律不放。</summary>
        private static void BuildEntry()
        {
            GameObject root = Root("EndlessEntryPanel", typeof(EndlessEntryPanel));
            try
            {
                // 底层铺神殿原画（RawImage 不走图集，和战斗背景共用同一张图）。
                RawImage temple = Rect("templeArt", root.transform, 0, 0, 1, 1).AddComponent<RawImage>();
                temple.texture = AssetDatabase.LoadAssetAtPath<Texture2D>(TempleTexture);
                temple.raycastTarget = true;
                Panel("entry", root.transform, .16f, .1f, .84f, .9f);
                Label("title", root.transform, "无尽圣坛", .2f, .74f, .8f, .85f, 56);
                Label("best", root.transform, "", .2f, .68f, .8f, .74f, 28);
                string[] names =
                {
                    "弩雨\n远程穿透", "炎环\n燃烧爆炸", "守眼\n稳扎稳打"
                };
                for (int i = 0; i < 3; i++)
                {
                    Button("formation" + i, root.transform, names[i], .235f + i * .18f, .36f, .405f + i * .18f, .66f, 34);
                }
                Button("start", root.transform, "开始", .38f, .17f, .62f, .32f, 44);
                Button("resume", root.transform, "继续上局", .635f, .185f, .79f, .305f, 28);
                Button("close", root.transform, "", .775f, .78f, .825f, .87f);
                GameArcaneArtBuilder.StyleGeneratedPanel(root);
                PrefabUtility.SaveAsPrefabAsset(root, "Assets/GameRes/UI/Panel/Endless/EndlessEntryPanel.prefab");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }
        private static void AddHomeButton()
        {
            const string path = "Assets/GameRes/UI/Panel/HomePanel.prefab";
            GameObject home = PrefabUtility.LoadPrefabContents(path);
            try
            {
                Transform existing = home.transform.Find("btnEndless");
                if (existing == null)
                {
                    Button("btnEndless", home.transform, "棋阵幸存者 · 无尽", .72f, .14f, .97f, .23f, 26);
                    existing = home.transform.Find("btnEndless");
                }
                // 大厅不能引用 UIEndless 图集里的图：否则只是打开大厅，整张无尽图集都会进内存。
                // 这里用大厅本来就在用的按钮图，只染金色做区分。
                Image homeImage = existing.GetComponent<Image>();
                homeImage.sprite = AssetDatabase.LoadAssetAtPath<Sprite>("Assets/GameRes/Art/UI/UI_Tavern_btnClose.png");
                homeImage.type = Image.Type.Sliced;
                homeImage.color = new Color(1f, .90f, .58f);
                PrefabUtility.SaveAsPrefabAsset(home, path);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(home);
            }
        }
    }
}
