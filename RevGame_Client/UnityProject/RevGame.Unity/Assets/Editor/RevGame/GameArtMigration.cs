// ============================================================
// GameArtMigration.cs —— 通过 Unity MCP 导入并应用新版界面美术。
// 为什么单独保留：替换的不只是眼前的实例，还要保存预制体中的隐藏状态、手牌选中图和动态图标。
// 本工具只在编辑器执行；外部文件仅作为制作素材，运行时仍加载 GameRes 预制体及其序列化引用。
// ============================================================
using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;
using RevGame.View;
using RevGame.UI;
using RevGame.Core;
using Object = UnityEngine.Object;

namespace RevGame.Editor
{
    public static class GameArtMigration
    {
        public const string NewUi = "Assets/ArtRes/界面/新版/";
        public const string BackgroundPath = "Assets/ArtRes/背景/棋盘_开始场景_背景.png";
        private const string UiRoot = "Assets/GameRes/UI/";

        /// <summary>由 MCP 传入原版美术根目录；导入完成后才修改引用，避免先把界面替成空图。</summary>
        public static string ImportAndApply(string sourceRoot)
        {
            if (EditorApplication.isPlaying)
            {
                throw new InvalidOperationException("请先退出 Play，再保存美术替换，避免修改只留在运行时实例里。");
            }

            string sourceUi = Path.Combine(sourceRoot, "03_UI图片");
            string sourceBackground = Path.Combine(sourceRoot, "02_精灵图片/棋盘地图/棋盘_开始场景_背景.png");
            if (!Directory.Exists(sourceUi) || !File.Exists(sourceBackground))
            {
                throw new DirectoryNotFoundException("原版美术目录不完整：" + sourceRoot);
            }

            Folder(NewUi.TrimEnd('/'));
            Folder("Assets/ArtRes/背景");
            int imported = 0;
            foreach (string file in Directory.GetFiles(sourceUi, "*.png", SearchOption.AllDirectories))
            {
                // ★ UI_ 开头不代表新版。旧文件也有这个前缀，所以必须检查整个文件名是否全为英文/ASCII。
                string name = Path.GetFileName(file);
                if (!Regex.IsMatch(name, @"^[\x00-\x7F]+$"))
                {
                    continue;
                }

                ImportSprite(file, NewUi + name);
                imported++;
            }

            ImportSprite(sourceBackground, BackgroundPath);
            ApplyAll();
            return "已通过 Unity MCP 导入 " + imported + " 张英文新版 UI 和指定背景。\n" + Audit();
        }

