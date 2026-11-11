using System;
using RevGame.View;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace RevGame.Editor
{
    // 决斗场景专用 HUD 配方。设计坐标与 GameBattleLayout 一致，场景内由安全区组件等比缩放。
    public static partial class GamePvpBuild
    {
        // HUD Sprite 只供内置决斗场景使用，和角色共用决斗图集。
        private const string DuelHudArt = ArcadeArt + "Hud/";
        // 黑方标识和血条使用红色。
        private static readonly Color BlackHud = new Color(1f, .34f, .37f);
        // 白方标识和血条使用冰蓝色。
        private static readonly Color WhiteHud = new Color(.32f, .85f, 1f);
        // 双方能量使用同一暖金色，和血量明确区分。
        private static readonly Color EnergyHud = new Color(1f, .75f, .37f);
        // 场景装配和图集校验共用该白名单，避免遗落某个圆键或图标。
        private static readonly string[] DuelHudSprites =
        {
            "Disc", "Ring", "StickBase", "StickKnob", "HealthFrame", "MeterFill", "EnergyFrame", "Dividers",
            "ClockFrame", "PortraitFrame", "EmberPortrait", "SmallFrame", "Jab", "Thrust", "Plunge", "Crimson", "Jump"
        };

        // HUD 作为全屏场景下的单独子树，背景、角色和镜头组件仍占用自己的层。
        private static void BuildDuelHud(PvpDuelView view)
        {
            RectTransform hud = Rect("BattleHud", view.transform, 0, 0, 1, 1);
            view.Hud = hud;
            BuildDuelHeader(view, hud);
            BuildDuelControls(view, hud);
        }

        // 上方从外向内排布头像、玩家名、生命和能量，时钟保持居中。
        private static void BuildDuelHeader(PvpDuelView view, Transform hud)
        {
            MakePortrait(hud, "BlackPortrait", "1P", 52, BlackHud, false);
            MakePortrait(hud, "WhitePortrait", "2P", 1772, WhiteHud, true);
            view.FirstName = MakePlayerName(hud, "BlackName", 168, TextAnchor.MiddleLeft, BlackHud);
            view.SecondName = MakePlayerName(hud, "WhiteName", 1051, TextAnchor.MiddleRight, WhiteHud);
            view.FirstHealth = MakeMeter(hud, "BlackHealth", 162, 971, BlackHud, false, false);
            view.SecondHealth = MakeMeter(hud, "WhiteHealth", 1050, 971, WhiteHud, true, false);
            view.FirstEnergy = MakeMeter(hud, "BlackEnergy", 162, 943, EnergyHud, false, true);
            view.SecondEnergy = MakeMeter(hud, "WhiteEnergy", 1050, 943, EnergyHud, true, true);
            view.FirstHealthText = HudText("BlackHp", hud, "100", 183, 971, 82, 43, 27, TextAnchor.MiddleLeft);
            view.SecondHealthText = HudText("WhiteHp", hud, "100", 1655, 971, 82, 43, 27, TextAnchor.MiddleRight);
            HudImage(HudBox("ClockPlate", hud, 899, 948, 122, 105), "ClockFrame");
            view.Clock = HudText("Clock", hud, "∞", 899, 948, 122, 105, 55);
            view.FirstCombo = HudText("BlackCombo", hud, "", 174, 804, 290, 79, 52, TextAnchor.MiddleLeft);
            view.SecondCombo = HudText("WhiteCombo", hud, "", 1456, 804, 290, 79, 52, TextAnchor.MiddleRight);
            view.FirstCombo.color = BlackHud;
            view.SecondCombo.color = WhiteHud;
            view.Banner = HudText("Banner", hud, "等待双方进入决斗场", 410, 767, 1100, 107, 52);
        }

        // 左手只负责走位与跳跃。右手以最大普攻为中心，上方和左侧分布三种技能。
        private static void BuildDuelControls(PvpDuelView view, Transform hud)
        {
            RectTransform stick = HudBox("MoveStick", hud, 51, 61, 229, 229);
            HudImage(stick, "StickBase", true);
            stick.gameObject.AddComponent<DuelCircleHitArea>();
            view.MoveStick = stick.gameObject.AddComponent<DuelMoveStick>();
            view.MoveStick.Knob = HudBox("Knob", stick, 69, 69, 91, 91, 229, 229);
            HudImage(view.MoveStick.Knob, "StickKnob");

            view.Jump = MakeRoundAction(hud, "Jump", "Jump", "跳跃", "↑", 325, 257, 104, WhiteHud);
            view.Jab = MakeRoundAction(hud, "Jab", "Jab", "普攻", "J", 1748, 156, 164, BlackHud);
            view.Skill = MakeRoundAction(hud, "Skill", "Thrust", "贯枪", "K", 1555, 125, 124, EnergyHud);
            view.SkillTwo = MakeRoundAction(hud, "SkillTwo", "Plunge", "坠枪", "L", 1573, 280, 124, WhiteHud);
            view.Ultimate = MakeRoundAction(hud, "Ultimate", "Crimson", "赤阵", "I", 1739, 354, 136, new Color(1f, .44f, .65f));
            view.Exit = MakeHudLink(hud, "Back", "返回大厅", 51);
            view.Reconnect = MakeHudLink(hud, "Reconnect", "重连", 220);
            view.Reconnect.gameObject.SetActive(false);

            // 单独的桌面键盘图例在缩小的 Game View 中仍有足够字号。
            RectTransform guide = HudBox("KeyboardGuide", hud, 408, 26, 1040, 52);
            HudImage(guide, "HealthFrame");
            Label("Keys", guide, "A/D 移动    SPACE 跳跃    J 普攻    K 贯枪    L 坠枪    I 赤阵",
                .025f, 0, .975f, 1, 28);
            view.KeyboardGuide = guide.gameObject;
            view.InputFeedback = HudText("InputFeedback", hud, "", 460, 82, 960, 58, 30);
        }

        // 先建立双席的头像框与朝向，角色装配阶段把白方图像绑定到鸦鸦差分。
        private static void MakePortrait(Transform hud, string name, string seat, float x, Color tint, bool mirror)
        {
            HudImage(HudBox(name + "Frame", hud, x, 939, 96, 96), "PortraitFrame").color = tint;
            Image art = HudImage(HudBox(name, hud, x + 5, 944, 86, 86), "EmberPortrait");
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
            image.raycastTarget = raycast;
            return image;
        }

        // 缺图时中止生成配方，防止正式场景保存无 Sprite 的控件。
        private static Sprite LoadHudSprite(string name)
        {
            return AssetDatabase.LoadAssetAtPath<Sprite>(DuelHudArt + name + ".png") ??
                throw new InvalidOperationException("决斗 HUD 素材未导入：" + name);
        }
    }
}
