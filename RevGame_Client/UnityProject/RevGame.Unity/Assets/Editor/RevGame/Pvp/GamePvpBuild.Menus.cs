using System;
using RevGame.Pvp;
using RevGame.UI;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace RevGame.Editor
{
    public static partial class GamePvpBuild
    {
        /// <summary>浅色底板占安全设计框的宽高比例，边缘留出漩涡背景。</summary>
        private const float MenuPlateRatio = .94f;

        /// <summary>联机操作和设备列表共用的浅色长按钮，左右圆端由切片保留。</summary>
        private const string MenuActionSprite = "PvpMenu/PvpMatchButton";

        /// <summary>仅重新制作四张对战菜单，保留 Prefab GUID 和控件绑定路径。可在编辑态单独运行。</summary>
        public static string BuildMenus()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                throw new InvalidOperationException("请先退出 Play 再制作对战菜单");
            }
            _font = GameAssetBuilder.Font();
            BuildStandardMenus();
            BuildAgentMenu();
            AssetDatabase.SaveAssets();
            return "已更新对战入口、服务器、局域网和 Agent 设置面板";
        }

        /// <summary>制作入口、服务器和局域网三张常规菜单，供完整构建和菜单增量构建共用。</summary>
        private static void BuildStandardMenus()
        {
            System.IO.Directory.CreateDirectory("Assets/GameRes/UI/Panel/Pvp/Menu");
            SetBorder("UI_Pvp_imgBK", new Vector4(150, 100, 100, 100));
            SetBorder("UI_achieveItem_imgBK", new Vector4(24, 12, 24, 12));
            ConfigureMenuSlices();
            // 控件坐标先以底板为局部设计框，保存时映射回面板根以保留现有绑定。
            Menu("PvpEntryPanel", typeof(PvpEntryPanel), root =>
            {
                Label("heading", root, "玩家对战", .19f, .82f, .81f, .94f, 43, true);
                Label("subtitle", root, "选择一种方式，与棋友相遇", .16f, .74f, .84f, .82f, 25, true);
                string[] names = { "server", "lan", "computer", "local" };
                string[] labels = { "服务器匹配", "局域网联机", "本地人机对局", "本地双人对局" };
                string[] art = { "PvpServerButton", "PvpLanButton", "PvpComputerButton", "PvpLocalButton" };
                for (int i = 0; i < names.Length; i++)
                {
                    float y = .61f - i * .125f;
                    Button choice = Button(names[i], root, labels[i], .24f, y, .76f, y + .105f, 30, "PvpMenu/" + art[i], .08f);
                    ((Image)choice.targetGraphic).preserveAspect = true;
                }
                // 右下棋子插画占据边缘，设置按钮沿底部左移并保留原宽度。
                Button agent = Button("agentSettings", root, "Agent设置", .60f, .06f, .81f, .15f, 25, "PvpMenu/PvpAgentButton", .12f);
                ((Image)agent.targetGraphic).preserveAspect = true;
            });
            Menu("PvpServerPanel", typeof(PvpServerPanel), root =>
            {
                Label("heading", root, "服务器匹配", .2f, .8f, .8f, .91f, 44, true);
                Label("nameLabel", root, "棋友昵称", .13f, .65f, .32f, .75f, 27, true);
                Input("nickname", root, "棋友", .34f, .65f, .86f, .75f, 16);
                Label("endpointLabel", root, PvpServerPanel.ServerAddressLabel, .13f, .53f, .32f, .63f, 27, true);
                Input("endpoint", root, string.Empty, .34f, .53f, .86f, .63f, PvpServerPanel.ServerAddressLimit);
                MenuButton("mode", root, "新手场 · 二次确认落子", .23f, .41f, .77f, .51f, 25);
                Label("state", root, "准备连接", .13f, .31f, .87f, .40f, 27, true);
                Label("duration", root, PvpServerPanel.ServerAddressHint, .15f, .22f, .85f, .31f, 26, true);
                MenuButton("connect", root, "连接 / 重连", .29f, .06f, .53f, .16f, 27);
                MenuButton("match", root, "开始匹配", .56f, .06f, .80f, .16f, 27);
            });
            Menu("PvpLanPanel", typeof(PvpLanPanel), root =>
            {
                Label("heading", root, "局域网联机", .2f, .8f, .8f, .91f, 44, true);
                Input("nickname", root, "棋友", .16f, .68f, .54f, .78f, 16);
                MenuButton("search", root, "搜索同网设备", .57f, .68f, .86f, .78f, 27);
                MenuButton("presence", root, "允许大厅接收邀请", .16f, .554f, .42f, .65f, 26);
                Label("state", root, "开启权限后，同网棋友可在大厅找到你", .44f, .53f, .86f, .67f, 24, true);
                // 行高沿用运行期列表的 116 设计单位，只调整视口位置与宽度。
                RectTransform scroll = Rect("scroll", root, .12f, .17f, .88f, .52f);
                var scrollRect = scroll.gameObject.AddComponent<ScrollRect>();
                scrollRect.horizontal = false;
                scrollRect.movementType = ScrollRect.MovementType.Clamped;
                RectTransform viewport = Rect("viewport", scroll, 0, 0, 1, 1);
                viewport.gameObject.AddComponent<RectMask2D>();
                RectTransform content = Rect("content", viewport, 0, 1, 1, 1);
                content.pivot = new Vector2(.5f, 1);
                content.sizeDelta = new Vector2(0, 116);
                scrollRect.viewport = viewport;
                scrollRect.content = content;
                RectTransform item = Rect("item", content, 0, 1, 1, 1);
                item.pivot = new Vector2(.5f, 1);
                item.sizeDelta = new Vector2(0, 106);
                Image image = Plate(item, MenuActionSprite, Color.white, true);
                FitMenuSlices(image, item.sizeDelta.y);
                var button = item.gameObject.AddComponent<Button>();
                button.targetGraphic = image;
                Label("name", item, "棋友 · 点击邀请", .04f, .52f, .96f, .96f, 27, true);
                Label("detail", item, "设备名称 / 机型", .04f, .04f, .96f, .53f, 21, true);
                item.gameObject.SetActive(false);
            });
        }

        /// <summary>制作对局通用通知条。它是叠加提示，不使用整屏菜单背景。</summary>
        private static void BuildNotice()
        {
            var notice = new GameObject("PvpNoticePanel", typeof(RectTransform), typeof(PvpNoticePanel));
            try
            {
                StretchRoot(notice);
                RectTransform banner = Rect("banner", notice.transform, .18f, 1, .82f, 1);
                banner.pivot = new Vector2(.5f, 1);
                banner.sizeDelta = new Vector2(0, 100);
                banner.anchoredPosition = new Vector2(0, 150);
                Plate(banner, "UI_achieveItem_imgBK", Color.white);
                Label("text", banner, "", .04f, .08f, .96f, .92f, 28, true);
                PrefabUtility.SaveAsPrefabAsset(notice, "Assets/GameRes/UI/Panel/Pvp/Menu/PvpNoticePanel.prefab");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(notice);
            }
        }
        /// <summary>为浅色输入框和操作按钮划定整矩形切片，左右各保留圆端、金边和铆钉。</summary>
        private static void ConfigureMenuSlices()
        {
            SetBorder("PvpMenu/PvpTextInput", new Vector4(108, 0, 108, 0), fullRect: true);
            SetBorder(MenuActionSprite, new Vector4(136, 0, 136, 0), fullRect: true);
        }

        /// <summary>按源图片像素设置切片边距和可选的整矩形网格，只在参数变化时重导入。</summary>
        private static void SetBorder(string name, Vector4 border, bool fullRect = false)
        {
            var importer = AssetImporter.GetAtPath("Assets/GameRes/Art/UI/" + name + ".png") as TextureImporter;
            if (importer == null)
            {
                throw new InvalidOperationException("缺少UI素材：" + name);
            }
            var settings = new TextureImporterSettings();
            importer.ReadTextureSettings(settings);
            if (settings.spriteBorder == border && (!fullRect || settings.spriteMeshType == SpriteMeshType.FullRect))
            {
                return;
            }
            settings.spriteBorder = border;
            if (fullRect)
            {
                settings.spriteMeshType = SpriteMeshType.FullRect;
            }
            importer.SetTextureSettings(settings);
            importer.SaveAndReimport();
        }

        /// <summary>保留既有按钮绑定和深墨色文字，显示高度以底板在 1920×1080 安全设计框中的比例换算。</summary>
        private static void MenuButton(string name, Transform parent, string value, float x0, float y0, float x1, float y1, int size)
        {
            Button button = Button(name, parent, value, x0, y0, x1, y1, size, MenuActionSprite);
            FitMenuSlices((Image)button.targetGraphic, (y1 - y0) * MenuPlateRatio * GameBattleLayout.DesignResolution.y);
        }

        /// <summary>让切片圆端按控件设计高度等比缩放，额外宽度只分给中间的平直区域。</summary>
        private static void FitMenuSlices(Image image, float designHeight)
        {
            image.type = Image.Type.Sliced;
            image.pixelsPerUnitMultiplier = image.sprite.rect.height / designHeight;
        }

        /// <summary>以现有漩涡材质铺满屏幕，装配共享浅色底板和返回按钮，控件仍用原节点名绑定。</summary>
        private static void Menu(string name, Type panelType, Action<Transform> compose)
        {
            Material backdrop = AssetDatabase.LoadAssetAtPath<Material>("Assets/GameRes/Material/Background/ArcaneBackdrop.mat");
            if (backdrop == null)
            {
                throw new InvalidOperationException("找不到加载面板使用的漩涡材质");
            }
            var root = new GameObject(name, typeof(RectTransform), panelType);
            try
            {
                StretchRoot(root);
                // 与加载面板共享材质和 UV1 归一化组件。全屏图拦截穿透点击，装饰底板不抢控件事件。
                RectTransform screen = Rect("screenBackground", root.transform, 0, 0, 1, 1);
                Image effect = screen.gameObject.AddComponent<Image>();
                effect.material = backdrop;
                screen.gameObject.AddComponent<ArcaneImageCoordinates>();
                float margin = (1f - MenuPlateRatio) * .5f;
                Image plate = Plate(Rect("background", root.transform, margin, margin, 1f - margin, 1f - margin),
                    "PvpMenu/PvpMenuPlate", Color.white);
                plate.type = Image.Type.Simple;
                plate.preserveAspect = true;
                compose(root.transform);
                Button back = Button("back", root.transform, "返回", .08f, .06f, .24f, .15f, 27, "PvpMenu/PvpBackButton", .14f);
                ((Image)back.targetGraphic).preserveAspect = true;
                // 前两层已使用全屏坐标。其余根节点仍在原层级，只映射归一化锚点，字体保留设计字号。
                Vector2 origin = Vector2.one * margin;
                for (int i = 2; i < root.transform.childCount; i++)
                {
                    var control = (RectTransform)root.transform.GetChild(i);
                    control.anchorMin = origin + control.anchorMin * MenuPlateRatio;
                    control.anchorMax = origin + control.anchorMax * MenuPlateRatio;
                    control.offsetMin = control.offsetMax = Vector2.zero;
                }
                PrefabUtility.SaveAsPrefabAsset(root, "Assets/GameRes/UI/Panel/Pvp/Menu/" + name + ".prefab");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        /// <summary>菜单宿主铺满 UI 层，运行期可将控件区域限制在安全框内。</summary>
        private static void StretchRoot(GameObject root)
        {
            RectTransform rect = (RectTransform)root.transform;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.sizeDelta = Vector2.zero;
            rect.anchoredPosition = Vector2.zero;
        }
    }
}
