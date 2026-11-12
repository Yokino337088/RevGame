using System;
using RevGame.Pvp.Duel;
using RevGame.UI;
using RevGame.View;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace RevGame.Editor
{
    // 决斗 HUD 预制体的唯一制作配方，设计坐标与 GameBattleLayout 一致。
    public static partial class GamePvpBuild
    {
        // 两种控件素材归到同一个决斗 HUD 图集，场景直接持有的角色切片独立。
        private const string DuelHudArt = "Assets/GameRes/Art/UI/PvpDuelHud/";
        // RevUIPanel 的逻辑根和此生成目录对应。
        private const string DuelHudPrefabPath = "Assets/GameRes/UI/Panel/Pvp/DuelHud/DuelHudPanel.prefab";
        // 黑方标识和血条使用红色。
        private static readonly Color BlackHud = new Color(1f, .34f, .37f);
        // 白方标识和血条使用冰蓝色。
        private static readonly Color WhiteHud = new Color(.32f, .85f, 1f);
        // 双方能量使用同一暖金色，和血量明确区分。
        private static readonly Color EnergyHud = new Color(1f, .75f, .37f);
        // 仅长条和斜角底板切九宫格。尺寸以实际源图为上限，圆键保留原有轮廓。
        private static void PrepareDuelHudBorders()
        {
            foreach (string sprite in new[] { "HealthFrame", "EnergyFrame", "SmallFrame" })
            {
                string path = DuelHudArt + "Controls/" + sprite + ".png";
                var importer = (TextureImporter)AssetImporter.GetAtPath(path);
                importer.GetSourceTextureWidthAndHeight(out int width, out int height);
                float horizontal = Mathf.Min(24, width / 4);
                float vertical = Mathf.Min(16, height / 4);
                var border = new Vector4(horizontal, vertical, horizontal, vertical);
                if (importer.spriteBorder != border)
                {
                    importer.spriteBorder = border;
                    importer.SaveAndReimport();
                }
            }
        }

        // 保存全屏 RevUI 预制体。节点绑定由面板特性读取，场景不复制这些控件。
        private static void BuildDuelHud()
        {
            PrepareDuelHudBorders();
            if (!AssetDatabase.IsValidFolder("Assets/GameRes/UI/Panel/Pvp/DuelHud"))
            {
                AssetDatabase.CreateFolder("Assets/GameRes/UI/Panel/Pvp", "DuelHud");
            }
            RectTransform root = Rect("DuelHudPanel", null, 0, 0, 1, 1);
            try
            {
                var panel = root.gameObject.AddComponent<DuelHudPanel>();
                using var data = new SerializedObject(panel);
                data.FindProperty("EmberPortrait").objectReferenceValue = LoadHudSprite("EmberPortrait");
                SerializedProperty portraits = data.FindProperty("YayaPortraits");
                portraits.arraySize = YayaPortraitExpressions.Length;
                for (int index = 0; index < YayaPortraitExpressions.Length; index++)
                {
                    portraits.GetArrayElementAtIndex(index).objectReferenceValue = LoadYayaDuelPortrait(YayaPortraitExpressions[index]);
                }
                data.ApplyModifiedPropertiesWithoutUndo();
                BuildDuelHeader(root);
                BuildDuelControls(root);
                if (PrefabUtility.SaveAsPrefabAsset(root.gameObject, DuelHudPrefabPath) == null)
                {
                    throw new InvalidOperationException("无法保存决斗 HUD 预制体。");
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root.gameObject);
            }
        }

        // 上方从外向内排布头像、玩家名、生命和能量，时钟保持居中。
        private static void BuildDuelHeader(Transform hud)
        {
            MakePortrait(hud, "BlackPortrait", "1P", 52, BlackHud, false);
            MakePortrait(hud, "WhitePortrait", "2P", 1772, WhiteHud, true);
            MakePlayerName(hud, "BlackName", 168, TextAnchor.MiddleLeft, BlackHud);
            MakePlayerName(hud, "WhiteName", 1051, TextAnchor.MiddleRight, WhiteHud);
            MakeMeter(hud, "BlackHealth", 162, 971, BlackHud, false, false);
            MakeMeter(hud, "WhiteHealth", 1050, 971, WhiteHud, true, false);
            MakeMeter(hud, "BlackEnergy", 162, 943, EnergyHud, false, true);
            MakeMeter(hud, "WhiteEnergy", 1050, 943, EnergyHud, true, true);
            HudText("BlackHp", hud, DuelConfig.Health.ToString(), 183, 971, 82, 43, 27, TextAnchor.MiddleLeft);
            HudText("WhiteHp", hud, DuelConfig.Health.ToString(), 1655, 971, 82, 43, 27, TextAnchor.MiddleRight);
            HudImage(HudBox("ClockPlate", hud, 899, 948, 122, 105), "ClockFrame");
            HudText("Clock", hud, "∞", 899, 948, 122, 105, 55);
            HudText("BlackCombo", hud, "", 174, 804, 290, 79, 52, TextAnchor.MiddleLeft).color = BlackHud;
            HudText("WhiteCombo", hud, "", 1456, 804, 290, 79, 52, TextAnchor.MiddleRight).color = WhiteHud;
            HudText("Banner", hud, "等待双方进入决斗场", 410, 767, 1100, 107, 52);
        }

        // 左手只负责走位与跳跃。右手以最大普攻为中心，上方和左侧分布三种技能。
        private static void BuildDuelControls(Transform hud)
        {
            RectTransform stick = HudBox("MoveStick", hud, 51, 61, 229, 229);
            HudImage(stick, "StickBase", true);
            stick.gameObject.AddComponent<DuelCircleHitArea>();
            var movement = stick.gameObject.AddComponent<DuelMoveStick>();
            movement.Knob = HudBox("Knob", stick, 69, 69, 91, 91, 229, 229);
            HudImage(movement.Knob, "StickKnob");

            MakeRoundAction(hud, "Jump", "Jump", "跳跃", "↑", 325, 257, 104, WhiteHud);
            MakeRoundAction(hud, "Jab", "Jab", "普攻", "J", 1748, 156, 164, BlackHud);
            MakeRoundAction(hud, "Skill", "Thrust", "贯枪", "K", 1555, 125, 124, EnergyHud);
            MakeRoundAction(hud, "SkillTwo", "Plunge", "坠枪", "L", 1573, 280, 124, WhiteHud);
            MakeRoundAction(hud, "Ultimate", "Crimson", "赤阵", "I", 1739, 354, 136, new Color(1f, .44f, .65f));
            MakeHudLink(hud, "Back", "返回大厅", 51);
            MakeHudLink(hud, "Reconnect", "重连", 220).gameObject.SetActive(false);

            // 单独的桌面键盘图例在缩小的 Game View 中仍有足够字号。
            RectTransform guide = HudBox("KeyboardGuide", hud, 408, 26, 1040, 52);
            HudImage(guide, "HealthFrame");
            Label("Keys", guide, "A/D 移动    SPACE 跳跃    J 普攻    K 贯枪    L 坠枪    I 赤阵",
                .025f, 0, .975f, 1, 28);
            HudText("InputFeedback", hud, "", 460, 82, 960, 58, 30);
        }

        // 先建立双席的头像框与朝向，角色装配阶段把白方图像绑定到鸦鸦差分。
        private static void MakePortrait(Transform hud, string name, string seat, float x, Color tint, bool mirror)
        {
            HudImage(HudBox(name + "Frame", hud, x, 939, 96, 96), "PortraitFrame").color = tint;
            Image art = HudImage(HudBox(name, hud, x + 5, 944, 86, 86), "EmberPortrait");
            if (mirror)
            {
                art.sprite = LoadYayaDuelPortrait(YayaPortraitExpressions[0]);
            }
            art.rectTransform.localScale = new Vector3(mirror ? -1 : 1, 1, 1);
            RectTransform badge = HudBox(name + "Badge", hud, x + 24, 923, 48, 27);
            HudImage(badge, "SmallFrame").color = tint;
            Label("Seat", badge, seat, 0, 0, 1, 1, 18);
        }

        // 字体的行高由实际字形决定。使用单行缩放来适配长昵称和中文字体，避免整行被裁掉。
        private static Text MakePlayerName(Transform hud, string name, float x, TextAnchor alignment, Color tint)
        {
            Text label = HudText(name, hud, "绯焰", x, 1018, 701, 50, 28, alignment);
            label.resizeTextForBestFit = true;
            label.resizeTextMinSize = 17;
            label.resizeTextMaxSize = 28;
            label.color = tint;
            return label;
        }

        // 生命与能量从两侧镜像填充，能量分格纹理盖在填充之上。
        private static Image MakeMeter(Transform hud, string name, float x, float y, Color tint, bool mirror, bool energy)
        {
            RectTransform box = HudBox(name, hud, x, y, 708, energy ? 21 : 43);
            HudImage(box, energy ? "EnergyFrame" : "HealthFrame");
            RectTransform strip = Rect("Fill", box, .012f, energy ? .16f : .19f, .988f, energy ? .84f : .81f);
            Image fill = HudImage(strip, "MeterFill");
            fill.type = Image.Type.Filled;
            fill.fillMethod = Image.FillMethod.Horizontal;
            fill.fillOrigin = mirror ? 1 : 0;
            fill.color = tint;
            if (energy)
            {
                HudImage(Rect("Segments", box, 0, 0, 1, 1), "Dividers");
            }
            return fill;
        }

        // 圆键的图标、名称和键位均不接收射线，底板负责整个可见圆的命中。
        private static Button MakeRoundAction(Transform hud, string name, string glyph, string caption, string key, float centerX, float centerY, float diameter, Color tint)
        {
            RectTransform rect = HudBox(name, hud, centerX - diameter * .5f, centerY - diameter * .5f, diameter, diameter);
            Image disc = HudImage(rect, "Disc", true);
            rect.gameObject.AddComponent<DuelCircleHitArea>();
            HudImage(Rect("Ring", rect, 0, 0, 1, 1), "Ring").color = tint;
            HudImage(Rect("Icon", rect, .29f, .41f, .71f, .83f), glyph);
            Label("Caption", rect, caption, .10f, .20f, .90f, .45f, diameter > 140 ? 27 : 21);
            Label("Key", rect, key, .25f, .075f, .75f, .25f, 24).color = tint;
            Button button = rect.gameObject.AddComponent<DuelPressButton>();
            button.targetGraphic = disc;
            return button;
        }

        // 返回和重连属于状态操作，收在头像下方的小型斜角底板中。
        private static Button MakeHudLink(Transform hud, string name, string label, float x)
        {
            RectTransform rect = HudBox(name, hud, x, 862, 151, 42);
            Image image = HudImage(rect, "SmallFrame", true);
            Label("Label", rect, label, 0, 0, 1, 1, 20);
            Button button = rect.gameObject.AddComponent<Button>();
            button.targetGraphic = image;
            return button;
        }

        // 将按像素阅读的布局映射到固定比例设计框，不在运行中重新摆放各控件。
        private static RectTransform HudBox(string name, Transform parent, float x, float y, float width, float height, float designWidth = 1920, float designHeight = 1080)
        {
            return Rect(name, parent, x / designWidth, y / designHeight, (x + width) / designWidth, (y + height) / designHeight);
        }

        // HUD 文字独立控制对齐和可读性。超长昵称在本席位边界内截断。
        private static Text HudText(string name, Transform parent, string value, float x, float y, float width, float height, int size, TextAnchor alignment = TextAnchor.MiddleCenter)
        {
            Text text = Label(name, parent, value, x / 1920, y / 1080, (x + width) / 1920, (y + height) / 1080, size);
            text.alignment = alignment;
            text.verticalOverflow = VerticalWrapMode.Truncate;
            var outline = text.gameObject.AddComponent<Outline>();
            outline.effectColor = new Color(0, 0, 0, .88f);
            outline.effectDistance = new Vector2(1.6f, -1.6f);
            return text;
        }

        // 将已导入的 Sprite 直接序列化到场景，运行时无需查找散图。
        private static Image HudImage(RectTransform rect, string sprite, bool raycast = false)
        {
            Image image = rect.gameObject.AddComponent<Image>();
            image.sprite = LoadHudSprite(sprite);
            if (image.sprite.border.sqrMagnitude > 0)
            {
                image.type = Image.Type.Sliced;
            }
            image.raycastTarget = raycast;
            return image;
        }

        // 缺图时中止生成配方，防止正式场景保存无 Sprite 的控件。
        private static Sprite LoadHudSprite(string name)
        {
            return AssetDatabase.LoadAssetAtPath<Sprite>(DuelHudArt + "Controls/" + name + ".png") ??
                throw new InvalidOperationException("决斗 HUD 素材未导入：" + name);
        }
    }
}
