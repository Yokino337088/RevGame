using System;
using System.IO;
using RevGame.Data;
using RevGame.Services;
using RevGame.UI;
using UnityEditor;
using UnityEngine;

namespace RevGame.Editor
{
    /// <summary>收藏书首装和重建入口。已有配置文案和资源 GUID 保留，正片使用统一画布的原始分层。</summary>
    public static class GameYayaMemoryBookBuild
    {
        private const string Root = "Assets/GameRes/";
        private const string Panel = Root + "UI/Panel/YayaMemories/";
        private const string MaterialRoot = Root + "Material/UI/YayaMemories/";
        private const string ThemeArt = Root + "Art/UI/YayaMemories/";
        private const string ShaderRoot = "Assets/GameShader/YayaMemories/";
        private static readonly Color Ink = new Color(.29f, .15f, .071f);
        private static readonly Color LightInk = new Color(1f, .91f, .73f);
        private static readonly Color Bronze = new Color(.27f, .134f, .070f);
        private static readonly Color Gold = new Color(.72f, .46f, .22f);
        private static readonly string[] Titles = { "星落小径", "青藤村", "镜湖", "迷雾沼泽", "寒星原", "象牙金神殿" };
        private static readonly string[] Captions =
        {
            "冒险家走快点，再慢的话就要天黑了",
            "冒险家看你廋的，来多吃点",
            "镜湖边的风景真好啊，但更重要的是能和冒险家一起欣赏这么美丽的景色",
            "别乱走，抓紧我的手，沼泽很危险！",
            "本鸦就是感觉有点冷，靠一下取暖而已，别想太多！",
            "冒险家想走？晚了！本鸦彻底反悔了，你还是留下来陪本鸦一辈子吧！"
        };

        [MenuItem("RevGame/资源/装配鸦鸦收藏书")]
        public static string Configure()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                throw new InvalidOperationException("收藏书资源只在编辑态装配");
            }

            Directory.CreateDirectory(MaterialRoot);
            AssetDatabase.Refresh();
            Texture2D openBook = TextureAt(Panel + "Book/OpenBook.png", false);
            Texture2D cover = TextureAt(Panel + "Book/BookCover.png", false);
            var paperMaterial = MakeMaterial("BookPaper", "YayaBookPaper");
            var coverMaterial = MakeMaterial("BookCoverFace", "YayaBookPaper");
            coverMaterial.SetFloat("_CoverMode", 1);
            EditorUtility.SetDirty(coverMaterial);
            var sceneMaterial = MakeMaterial("MemorySceneComposite", "YayaMemoryScene");
            var book = ConfigureText();
            for (int chapter = 1; chapter <= YayaMemoryProgress.ChapterCount; chapter++)
            {
                ConfigureArt(chapter);
            }

