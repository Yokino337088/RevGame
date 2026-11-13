using System;
using RevGame.Pvp;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace RevGame.Editor
{
    // 红青裂隙主题的选角与加载由同一配方重建，只写两张目标 Prefab、纹理导入参数与共享材质。
    public static partial class GamePvpBuild
    {
        // 本地项目美术进入独立图集，两个面板不引用决斗内置场景的散图。
        private const string PrepArt = "Assets/GameRes/Art/UI/PvpDuelPrep/";
        private const string PrepPanels = "Assets/GameRes/UI/Panel/Pvp/DuelPrep";
        // 1920×1080 设计单位。绯焰原图的头顶留白更大，补足后两人的可见身高接近。
        private const float SelectEmberPortraitSize = 816f;
        private const float SelectYayaPortraitSize = 765f;
        // 准备页的两份专用材质归 Material，分包仍跟随选角和加载页的生命周期。
        private const string PrepMaterials = "Assets/GameRes/Material/UI/PvpDuelPrep";
        // 黑白席颜色相同贯穿选角、加载和镜像战斗；编号提供第二种识别手段。
        private static readonly Color PrepWarm = new Color(1f, .52f, .39f);
        private static readonly Color PrepCool = new Color(.39f, .89f, .94f);
        private static readonly Color PrepGold = new Color(1f, .88f, .66f);
        private static readonly Color PrepIvory = new Color(.93f, .96f, .96f);

        // 菜单和 RevCLI 共用入口。原图先进入项目，再由 AssetDatabase 导入和保存。
        [MenuItem("RevGame/PvP/制作决斗准备面板")]
        public static string BuildDuelPreparationPanels()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                throw new InvalidOperationException("请退出 Play 再制作决斗准备面板。");
            }
            string[] names = { "DuelAtrium", "DuelEmber", "DuelYaya", "DuelPlate", "DuelButton", "DuelSigil", "DuelMeter" };
            foreach (string name in names)
            {
                ImportPrepSprite(name);
            }
            if (!AssetDatabase.IsValidFolder(PrepPanels))
            {
                AssetDatabase.CreateFolder("Assets/GameRes/UI/Panel/Pvp", "DuelPrep");
            }
            if (!AssetDatabase.IsValidFolder(PrepMaterials))
            {
                AssetDatabase.CreateFolder("Assets/GameRes/Material/UI", "PvpDuelPrep");
            }
            Material sigil = PrepMaterial("DuelSigil", "RevGame/UI/Duel Preparation Sigil");
            Material rift = PrepMaterial("DuelRift", "RevGame/UI/Duel Preparation Rift");
            Font font = GameAssetBuilder.Font();
            SavePrepPanel<DuelSelectPanel>(false, font, sigil, rift);
            SavePrepPanel<DuelLoadingPanel>(true, font, sigil, rift);
            AssetDatabase.SaveAssets();
            return "选角、加载 Prefab 已保存，待分包与图集验证。";
        }

        // 两种特效各共享一份材质，生成失败时不保存无 Shader 的面板。
        private static Material PrepMaterial(string name, string shaderName)
        {
            var shader = Shader.Find(shaderName);
            if (shader == null || !shader.isSupported)
            {
                throw new InvalidOperationException("决斗准备 Shader 未编译成功：" + shaderName);
            }
            string materialPath = PrepMaterials + "/" + name + ".mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
            if (material == null)
            {
                material = new Material(shader);
                AssetDatabase.CreateAsset(material, materialPath);
            }
            return material;
        }

        // PNG 一图一 Sprite；框和按钮使用九宫格，透明轮廓关闭 Mipmap。
        private static void ImportPrepSprite(string name)
        {
            string path = PrepArt + name + ".png";
            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer == null)
            {
                throw new InvalidOperationException("缺少决斗准备素材：" + path);
            }
            Vector4 border = name is "DuelPlate" or "DuelButton" ? Vector4.one * 24 : Vector4.zero;
            // 立绘保留原始 PNG。最大展示高度 816，导入尺寸 1024 可满足设计分辨率并与底图同图集。
            int maxSize = name is "DuelEmber" or "DuelYaya" ? 1024 : 2048;
            if (importer.textureType != TextureImporterType.Sprite || importer.spriteImportMode != SpriteImportMode.Single ||
                importer.mipmapEnabled || importer.npotScale != TextureImporterNPOTScale.None || importer.spriteBorder != border ||
                importer.maxTextureSize != maxSize)
            {
                importer.textureType = TextureImporterType.Sprite;
                importer.spriteImportMode = SpriteImportMode.Single;
                importer.npotScale = TextureImporterNPOTScale.None;
                importer.mipmapEnabled = false;
                importer.alphaIsTransparency = true;
                importer.wrapMode = TextureWrapMode.Clamp;
                importer.spriteBorder = border;
                importer.maxTextureSize = maxSize;
                importer.SaveAndReimport();
            }
        }

        // 两个面板都使用 1920×1080 设计安全区，按各自内容布置席位。
        private static void SavePrepPanel<T>(bool loading, Font font, Material material, Material rift) where T : DuelPrepPanelBase
        {
            string name = typeof(T).Name;
            var root = new GameObject(name, typeof(RectTransform), typeof(CanvasGroup));
            try
            {
                RectTransform rect = (RectTransform)root.transform;
                rect.anchorMin = Vector2.zero;
                rect.anchorMax = Vector2.one;
                rect.offsetMin = rect.offsetMax = Vector2.zero;
                var panel = root.AddComponent<T>();
                panel.Ember = PrepSprite("DuelEmber");
                panel.Yaya = PrepSprite("DuelYaya");
                // 保存设计尺寸，Prefab Stage 与 RevUI 都通过同一个组件将整个设计框等比缩放。
                Image background = PrepImage(PrepAt("screenBackground", rect, .5f, .5f, 1920, 1080),
                    "DuelAtrium", Color.white, raycast: true);
                background.material = rift;
                RectTransform layout = PrepAt("layout", rect, .5f, .5f, 1920, 1080);
                var fit = root.AddComponent<DuelPreparationLayout>();
                using (var settings = new SerializedObject(fit))
                {
                    settings.FindProperty("design").objectReferenceValue = layout;
                    settings.FindProperty("backdrop").objectReferenceValue = background;
                    settings.ApplyModifiedPropertiesWithoutUndo();
                }
                PrepImage(PrepAt("titlePlate", layout, .5f, .917f, 666, 112), "DuelPlate", Color.white, true);
                PrepText(layout, "eyebrow", "R E V G A M E    /    D U E L", .5f, .949f, 640, 36, 22, font, PrepGold);
                PrepText(layout, "title", loading ? "决 斗 载 入" : "选 择 角 色", .5f, .896f, 660, 64, 56, font, PrepIvory);
                RectTransform cancel = PrepAt("cancel", layout, .1f, .92f, 154, 52);
                PrepButton(cancel);
                PrepText(cancel, "text", "退出准备", .5f, .5f, 144, 46, 24, font, PrepIvory);
                panel.BlackSeat = BuildPrepSeat(layout, false, loading, font, material, panel.Ember);
                panel.WhiteSeat = BuildPrepSeat(layout, true, loading, font, material, panel.Yaya);
                if (loading)
                {
                    var sigil = PrepImage(PrepAt("versusSigil", layout, .5f, .51f, 310, 170), "DuelSigil", PrepGold);
                    sigil.material = material;
                    panel.Accent = sigil.rectTransform;
                    PrepText(layout, "versus", "VS", .5f, .51f, 220, 120, 96, font, PrepGold);
                    PrepText(layout, "prompt", "正在准备决斗", .5f, .127f, 720, 64, 34, font, PrepIvory);
                    PrepText(layout, "hint", "两席准备完成后，决斗才会开始", .5f, .066f, 700, 42, 22, font, PrepGold);
                }
                else
                {
                    var sigil = PrepImage(PrepAt("timerSigil", layout, .5f, .741f, 228, 104), "DuelSigil", PrepGold);
                    sigil.material = material;
                    panel.Accent = sigil.rectTransform;
                    PrepText(layout, "timer", "—", .5f, .742f, 176, 90, 66, font, PrepGold);
                    RectTransform roster = PrepStretch("roster", layout);
                    BuildPrepChoice(roster, "ember", "绯焰", "红枪", "DuelEmber", .573f, font);
                    BuildPrepChoice(roster, "yaya", "鸦鸦", "夜羽", "DuelYaya", .423f, font);
                    roster.Find("ember/seats").GetComponent<Text>().text = "<color=#FFAE94>1P 黑方</color>";
                    roster.Find("yaya/seats").GetComponent<Text>().text = "<color=#8DEDE3>2P 白方</color>";
                    PrepText(layout, "prompt", "点选角色 · 确认后锁定", .5f, .309f, 510, 44, 27, font, PrepIvory);
                    RectTransform confirm = PrepAt("confirm", layout, .5f, .203f, 302, 78);
                    PrepButton(confirm);
                    PrepText(confirm, "text", "确认角色", .5f, .5f, 284, 64, 35, font, PrepGold);
                    PrepText(layout, "hint", "可选择相同角色 · 席位颜色与徽记不变", .5f, .104f, 580, 42, 22, font, PrepIvory);
                }
                string path = PrepPanels + "/" + name + ".prefab";
                if (PrefabUtility.SaveAsPrefabAsset(root, path) == null)
                {
                    throw new InvalidOperationException("保存决斗准备面板失败：" + path);
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        // 选角席从上往下放徽记、立绘和铭牌。加载席另留进度条的空间。
        private static DuelPrepSeatView BuildPrepSeat(RectTransform layout, bool white, bool loading, Font font, Material material, Sprite sprite)
        {
            Color accent = white ? PrepCool : PrepWarm;
            float x = loading ? (white ? .737f : .263f) : (white ? .804f : .196f);
            RectTransform seat = PrepAt(white ? "whiteSeat" : "blackSeat", layout, x, loading ? .485f : .5f,
                loading ? 430 : 540, loading ? 710 : 1000);
            if (loading)
            {
                PrepImage(PrepStretch("card", seat), "DuelPlate", Color.white, true);
            }
            // 放大的角色残影呼应图形化底图，亮度低于前景立绘，角色换选时一起更新。
            var echo = PrepAt("portraitEcho", seat, white ? .64f : .36f, .54f, 690, 705).gameObject.AddComponent<Image>();
            echo.sprite = sprite;
            echo.preserveAspect = true;
            echo.color = new Color(accent.r * .6f, accent.g * .6f, accent.b * .6f, loading ? .15f : .10f);
            echo.raycastTarget = false;
            var halo = PrepImage(PrepAt("sigil", seat, .5f, loading ? .289f : .145f,
                loading ? 410 : 450, loading ? 160 : 86), "DuelSigil", accent);
            halo.material = material;
            float portraitSize = loading ? 510f : white ? SelectYayaPortraitSize : SelectEmberPortraitSize;
            RectTransform portraitRect = PrepAt("portrait", seat, .5f, loading ? .548f : .110f, portraitSize, portraitSize);
            if (!loading)
            {
                // 底部是两人的公共站位。换角时以脚下为轴缩放，姓名区上方的空隙始终保留。
                portraitRect.pivot = new Vector2(.5f, 0);
            }
            var portrait = portraitRect.gameObject.AddComponent<Image>();
            portrait.sprite = sprite;
            portrait.preserveAspect = true;
            portrait.raycastTarget = false;
            float badgeY = loading ? .956f : .903f;
            PrepImage(PrepAt("seatBadge", seat, .5f, badgeY, loading ? 382 : 418, 54), "DuelPlate", Color.white, true);
            var player = PrepText(seat, "player", white ? "2P · 白方  /  白方棋友" : "1P · 黑方  /  你",
                .5f, badgeY, loading ? 358 : 394, 48, 25, font, accent);
            player.resizeTextForBestFit = true;
            player.resizeTextMinSize = 16;
            player.resizeTextMaxSize = 25;
            RectTransform caption = seat;
            if (!loading)
            {
                // 铭牌复用九宫格底板。它的上沿低于立绘的底锚点，换角时不会压住角色。
                caption = PrepAt("namePlate", seat, .5f, .054f, 352, 90);
                PrepImage(caption, "DuelPlate", Color.white, true);
            }
            var name = PrepText(caption, "roleName", white ? "鸦鸦" : "绯焰", .5f, loading ? .22f : .70f,
                loading ? 356 : 320, loading ? 65 : 49, loading ? 52 : 46, font, PrepIvory);
            var status = PrepText(caption, "status", loading ? "正在准备" : "预选中", .5f, loading ? .131f : .23f,
                loading ? 362 : 320, loading ? 39 : 28, loading ? 25 : 23, font, accent);
            var view = new DuelPrepSeatView
            {
                Portrait = portrait, Echo = echo, Portal = halo.rectTransform, RoleName = name, Player = player, Status = status,
                EmberPortraitSize = loading ? 0 : SelectEmberPortraitSize,
                YayaPortraitSize = loading ? 0 : SelectYayaPortraitSize
            };
            if (loading)
            {
                PrepImage(PrepAt("track", seat, .5f, .074f, 331, 34), "DuelPlate", Color.white, true);
                view.Meter = PrepImage(PrepAt("meter", seat, .5f, .074f, 310, 18), "DuelMeter", accent);
                view.Meter.type = Image.Type.Filled;
                view.Meter.fillMethod = Image.FillMethod.Horizontal;
                view.Meter.fillAmount = 0;
                view.Percent = PrepText(seat, "percent", "0%", .5f, .02f, 136, 38, 28, font, PrepIvory);
            }
            return view;
        }

        // 角色卡使用已有站姿的缩略图和两席选择标志，允许同时高亮同一张。
        private static void BuildPrepChoice(RectTransform parent, string id, string title, string style, string art, float y, Font font)
        {
            RectTransform card = PrepAt(id, parent, .5f, y, 438, 133);
            PrepButton(card);
            var icon = PrepImage(PrepAt("icon", card, .187f, .51f, 150, 126), art, Color.white);
            icon.preserveAspect = true;
            PrepText(card, "name", title, .626f, .646f, 200, 56, 39, font, PrepIvory);
            PrepText(card, "style", style, .868f, .645f, 76, 45, 22, font, PrepGold);
            PrepText(card, "seats", "", .627f, .275f, 290, 37, 22, font, PrepIvory);
        }

        // 按钮仅把背景设为射线目标，文字和图层不吞触点。
        private static void PrepButton(RectTransform rect)
        {
            Image skin = PrepImage(rect, "DuelButton", Color.white, true, true);
            Button button = rect.gameObject.AddComponent<Button>();
            button.targetGraphic = skin;
            var colors = button.colors;
            colors.highlightedColor = new Color(1f, .94f, .74f);
            colors.pressedColor = new Color(.78f, .83f, .81f);
            colors.disabledColor = new Color(.52f, .56f, .57f);
            button.colors = colors;
        }

        // 唯一 Sprite 路径校验，缺图时不保存白色底板。
        private static Sprite PrepSprite(string name)
        {
            return AssetDatabase.LoadAssetAtPath<Sprite>(PrepArt + name + ".png") ??
                throw new InvalidOperationException("决斗准备 Sprite 导入失败：" + name);
        }

        // 图片默认不接收射线；全屏底图和按钮按用途显式开启。
        private static Image PrepImage(RectTransform rect, string asset, Color tint, bool sliced = false, bool raycast = false)
        {
            var image = rect.gameObject.AddComponent<Image>();
            image.sprite = PrepSprite(asset);
            image.color = tint;
            image.raycastTarget = raycast;
            image.type = sliced ? Image.Type.Sliced : Image.Type.Simple;
            return image;
        }

        // 文案在 Unity 中绘制，字号和安全边距可独立于美术调整。
        private static Text PrepText(Transform parent, string name, string value, float x, float y, float width, float height, int size, Font font, Color tint)
        {
            var text = PrepAt(name, parent, x, y, width, height).gameObject.AddComponent<Text>();
            text.font = font;
            text.text = value;
            text.fontSize = size;
            text.fontStyle = FontStyle.Bold;
            text.alignment = TextAnchor.MiddleCenter;
            // Noto 的行高大于字号，不能用默认 Truncate 把标题、数字和百分比整行裁掉。
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.color = tint;
            text.raycastTarget = false;
            return text;
        }

        // 按归一化锚点摆放，尺寸使用项目 1920x1080 设计单位。
        private static RectTransform PrepAt(string name, Transform parent, float x, float y, float width, float height)
        {
            RectTransform rect = Rect(name, parent, x, y, x, y);
            rect.anchoredPosition = Vector2.zero;
            rect.sizeDelta = new Vector2(width, height);
            return rect;
        }

        // 全铺层用于背景、容器和卡片。
        private static RectTransform PrepStretch(string name, Transform parent)
        {
            return Rect(name, parent, 0, 0, 1, 1);
        }
    }
}
