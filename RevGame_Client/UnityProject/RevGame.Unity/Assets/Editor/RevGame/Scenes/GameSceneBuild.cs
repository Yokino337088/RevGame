using System;
using System.Linq;
using RevGame.Bootstrap;
using RevGame.UI;
using RevGame.View;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace RevGame.Editor
{
    /// <summary>
    /// 场景拆分的幂等装配工具，由Unity MCP调用。使用AssetDatabase/PrefabUtility/EditorSceneManager保存资源，
    /// 保留现有StartScene与GameScene的GUID，不通过文本重写YAML或复制一份背景图片。
    /// 编辑器入口只存在于Assets/Editor，不进入手机运行时代码。
    /// </summary>
    public static class GameSceneBuild
    {
        public static string Apply()
        {
            if (EditorApplication.isPlaying)
            {
                throw new InvalidOperationException("请退出Play再装配场景");
            }
            BuildLoadingPanel();
            int imageCount = BindAtlasImages();
            Scene game = EditorSceneManager.OpenScene("Assets/Scenes/GameScene.unity", OpenSceneMode.Single);
            // 清除先前验收遗留的空容器/框架运行时宿主；框架自己在Play时创建，不应序列化进正式场景。
            foreach (GameObject root in game.GetRootGameObjects())
            {
                if (root.name == "池化动画验收" || root.name == "[RevObjectPool]" || root.name == "[RevResAutoUnloader]")
                {
                    UnityEngine.Object.DestroyImmediate(root);
                }
            }
            BoardView board = game.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<BoardView>(true)).Single();
            board.gameObject.SetActive(false);
            EditorSceneManager.SaveScene(game);

            Scene start = EditorSceneManager.OpenScene("Assets/Scenes/StartScene.unity", OpenSceneMode.Additive);
            // StartScene只放入口、相机、光源和局外表现；每次按类型查找，重复执行不会多出第二套宿主。
            Camera camera = start.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<Camera>(true)).FirstOrDefault();

            if (camera == null)
            {
                var go = new GameObject("Main Camera", typeof(Camera), typeof(AudioListener));
                SceneManager.MoveGameObjectToScene(go, start);
                camera = go.GetComponent<Camera>();
            }
            camera.tag = "MainCamera";
            camera.orthographic = true;
            camera.transform.position = new Vector3(0f, 0f, -10f);
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.035f, 0.10f, 0.10f);
            GetOrCreate<GameBootstrap>(start, "GameBootstrap");
            GamePresentation presentation = GetOrCreate<GamePresentation>(start, "GamePresentation");

            if (presentation.GetComponent<GameAudio>() == null)
            {
                presentation.gameObject.AddComponent<GameAudio>();
            }
            var fields = new SerializedObject(presentation);
            fields.FindProperty("boardRoot").objectReferenceValue = null;
            fields.FindProperty("boardCamera").objectReferenceValue = camera;
            fields.FindProperty("backgroundRenderer").objectReferenceValue = null;
            fields.ApplyModifiedPropertiesWithoutUndo();
            Light light = GetOrCreate<Light>(start, "Main Light");
            light.type = LightType.Directional;
            EditorSceneManager.SaveScene(start);
            EditorSceneManager.CloseScene(game, true);
            SceneManager.SetActiveScene(start);
            EditorBuildSettings.scenes = new[]
            {
                new EditorBuildSettingsScene("Assets/Scenes/StartScene.unity", true),
                new EditorBuildSettingsScene("Assets/Scenes/GameScene.unity", true),
                // 教学是正式首启流程的一部分，重建场景不能从发布清单移除它。
                new EditorBuildSettingsScene("Assets/Scenes/TutorialScene.unity", true)
            };
            AssetDatabase.SaveAssets();
            return "StartScene/GameScene已拆分，启动场景在Build Settings第0位；加载面板已保存；" + imageCount + "个Image已接图集晚绑定刷新";
        }
        private static T GetOrCreate<T>(Scene scene, string name) where T : Component
        {
            T found = scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<T>(true)).FirstOrDefault();

            if (found != null)
            {
                return found;
            }
            var root = new GameObject(name);
            SceneManager.MoveGameObjectToScene(root, scene);
            return root.AddComponent<T>();
        }
        /// <summary>单独的ui_loading包只依赖字体与程序背景材质，不能因为打开加载界面就先拉入整套战斗图集。</summary>
        private static void BuildLoadingPanel()
        {
            const string folder = "Assets/GameRes/UI/Panel/Loading";

            if (!AssetDatabase.IsValidFolder(folder))
            {
                AssetDatabase.CreateFolder("Assets/GameRes/UI/Panel", "Loading");
            }
            GameObject home = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/GameRes/UI/Panel/HomePanel.prefab");
            Font font = home.GetComponentInChildren<Text>(true).font;
            Material backdrop = AssetDatabase.LoadAssetAtPath<Material>("Assets/GameRes/Material/Background/ArcaneBackdrop.mat");

            if (backdrop == null || font == null)
            {
                throw new InvalidOperationException("加载面板缺少现有星雾材质或字体");
            }
            var root = new GameObject("LoadingPanel", typeof(RectTransform), typeof(CanvasGroup), typeof(LoadingPanel));
            try
            {
                Stretch((RectTransform)root.transform);
                Image background = NewImage(root.transform, "screenBackground", Color.white);
                Stretch(background.rectTransform);
                background.material = backdrop;
                background.raycastTarget = true; // 真正盖住全屏点击，不受安全区内缩影响。
                background.gameObject.AddComponent<ArcaneImageCoordinates>();
                NewText(root.transform, "brand", "围 棋 史 莱 姆", font, 28, new Vector2(0f, 120f), new Vector2(900f, 70f));
                NewText(root.transform, "message", "正在唤醒营地…", font, 38, new Vector2(0f, 10f), new Vector2(1400f, 100f));
                Image track = NewImage(root.transform, "progress", new Color(0.025f, 0.08f, 0.08f, 0.85f));
                Position(track.rectTransform, new Vector2(0f, -100f), new Vector2(760f, 16f));
                Image fill = NewImage(track.transform, "fill", new Color(0.64f, 0.93f, 0.73f, 1f));
                Stretch(fill.rectTransform);
                Slider slider = track.gameObject.AddComponent<Slider>();
                slider.fillRect = fill.rectTransform;
                slider.minValue = 0f;
                slider.maxValue = 1f;
                slider.interactable = false;
                slider.transition = Selectable.Transition.None;
                NewText(root.transform, "percent", "0%", font, 24, new Vector2(0f, -145f), new Vector2(160f, 50f));
                Image button = NewImage(root.transform, "btnReturn", new Color(0.13f, 0.28f, 0.25f, 1f));
                Position(button.rectTransform, new Vector2(0f, -240f), new Vector2(290f, 68f));
                button.raycastTarget = true;
                button.gameObject.AddComponent<Button>().targetGraphic = button;
                Text label = NewText(button.transform, "label", "返回大厅", font, 28, Vector2.zero, new Vector2(290f, 68f));
                PrefabUtility.SaveAsPrefabAsset(root, folder + "/LoadingPanel.prefab");
                AssetImporter.GetAtPath(folder).SetAssetBundleNameAndVariant("ui_loading", string.Empty);
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }
        /// <summary>所有UI/事件通知的Image统一接入。保留原Sprite、字体、材质和布局，只加生命周期适配组件。</summary>
        private static int BindAtlasImages()
        {
            int count = 0;
            foreach (string guid in AssetDatabase.FindAssets("t:Prefab", new[] { "Assets/GameRes/UI" }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                GameObject root = PrefabUtility.LoadPrefabContents(path);
                try
                {
                    foreach (Image image in root.GetComponentsInChildren<Image>(true))
                    {
                        if (image.GetComponent<AtlasImageBinding>() == null)
                        {
                            image.gameObject.AddComponent<AtlasImageBinding>();
                        }
                        count++;
                    }
                    PrefabUtility.SaveAsPrefabAsset(root, path);
                }
                finally { PrefabUtility.UnloadPrefabContents(root); }
            }
            return count;
        }
        private static Image NewImage(Transform parent, string name, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            Image image = go.GetComponent<Image>();
            image.color = color;
            image.raycastTarget = false;
            return image;
        }
        private static Text NewText(Transform parent, string name, string value, Font font, int size, Vector2 position, Vector2 dimensions)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Text), typeof(Shadow));
            go.transform.SetParent(parent, false);
            Text text = go.GetComponent<Text>();
            text.font = font;
            text.text = value;
            text.fontSize = size;
            text.fontStyle = FontStyle.Bold;
            text.alignment = TextAnchor.MiddleCenter;
            text.color = new Color(0.96f, 0.99f, 0.85f, 1f);
            text.raycastTarget = false;
            go.GetComponent<Shadow>().effectColor = new Color(0.01f, 0.04f, 0.03f, 0.9f);
            Position(text.rectTransform, position, dimensions);
            return text;
        }
        private static void Position(RectTransform rect, Vector2 position, Vector2 size)
        {
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = position;
            rect.sizeDelta = size;
        }
        private static void Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = rect.offsetMax = Vector2.zero;
        }
    }
}