        /// <summary>供原有生成器在最后调用：重新生成面板也必须继续使用新版皮肤。</summary>
        public static void ApplyAll()
        {
            foreach (string guid in AssetDatabase.FindAssets("t:Prefab", new[] { UiRoot.TrimEnd('/') }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                GameObject root = PrefabUtility.LoadPrefabContents(path);
                try
                {
                    ReplaceSerializedSprites(root);
                    StylePanel(root);
                    GameMobileLayout mobile = root.GetComponent<GameMobileLayout>();
                    if (mobile == null && root.GetComponent<Revolution.RevUIPanel>() != null)
                    {
                        mobile = root.AddComponent<GameMobileLayout>();
                    }
                    if (mobile != null) { mobile.Configure(root.name); }
                    PrefabUtility.SaveAsPrefabAsset(root, path);
                }
                finally
                {
                    // ★ 编辑内容加载在临时场景中，成功或失败都要卸载，不能在层级窗口遗留一份“幽灵面板”。
                    PrefabUtility.UnloadPrefabContents(root);
                }
            }

            ApplySceneBackground();
            ApplyBoardArt();
            StyleBattleEffects();
            ApplyTypography();
            AssetDatabase.SaveAssets();
        }

        /// <summary>只更新文字资产；由 Unity MCP 执行并保存，运行时无需逐帧扫描面板或动态创建字体。</summary>
        public static string ApplyTypography()
        {
            if (EditorApplication.isPlaying)
            {
                throw new InvalidOperationException("请先退出 Play 再保存字体，避免只修改临时界面而丢失预制体设置。");
            }
            Font font = GameAssetBuilder.Font();
            int count = 0;
            foreach (string guid in AssetDatabase.FindAssets("t:Prefab", new[] { "Assets/GameRes/UI", "Assets/GameRes/Effects" }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                GameObject root = PrefabUtility.LoadPrefabContents(path);
                try
                {
                    bool notice = root.GetComponent<BattleEventNoticeView>() != null;
                    foreach (Text text in root.GetComponentsInChildren<Text>(true))
                    {
                        // 深色提示底板用亮字；木牌、卡牌和弹窗用近黑字，不能一律改白后让浅底上的字消失。
                        bool light = notice || text.name == "hintText" || text.name == "assistText" || text.name == "eventText" || text.name == "bossText";
                        text.font = font;
                        text.fontStyle = FontStyle.Bold;
                        text.alignment = TextAnchor.MiddleCenter;
                        text.color = light ? new Color(1f, 0.98f, 0.92f) : new Color(0.055f, 0.04f, 0.025f);
                        text.horizontalOverflow = HorizontalWrapMode.Wrap;
                        text.verticalOverflow = VerticalWrapMode.Truncate;
                        // 描边只负责把文字边缘从木纹中分离。过粗描边会堵住“回、眼”等字的内部，所以只用细边。
                        Outline outline = text.GetComponent<Outline>();
                        if (outline == null) { outline = text.gameObject.AddComponent<Outline>(); }
                        outline.effectColor = light ? new Color(0.035f, 0.025f, 0.015f, 0.95f) : new Color(1f, 0.98f, 0.90f, 0.65f);
                        outline.effectDistance = light ? new Vector2(0.8f, -0.8f) : new Vector2(0.45f, -0.45f);
                        outline.useGraphicAlpha = true;
                        if (text.name == "label") { text.fontSize = 32; }
                        if (text.name == "detailText") { text.fontSize = 28; }
                        if (root.name == "BattleHudPanel" && text.name == "countText") { text.fontSize = 30; }
                        count++;
                    }
                    PrefabUtility.SaveAsPrefabAsset(root, path);
                }
                finally
                {
                    // 即使某张预制体保存失败，也要卸载临时编辑场景，不能多留一份界面在层级里。
                    PrefabUtility.UnloadPrefabContents(root);
                }
            }
            AssetDatabase.SaveAssets();
            return "已保存 " + count + " 个 UI 文字节点：中文黑体粗体、居中、深浅底对比色与细描边。";
        }

        /// <summary>查完整序列化引用，包括脚本数组及 Button 的选中/按下状态，避免只换 Image 漏掉动态切图。</summary>
        private static void ReplaceSerializedSprites(GameObject root)
        {
            foreach (MonoBehaviour component in root.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (component == null)
                {
                    throw new InvalidOperationException("预制体含丢失脚本：" + root.name);
                }

                var serialized = new SerializedObject(component);
                SerializedProperty property = serialized.GetIterator();
                while (property.NextVisible(true))
                {
                    if (property.propertyType != SerializedPropertyType.ObjectReference || !(property.objectReferenceValue is Sprite oldSprite))
                    {
                        continue;
                    }

                    string oldPath = AssetDatabase.GetAssetPath(oldSprite);
                    string replacement = ReplacementPath(oldPath);
                    if (replacement != oldPath)
                    {
                        property.objectReferenceValue = Load(replacement);
                    }
                }

                serialized.ApplyModifiedPropertiesWithoutUndo();
            }
        }

        private static string ReplacementPath(string path)
        {
            if (path.StartsWith(NewUi, StringComparison.Ordinal) || path == BackgroundPath)
            {
                return path;
            }

            string name = Path.GetFileNameWithoutExtension(path);
            switch (name)
            {
                case "选关_背景": return BackgroundPath;
                case "结算_背景":
                case "UI_三选一_弹窗底": return NewUi + "UI_Pvp_imgBK.png";
                case "UI_卡牌底_默认": return NewUi + "UI_roleItem_imgBK.png";
                case "UI_卡牌底_选中": return NewUi + "UI_roleItem_roleItem.png";
                case "UI_准备按钮_默认": return NewUi + "UI_Tavern_btnClose.png";
                case "UI_阶段指示_准备":
                case "UI_阶段指示_战斗":
                case "UI_阶段指示_结算": return NewUi + "UI_Tavern_Background.png";
                case "UI_回合进度条_底":
                case "UI_Boss血条框": return NewUi + "UI_Game_imgMobilityBK.png";
                case "UI_回合进度条_填充": return NewUi + "UI_Game_imgEnemyMobility.png";
                case "UI_战斗日志_弹条": return NewUi + "UI_Tip_imgBK.png";
            }

            // 新版没有独立的事件/奖励插画，用新版对勾表示通知，具体含义仍由原有中文标题和说明表达。
            if (name.StartsWith("图标_事件_", StringComparison.Ordinal) || name.StartsWith("图标_奖励_", StringComparison.Ordinal))
            {
                return NewUi + "UI_Setting_Checkmark.png";
            }

            return path;
        }

        private static void StylePanel(GameObject root)
        {
            foreach (Image image in root.GetComponentsInChildren<Image>(true))
            {
                if (!AssetDatabase.GetAssetPath(image.sprite).StartsWith(NewUi, StringComparison.Ordinal))
                {
                    continue;
                }

                image.color = Color.white;
                if (image.type != Image.Type.Filled)
                {
                    image.type = image.sprite.border == Vector4.zero ? Image.Type.Simple : Image.Type.Sliced;
                }

                image.preserveAspect = false;
            }

            if (root.name == "HomePanel" || root.name == "LevelSelectPanel")
            {
                AddScreenBackground(root);
                if (root.name == "HomePanel")
                {
                    Image plate = EnsurePicture(root, "contentPlate", "UI_Pvp_imgBK.png", new Vector2(1140, 660), Vector2.zero);
                    plate.transform.SetSiblingIndex(1);
                    Image play = root.transform.Find("btnPlay").GetComponent<Image>();
                    SetImage(play, "UI_Begin_btnAdventure.png", true);
                    play.transform.Find("label").gameObject.SetActive(false);
                    root.transform.Find("title").GetComponent<Text>().fontSize = 76;
                    root.transform.Find("title").GetComponent<RectTransform>().anchoredPosition = new Vector2(0, 190);
                    root.transform.Find("subtitle").GetComponent<RectTransform>().anchoredPosition = new Vector2(0, 85);
                    root.transform.Find("hint").GetComponent<RectTransform>().anchoredPosition = new Vector2(0, -220);
                    root.transform.Find("hint").GetComponent<RectTransform>().sizeDelta = new Vector2(740, 100);
                }
                else
                {
                    Image plate = EnsurePicture(root, "contentPlate", "UI_Pvp_imgBK.png", new Vector2(1660, 960), Vector2.zero);
                    plate.transform.SetSiblingIndex(1);
                    for (int i = 1; i <= 6; i++)
                    {
                        SetImage(root.transform.Find("chapter" + i).GetComponent<Image>(), "UI_Tavern_Background.png", false);
                    }

                    root.transform.Find("btnHome").GetComponent<RectTransform>().anchoredPosition = new Vector2(0, -385);
                }
            }
            else if (root.name == "SettlePanel" || root.name == "RewardPanel")
            {
                Image blocker = root.GetComponent<Image>();
                if (blocker == null)
                {
                    blocker = root.AddComponent<Image>();
                }

                blocker.sprite = null;
                blocker.color = new Color(0.06f, 0.04f, 0.025f, 0.7f);
                blocker.raycastTarget = true;
                Image background = root.transform.Find("background").GetComponent<Image>();
                SetImage(background, "UI_Pvp_imgBK.png", true);
                float width = root.name == "RewardPanel" ? 1380f : 1000f;
                background.rectTransform.sizeDelta = new Vector2(width, width * 571f / 1013f);
                if (root.name == "RewardPanel")
                {
                    for (int i = 0; i < 3; i++)
                    {
                        SetImage(root.transform.Find("choice" + i).GetComponent<Image>(), "UI_roleItem_imgBK.png", false);
                    }
                }
            }
            else if (root.name == "BattleHudPanel")
            {
                StyleHud(root);
            }
            else if (root.name == "PreparePanel")
            {
                // 已存在的预制体不会因为改常量自动长出第六张牌，必须一起迁移资产。
                // 使用含隐藏项的固定槽位，恢复激活仅用于保存模板；运行时由 Hand 数据决定显隐。
                Transform cards = root.transform.Find("cards");
                GameObject cardPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(UiRoot + "CardItem.prefab");
                while (cards.childCount < Consts.MaxHandSize)
                {
                    GameObject slot = (GameObject)PrefabUtility.InstantiatePrefab(cardPrefab, cards);
                    slot.name = "card" + (cards.childCount - 1);
                }
                foreach (Transform slot in cards) { slot.gameObject.SetActive(true); }
                // 老预制体也要补上刷新入口；此装配每次可重复执行，不会叠出多个按钮。
                if (root.transform.Find("btnRefresh") == null)
                {
                    Button refresh = GameAssetBuilder.MakeButton("btnRefresh", root.transform, "刷新手牌（6张）", new Vector2(330, 100), new Vector2(-205, 225));
                    GameAssetBuilder.Anchor(refresh.GetComponent<RectTransform>(), new Vector2(1, 0));
                }
                SetImage(root.transform.Find("btnRefresh").GetComponent<Image>(), "UI_Tavern_Background.png", false);
                RectTransform hint = root.transform.Find("hintText").GetComponent<RectTransform>();
                Image plate = EnsurePicture(root, "hintPlate", "UI_Tip_imgBK.png", hint.sizeDelta + new Vector2(24, 20), hint.anchoredPosition);
                GameAssetBuilder.Anchor(plate.rectTransform, hint.anchorMin);
                plate.preserveAspect = false;
                plate.transform.SetAsFirstSibling();
                hint.GetComponent<Text>().color = Color.white;
            }

            foreach (Text text in root.GetComponentsInChildren<Text>(true))
            {
                if (text.name == "stats")
                {
                    text.fontSize = 18;
                }
            }
        }

        private static void StyleHud(GameObject root)
        {
            if (root.transform.Find("assistText") == null)
            {
                Text assist = GameAssetBuilder.Label("assistText", root.transform, "", new Vector2(580, 70), new Vector2(-330, -175), 23);
                GameAssetBuilder.Anchor(assist.rectTransform, new Vector2(1, 1));
                assist.fontStyle = FontStyle.Bold;
                assist.color = Color.white;
                Image plate = EnsurePicture(root, "assistPlate", "UI_Tip_imgBK.png", new Vector2(610, 90), assist.rectTransform.anchoredPosition);
                GameAssetBuilder.Anchor(plate.rectTransform, new Vector2(1, 1));
                plate.preserveAspect = false;
                plate.transform.SetSiblingIndex(assist.transform.GetSiblingIndex());
            }

            Image phase = root.transform.Find("phaseIcon").GetComponent<Image>();
            phase.color = Color.white;
            if (root.transform.Find("phaseLabel") == null)
            {
                Text label = GameAssetBuilder.Label("phaseLabel", root.transform, "准备", phase.rectTransform.sizeDelta, phase.rectTransform.anchoredPosition, 28);
                GameAssetBuilder.Anchor(label.rectTransform, phase.rectTransform.anchorMin);
                label.fontStyle = FontStyle.Bold;
            }

            SetImage(root.transform.Find("progressBar").GetComponent<Image>(), "UI_Game_imgMobilityBK.png", false);
            SetImage(root.transform.Find("progressFill").GetComponent<Image>(), "UI_Game_imgEnemyMobility.png", false);
            if (root.transform.Find("bossFrame") == null)
            {
                return;
            }
            SetImage(root.transform.Find("bossFrame").GetComponent<Image>(), "UI_Game_imgMobilityBK.png", false);
            SetImage(root.transform.Find("bossBar").GetComponent<Image>(), "UI_Game_imgEnemyMobility.png", false);
            Image fill = root.transform.Find("bossBar").GetComponent<Image>();
            fill.color = new Color(0.85f, 0.25f, 0.15f);
            fill.type = Image.Type.Filled;
            fill.fillMethod = Image.FillMethod.Horizontal;
            fill.preserveAspect = false;
            Image backdrop = root.transform.Find("bossFrame/backdrop").GetComponent<Image>();
            backdrop.color = Color.clear;
            root.transform.Find("notice/eventText").GetComponent<Text>().color = Color.white;

            // 左侧文字原来直接落在纯色相机底上。换成场景画后补木牌，避免背景纹理盖住信息。
            Image info = EnsurePicture(root, "infoPlate", "UI_Pvp_imgBK.png", new Vector2(390, 330), new Vector2(205, 60));
            info.preserveAspect = false;
            GameAssetBuilder.Anchor(info.rectTransform, new Vector2(0, 0.5f));
            info.transform.SetAsFirstSibling();
        }

        private static void AddScreenBackground(GameObject root)
        {
            // ★ 背景缩放器只挂到图片子节点，不能挂在面板根上，否则按钮和文字会跟着背景一起变形。
            Image oldRootImage = root.GetComponent<Image>();
            if (oldRootImage != null)
            {
                Object.DestroyImmediate(oldRootImage);
            }

            Transform existing = root.transform.Find("screenBackground");
            Image image = existing == null ? GameAssetBuilder.Picture("screenBackground", root.transform, Load(BackgroundPath), Vector2.zero, Vector2.zero) : existing.GetComponent<Image>();
            image.sprite = Load(BackgroundPath);
            image.color = Color.white;
            image.raycastTarget = false;
            image.type = Image.Type.Simple;
            image.preserveAspect = false;
            image.transform.SetAsFirstSibling();
            AspectRatioFitter fit = image.GetComponent<AspectRatioFitter>();
            if (fit == null)
            {
                fit = image.gameObject.AddComponent<AspectRatioFitter>();
            }

            fit.aspectRatio = image.sprite.rect.width / image.sprite.rect.height;
            fit.aspectMode = AspectRatioFitter.AspectMode.EnvelopeParent;
        }

        private static Image EnsurePicture(GameObject root, string name, string sprite, Vector2 size, Vector2 position)
        {
            Transform existing = root.transform.Find(name);
            Image image = existing == null ? GameAssetBuilder.Picture(name, root.transform, Load(NewUi + sprite), size, position) : existing.GetComponent<Image>();
            image.rectTransform.sizeDelta = size;
            image.rectTransform.anchoredPosition = position;
            SetImage(image, sprite, true);
            return image;
        }

        private static void SetImage(Image image, string name, bool preserveAspect)
        {
            bool filled = image.type == Image.Type.Filled;
            image.sprite = Load(NewUi + name);
            image.overrideSprite = null;
            image.color = Color.white;
            image.preserveAspect = preserveAspect;
            image.type = filled ? Image.Type.Filled : image.sprite.border == Vector4.zero ? Image.Type.Simple : Image.Type.Sliced;
        }

        private static void ApplySceneBackground()
        {
            GameObject host = GameObject.Find("GamePresentation");
            if (host == null)
            {
                throw new InvalidOperationException("当前场景缺少 GamePresentation，请先打开游戏的 SampleScene。");
            }

            GameObject background = GameObject.Find("GameBackground");
            if (background == null)
            {
                background = new GameObject("GameBackground");
            }

            SpriteRenderer renderer = background.GetComponent<SpriteRenderer>();
            if (renderer == null)
            {
                renderer = background.AddComponent<SpriteRenderer>();
            }

            renderer.sprite = Load(BackgroundPath);
            renderer.sortingOrder = -1000;
            renderer.color = Color.white;
            Camera camera = Camera.main;
            Vector2 sourceSize = renderer.sprite.bounds.size;
            float height = camera.orthographicSize * 2f;
            float scale = Mathf.Max(height * camera.aspect / sourceSize.x, height / sourceSize.y);
            background.transform.localScale = Vector3.one * scale;
            background.transform.position = new Vector3(camera.transform.position.x, camera.transform.position.y, 5f);
            GameAssetBuilder.SetReference(host.GetComponent<GamePresentation>(), "backgroundRenderer", renderer);
            EditorSceneManager.MarkSceneDirty(host.scene);
            EditorSceneManager.SaveScene(host.scene);
        }

        /// <summary>用户确认恢复逐格拼接：一个逻辑格就是一个世界单位，图像、高亮与点击区域共用同一坐标。</summary>
        private static void ApplyBoardArt()
        {
            const string cellPath = "Assets/GameRes/Board/BoardCell.prefab";
            GameObject cell = PrefabUtility.LoadPrefabContents(cellPath);
            try
            {
                SpriteRenderer body = cell.GetComponent<SpriteRenderer>();
                body.sprite = Load("Assets/ArtRes/棋盘/棋盘_单格底_平铺用.png");
                body.drawMode = SpriteDrawMode.Sliced;
                body.size = Vector2.one * Consts.CellSize;
                body.color = Color.white;
                cell.GetComponent<BoxCollider2D>().size = Vector2.one * Consts.CellSize;
                SpriteRenderer star = cell.transform.Find("星位").GetComponent<SpriteRenderer>();
                star.sprite = Load("Assets/ArtRes/棋盘/棋盘_星位点.png");
                star.drawMode = SpriteDrawMode.Simple;
                star.transform.localScale = Vector3.one;
                star.color = Color.white;
                SpriteRenderer highlight = cell.transform.Find("可落点").GetComponent<SpriteRenderer>();
                highlight.sprite = Load("Assets/ArtRes/棋盘/棋盘_可落子高亮.png");
                highlight.drawMode = SpriteDrawMode.Sliced;
                highlight.size = Vector2.one * Consts.CellSize;
                highlight.color = Color.white;
                SpriteRenderer eye = cell.transform.Find("眼").GetComponent<SpriteRenderer>();
                eye.sprite = Load(NewUi + "UI_Setting_Checkmark.png");
                eye.transform.localScale = Vector3.one * 0.35f;
                PrefabUtility.SaveAsPrefabAsset(cell, cellPath);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(cell);
            }

            GamePresentation presentation = Object.FindObjectOfType<GamePresentation>();
            var serialized = new SerializedObject(presentation);
            GameObject board = (GameObject)serialized.FindProperty("boardRoot").objectReferenceValue;
            Transform existing = board.transform.Find("boardArt");
            if (existing != null)
            {
                Object.DestroyImmediate(existing.gameObject);
            }
            EditorSceneManager.MarkSceneDirty(board.scene);
            EditorSceneManager.SaveScene(board.scene);
        }

        /// <summary>通过 MCP 保存弹道和大号描边数字的预制体，运行时只做池化播放。</summary>
        private static void StyleBattleEffects()
        {
            const string path = "Assets/GameRes/Effects/BattleEffect.prefab";
            GameObject root = PrefabUtility.LoadPrefabContents(path);
            try
            {
                BattleEffectView effect = root.GetComponent<BattleEffectView>();
                GameAssetBuilder.SetReference(effect, "projectileSprite", Load(NewUi + "UI_Game_imgEnemyMobility.png"));
                TextMesh text = root.transform.Find("damage").GetComponent<TextMesh>();
                text.fontSize = 80;
                text.characterSize = 0.1f;
                text.fontStyle = FontStyle.Bold;
                text.GetComponent<MeshRenderer>().sortingOrder = 90;
                var so = new SerializedObject(effect);
                SerializedProperty outlines = so.FindProperty("damageOutlines");
                outlines.arraySize = 4;
                Vector2[] offsets = { new Vector2(-0.025f, 0f), new Vector2(0.025f, 0f), new Vector2(0f, -0.025f), new Vector2(0f, 0.025f) };
                for (int i = 0; i < offsets.Length; i++)
                {
                    Transform child = text.transform.Find("outline" + i);
                    GameObject go = child == null ? new GameObject("outline" + i) : child.gameObject;
                    go.transform.SetParent(text.transform, false);
                    go.transform.localPosition = offsets[i];
                    TextMesh outline = go.GetComponent<TextMesh>();
                    if (outline == null)
                    {
                        outline = go.AddComponent<TextMesh>();
                    }

                    outline.font = text.font;
                    outline.fontSize = text.fontSize;
                    outline.characterSize = text.characterSize;
                    outline.fontStyle = text.fontStyle;
                    outline.anchor = text.anchor;
                    MeshRenderer renderer = outline.GetComponent<MeshRenderer>();
                    renderer.sharedMaterial = text.font.material;
                    renderer.sortingOrder = 89;
                    outlines.GetArrayElementAtIndex(i).objectReferenceValue = outline;
                }

                so.ApplyModifiedPropertiesWithoutUndo();
                PrefabUtility.SaveAsPrefabAsset(root, path);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        /// <summary>交付检查：UI 内除棋子立绘外，不能继续引用旧版界面/图标文件夹中的图片。</summary>
        public static string Audit()
        {
            int count = 0;
            int prefabs = 0;
            var failures = new StringBuilder();
            var paths = new System.Collections.Generic.List<string>();
            foreach (string guid in AssetDatabase.FindAssets("t:Prefab", new[] { UiRoot.TrimEnd('/') }))
            {
                paths.Add(AssetDatabase.GUIDToAssetPath(guid));
            }
            // 升级/增益提示属于池化 UI 零件，放在 Effects 下，也必须纳入新版美术引用检查。
            const string notice = "Assets/GameRes/Effects/BattleEventNotice.prefab";
            if (AssetDatabase.LoadAssetAtPath<GameObject>(notice) != null) { paths.Add(notice); }
            foreach (string prefabPath in paths)
            {
                prefabs++;
                GameObject root = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
                foreach (MonoBehaviour component in root.GetComponentsInChildren<MonoBehaviour>(true))
                {
                    var property = new SerializedObject(component).GetIterator();
                    while (property.NextVisible(true))
                    {
                        if (property.propertyType != SerializedPropertyType.ObjectReference || !(property.objectReferenceValue is Sprite))
                        {
                            continue;
                        }

                        count++;
                        string path = AssetDatabase.GetAssetPath(property.objectReferenceValue);
                        bool oldUi = path.StartsWith("Assets/ArtRes/界面/", StringComparison.Ordinal) && !path.StartsWith(NewUi, StringComparison.Ordinal);
                        if (oldUi || path.StartsWith("Assets/ArtRes/图标/", StringComparison.Ordinal))
                        {
                            failures.AppendLine(root.name + "/" + component.name + ": " + path);
                        }
                    }
                }
            }

            if (failures.Length > 0)
            {
                throw new InvalidOperationException("仍有旧版 UI 图片引用：\n" + failures);
            }

            return "已检查 " + prefabs + " 个界面/提示预制体，共 " + count + " 个 Sprite 引用；旧版 UI 引用为 0。";
        }

        public static Sprite Load(string path)
        {
            Sprite sprite = AssetDatabase.LoadAssetAtPath<Sprite>(path);
            if (sprite == null)
            {
                throw new InvalidOperationException("美术未导入或不是 Sprite：" + path);
            }

            return sprite;
        }

        private static void ImportSprite(string source, string target)
        {
            File.Copy(source, target, true);
            AssetDatabase.ImportAsset(target, ImportAssetOptions.ForceSynchronousImport);
            var importer = (TextureImporter)AssetImporter.GetAtPath(target);
            importer.textureType = TextureImporterType.Sprite;
            importer.spriteImportMode = SpriteImportMode.Single;
            importer.spritePixelsPerUnit = 100f;
            importer.alphaIsTransparency = true;
            importer.mipmapEnabled = false;
            importer.npotScale = TextureImporterNPOTScale.None;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.maxTextureSize = 4096;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            string name = Path.GetFileNameWithoutExtension(target);
            if (name == "UI_Tavern_btnClose")
            {
                importer.spriteBorder = new Vector4(32, 25, 32, 25);
            }
            else if (name == "UI_roleItem_imgBK" || name == "UI_roleItem_roleItem")
            {
                importer.spriteBorder = new Vector4(16, 16, 16, 16);
            }
            else if (name == "UI_Tavern_Background" || name == "UI_Tavern_Checkmark")
            {
                importer.spriteBorder = new Vector4(24, 20, 24, 20);
            }
            if (name == "UI_Game_imgEnemyMobility")
            {
                // 此条形图也用于弹道九宫格。FullRect 保证重导美术后仍可拉伸，不再出现 Tiling 警告。
                var settings = new TextureImporterSettings();
                importer.ReadTextureSettings(settings);
                settings.spriteMeshType = SpriteMeshType.FullRect;
                importer.SetTextureSettings(settings);
            }

            importer.SaveAndReimport();
        }

        private static void Folder(string path)
        {
            if (AssetDatabase.IsValidFolder(path))
            {
                return;
            }

            string parent = Path.GetDirectoryName(path).Replace('\\', '/');
            Folder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
        }
    }
}
