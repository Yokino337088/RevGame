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
        /// <summary>加载面板的正式预制体位置，局部换图和完整场景装配共用。</summary>
        private const string LoadingPanelPath = "Assets/GameRes/UI/Panel/Loading/LoadingPanel.prefab";
        /// <summary>过场正式美术目录，保存进度条源图和传送门整图。</summary>
        private const string LoadingArtPath = "Assets/GameRes/Art/UI/Loading/";
        /// <summary>过场整图采样所用的共享材质路径，使用原画 UV 渲染动效。</summary>
        private const string LoadingPortalMaterialPath = "Assets/GameRes/Material/UI/LoadingPortal.mat";
        /// <summary>预制体内保存传送门原画的独立背景图层名。</summary>
        private const string PortalBackgroundName = "portalBackground";
        /// <summary>进度条在 1920×1080 设计画布上的宽度。</summary>
        private const float LoadingTrackWidth = 760f;

        /// <summary>依次制作加载面板、图集晚绑定组件与两个场景，编辑态执行。</summary>
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
            // 重建主线两个场景时，保留无尽、联机和视觉小说各自登记的发布场景。
            const string startPath = "Assets/Scenes/StartScene.unity";
            const string gamePath = "Assets/Scenes/GameScene.unity";
            const string galPath = "Assets/Scenes/GalScene.unity";
            var buildScenes = EditorBuildSettings.scenes
                .Where(scene => scene.path != startPath && scene.path != gamePath).ToList();
            buildScenes.Insert(0, new EditorBuildSettingsScene(startPath, true));
            buildScenes.Insert(1, new EditorBuildSettingsScene(gamePath, true));

            int galIndex = buildScenes.FindIndex(scene => scene.path == galPath);
            if (galIndex < 0)
            {
                buildScenes.Add(new EditorBuildSettingsScene(galPath, true));
            }
            else if (!buildScenes[galIndex].enabled)
            {
                buildScenes[galIndex] = new EditorBuildSettingsScene(galPath, true);
            }
            EditorBuildSettings.scenes = buildScenes.ToArray();
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
        /// <summary>完整制作独立加载面板，保留已交付面板的背景选择。</summary>
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
                NewText(button.transform, "label", "返回大厅", font, 28, Vector2.zero, new Vector2(290f, 68f));
                ConfigureLoadingProgress(root.transform);
                LoadingPanel oldPanel = AssetDatabase.LoadAssetAtPath<GameObject>(LoadingPanelPath)?.GetComponent<LoadingPanel>();
                if (oldPanel != null && oldPanel.transform.Find(PortalBackgroundName) != null)
                {
                    var oldOptions = new SerializedObject(oldPanel);
                    var newOptions = new SerializedObject(root.GetComponent<LoadingPanel>());
                    newOptions.FindProperty("usePortalBackdrop").boolValue = oldOptions.FindProperty("usePortalBackdrop").boolValue;
                    newOptions.ApplyModifiedPropertiesWithoutUndo();
                }
                ConfigureLoadingBackdrop(root.transform);
                PrefabUtility.SaveAsPrefabAsset(root, LoadingPanelPath);
                AssetImporter.GetAtPath(folder).SetAssetBundleNameAndVariant("ui_loading", string.Empty);
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }

        /// <summary>局部写回加载背景，保留旧漩涡材质和已设定的背景选择。</summary>
        public static string UpdateLoadingBackdrop()
        {
            GameObject root = PrefabUtility.LoadPrefabContents(LoadingPanelPath);
            try
            {
                ConfigureLoadingBackdrop(root.transform, updatingExistingPrefab: true);
                PrefabUtility.SaveAsPrefabAsset(root, LoadingPanelPath, out bool saved);
                if (!saved)
                {
                    throw new InvalidOperationException("加载背景保存失败：" + LoadingPanelPath);
                }
                return "传送门背景已写回，原彩虹漩涡仍可通过 LoadingPanel 的背景选择使用";
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        /// <summary>在旧漩涡图层旁装配完整原画，选中的背景负责拦截全屏点击。</summary>
        private static void ConfigureLoadingBackdrop(Transform root, bool updatingExistingPrefab = false)
        {
            string path = LoadingArtPath + "LoadingPortalBackdrop.png";
            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            Material material = AssetDatabase.LoadAssetAtPath<Material>(LoadingPortalMaterialPath);
            LoadingPanel panel = root.GetComponent<LoadingPanel>();
            Transform rainbow = root.Find("screenBackground");
            if (importer == null || material == null || panel == null || rainbow == null)
            {
                throw new InvalidOperationException("加载背景缺少独立原画、共享动效材质或现有漩涡与面板");
            }
            ConfigureLoadingBackdropImporter(importer);
            Texture2D artwork = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            if (artwork == null)
            {
                throw new InvalidOperationException("无法读取加载背景整图：" + path);
            }

            // 新旧图层都保留全屏锚点。新图的 UV 由 GameMobileLayout 根据视口等比裁切。
            Transform existing = root.Find(PortalBackgroundName);
            RawImage portal;
            if (existing == null)
            {
                var node = new GameObject(PortalBackgroundName, typeof(RectTransform), typeof(RawImage));
                node.transform.SetParent(root, false);
                portal = node.GetComponent<RawImage>();
            }
            else
            {
                portal = existing.GetComponent<RawImage>();
                if (portal == null)
                {
                    throw new InvalidOperationException("传送门背景节点缺少 RawImage");
                }
            }
            Stretch(portal.rectTransform);
            portal.transform.SetAsFirstSibling();
            portal.texture = artwork;
            portal.material = material;
            portal.color = Color.white;
            portal.uvRect = new Rect(0f, 0f, 1f, 1f);
            portal.raycastTarget = true;
            var options = new SerializedObject(panel);
            SerializedProperty selection = options.FindProperty("usePortalBackdrop");
            // 更新没有传送门图层的旧预制体时，显式保存新版默认值。
            if (updatingExistingPrefab && existing == null)
            {
                selection.boolValue = true;
                options.ApplyModifiedPropertiesWithoutUndo();
            }
            bool showPortal = selection.boolValue;
            portal.gameObject.SetActive(showPortal);
            rainbow.gameObject.SetActive(!showPortal);
        }

        /// <summary>按整图导入过场插画，保留原始宽高比并为两个目标平台压缩。已达标时跳过重导。</summary>
        private static void ConfigureLoadingBackdropImporter(TextureImporter importer)
        {
            const int maxSize = 2048;
            TextureImporterPlatformSettings pc = importer.GetPlatformTextureSettings("Standalone");
            TextureImporterPlatformSettings android = importer.GetPlatformTextureSettings("Android");
            if (importer.textureType == TextureImporterType.Default && importer.alphaSource == TextureImporterAlphaSource.None &&
                !importer.alphaIsTransparency && !importer.mipmapEnabled && !importer.isReadable && importer.sRGBTexture &&
                importer.npotScale == TextureImporterNPOTScale.None && importer.wrapMode == TextureWrapMode.Clamp &&
                importer.filterMode == FilterMode.Bilinear && pc.overridden && android.overridden &&
                pc.maxTextureSize == maxSize && android.maxTextureSize == maxSize &&
                pc.format == TextureImporterFormat.BC7 && android.format == TextureImporterFormat.ASTC_6x6)
            {
                return;
            }

            // 全屏插画没有透明区，由 CanvasGroup 统一淡出。关掉 Mip 与 CPU 副本来控制过场纹理驻留。
            importer.textureType = TextureImporterType.Default;
            importer.alphaSource = TextureImporterAlphaSource.None;
            importer.alphaIsTransparency = false;
            importer.mipmapEnabled = false;
            importer.isReadable = false;
            importer.sRGBTexture = true;
            importer.npotScale = TextureImporterNPOTScale.None;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.filterMode = FilterMode.Bilinear;
            // 采用项目已用的压缩格式，保留夜景暗部渐变。保存后再用实际导入的纹理写回预制体。
            importer.SetPlatformTextureSettings(new TextureImporterPlatformSettings
            {
                name = "Standalone", overridden = true, maxTextureSize = maxSize,
                format = TextureImporterFormat.BC7, compressionQuality = 100
            });
            importer.SetPlatformTextureSettings(new TextureImporterPlatformSettings
            {
                name = "Android", overridden = true, maxTextureSize = maxSize,
                format = TextureImporterFormat.ASTC_6x6, compressionQuality = 100
            });
            importer.SaveAndReimport();
        }

        /// <summary>只更新现有加载面板的进度条和百分比位置，保存原预制体并归还临时编辑实例。</summary>
        public static string UpdateLoadingProgress()
        {
            GameObject root = PrefabUtility.LoadPrefabContents(LoadingPanelPath);
            try
            {
                ConfigureLoadingProgress(root.transform);
                PrefabUtility.SaveAsPrefabAsset(root, LoadingPanelPath, out bool saved);
                if (!saved)
                {
                    throw new InvalidOperationException("加载面板保存失败：" + LoadingPanelPath);
                }
                return "加载进度条已写回，底槽按原画比例显示，填充横向裁切";
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        /// <summary>沿用 Slider 控制进度。背景和填充共用等比缩放，Slider 只改变横向可见范围。</summary>
        private static void ConfigureLoadingProgress(Transform root)
        {
            Sprite trackSprite = AssetDatabase.LoadAssetAtPath<Sprite>(LoadingArtPath + "LoadingProgressTrack.png");
            Sprite fillSprite = AssetDatabase.LoadAssetAtPath<Sprite>(LoadingArtPath + "LoadingProgressFill.png");
            Image track = root.Find("progress")?.GetComponent<Image>();
            Image fill = root.Find("progress/fill")?.GetComponent<Image>();
            RectTransform percent = root.Find("percent") as RectTransform;
            Slider slider = track != null ? track.GetComponent<Slider>() : null;
            if (trackSprite == null || fillSprite == null || slider == null || fill == null || percent == null)
            {
                throw new InvalidOperationException("加载面板缺少进度条 Sprite、Slider 或百分比节点");
            }
            if (fillSprite.rect.width > trackSprite.rect.width || fillSprite.rect.height > trackSprite.rect.height)
            {
                throw new InvalidOperationException("加载进度条填充原画应完全位于底槽尺寸内");
            }

            // 固定底槽的宽度按比例求高，留出边框对应的上下左右内边距。
            float scale = LoadingTrackWidth / trackSprite.rect.width;
            Vector2 trackSize = trackSprite.rect.size * scale;
            Vector2 fillInset = (trackSize - fillSprite.rect.size * scale) * .5f;
            track.sprite = trackSprite;
            track.color = Color.white;
            track.type = Image.Type.Simple;
            track.preserveAspect = true;
            track.useSpriteMesh = false;
            track.raycastTarget = false;
            track.rectTransform.sizeDelta = trackSize;

            // Filled 模式让 Slider 更新 fillAmount 时保持整张图的排版矩形。
            fill.sprite = fillSprite;
            fill.color = Color.white;
            fill.type = Image.Type.Filled;
            fill.preserveAspect = true;
            fill.fillMethod = Image.FillMethod.Horizontal;
            fill.fillOrigin = (int)Image.OriginHorizontal.Left;
            fill.fillAmount = 0f;
            fill.raycastTarget = false;
            Stretch(fill.rectTransform);
            fill.rectTransform.offsetMin = fillInset;
            fill.rectTransform.offsetMax = -fillInset;
            slider.fillRect = fill.rectTransform;
            slider.direction = Slider.Direction.LeftToRight;
            slider.SetValueWithoutNotify(0f);

            // 百分比随新底槽高度向下留出间距，避免文字压到金边。
            percent.anchoredPosition = new Vector2(percent.anchoredPosition.x,
                track.rectTransform.anchoredPosition.y - trackSize.y / 2f - percent.sizeDelta.y / 2f - 12f);
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
