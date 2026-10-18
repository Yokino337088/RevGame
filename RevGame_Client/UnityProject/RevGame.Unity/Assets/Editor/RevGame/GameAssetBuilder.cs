// ============================================================
// GameAssetBuilder.cs —— 可重复执行的游戏预制体装配工具（仅编辑器）。
// 为什么保留工具：节点名就是 RevBind 的契约，用同一份代码生成可避免手工拖引用漏接。
// 通过 Unity MCP 调用 BuildP0；运行时不扫描磁盘，也不依赖 AssetDatabase。
// ============================================================
using RevGame.UI;
using RevGame.View;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace RevGame.Editor
{
    public static class GameAssetBuilder
    {
        private const string Art = "Assets/ArtRes/";
        private const string Ui = "Assets/GameRes/UI/Panel/";
        private static readonly Color Ink = new Color(0.055f, 0.04f, 0.025f);
        public static string BuildP2()
        {
            GameObject root = Rect("HomePanel", null, Vector2.zero, Vector2.zero);
            Stretch(root.GetComponent<RectTransform>());
            Image background = root.AddComponent<Image>();
            background.sprite = Sprite("背景/棋盘_开始场景_背景.png");
            background.color = new Color(0.92f, 0.9f, 0.82f);
            root.AddComponent<HomePanel>();
            Label("title", root.transform, "围棋史莱姆", new Vector2(1100, 130), new Vector2(0, 260), 82);
            Label("subtitle", root.transform, "落子布阵 · 连成一片 · 围住主将", new Vector2(1100, 60), new Vector2(0, 135), 32);
            MakeButton("btnPlay", root.transform, "开始闯关", new Vector2(370, 110), new Vector2(0, -70));
            Label("hint", root.transform, "点手牌，再点棋盘。准备完毕后，棋子自动作战。", new Vector2(1250, 60), new Vector2(0, -260), 30);
            Save(root, "HomePanel");
            root = Rect("LevelSelectPanel", null, Vector2.zero, Vector2.zero);
            Stretch(root.GetComponent<RectTransform>());
            background = root.AddComponent<Image>();
            background.sprite = Sprite("背景/棋盘_开始场景_背景.png");
            root.AddComponent<LevelSelectPanel>();
            Label("title", root.transform, "选择章节", new Vector2(1000, 100), new Vector2(0, 345), 58);
            for (int i = 0; i < 6; i++)
            {
                MakeButton("chapter" + (i + 1), root.transform, "第 " + (i + 1) + " 章", new Vector2(350, 180), new Vector2((i % 3 - 1) * 400, 130 - (i / 3) * 220));
            }

            Label("hint", root.transform, "", new Vector2(1100, 100), new Vector2(0, -290), 30);
            MakeButton("btnHome", root.transform, "返回主界面", new Vector2(300, 80), new Vector2(0, -420));
            Save(root, "LevelSelectPanel");
            root = PrefabUtility.LoadPrefabContents(Ui + "BattleHudPanel.prefab");
            try
            {
                if (root.transform.Find("btnHome") == null)
                {
                    var button = MakeButton("btnHome", root.transform, "返回主界面", new Vector2(260, 70), new Vector2(-160, -55));
                    Anchor(button.GetComponent<RectTransform>(), new Vector2(1, 1));
                }

                PrefabUtility.SaveAsPrefabAsset(root, Ui + "BattleHudPanel.prefab");
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }

            GameObject board = null;
            foreach (GameObject candidate in UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects())
            {
                if (candidate.name == "BoardView")
                {
                    board = candidate;
                    break;
                }
            }

            if (board == null)
            {
                throw new System.InvalidOperationException("场景缺少 BoardView 根节点");
            }

            board.SetActive(false);
            GameObject host = GameObject.Find("GamePresentation");
            if (host == null)
            {
                host = new GameObject("GamePresentation");
            }

            GamePresentation presentation = host.GetComponent<GamePresentation>();
            if (presentation == null)
            {
                presentation = host.AddComponent<GamePresentation>();
            }

            SetReference(presentation, "boardRoot", board);
            SetReference(presentation, "boardCamera", Camera.main);
            EditorSceneManager.MarkSceneDirty(UnityEngine.SceneManagement.SceneManager.GetActiveScene());
            EditorSceneManager.SaveScene(UnityEngine.SceneManagement.SceneManager.GetActiveScene());
            AssetDatabase.SaveAssets();
            GameArtMigration.ApplyAll();
            return "主界面、六章选关、返回按钮、场景流程接线已保存";
        }

        public static string BuildP1()
        {
            BuildCards();
            var root = Rect("RewardPanel", null, Vector2.zero, Vector2.zero);
            Stretch(root.GetComponent<RectTransform>());
            var blocker = root.AddComponent<Image>();
            blocker.color = new Color(0.09f, 0.07f, 0.04f, 0.85f);
            Picture("background", root.transform, Sprite("界面/新版/UI_Pvp_imgBK.png"), new Vector2(1380, 778), Vector2.zero);
            Label("heading", root.transform, "本章通关 · 选择一份奖励", new Vector2(1050, 80), new Vector2(0, 245), 40);
            for (int i = 0; i < 3; i++)
            {
                Button button = MakeButton("choice" + i, root.transform, "奖励", new Vector2(340, 350), new Vector2((i - 1) * 380, -20));
                RectTransform label = (RectTransform)button.transform.Find("label");
                label.sizeDelta = new Vector2(300, 190);
                label.anchoredPosition = new Vector2(0, -60);
                Picture("icon", button.transform, null, new Vector2(100, 100), new Vector2(0, 100));
            }

            RewardPanel panel = root.AddComponent<RewardPanel>();
            var so = new SerializedObject(panel);
            var icons = so.FindProperty("rewardIcons");
            icons.arraySize = 4;
            string[] names =
            {
                "全队强化",
                "同名牌",
                "新棋子",
                "眼位石"
            };
            for (int i = 0; i < names.Length; i++)
            {
                icons.GetArrayElementAtIndex(i).objectReferenceValue = Sprite("界面/新版/UI_Setting_Checkmark.png");
            }

            so.ApplyModifiedPropertiesWithoutUndo();
            Save(root, "RewardPanel");
            const string path = "Assets/GameRes/Pieces/Unit/SlimePiece.prefab";
            root = PrefabUtility.LoadPrefabContents(path);
            try
            {
                var piece = root.GetComponent<PieceView>();
                // Boss身体播放图鉴中的真实序列帧，构建器只装配王冠和光环，不再写静态替换图。
                var crown = SpriteChild(root, "王冠", "界面/UI_王冠.png", 25);
                crown.transform.localPosition = new Vector3(0, 0.8f, 0);
                crown.enabled = false;
                var halo = SpriteChild(root, "大棋光环", "棋子/棋子_Boss_底座光环.png", 9);
                halo.enabled = false;
                SetReference(piece, "crownRenderer", crown);
                SetReference(piece, "haloRenderer", halo);
                PrefabUtility.SaveAsPrefabAsset(root, path);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }

            AssetDatabase.SaveAssets();
            GameArtMigration.ApplyAll();
            return "奖励、眼位石/跳过按钮、Boss 外观已保存";
        }

        /// <summary>只装配本项目拥有的资源，不修改框架快照。</summary>
        public static string BuildP0()
        {
            BuildCell();
            BuildCards();
            LayoutHud();
            LayoutSettle();
            if (!AssetDatabase.IsValidFolder("Assets/Editor"))
            {
                AssetDatabase.CreateFolder("Assets", "Editor");
            }

            const string configPath = "Assets/Editor/ABBuildConfig.asset";
            var config = AssetDatabase.LoadAssetAtPath<ScriptableObject>(configPath);
            if (config == null)
            {
                // 框架编辑器程序集不自动引用到 Assembly-CSharp-Editor，资产仍通过公开序列化接口编辑。
                config = ScriptableObject.CreateInstance("ABBuildConfig");
                AssetDatabase.CreateAsset(config, configPath);
            }

            var serializedConfig = new SerializedObject(config);
            serializedConfig.FindProperty("resRoot").stringValue = "Assets/GameRes/";
            serializedConfig.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(config);
            AssetDatabase.SaveAssets();
            GameArtMigration.ApplyAll();
            return "棋盘格、卡牌、准备/HUD/结算布局和持久资源根目录已保存";
        }

        private static void BuildCell()
        {
            const string path = "Assets/GameRes/Board/Tile/BoardCell.prefab";
            GameObject root = PrefabUtility.LoadPrefabContents(path);
            try
            {
                root.GetComponent<SpriteRenderer>().transform.localScale = Vector3.one;
                SpriteRenderer body = root.GetComponent<SpriteRenderer>();
                // 图片是 64 像素、100 PPU，原始宽仅 0.64；用 size 将它严格铺成一个世界单位。
                body.drawMode = SpriteDrawMode.Sliced;
                body.size = Vector2.one;
                var collider = root.GetComponent<BoxCollider2D>();
                if (collider == null)
                {
                    collider = root.AddComponent<BoxCollider2D>();
                }

                collider.size = Vector2.one;
                var view = root.GetComponent<BoardCellView>();
                if (view == null)
                {
                    view = root.AddComponent<BoardCellView>();
                }

                SpriteRenderer star = SpriteChild(root, "星位", "棋盘/棋盘_星位点.png", 1);
                SpriteRenderer highlight = SpriteChild(root, "可落点", "棋盘/棋盘_可落子高亮.png", 5);
                highlight.drawMode = SpriteDrawMode.Sliced;
                highlight.size = Vector2.one;
                SpriteRenderer eye = SpriteChild(root, "眼", "状态标记/标记_眼_金色.png", 6);
                SetReference(view, "starRenderer", star);
                SetReference(view, "highlightRenderer", highlight);
                SetReference(view, "eyeRenderer", eye);
                highlight.enabled = false;
                eye.enabled = false;
                PrefabUtility.SaveAsPrefabAsset(root, path);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        private static SpriteRenderer SpriteChild(GameObject root, string name, string path, int order)
        {
            Transform existing = root.transform.Find(name);
            GameObject child = existing != null ? existing.gameObject : new GameObject(name);
            child.transform.SetParent(root.transform, false);
            var renderer = child.GetComponent<SpriteRenderer>();
            if (renderer == null)
            {
                renderer = child.AddComponent<SpriteRenderer>();
            }

            renderer.sprite = AssetDatabase.LoadAssetAtPath<Sprite>(GameArtPaths.Resolve(Art + path));
            renderer.sortingOrder = order;
            return renderer;
        }

        private static void BuildCards()
        {
            GameObject card = Rect("CardItem", null, new Vector2(180, 178), Vector2.zero);
            Image background = card.AddComponent<Image>();
            background.sprite = Sprite("界面/新版/UI_roleItem_imgBK.png");
            Button button = card.AddComponent<Button>();
            button.targetGraphic = background;
            Image portrait = Picture("portrait", card.transform, null, new Vector2(96, 96), new Vector2(0, 15));
            Text title = Label("title", card.transform, "棋子", new Vector2(170, 30), new Vector2(0, -42), 23);
            Text stats = Label("stats", card.transform, "生命 / 攻击", new Vector2(176, 26), new Vector2(0, -70), 15);
            CardItem item = card.AddComponent<CardItem>();
            SetReference(item, "button", button);
            SetReference(item, "background", background);
            SetReference(item, "portrait", portrait);
            SetReference(item, "title", title);
            SetReference(item, "stats", stats);
            SetReference(item, "normalSprite", background.sprite);
            SetReference(item, "selectedSprite", Sprite("界面/新版/UI_roleItem_roleItem.png"));
            SetReference(item, "artConfig", AssetDatabase.LoadAssetAtPath<PieceArtConfig>("Assets/GameRes/Config/Art/PieceArt.asset"));
            GameObject prefab = PrefabUtility.SaveAsPrefabAsset(card, "Assets/GameRes/UI/Item/Card/CardItem.prefab");
            Object.DestroyImmediate(card);
            GameObject root = Rect("PreparePanel", null, Vector2.zero, Vector2.zero);
            Stretch(root.GetComponent<RectTransform>());
            root.AddComponent<PreparePanel>();
            Text hint = Label("hintText", root.transform, "选择手牌", new Vector2(900, 64), new Vector2(0, 228), 25);
            Anchor(hint.rectTransform, new Vector2(0.5f, 0));
            GameObject row = Rect("cards", root.transform, new Vector2(960, 178), new Vector2(0, 105));
            Anchor(row.GetComponent<RectTransform>(), new Vector2(0.5f, 0));
            var layout = row.AddComponent<HorizontalLayoutGroup>();
            layout.spacing = 14;
            layout.childAlignment = TextAnchor.MiddleCenter;
            layout.childControlWidth = false;
            layout.childControlHeight = false;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = false;
            for (int i = 0; i < Core.Consts.MaxHandSize; i++)
            {
                var slot = (GameObject)PrefabUtility.InstantiatePrefab(prefab, row.transform);
                slot.name = "card" + i;
            }

            Button ready = MakeButton("btnReady", root.transform, "准备完毕", new Vector2(240, 80), new Vector2(-165, 105));
            Anchor(ready.GetComponent<RectTransform>(), new Vector2(1, 0));
            Button skip = MakeButton("btnSkip", root.transform, "跳过 · 攻击 +10%", new Vector2(240, 64), new Vector2(-165, 195));
            Anchor(skip.GetComponent<RectTransform>(), new Vector2(1, 0));
            Button eye = MakeButton("btnEye", root.transform, "眼位石", new Vector2(230, 70), new Vector2(165, 105));
            Anchor(eye.GetComponent<RectTransform>(), new Vector2(0, 0));
            Button refresh = MakeButton("btnRefresh", root.transform, "刷新手牌（6张）", new Vector2(330, 100), new Vector2(-205, 225));
            Anchor(refresh.GetComponent<RectTransform>(), new Vector2(1, 0));
            Save(root, "PreparePanel");
        }

        private static void LayoutHud()
        {
            GameObject root = PrefabUtility.LoadPrefabContents(Ui + "BattleHudPanel.prefab");
            try
            {
                Stretch(root.GetComponent<RectTransform>());
                foreach (var graphic in root.GetComponentsInChildren<Graphic>(true))
                {
                    graphic.raycastTarget = false;
                }

                Move(root, "phaseIcon", new Vector2(0.5f, 1), new Vector2(-370, -55), new Vector2(170, 60));
                Move(root, "roundText", new Vector2(0.5f, 1), new Vector2(0, -50), new Vector2(560, 55));
                Move(root, "progressBar", new Vector2(0.5f, 1), new Vector2(0, -99), new Vector2(700, 18));
                Move(root, "progressFill", new Vector2(0.5f, 1), new Vector2(0, -99), new Vector2(700, 18));
                Move(root, "countText", new Vector2(0, 0.5f), new Vector2(205, 130), new Vector2(360, 70));
                Move(root, "logText", new Vector2(0, 0.5f), new Vector2(205, 0), new Vector2(360, 160));
                foreach (var text in root.GetComponentsInChildren<Text>())
                {
                    text.font = Font();
                    text.color = Ink;
                    text.fontSize = 28;
                    text.alignment = TextAnchor.MiddleCenter;
                }

                PrefabUtility.SaveAsPrefabAsset(root, Ui + "BattleHudPanel.prefab");
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        private static void LayoutSettle()
        {
            GameObject root = PrefabUtility.LoadPrefabContents(Ui + "SettlePanel.prefab");
            try
            {
                Stretch(root.GetComponent<RectTransform>());
                Move(root, "background", Vector2.one * 0.5f, Vector2.zero, new Vector2(800, 500));
                Move(root, "titleText", Vector2.one * 0.5f, new Vector2(0, 130), new Vector2(650, 100));
                Move(root, "detailText", Vector2.one * 0.5f, new Vector2(0, 20), new Vector2(650, 120));
                Move(root, "btnContinue", Vector2.one * 0.5f, new Vector2(0, -145), new Vector2(280, 80));
                foreach (var text in root.GetComponentsInChildren<Text>())
                {
                    text.font = Font();
                    text.color = Ink;
                }

                PrefabUtility.SaveAsPrefabAsset(root, Ui + "SettlePanel.prefab");
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        public static GameObject Rect(string name, Transform parent, Vector2 size, Vector2 position)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var rect = go.GetComponent<RectTransform>();
            rect.sizeDelta = size;
            rect.anchoredPosition = position;
            return go;
        }

        public static Text Label(string name, Transform parent, string value, Vector2 size, Vector2 position, int fontSize)
        {
            Text text = Rect(name, parent, size, position).AddComponent<Text>();
            text.font = Font();
            text.text = value;
            text.fontSize = fontSize;
            text.fontStyle = FontStyle.Bold;
            text.color = Ink;
            text.alignment = TextAnchor.MiddleCenter;
            text.raycastTarget = false;
            return text;
        }

        public static Image Picture(string name, Transform parent, Sprite sprite, Vector2 size, Vector2 position)
        {
            Image image = Rect(name, parent, size, position).AddComponent<Image>();
            image.sprite = sprite;
            image.preserveAspect = true;
            image.raycastTarget = false;
            return image;
        }

        public static Button MakeButton(string name, Transform parent, string text, Vector2 size, Vector2 position)
        {
            GameObject go = Rect(name, parent, size, position);
            Image image = go.AddComponent<Image>();
            image.sprite = Sprite("界面/新版/UI_Tavern_btnClose.png");
            image.color = new Color(0.9f, 0.82f, 0.63f);
            Button button = go.AddComponent<Button>();
            button.targetGraphic = image;
            Label("label", go.transform, text, size - new Vector2(12, 8), Vector2.zero, 28);
            return button;
        }

        public static void Anchor(RectTransform rect, Vector2 anchor)
        {
            rect.anchorMin = anchor;
            rect.anchorMax = anchor;
        }

        public static void Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }

        private static void Move(GameObject root, string child, Vector2 anchor, Vector2 pos, Vector2 size)
        {
            var rect = (RectTransform)root.transform.Find(child);
            Anchor(rect, anchor);
            rect.anchoredPosition = pos;
            rect.sizeDelta = size;
        }

        public static Sprite Sprite(string relativePath)
        {
            // 制作工具保留原来的素材分类参数；新版UI实际已进入可共享分包的GameRes/Art/UI。
            if (relativePath.StartsWith("界面/新版/", System.StringComparison.Ordinal))
            {
                return AssetDatabase.LoadAssetAtPath<Sprite>(GameArtMigration.NewUi + relativePath.Substring("界面/新版/".Length));
            }
            return AssetDatabase.LoadAssetAtPath<Sprite>(GameArtPaths.Resolve(Art + relativePath));
        }

        public static Font Font()
        {
            // 使用随工程保存的开源中文黑体粗体，而不是依赖开发电脑安装的系统字体。
            // 原明朝体的细笔画在手机缩小显示时容易融进木纹；真正的粗体字形能保留清楚的主笔画。
            Font font = AssetDatabase.LoadAssetAtPath<Font>("Assets/GameRes/Fonts/CJK/NotoSansCJKsc-Bold.otf");
            if (font == null)
            {
                throw new System.InvalidOperationException("缺少 UI 粗体字体，请先通过 Unity MCP 导入 NotoSansCJKsc-Bold.otf 及 OFL 许可。");
            }
            return font;
        }

        public static void SetReference(Object target, string field, Object value)
        {
            var serialized = new SerializedObject(target);
            serialized.FindProperty(field).objectReferenceValue = value;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        public static void Save(GameObject root, string name)
        {
            PrefabUtility.SaveAsPrefabAsset(root, Ui + name + ".prefab");
            Object.DestroyImmediate(root);
        }
    }
}
