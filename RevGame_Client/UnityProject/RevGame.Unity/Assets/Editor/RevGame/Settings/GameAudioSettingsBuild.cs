// 声音设置的可重复制作用例：真实UGUI预制体使用项目素材，不在运行时拼控件。
// 所有临时GameObject先制作后保存再销毁；只改本次设置预制体和三个入口，不操作用户场景。
using System;
using System.Collections.Generic;
using System.IO;
using RevGame.Services;
using RevGame.UI;
using Revolution;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.SceneManagement;
using UnityEngine.U2D;

namespace RevGame.Editor
{
    public static class GameAudioSettingsBuild
    {
        private const string Folder = "Assets/GameRes/UI/Panel/Settings";
        private static readonly Color Ink = new Color(.16f, .24f, .22f);
        /// <summary>只创建设置面板及入口，重复执行覆盖同一个预制体、保留已有GUID；图集/AB由项目资源工具配置。</summary>
        public static string Configure()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                throw new InvalidOperationException("设置预制体制作需要编辑态");
            }
            // 用户已在Prefab编辑器选择新图片/调整布局后，不再用旧制作配方覆盖已有预制体。
            if (AssetDatabase.LoadAssetAtPath<GameObject>(Folder + "/AudioSettingsPanel.prefab") != null)
            {
                return "声音设置预制体已存在，保留用户图片与尺寸；需要调滑杆时执行slider-layout入口";
            }
            if (!AssetDatabase.IsValidFolder(Folder))
            {
                AssetDatabase.CreateFolder("Assets/GameRes/UI/Panel", "Settings");
            }
            var root = new GameObject("AudioSettingsPanel", typeof(RectTransform), typeof(CanvasGroup), typeof(AudioSettingsPanel));
            try
            {
                ((RectTransform)root.transform).sizeDelta = new Vector2(1920, 1080);
                Image veil = Image("veil", root.transform, new Vector2(1920, 1080), Vector2.zero, null);
                GameAssetBuilder.Stretch(veil.rectTransform);
                veil.color = new Color(.03f, .07f, .08f, .55f);
                veil.raycastTarget = true;
                Image card = Image("card", root.transform, new Vector2(880, 490), Vector2.zero, Sprite("UI_Settlement_imgBK"));
                card.type = UnityEngine.UI.Image.Type.Sliced;
                card.raycastTarget = true;
                Label("title", card.transform, "声音设置", new Vector2(620, 75), new Vector2(0, 157), 40);
                Label("bgmLabel", card.transform, "背景音乐", new Vector2(190, 55), new Vector2(-270, 62), 30);
                Label("volumeText", card.transform, "60%", new Vector2(110, 55), new Vector2(282, 62), 32);
                Label("help", card.transform, "拖动立即生效，关闭后保存", new Vector2(650, 45), new Vector2(0, -160), 22);
                Image track = Image("sldBgm", card.transform, new Vector2(545, 56), new Vector2(0, 2), Sprite("UI_Tavern_Background"));
                track.type = UnityEngine.UI.Image.Type.Sliced;
                track.color = new Color(.38f, .48f, .40f);
                track.raycastTarget = true;
                Slider slider = track.gameObject.AddComponent<Slider>();
                RectTransform fillArea = Rect("fillArea", track.transform, new Vector2(511, 24), Vector2.zero);
                Image fill = Image("fill", fillArea, new Vector2(511, 24), Vector2.zero, Sprite("UI_Setting_Background"));
                // Slider只改fillRect锚点，保留sizeDelta会把绿色填充额外撑宽；必须先清掉固定尺寸。
                GameAssetBuilder.Stretch(fill.rectTransform);
                fill.type = UnityEngine.UI.Image.Type.Sliced;
                fill.color = new Color(.5f, .77f, .45f);
                RectTransform handleArea = Rect("handleArea", track.transform, new Vector2(511, 56), Vector2.zero);
                Image handle = Image("handle", handleArea, new Vector2(42, 60), Vector2.zero, Sprite("UI_Tavern_Handle"));
                handle.raycastTarget = true;
                slider.fillRect = fill.rectTransform;
                slider.handleRect = handle.rectTransform;
                slider.targetGraphic = handle;
                slider.direction = Slider.Direction.LeftToRight;
                slider.minValue = 0;
                slider.maxValue = 1;
                slider.value = GameAudioSettings.DefaultVolume;
                RectTransform toggleRoot = Rect("tglMute", card.transform, new Vector2(255, 60), new Vector2(-172, -82));
                Toggle toggle = toggleRoot.gameObject.AddComponent<Toggle>();
                Image box = Image("box", toggleRoot, new Vector2(46, 46), new Vector2(-90, 0), Sprite("UI_Setting_Background"));
                box.raycastTarget = true;
                Image check = Image("check", box.transform, new Vector2(34, 34), Vector2.zero, Sprite("UI_Setting_Checkmark"));
                toggle.targetGraphic = box;
                toggle.graphic = check;
                Label("muteLabel", toggleRoot, "音乐静音", new Vector2(180, 50), new Vector2(40, 0), 26);
                Button("btnDefault", card.transform, "恢复默认", new Vector2(245, 68), new Vector2(170, -82));
                Button("btnClose", card.transform, "关闭", new Vector2(105, 58), new Vector2(328, 164));

                if (PrefabUtility.SaveAsPrefabAsset(root, Folder + "/AudioSettingsPanel.prefab") == null)
                {
                    throw new InvalidOperationException("声音设置预制体保存失败");
                }
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
            AddEntry("HomePanel", false);
            AddEntry("LevelSelectPanel", false);
            AddEntry("BattleHudPanel", true);
            return "声音设置预制体、滑杆/静音/默认按钮、首页/选关/战斗入口已保存";
        }
        /// <summary>只调整滑杆，优先修改用户正在编辑的同一Prefab Stage，避免额外加载旧副本覆盖未保存的图片选择。</summary>
        public static string AdjustSlider()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                throw new InvalidOperationException("滑杆布局调整只能在编辑态执行");
            }
            string path = Folder + "/AudioSettingsPanel.prefab";
            PrefabStage stage = PrefabStageUtility.GetCurrentPrefabStage();
            bool current = stage != null && stage.assetPath == path;
            GameObject root = current ? stage.prefabContentsRoot : PrefabUtility.LoadPrefabContents(path);
            try
            {
                RectTransform track = (RectTransform)root.transform.Find("card/sldBgm");
                RectTransform fillArea = (RectTransform)track.Find("fillArea");
                RectTransform fill = (RectTransform)fillArea.Find("fill");
                RectTransform handleArea = (RectTransform)track.Find("handleArea");
                RectTransform handle = (RectTransform)handleArea.Find("handle");
                Slider slider = track.GetComponent<Slider>();

                if (slider == null || fill == null || handle == null)
                {
                    throw new InvalidOperationException("声音滑杆接线不完整");
                }
                Undo.RecordObjects(new UnityEngine.Object[] { track, fillArea, fill, handleArea, handle }, "调整声音设置滑杆尺寸");
                track.sizeDelta = new Vector2(545, 46);
                fillArea.sizeDelta = new Vector2(545, 46);
                fillArea.anchoredPosition = Vector2.zero;
                handleArea.sizeDelta = new Vector2(509, 46);
                handleArea.anchoredPosition = Vector2.zero;
                // Slider会把fill纵向锚点设为0～1；sizeDelta.y必须为0，实际高度等于fillArea的46。
                fill.sizeDelta = Vector2.zero;
                fill.anchoredPosition = Vector2.zero;
                // Slider会把handle纵向锚点设为0～1；实际高度=46+14=60，宽22接近原31:84图片比例。
                // 清零位置让手柄中心对准轨道，509宽的行程留出端部边框，0/100%不会滑出轨道。
                handle.sizeDelta = new Vector2(22, 14);
                handle.anchoredPosition = Vector2.zero;
                float value = slider.value;
                slider.SetValueWithoutNotify(value < .5f ? 1 : 0);
                slider.SetValueWithoutNotify(value);
                EditorUtility.SetDirty(root);

                if (PrefabUtility.SaveAsPrefabAsset(root, path) == null)
                {
                    throw new InvalidOperationException("滑杆尺寸保存失败");
                }
                // 保持当前Prefab预览不关闭；同一Stage的最新图片、尺寸与磁盘保存内容一致。
                if (current)
                {
                    EditorSceneManager.MarkSceneDirty(stage.scene);
                }
                return "轨道545×46，填充跟随轨道，手柄实际22×60；已保留图片、音量值和其它控件位置";
            }
            finally
            {
                if (!current)
                {
                    PrefabUtility.UnloadPrefabContents(root);
                }
            }
        }
        /// <summary>战斗顶栏原返回按钮拆出右侧设置位，保证两个按钮不互相覆盖；大厅入口放右上角。</summary>
        private static void AddEntry(string name, bool battle)
        {
            string path = "Assets/GameRes/UI/Panel/" + name + ".prefab";
            GameObject root = PrefabUtility.LoadPrefabContents(path);
            try
            {
                Transform existing = root.transform.Find("btnSettings");

                if (existing == null)
                {
                    Button button = Button("btnSettings", root.transform, "设置", new Vector2(battle ? 130 : 155, battle ? 78 : 68), Vector2.zero);
                    button.GetComponent<Image>().sprite = Sprite("UI_Tavern_Background");
                    button.GetComponent<Image>().type = UnityEngine.UI.Image.Type.Sliced;
                    existing = button.transform;
                }
                var rect = (RectTransform)existing;
                GameAssetBuilder.Anchor(rect, Vector2.one);
                rect.anchoredPosition = new Vector2(battle ? -103 : -130, battle ? -72 : -80);

                if (battle)
                {
                    var home = (RectTransform)root.transform.Find("btnHome");
                    GameAssetBuilder.Anchor(home, Vector2.one);
                    home.anchoredPosition = new Vector2(-317, -72);
                    home.sizeDelta = new Vector2(270, 78);
                }
                PrefabUtility.SaveAsPrefabAsset(root, path);
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
        }
        /// <summary>临时PlayerPrefs键验证保存/重建/静音/恢复默认，不改玩家设置或当前声音总线。</summary>
        public static string CheckPersistence()
        {
            string key = "RevGame.Tests.Audio." + Guid.NewGuid().ToString("N");
            try
            {
                var first = new GameAudioSettings(key, false);

                if (first.Volume != GameAudioSettings.DefaultVolume || first.Muted)
                {
                    throw new InvalidOperationException("首次设置错误");
                }
                first.SetVolume(.42f);
                first.SetMuted(true);
                first.Save();
                var restored = new GameAudioSettings(key, false);

                if (Mathf.Abs(restored.Volume - .42f) > .0001f || !restored.Muted)
                {
                    throw new InvalidOperationException("音量/静音未持久化");
                }
                restored.SetVolume(2);

                if (restored.Volume != 1)
                {
                    throw new InvalidOperationException("上界未截断");
                }
                restored.SetVolume(-1);

                if (restored.Volume != 0)
                {
                    throw new InvalidOperationException("下界未截断");
                }
                bool rejected = false;
                try
                {
                    restored.SetVolume(float.NaN);
                }
                catch (ArgumentOutOfRangeException) { rejected = true; }

                if (!rejected)
                {
                    throw new InvalidOperationException("NaN未拒绝");
                }
                restored.RestoreDefault();
                restored.Save();
                var reset = new GameAudioSettings(key, false);

                if (reset.Volume != GameAudioSettings.DefaultVolume || reset.Muted)
                {
                    throw new InvalidOperationException("恢复默认未保存");
                }
                if (Mathf.Abs(GameAudioSettings.MusicBaseVolume * GameAudioSettings.DefaultVolume - .06f * 1.2f) > .000001f)
                {
                    throw new InvalidOperationException("默认BGM不是原音量120%");
                }
                return "首次默认、持久化、静音、上下界、NaN拒绝、恢复默认与音量120%检查通过";
            }
            finally { PlayerPrefs.DeleteKey(key); PlayerPrefs.DeleteKey(key + ".Muted"); PlayerPrefs.Save(); }
        }
        /// <summary>真实PC AB冷加载UGUI预制体；测试隔离的声音设置与属性回调，不初始化游戏或操作玩家存档。</summary>
        public static string Preview()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || RevServices.Get<GameAudioSettings>() != null)
            {
                throw new InvalidOperationException("设置离屏验收需编辑态且不能占用已有设置服务");
            }
            foreach (AssetBundle existing in AssetBundle.GetAllLoadedAssetBundles())
            {
                throw new InvalidOperationException("已有AB被其它工具持有：" + existing.name);
            }
            string key = "RevGame.Tests.AudioPreview." + Guid.NewGuid().ToString("N");
            string output = "Temp/AudioSettingsChecks";
            System.IO.Directory.CreateDirectory(output);
            float oldBgm = RevSound.GetVolume(RevSoundKind.Bgm);
            float oldSfx = RevSound.GetVolume(RevSoundKind.Sfx);
            var bundles = new List<AssetBundle>();
            Scene scene = EditorSceneManager.NewPreviewScene();
            RenderTexture target = null;
            Texture2D readback = null;
            RenderTexture previous = RenderTexture.active;
            var settings = new GameAudioSettings(key, true);
            bool registered = false;
            var atlases = new Dictionary<string, SpriteAtlas>(StringComparer.Ordinal);
            var previewSprites = new Dictionary<string, Sprite>(StringComparer.Ordinal);
            Action<string, Action<SpriteAtlas>> atlasRequested = (tag, bind) =>
            {
                if (!atlases.TryGetValue(tag, out SpriteAtlas atlas))
                {
                    throw new InvalidOperationException("设置预览缺少图集：" + tag);
                }
                bind(atlas);
            };
            try
            {
                RevServices.Register<GameAudioSettings>(settings);
                registered = true;
                AssetBundle main = LoadBundle("PC", bundles);
                AssetBundleManifest manifest = main.LoadAsset<AssetBundleManifest>("AssetBundleManifest");

                if (manifest == null)
                {
                    throw new InvalidOperationException("PC AB主包缺失Manifest");
                }
                foreach (string dependency in manifest.GetAllDependencies("ui_settings"))
                {
                    AssetBundle loaded = LoadBundle(dependency, bundles);
                    foreach (SpriteAtlas atlas in loaded.LoadAllAssets<SpriteAtlas>())
                    {
                        atlases[atlas.tag] = atlas;
                    }
                }
                // 仅夹具提供已冷加载的图集；正式游戏仍使用GameSpriteAtlasService/RevResManager。
                SpriteAtlasManager.atlasRequested += atlasRequested;
                AssetBundle bundle = LoadBundle("ui_settings", bundles);
                GameObject prefab = bundle.LoadAsset<GameObject>("assets/gameres/ui/panel/settings/audiosettingspanel.prefab");

                if (prefab == null)
                {
                    throw new InvalidOperationException("真实AB设置预制体缺失");
                }
                var cameraObject = new GameObject("设置验收相机", typeof(Camera));
                SceneManager.MoveGameObjectToScene(cameraObject, scene);
                Camera camera = cameraObject.GetComponent<Camera>();
                camera.enabled = false;
                camera.orthographic = true;
                camera.orthographicSize = 540;
                camera.transform.position = new Vector3(0, 0, -100);
                camera.nearClipPlane = 1;
                camera.farClipPlane = 500;
                camera.scene = scene;
                camera.backgroundColor = new Color(.1f, .2f, .18f);
                camera.clearFlags = CameraClearFlags.SolidColor;
                target = new RenderTexture(1920, 1080, 24, RenderTextureFormat.ARGB32);
                target.Create();
                camera.targetTexture = target;
                readback = new Texture2D(1920, 1080, TextureFormat.RGBA32, false);
                var canvasObject = new GameObject("设置验收画布", typeof(RectTransform), typeof(Canvas));
                SceneManager.MoveGameObjectToScene(canvasObject, scene);
                Canvas canvas = canvasObject.GetComponent<Canvas>();
                canvas.renderMode = RenderMode.WorldSpace;
                canvas.worldCamera = camera;
                ((RectTransform)canvas.transform).sizeDelta = new Vector2(1920, 1080);
                GameObject instance = UnityEngine.Object.Instantiate(prefab, canvas.transform, false);
                // 非Play编辑器不保证atlasRequested立即完成；隔离夹具显式借真实AB图集Sprite绘制布局。
                // 每个名字只克隆一次并在finally销毁。这只验证包中素材与UGUI排版，不冒充运行时首次自动绑定。
                foreach (Image image in instance.GetComponentsInChildren<Image>(true))
                {
                    if (image.sprite == null)
                    {
                        continue;
                    }
                    string spriteName = image.sprite.name;

                    if (!previewSprites.TryGetValue(spriteName, out Sprite sprite))
                    {
                        foreach (SpriteAtlas atlas in atlases.Values)
                        {
                            sprite = atlas.GetSprite(spriteName);

                            if (sprite != null)
                            {
                                break;
                            }
                        }

                        if (sprite == null || sprite.texture == null)
                        {
                            throw new InvalidOperationException("真实AB图集未包含设置素材：" + spriteName);
                        }
                        previewSprites.Add(spriteName, sprite);
                    }
                    image.sprite = sprite;
                    image.SetAllDirty();
                }
                var panel = instance.GetComponent<AudioSettingsPanel>();

                if (panel == null)
                {
                    throw new InvalidOperationException("设置Panel组件未装配");
                }
                Slider slider = instance.GetComponentInChildren<Slider>(true);
                Toggle toggle = instance.GetComponentInChildren<Toggle>(true);
                Text volume = instance.transform.Find("card/volumeText").GetComponent<Text>();

                if (slider == null || slider.handleRect == null || slider.fillRect == null || toggle.graphic == null)
                {
                    throw new InvalidOperationException("滑杆/静音预制体接线不完整");
                }
                // 编辑态不启动RevUI管理器；夹具将真实控件事件转入同一组属性回调，验证业务处理与总线结果。
                slider.onValueChanged.AddListener(v => RevUIWidgetEvents.Invoke(panel, "sldBgm", RevUIWidgetEventKind.SliderChanged, v));
                toggle.onValueChanged.AddListener(v => RevUIWidgetEvents.Invoke(panel, "tglMute", RevUIWidgetEventKind.ToggleChanged, v));
                slider.value = .42f;

                if (Mathf.Abs(settings.Volume - .42f) > .0001f || Mathf.Abs(RevSound.GetVolume(RevSoundKind.Bgm) - .42f) > .0001f)
                {
                    throw new InvalidOperationException("滑杆属性回调未即时更新BGM");
                }
                volume.text = "42%";
                Capture(camera, target, readback, output + "/Volume42.png");
                toggle.isOn = true;

                if (!settings.Muted || RevSound.GetVolume(RevSoundKind.Bgm) != 0 || RevSound.GetVolume(RevSoundKind.Sfx) != oldSfx)
                {
                    throw new InvalidOperationException("静音未生效或误伤音效分类");
                }
                Capture(camera, target, readback, output + "/Muted.png");
                RevUIWidgetEvents.Invoke(panel, "btnDefault", RevUIWidgetEventKind.Click);

                if (settings.Volume != GameAudioSettings.DefaultVolume || settings.Muted)
                {
                    throw new InvalidOperationException("恢复默认按钮回调失败");
                }
                slider.SetValueWithoutNotify(settings.Volume);
                toggle.SetIsOnWithoutNotify(settings.Muted);
                volume.text = "60%";
                Capture(camera, target, readback, output + "/Default.png");
                settings.Save();
                var restored = new GameAudioSettings(key, false);

                if (restored.Volume != .6f || restored.Muted)
                {
                    throw new InvalidOperationException("真实控件操作后的持久化失败");
                }
                foreach (Image image in instance.GetComponentsInChildren<Image>(true))
                {
                    if (image.sprite != null && image.mainTexture == null)
                    {
                        throw new InvalidOperationException("设置UI图集未绑定：" + image.name);
                    }
                }
                return "真实PC AB设置UI已绘制；滑杆42%、静音仅BGM、恢复默认60%、保存重建通过；" + Path.GetFullPath(output);
            }
            finally
            {
                RenderTexture.active = previous;
                SpriteAtlasManager.atlasRequested -= atlasRequested;
                EditorSceneManager.ClosePreviewScene(scene);
                foreach (Sprite sprite in previewSprites.Values)
                {
                    UnityEngine.Object.DestroyImmediate(sprite);
                }
                if (readback != null)
                {
                    UnityEngine.Object.DestroyImmediate(readback);
                }
                if (target != null)
                {
                    target.Release();
                    UnityEngine.Object.DestroyImmediate(target);
                }
                if (registered)
                {
                    RevServices.Unregister<GameAudioSettings>();
                }
                settings.Dispose();
                RevSound.SetVolume(RevSoundKind.Bgm, oldBgm);
                for (int i = bundles.Count - 1; i >= 0; i--)
                {
                    bundles[i].Unload(true);
                }
                PlayerPrefs.DeleteKey(key);
                PlayerPrefs.DeleteKey(key + ".Muted");
                PlayerPrefs.Save();
            }
        }
        private static AssetBundle LoadBundle(string name, List<AssetBundle> owned)
        {
            AssetBundle bundle = AssetBundle.LoadFromFile("Assets/StreamingAssets/PC/" + name);

            if (bundle == null)
            {
                throw new InvalidOperationException("设置AB加载失败：" + name);
            }
            owned.Add(bundle);
            return bundle;
        }
        private static void Capture(Camera camera, RenderTexture target, Texture2D readback, string file)
        {
            Canvas.ForceUpdateCanvases();
            camera.Render();
            RenderTexture.active = target;
            readback.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0);
            readback.Apply(false, false);
            File.WriteAllBytes(file, readback.EncodeToPNG());
        }
        private static Sprite Sprite(string name)
        {
            Sprite sprite = AssetDatabase.LoadAssetAtPath<Sprite>("Assets/GameRes/Art/UI/" + name + ".png");

            if (sprite == null)
            {
                throw new InvalidOperationException("设置素材缺失：" + name);
            }
            return sprite;
        }
        private static RectTransform Rect(string name, Transform parent, Vector2 size, Vector2 position)
        {
            return (RectTransform)GameAssetBuilder.Rect(name, parent, size, position).transform;
        }
        private static Image Image(string name, Transform parent, Vector2 size, Vector2 position, Sprite sprite)
        {
            Image image = Rect(name, parent, size, position).gameObject.AddComponent<Image>();
            image.sprite = sprite;
            image.raycastTarget = false;
            return image;
        }
        private static Text Label(string name, Transform parent, string value, Vector2 size, Vector2 position, int fontSize)
        {
            Text text = GameAssetBuilder.Label(name, parent, value, size, position, fontSize);
            text.color = Ink;
            return text;
        }
        private static Button Button(string name, Transform parent, string value, Vector2 size, Vector2 position)
        {
            Button button = GameAssetBuilder.MakeButton(name, parent, value, size, position);
            button.image.raycastTarget = true;
            return button;
        }
    }
}
