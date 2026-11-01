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
                c.orthographicSize = 7.2f;
                c.clearFlags = CameraClearFlags.SolidColor;
                c.backgroundColor = new Color(0.01f, 0.02f, 0.05f);
                camera.transform.position = new Vector3(3.2f, 4, -10);
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
                arena.FindProperty("backgroundRenderer").objectReferenceValue = Quad(root.transform, "古希腊神殿", TempleBackground(), new Vector3(3.2f, 4, 5), new Vector3(25.6f, 14.4f, 1));
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
            ApplyTempleImage(image, name, true);
            Button button = go.AddComponent<Button>();
            button.targetGraphic = image;
            Label("label", go.transform, text, 0.03f, 0.04f, 0.97f, 0.96f, size);
        }
        private static GameObject Root(string name, Type component)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasGroup), component);
            ((RectTransform)go.transform).sizeDelta = new Vector2(1920, 1080);
            return go;
        }
        private static void BuildHud()
        {
            GameObject root = Root("EndlessHudPanel", typeof(EndlessHudPanel));
            try
            {
                Transform top = Panel("top", root.transform, 0, .92f, 1, 1).transform;
                Label("status", top, "棋阵幸存者", .02f, 0, .86f, 1, 28);
                Transform track = Panel("heartTrack", top, .03f, .02f, .84f, .075f).transform;
                Panel("heartFill", track, 0, 0, 1, 1);
                Button("pause", top, "暂停", .88f, .15f, .98f, .85f);
                Transform left = Panel("left", root.transform, .01f, .16f, .19f, .9f).transform;
                Label("build", left, "构筑档案", .05f, .25f, .95f, .97f, 19).alignment = TextAnchor.UpperLeft;
                string[] routeNames =
                {
                    "弩雨配方", "炎爆配方", "守眼配方", "雷链配方", "寒潮配方", "连气配方"
                };
                for (int i = 0; i < routeNames.Length; i++)
                {
                    float x = .04f + (i % 2) * .48f, y = .02f + (2 - i / 2) * .073f;
                    Button("route" + i, left, routeNames[i], x, y, x + .44f, y + .062f, 19);
                }
                Transform right = Panel("right", root.transform, .82f, .16f, .99f, .9f).transform;
                Label("info", right, "敌潮导演", .05f, .60f, .95f, .98f, 19);
                string[] actions =
                {
                    "start", "rearrange", "merge", "evolve", "disassemble", "exchange", "effects", "flash", "shake"
                };
                string[] titles =
                {
                    "完成布阵 · 迎战", "本分钟改阵", "选中主力 · 三合一", "满足配方 · 进化", "分解选中手牌", "兑换选中棋同名牌", "切换特效强度", "开关攻击闪光", "开关受击震动"
                };
                for (int i = 0; i < actions.Length; i++)
                {
                    float y = .53f - i * .06f;
                    Button(actions[i], right, titles[i], .05f, y, .95f, y + .055f, 18);
                }
                Transform bottom = Panel("bottom", root.transform, .01f, .01f, .99f, .14f).transform;
                Label("experience", bottom, "经验", .01f, .67f, .99f, .98f, 23);
                for (int i = 0; i < 6; i++)
                {
                    Button("card" + i, bottom, "手牌", .01f + i * .164f, .08f, .16f + i * .164f, .62f);
                }
                Transform modal = Panel("modal", root.transform, .2f, .23f, .81f, .83f).transform;
                Label("title", modal, "升级选择", .04f, .66f, .96f, .97f, 26);
                for (int i = 0; i < 3; i++)
                {
                    Button("choice" + i, modal, "选择", .03f + i * .325f, .24f, .32f + i * .325f, .64f, 24);
                    Button("banish" + i, modal, "放逐此项", .05f + i * .325f, .12f, .30f + i * .325f, .21f, 18);
                }
                Button("reroll", modal, "免费重抽", .35f, .015f, .65f, .095f, 20);
                Button("detailClose", modal, "关闭配方", .35f, .015f, .65f, .095f, 20);
                Button("breakthrough", modal, "切换突破印", .07f, .08f, .45f, .2f, 20);
                Button("affix", modal, "切换侵袭词条", .55f, .08f, .93f, .2f, 20);
                PrefabUtility.SaveAsPrefabAsset(root, "Assets/GameRes/UI/Panel/Endless/EndlessHudPanel.prefab");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }
        private static void BuildEntry()
        {
            GameObject root = Root("EndlessEntryPanel", typeof(EndlessEntryPanel));
            try
            {
                // 底层铺神殿原画（RawImage 不走图集，和战斗背景共用同一张图），上层是藤蔓框面板。
                RawImage temple = Rect("templeArt", root.transform, 0, 0, 1, 1).AddComponent<RawImage>();
                temple.texture = AssetDatabase.LoadAssetAtPath<Texture2D>(TempleTexture);
                temple.raycastTarget = true;
                Panel("entry", root.transform, .1f, .03f, .9f, .97f);
                Label("title", root.transform, "无尽圣坛\nENDLESS · 守住棋心，长成自己的流派", .1f, .77f, .9f, .94f, 36);
                Label("description", root.transform, "", .15f, .34f, .85f, .75f, 30);
                for (int i = 0; i < 3; i++)
                {
                    string[] names =
                    {
                        "弩雨棋阵", "炎环棋阵", "守眼棋阵"
                    };
                    Button("formation" + i, root.transform, names[i], .15f + i * .24f, .25f, .36f + i * .24f, .33f, 26);
                }
                Button("difficulty", root.transform, "切换难度 / 每日", .15f, .15f, .36f, .23f);
                Button("start", root.transform, "开启新局", .39f, .15f, .6f, .23f, 26);
                Button("resume", root.transform, "继续本局", .63f, .15f, .84f, .23f, 26);
                Button("close", root.transform, "返回大厅", .39f, .04f, .6f, .12f);
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
