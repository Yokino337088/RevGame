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
                arena.FindProperty("effectRenderer").objectReferenceValue = Quad(root.transform, "攻击脉冲批次", Material("EndlessImpact", "RevGame/Endless/Impact"), Vector3.zero, Vector3.one);
                arena.ApplyModifiedPropertiesWithoutUndo();
                PrefabUtility.SaveAsPrefabAsset(root, "Assets/GameRes/Endless/EndlessArena.prefab");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
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
        /// 战斗 HUD。节点越少越好：常驻的只有顶部血条、暂停、等级、手牌。
        /// 按钮都做得足够大（手机上手指能点准），两侧留给棋盘外的敌潮，不放任何面板。
        /// </summary>
        private static void BuildHud()
        {
            GameObject root = Root("EndlessHudPanel", typeof(EndlessHudPanel));
            try
            {
                Transform top = Panel("top", root.transform, .28f, .905f, .72f, .995f).transform;
                Label("status", top, "棋心", .05f, .42f, .95f, .94f, 30);
                Transform heart = Panel("heartTrack", top, .08f, .12f, .92f, .44f).transform;
                Panel("heartFill", heart, 0, 0, 1, 1);
                Button("pause", root.transform, "", .925f, .885f, .985f, .99f);
                // 领主血条放在左侧空白区、提示框上方：不遮挡棋盘，也不和顶栏抢位置。
                Transform boss = Rect("boss", root.transform, .015f, .7f, .2f, .8f).transform;
                Transform bossTrack = Panel("bossTrack", boss, 0, 0, 1, .4f).transform;
                Panel("bossFill", bossTrack, 0, 0, 1, 1).GetComponent<Image>().color = new Color(1f, .42f, .3f);
                Label("name", boss, "领主", 0, .42f, 1, 1, 28);
                Transform tip = Panel("tip", root.transform, .015f, .42f, .2f, .68f).transform;
                Label("text", tip, "", .08f, .08f, .92f, .92f, 28);
                Transform level = Panel("level", root.transform, .015f, .025f, .135f, .125f).transform;
                Label("label", level, "Lv 1", .05f, .05f, .95f, .95f, 34);
                Transform bottom = Rect("bottom", root.transform, 0, 0, 1, .17f).transform;
                Transform xp = Panel("xpTrack", bottom, .15f, .84f, .85f, .98f).transform;
                Panel("xpFill", xp, 0, 0, 1, 1);
                for (int i = 0; i < 6; i++)
                {
                    GameObject card = Panel("card" + i, bottom, .15f + i * .118f, .05f, .259f + i * .118f, .8f);
                    UnityEngine.UI.Button button = card.AddComponent<UnityEngine.UI.Button>();
                    button.targetGraphic = card.GetComponent<Image>();
                    Panel("icon", card.transform, .12f, .3f, .88f, .96f).GetComponent<Image>().raycastTarget = false;
                    Label("label", card.transform, "手牌", .02f, .02f, .98f, .32f, 24);
                }
                Button("start", root.transform, "开战", .855f, .02f, .99f, .14f, 40);
                // 弹窗比例贴近 UI_Pvp_imgBK 原图（1013x571），中间的木纹才不会被拉花。
                Transform modal = Panel("modal", root.transform, .17f, .14f, .83f, .86f).transform;
                Label("title", modal, "升级！选一个", .1f, .7f, .9f, .92f, 38);
                for (int i = 0; i < 3; i++)
                {
                    Button("choice" + i, modal, "选择", .09f + i * .28f, .2f, .35f + i * .28f, .68f, 28);
                }
                Button("reroll", modal, "换一批", .38f, .05f, .62f, .17f, 28);
                PrefabUtility.SaveAsPrefabAsset(root, "Assets/GameRes/UI/Panel/Endless/EndlessHudPanel.prefab");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }
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
