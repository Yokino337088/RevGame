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
        // 准备页两席共用的鸦鸦立绘尺寸，单位为 1920×1080 画布像素。
        private const float SelectYayaPortraitSize = 765f;
        // 准备页的两份专用材质归 Material，分包仍跟随选角和加载页的生命周期。
        private const string PrepMaterials = "Assets/GameRes/Material/UI/PvpDuelPrep";
        // 黑白席颜色相同贯穿选角、加载和镜像战斗；编号提供第二种识别手段。
        private static readonly Color PrepWarm = new Color(1f, .52f, .39f);
        private static readonly Color PrepCool = new Color(.39f, .89f, .94f);
        private static readonly Color PrepGold = new Color(1f, .88f, .66f);
        private static readonly Color PrepIvory = new Color(.93f, .96f, .96f);

        // 菜单和 RevCLI 共用入口，装配鸦鸦准备页和双席加载页并保存两张预制体。
        [MenuItem("RevGame/PvP/制作决斗准备面板")]
        public static string BuildDuelPreparationPanels()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                throw new InvalidOperationException("请退出 Play 再制作决斗准备面板。");
            }
            string[] names = { "DuelAtrium", "DuelYaya", "DuelPlate", "DuelButton", "DuelSigil", "DuelMeter" };
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
            return "鸦鸦准备页和双席加载 Prefab 已保存。";
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

        // 配置准备页 PNG 的单 Sprite 导入参数。透明轮廓关闭 Mipmap，立绘使用 1024 上限。
        private static void ImportPrepSprite(string name)
        {
            string path = PrepArt + name + ".png";
            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer == null)
            {
                throw new InvalidOperationException("缺少决斗准备素材：" + path);
            }
            Vector4 border = name is "DuelPlate" or "DuelButton" ? Vector4.one * 24 : Vector4.zero;
            int maxSize = name == "DuelYaya" ? 1024 : 2048;
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

        // 两个面板共用设计安全区、鸦鸦立绘与席位颜色。准备页确认后才进入加载页。
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
                Sprite yaya = PrepSprite("DuelYaya");
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
                PrepText(layout, "title", loading ? "决 斗 载 入" : "决 斗 准 备", .5f, .896f, 660, 64, 56, font, PrepIvory);
                RectTransform cancel = PrepAt("cancel", layout, .1f, .92f, 154, 52);
                PrepButton(cancel);
                PrepText(cancel, "text", "退出准备", .5f, .5f, 144, 46, 24, font, PrepIvory);
                panel.BlackSeat = BuildPrepSeat(layout, false, loading, font, material, yaya);
                panel.WhiteSeat = BuildPrepSeat(layout, true, loading, font, material, yaya);
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
                    BuildPrepRoleCard(roster, font);
                    PrepText(layout, "prompt", "等待双方进入准备页", .5f, .309f, 510, 44, 27, font, PrepIvory);
                    RectTransform confirm = PrepAt("confirm", layout, .5f, .203f, 302, 78);
                    PrepButton(confirm);
                    PrepText(confirm, "text", "确认参战", .5f, .5f, 284, 64, 35, font, PrepGold);
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

        // 两席用相同鸦鸦立绘，通过朝向、席位色和编号区别。加载页另外预留进度条。
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
            // 角色和残影共用同一朝向，亮度较低的残影把席位色延伸到底图。
            var facing = new Vector3(white ? 1 : -1, 1, 1);
            var echo = PrepAt("portraitEcho", seat, white ? .64f : .36f, .54f, 690, 705).gameObject.AddComponent<Image>();
            echo.sprite = sprite;
            echo.preserveAspect = true;
            echo.color = new Color(accent.r * .6f, accent.g * .6f, accent.b * .6f, loading ? .15f : .10f);
            echo.raycastTarget = false;
            echo.rectTransform.localScale = facing;
            var halo = PrepImage(PrepAt("sigil", seat, .5f, loading ? .289f : .145f,
                loading ? 410 : 450, loading ? 160 : 86), "DuelSigil", accent);
            halo.material = material;
            float portraitSize = loading ? 510f : SelectYayaPortraitSize;
            RectTransform portraitRect = PrepAt("portrait", seat, .5f, loading ? .548f : .110f, portraitSize, portraitSize);
            if (!loading)
            {
                // 以脚底为锚点对齐两席，让下方姓名区保持独立。
                portraitRect.pivot = new Vector2(.5f, 0);
            }
            var portrait = portraitRect.gameObject.AddComponent<Image>();
            portrait.sprite = sprite;
            portrait.preserveAspect = true;
            portrait.raycastTarget = false;
            portraitRect.localScale = facing;
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
                // 铭牌复用九宫格底板，上沿低于立绘脚底。
                caption = PrepAt("namePlate", seat, .5f, .054f, 352, 90);
                PrepImage(caption, "DuelPlate", Color.white, true);
            }
            PrepText(caption, "roleName", "鸦鸦", .5f, loading ? .22f : .70f,
                loading ? 356 : 320, loading ? 65 : 49, loading ? 52 : 46, font, PrepIvory);
            var status = PrepText(caption, "status", loading ? "正在准备" : "待确认", .5f, loading ? .131f : .23f,
                loading ? 362 : 320, loading ? 39 : 28, loading ? 25 : 23, font, accent);
            var view = new DuelPrepSeatView
            {
                Player = player, Status = status
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

        // 居中展示唯一可用的角色，参战交由下方独立的确认按钮提交。
        private static void BuildPrepRoleCard(RectTransform parent, Font font)
        {
            RectTransform card = PrepAt("yaya", parent, .5f, .505f, 438, 133);
            PrepImage(card, "DuelPlate", Color.white, true);
            var icon = PrepImage(PrepAt("icon", card, .187f, .51f, 150, 126), "DuelYaya", Color.white);
            icon.preserveAspect = true;
            PrepText(card, "name", "鸦鸦", .626f, .646f, 200, 56, 39, font, PrepIvory);
            PrepText(card, "style", "夜羽", .626f, .32f, 120, 34, 22, font, PrepGold);
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