            var font = Require<Font>(Root + "Fonts/CJK/NotoSansCJKsc-Bold.otf");
            var button = ImportPlate("MemoryBookButton", .14f);
            var title = ImportPlate("MemoryBookTitle", .105f);
            PatchHome(font);
            BuildPanel(font, button, book, openBook, cover, paperMaterial, coverMaterial, sceneMaterial);
            ApplyBookSkin(button, title);
            AssetDatabase.SaveAssets();
            return "已装配大厅入口、六章分层、独立文案、已打开的书本和封面";
        }

        /// <summary>从生成稿保留真实透明轮廓。切去透明留边仅改变 Sprite 的 UV，原始 PNG 与九宫格边饰完整保留。</summary>
        private static Sprite ImportPlate(string name, float horizontalBorder)
        {
            string path = ThemeArt + name + ".png";
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            if (importer == null)
            {
                throw new FileNotFoundException("收藏书主题贴图缺失", path);
            }
            var source = new Texture2D(2, 2);
            try
            {
                if (!ImageConversion.LoadImage(source, File.ReadAllBytes(path)))
                {
                    throw new InvalidOperationException("无法读取主题贴图：" + path);
                }
                Color32[] rgba = source.GetPixels32();
                int left = source.width, bottom = source.height, right = -1, top = -1;
                for (int y = 0; y < source.height; y++)
                {
                    for (int x = 0; x < source.width; x++)
                    {
                        if (rgba[y * source.width + x].a > 12)
                        {
                            left = Mathf.Min(left, x);
                            right = Mathf.Max(right, x);
                            bottom = Mathf.Min(bottom, y);
                            top = Mathf.Max(top, y);
                        }
                    }
                }
                if (right <= left || top <= bottom || top - bottom >= source.height * .92f)
                {
                    throw new InvalidOperationException("主题贴图需要独立透明边缘：" + path);
                }
                left = Mathf.Max(0, left - 2);
                right = Mathf.Min(source.width - 1, right + 2);
                bottom = Mathf.Max(0, bottom - 2);
                top = Mathf.Min(source.height - 1, top + 2);
                int width = right - left + 1;
                int height = top - bottom + 1;
                float sides = Mathf.Round(width * horizontalBorder);
                float edge = Mathf.Round(height * .22f);
                importer.textureType = TextureImporterType.Sprite;
                importer.spriteImportMode = SpriteImportMode.Multiple;
                importer.alphaSource = TextureImporterAlphaSource.FromInput;
                importer.alphaIsTransparency = true;
                importer.maxTextureSize = 1024;
                importer.mipmapEnabled = false;
                importer.isReadable = false;
                importer.npotScale = TextureImporterNPOTScale.None;
                importer.spritesheet = new[]
                {
                    new SpriteMetaData
                    {
                        name = name, rect = new Rect(left, bottom, width, height), alignment = (int)SpriteAlignment.Center,
                        pivot = new Vector2(.5f, .5f), border = new Vector4(sides, edge, sides, edge)
                    }
                };
                importer.SaveAndReimport();
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(source);
            }
            return Require<Sprite>(path);
        }

        private static YayaMemoryBook ConfigureText()
        {
            const string path = Panel + "YayaMemoryBook.asset";
            var existing = AssetDatabase.LoadAssetAtPath<YayaMemoryBook>(path);
            if (existing != null)
            {
                if (existing.Chapters == null || existing.Chapters.Length != Titles.Length || Array.Exists(existing.Chapters, chapter => chapter == null))
                {
                    throw new InvalidOperationException("收藏书配置需要六章文案：" + path);
                }
                return existing;
            }

            var book = ScriptableObject.CreateInstance<YayaMemoryBook>();
            book.Chapters = new YayaMemoryBook.Chapter[Titles.Length];
            for (int index = 0; index < Titles.Length; index++)
            {
                book.Chapters[index] = new YayaMemoryBook.Chapter { Title = Titles[index], Caption = Captions[index] };
            }
            AssetDatabase.CreateAsset(book, path);
            return book;
        }

        private static void ConfigureArt(int chapter)
        {
            string folder = Root + "YayaMemories/Chapter" + chapter.ToString("D2") + "/";
            string path = folder + "Illustration.asset";
            var art = AssetDatabase.LoadAssetAtPath<YayaMemoryPageArt>(path);
            if (art == null)
            {
                art = ScriptableObject.CreateInstance<YayaMemoryPageArt>();
                AssetDatabase.CreateAsset(art, path);
            }

            art.Backdrop = TextureAt(folder + "Backdrop.png", false);
            art.Atmosphere = TextureAt(folder + "Atmosphere.png", true, chapter is 1 or 3 or 4 or 5);
            art.ContactShadow = TextureAt(folder + "ContactShadow.png", true, chapter == 1);
            art.Cast = TextureAt(folder + "Cast.png", true);
            art.Foreground = TextureAt(folder + "Foreground.png", true, chapter is 2 or 4 or 6);

            foreach (Texture2D layer in new[] { art.Backdrop, art.Atmosphere, art.ContactShadow, art.Cast, art.Foreground })
            {
                if (layer != null && (layer.width != 1920 || layer.height != 1080))
                {
                    throw new InvalidOperationException("收藏插画全帧图层需要1920×1080：" + folder + layer.name);
                }
            }
            EditorUtility.SetDirty(art);
        }

        private static Texture2D TextureAt(string path, bool alpha, bool required = true)
        {
            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer == null)
            {
                return required ? Require<Texture2D>(path) : null;
            }
            importer.textureType = TextureImporterType.Default;
            importer.alphaSource = alpha ? TextureImporterAlphaSource.FromInput : TextureImporterAlphaSource.None;
            importer.alphaIsTransparency = alpha;
            importer.isReadable = false;
            importer.mipmapEnabled = false;
            importer.npotScale = TextureImporterNPOTScale.None;
            importer.sRGBTexture = true;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.filterMode = FilterMode.Bilinear;
            importer.SetPlatformTextureSettings(new TextureImporterPlatformSettings
            {
                name = "Standalone", overridden = true, maxTextureSize = 2048, compressionQuality = 100,
                format = alpha ? TextureImporterFormat.DXT5 : TextureImporterFormat.DXT1
            });
            importer.SetPlatformTextureSettings(new TextureImporterPlatformSettings
            {
                name = "Android", overridden = true, maxTextureSize = 2048, compressionQuality = 100,
                format = alpha ? TextureImporterFormat.ETC2_RGBA8 : TextureImporterFormat.ETC2_RGB4
            });
            importer.SaveAndReimport();
            return Require<Texture2D>(path);
        }

        private static Material MakeMaterial(string name, string shaderName)
        {
            string path = MaterialRoot + name + ".mat";
            Shader shader = Require<Shader>(ShaderRoot + shaderName + ".shader");
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                material = new Material(shader);
                AssetDatabase.CreateAsset(material, path);
            }
            material.shader = shader;
            EditorUtility.SetDirty(material);
            return material;
        }

        private static void PatchHome(Font font)
        {
            const string path = Root + "UI/Panel/HomePanel.prefab";
            GameObject root = PrefabUtility.LoadPrefabContents(path);
            try
            {
                var wood = root.transform.Find("btnSettings").GetComponent<UnityEngine.UI.Image>().sprite;
                if (root.transform.Find("btnMemories") == null)
                {
                    var entry = Button(root.transform, "btnMemories", "与鸦鸦的点滴", font, wood, new Vector2(.122f, .924f), new Vector2(292, 72), 36);
                    Sprite circle = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/Knob.psd");
                    var badge = ColorBlock(entry.transform, "Unread", new Vector2(.94f, .83f), new Vector2(27, 27), new Color(.91f, .13f, .10f));
                    badge.sprite = circle;
                    var rim = badge.gameObject.AddComponent<UnityEngine.UI.Outline>();
                    rim.effectColor = new Color(1f, .85f, .57f);
                    rim.effectDistance = new Vector2(2f, 2f);
                    var halo = ColorBlock(entry.transform, "Halo", new Vector2(.94f, .83f), new Vector2(38, 38), new Color(1f, .62f, .24f, .5f));
                    halo.sprite = circle;
                    halo.transform.SetSiblingIndex(1);
                    badge.gameObject.SetActive(false);
                    halo.gameObject.SetActive(false);
                }
                if (root.transform.Find("MemorySpeech") == null)
                {
                    var speech = Node(root.transform, "MemorySpeech", new Vector2(.36f, .77f), new Vector2(440, 135));
                    speech.pivot = new Vector2(0, .5f);
                    speech.anchoredPosition = Vector2.zero;
                    Group(speech);
                    Label(speech, "Speaker", "鸦鸦", font, LightInk, new Vector2(.5f, .77f), new Vector2(402, 33), 26, TextAnchor.MiddleLeft);
                    Label(speech, "Line", "", font, LightInk, new Vector2(.5f, .38f), new Vector2(402, 90), 26, TextAnchor.MiddleLeft);
                    speech.gameObject.SetActive(false);
                }

                SkinSpeech((RectTransform)root.transform.Find("MemorySpeech"));
                PrefabUtility.SaveAsPrefabAsset(root, path);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        private static void SkinSpeech(RectTransform speech)
        {
            foreach (string oldName in new[] { "Tail", "Border", "Parchment" })
            {
                Transform old = speech.Find(oldName);
                if (old != null)
                {
                    UnityEngine.Object.DestroyImmediate(old.gameObject);
                }
            }
            speech.sizeDelta = new Vector2(466, 164);
            var background = speech.Find("Background")?.GetComponent<UnityEngine.UI.Image>();
            if (background == null)
            {
                background = Fill(speech, "Background").gameObject.AddComponent<UnityEngine.UI.Image>();
                background.transform.SetAsFirstSibling();
            }
            background.sprite = Require<Sprite>(Root + "Art/UI/UI_Tip_imgBK.png");
            background.type = UnityEngine.UI.Image.Type.Sliced;
            background.raycastTarget = false;
            var speaker = speech.Find("Speaker").GetComponent<UnityEngine.UI.Text>();
            speaker.rectTransform.anchorMin = speaker.rectTransform.anchorMax = new Vector2(.5f, .775f);
            speaker.rectTransform.sizeDelta = new Vector2(403, 35);
            speaker.color = new Color(1f, .79f, .46f);
            var line = speech.Find("Line").GetComponent<UnityEngine.UI.Text>();
            line.rectTransform.anchorMin = line.rectTransform.anchorMax = new Vector2(.5f, .40f);
            line.rectTransform.sizeDelta = new Vector2(403, 95);
            line.color = LightInk;
            line.fontSize = 24;
            line.text = "冒险家，收藏书里添了新的一页。\n来陪本鸦看看吧！";
        }

        private static void BuildPanel(Font font, Sprite button, YayaMemoryBook book, Texture2D paperArt, Texture2D coverArt,
            Material paperMaterial, Material coverMaterial, Material sceneMaterial)
        {
            const string path = Panel + "YayaMemoryBookPanel.prefab";
            if (AssetDatabase.LoadAssetAtPath<GameObject>(path) != null)
            {
                return;
            }

            var frame = new GameObject("YayaMemoryBookPanel", typeof(RectTransform), typeof(CanvasGroup));
            try
            {
                var root = (RectTransform)frame.transform;
                root.sizeDelta = new Vector2(1920, 1080);
                var host = frame.AddComponent<YayaMemoryBookPanel>();
                var serialized = new SerializedObject(host);
                serialized.FindProperty("book").objectReferenceValue = book;
                serialized.FindProperty("sceneTemplate").objectReferenceValue = sceneMaterial;
                serialized.ApplyModifiedPropertiesWithoutUndo();

                var backdrop = Raw(root, "Backdrop", coverArt, null, true);
                backdrop.color = new Color(.37f, .28f, .24f);
                Raw(root, "Paper", paperArt, paperMaterial, true);

                var words = Fill(root, "Words");
                Group(words);
                Label(words, "Chapter", "", font, Ink, new Vector2(.5f, .932f), new Vector2(1040, 70), 52);
                Label(words, "Caption", "", font, Ink, new Vector2(.5f, .121f), new Vector2(1345, 75), 36);
                Label(words, "Unavailable", "", font, Ink, new Vector2(.5f, .529f), new Vector2(1100, 110), 42);

                Button(root, "btnPrevious", "上一页", font, button, new Vector2(.085f, .515f), new Vector2(134, 62), 26);
                Button(root, "btnNext", "下一页", font, button, new Vector2(.915f, .515f), new Vector2(134, 62), 26);
                Button(root, "btnClose", "合上书", font, button, new Vector2(.908f, .93f), new Vector2(148, 58), 27);
                Label(root, "PageNumber", "", font, Ink, new Vector2(.5f, .05f), new Vector2(220, 42), 27);
                Label(root, "DragHint", "拖动纸角翻页", font, Ink, new Vector2(.21f, .05f), new Vector2(222, 36), 23);

                var notice = Node(root, "Notice", new Vector2(.5f, .803f), new Vector2(772, 76));
                Group(notice);
                ColorBlock(notice, "Frame", new Vector2(.5f, .5f), new Vector2(772, 76), Gold);
                ColorBlock(notice, "Fill", new Vector2(.5f, .5f), new Vector2(764, 68), Bronze);
                Label(notice, "Label", "", font, LightInk, new Vector2(.5f, .5f), new Vector2(742, 64), 34);
                notice.gameObject.SetActive(false);

                Raw(root, "Cover", coverArt, coverMaterial, true);
                var coverTitle = Fill(root, "CoverTitle");
                Group(coverTitle);
                var stamp = Label(coverTitle, "Title", "与鸦鸦的点滴", font, LightInk, new Vector2(.5f, .599f), new Vector2(1440, 153), 100);
                var shadow = stamp.gameObject.AddComponent<UnityEngine.UI.Shadow>();
                shadow.effectColor = new Color(.21f, .08f, .034f, .91f);
                shadow.effectDistance = new Vector2(2, -5);
                PrefabUtility.SaveAsPrefabAsset(frame, path);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(frame);
            }
        }

        /// <summary>按名修改已有预制体节点，保留交互路径、字型和原资源 GUID。</summary>
        private static void ApplyBookSkin(Sprite button, Sprite title)
        {
            const string path = Panel + "YayaMemoryBookPanel.prefab";
            var root = PrefabUtility.LoadPrefabContents(path);
            try
            {
                var words = (RectTransform)root.transform.Find("Words");
                var titleText = words.Find("Chapter").GetComponent<UnityEngine.UI.Text>();
                Vector2 center = new Vector2(.5f, .951f);
                var plate = words.Find("ChapterPlate")?.GetComponent<UnityEngine.UI.Image>()
                    ?? ColorBlock(words, "ChapterPlate", center, new Vector2(674, 81), Color.white);
                plate.sprite = title;
                plate.type = UnityEngine.UI.Image.Type.Sliced;
                plate.pixelsPerUnitMultiplier = 2f;
                plate.transform.SetAsFirstSibling();
                titleText.rectTransform.anchorMin = titleText.rectTransform.anchorMax = center;
                titleText.rectTransform.sizeDelta = new Vector2(600, 64);
                titleText.fontSize = 43;

                foreach (string node in new[] { "btnPrevious", "btnNext", "btnClose" })
                {
                    var image = root.transform.Find(node).GetComponent<UnityEngine.UI.Image>();
                    image.sprite = button;
                    image.type = UnityEngine.UI.Image.Type.Sliced;
                    image.pixelsPerUnitMultiplier = 4f;
                    image.rectTransform.sizeDelta = new Vector2(133, node == "btnClose" ? 49 : 56);
                    var label = image.transform.Find("Label").GetComponent<UnityEngine.UI.Text>();
                    label.color = LightInk;
                    label.fontSize = 25;
                }
                PrefabUtility.SaveAsPrefabAsset(root, path);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        private static T Require<T>(string path) where T : UnityEngine.Object
        {
            T value = AssetDatabase.LoadAssetAtPath<T>(path);
            if (value == null)
            {
                throw new InvalidOperationException("找不到收藏书资源：" + path);
            }
            return value;
        }

        private static RectTransform Node(Transform parent, string name, Vector2 anchor, Vector2 size)
        {
            var rect = (RectTransform)new GameObject(name, typeof(RectTransform)).transform;
            rect.SetParent(parent, false);
            rect.anchorMin = rect.anchorMax = anchor;
            rect.sizeDelta = size;
            rect.anchoredPosition = Vector2.zero;
            return rect;
        }

        private static RectTransform Fill(Transform parent, string name)
        {
            var rect = Node(parent, name, Vector2.zero, Vector2.zero);
            rect.anchorMax = Vector2.one;
            rect.offsetMin = rect.offsetMax = Vector2.zero;
            return rect;
        }

        private static void Group(RectTransform rect)
        {
            var group = rect.gameObject.AddComponent<CanvasGroup>();
            group.interactable = false;
            group.blocksRaycasts = false;
        }

        private static UnityEngine.UI.RawImage Raw(Transform parent, string name, Texture2D texture, Material material, bool blocks)
        {
            var result = Fill(parent, name).gameObject.AddComponent<UnityEngine.UI.RawImage>();
            result.texture = texture;
            result.material = material;
            result.raycastTarget = blocks;
            return result;
        }

        private static UnityEngine.UI.Image ColorBlock(Transform parent, string name, Vector2 anchor, Vector2 size, Color color)
        {
            var result = Node(parent, name, anchor, size).gameObject.AddComponent<UnityEngine.UI.Image>();
            result.color = color;
            result.raycastTarget = false;
            return result;
        }

        private static UnityEngine.UI.Text Label(Transform parent, string name, string text, Font font, Color color, Vector2 anchor,
            Vector2 size, int fontSize, TextAnchor alignment = TextAnchor.MiddleCenter)
        {
            var result = Node(parent, name, anchor, size).gameObject.AddComponent<UnityEngine.UI.Text>();
            result.text = text;
            result.font = font;
            result.fontSize = fontSize;
            result.color = color;
            result.alignment = alignment;
            result.verticalOverflow = VerticalWrapMode.Overflow;
            result.raycastTarget = false;
            return result;
        }

        private static UnityEngine.UI.Button Button(Transform parent, string name, string text, Font font, Sprite sprite,
            Vector2 anchor, Vector2 size, int fontSize)
        {
            var image = ColorBlock(parent, name, anchor, size, Color.white);
            image.sprite = sprite;
            image.raycastTarget = true;
            var button = image.gameObject.AddComponent<UnityEngine.UI.Button>();
            button.targetGraphic = image;
            var colors = button.colors;
            colors.highlightedColor = new Color(1f, .93f, .73f);
            colors.pressedColor = new Color(.84f, .69f, .49f);
            colors.disabledColor = new Color(.67f, .59f, .46f, .61f);
            button.colors = colors;
            Label(image.transform, "Label", text, font, Ink, new Vector2(.5f, .5f), size, fontSize);
            return button;
        }

        /// <summary>用临时键读写两次，验证独立通关、重新载入、已阅和重复通关。</summary>
        public static string CheckProgress()
        {
            string key = "RevGame.YayaMemories.Check." + Guid.NewGuid().ToString("N");
            try
            {
                var story = new StoryProgress(key + ".story");
                var first = new YayaMemoryProgress(story, key);
                first.MarkRead(2);
                first.RecordClear(2);
                first.RecordClear(6);
                var reopened = new YayaMemoryProgress(story, key);
                if (reopened.Cleared != 34 || reopened.Read != 0 || reopened.Unread != 34 || reopened.OpeningChapter() != 2)
                {
                    throw new InvalidOperationException("收藏存档的新解锁未正确还原");
                }
                reopened.MarkRead(2);
                reopened.RecordClear(2);
                var next = new YayaMemoryProgress(story, key);
                if (next.Unread != 32 || next.Read != 2 || next.OpeningChapter() != 6)
                {
                    throw new InvalidOperationException("已阅状态或重复通关记账失败");
                }
                return "首次通关、已阅、跨实例重新读取和重复通关全部通过";
            }
            finally
            {
                PlayerPrefs.DeleteKey(key);
                PlayerPrefs.Save();
            }
        }

        /// <summary>在隔离进度下分配一次验收专用的磁盘键，以便真实停 Play 再启动。</summary>
        public static string ArmRestartCheck()
        {
            if (!SessionState.GetBool("RevGame.Story.IsolatedTest", false))
            {
                throw new InvalidOperationException("重启验收需要先开启剧情隔离");
            }
            DisarmRestartCheck();
            string key = YayaMemoryProgress.RestartTestPrefix + Guid.NewGuid().ToString("N");
            SessionState.SetString(YayaMemoryProgress.RestartTestSession, key);
            return "下一次 Play 将使用隔离收藏存档";
        }

        public static string DisarmRestartCheck()
        {
            string key = SessionState.GetString(YayaMemoryProgress.RestartTestSession, "");
            if (key.StartsWith(YayaMemoryProgress.RestartTestPrefix, StringComparison.Ordinal))
            {
                PlayerPrefs.DeleteKey(key);
                PlayerPrefs.Save();
            }
            SessionState.EraseString(YayaMemoryProgress.RestartTestSession);
            return "收藏重启验收已清理";
        }
    }
}
