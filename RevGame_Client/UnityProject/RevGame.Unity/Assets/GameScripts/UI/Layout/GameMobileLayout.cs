// ============================================================
// GameMobileLayout.cs —— 手机横屏排版：左侧手牌、中间大棋盘、右侧信息与操作。
// 框架 Canvas 负责缩放，本组件负责按钮位置；不会切换为竖屏布局。
// 位置使用画布逻辑单位，宽屏手机多出来的空间仍留在两侧，避免把棋盘格压小。
// ============================================================
using UnityEngine;
using UnityEngine.UI;

namespace RevGame.UI
{
    public sealed class GameMobileLayout : MonoBehaviour
    {
        [Tooltip("对应预制体名称，由编辑器装配工具填写；不依赖运行时 Clone 名称。")]
        [SerializeField] private string panelName;
        private Vector2 _lastSize;
        private Rect _lastSafe;
        private bool _applying;

        public void Configure(string value)
        {
            panelName = value;
            _lastSize = Vector2.zero;
            ApplyLayout();
        }

        private void OnEnable()
        {
            _lastSize = Vector2.zero;
            ApplyLayout();
        }

        private void OnRectTransformDimensionsChange() { ApplyLayout(); }
        // 刘海/模拟器旋转可能只改变安全区，不改变 RectTransform 尺寸，因此还要轻量比较一次。
        private void LateUpdate() { ApplyLayout(); }

        /// <summary>只在尺寸变化时重排；防止设置子节点尺寸触发递归排版。</summary>
        private void ApplyLayout()
        {
            RectTransform root = transform as RectTransform;
            if (_applying || root == null || string.IsNullOrEmpty(panelName))
            {
                return;
            }
            Vector2 size = root.rect.size;
            Rect safe = GameBattleLayout.SafeViewport();
            if (size.x < 10f || size.y < 10f || (size == _lastSize && safe == _lastSafe))
            {
                return;
            }
            _applying = true;
            _lastSize = size;
            _lastSafe = safe;
            // 两个战斗面板的预制体已按 1920×1080 无刘海布局保存；在设计分辨率下
            // 直接使用可编辑的预制体坐标，只有设备尺寸/安全区变化时才重新适配。
            if ((panelName == "PreparePanel" || panelName == "BattleHudPanel")
                && Mathf.Abs(size.x - GameBattleLayout.DesignResolution.x) < 0.01f
                && Mathf.Abs(size.y - GameBattleLayout.DesignResolution.y) < 0.01f
                && safe == new Rect(0f, 0f, 1f, 1f))
            {
                _applying = false;
                return;
            }
            float width = size.x;
            if (panelName == "PreparePanel")
            {
                LayoutHand(GameBattleLayout.Calculate(size, safe));
            }
            else if (panelName == "BattleHudPanel")
            {
                LayoutHud(GameBattleLayout.Calculate(size, safe));
            }
            else if (panelName == "HomePanel")
            {
                Place("contentPlate", 0.5f, 0.5f, 0, 0, 1140, 800);
                Place("title", 0.5f, 0.5f, 0, 230, width * 0.85f, 130);
                Place("subtitle", 0.5f, 0.5f, 0, 100, width * 0.8f, 90);
                Place("btnPlay", 0.5f, 0.5f, 0, -70, 420, 130);
                Place("hint", 0.5f, 0.5f, 0, -270, width * 0.75f, 110);
            }
            else if (panelName == "LevelSelectPanel")
            {
                Place("contentPlate", 0.5f, 0.5f, 0, 0, 1660, 960);
                Place("title", 0.5f, 0.5f, 0, 345, width * 0.8f, 100);
                for (int i = 0; i < 6; i++)
                {
                    float x = (i % 3 - 1) * 400;
                    float y = 130 - i / 3 * 220;
                    Place("chapter" + (i + 1), 0.5f, 0.5f, x, y, 350, 190);
                }
                Place("hint", 0.5f, 0.5f, 0, -290, width * 0.8f, 100);
                Place("btnHome", 0.5f, 0.5f, 0, -385, 340, 100);
            }
            else if (panelName == "SettlePanel")
            {
                Place("background", 0.5f, 0.5f, 0, 0, 1000, 670);
                Place("titleText", 0.5f, 0.5f, 0, 160, 800, 100);
                Place("detailText", 0.5f, 0.5f, 0, 20, 800, 150);
                Place("btnContinue", 0.5f, 0.5f, 0, -180, 350, 110);
            }
            else if (panelName == "RewardPanel")
            {
                Place("background", 0.5f, 0.5f, 0, 0, 1380, 778);
                Place("heading", 0.5f, 0.5f, 0, 245, width * 0.85f, 100);
                for (int i = 0; i < 3; i++)
                {
                    float cardWidth = 340f;
                    Place("choice" + i, 0.5f, 0.5f, (i - 1) * (cardWidth + 20), -30, cardWidth, 350);
                    Place("choice" + i + "/label", 0.5f, 0.5f, 0, -70, cardWidth - 26, 190);
                }
            }
            _applying = false;
        }

        private void LayoutHand(GameBattleLayout.Regions areas)
        {
            Transform row = transform.Find("cards");
            if (row != null)
            {
                // 自己计算每个槽位，避免 HorizontalLayoutGroup 在横屏侧栏里又把牌挤回一行。
                HorizontalLayoutGroup layout = row.GetComponent<HorizontalLayoutGroup>();
                if (layout != null)
                {
                    layout.enabled = false;
                }
                Rect hand = GameBattleLayout.InRegion(areas.Left, 0.50f, 1f, 0.65f);
                PlaceAbsolute("cards", hand);
                for (int i = 0; i < row.childCount; i++)
                {
                    Rect card = GameBattleLayout.HandCard(hand, areas.Safe.height, i);
                    // HandCard 返回面板坐标，子节点却以 cards 左下角为原点，因此减去手牌区域起点。
                    // 每次从区域重新计算，不能在上一次位置上追加间距，否则缩放窗口后两列会不断向外漂。
                    Vector2 center = card.center - hand.position;
                    Place("cards/" + row.GetChild(i).name, 0f, 0f, center.x, center.y, card.width, card.height);
                }
            }
            InRegion("hintText", areas.Left, 0.92f, 0.94f, 0.12f);
            InRegion("hintPlate", areas.Left, 0.92f, 1f, 0.15f);
            InRegion("btnReady", areas.Right, 0.06f, 0.94f, 0.10f);
            InRegion("btnRefresh", areas.Right, 0.18f, 0.94f, 0.10f);
            InRegion("btnEye", areas.Right, 0.30f, 0.88f, 0.095f);
            InRegion("btnSkip", areas.Right, 0.42f, 0.88f, 0.095f);
        }

        private void LayoutHud(GameBattleLayout.Regions areas)
        {
            Rect header = areas.Header;
            Rect phase = new Rect(header.xMin, header.yMin + header.height * 0.4f, header.width * 0.22f, header.height * 0.52f);
            PlaceAbsolute("phaseIcon", phase);
            PlaceAbsolute("phaseLabel", phase);
            PlaceAbsolute("roundText", new Rect(header.xMin + header.width * 0.24f, phase.yMin, header.width * 0.76f, phase.height));
            InRegion("progressBar", header, 0.18f, 0.96f, 0.12f);
            InRegion("progressFill", header, 0.18f, 0.96f, 0.12f);
            InRegion("btnHome", areas.Right, 0.95f, 0.94f, 0.08f);
            InRegion("infoPlate", areas.Right, 0.78f, 1f, 0.22f);
            InRegion("countText", areas.Right, 0.84f, 0.92f, 0.075f);
            InRegion("logText", areas.Right, 0.755f, 0.90f, 0.075f);
            InRegion("assistText", areas.Left, 0.065f, 0.94f, 0.10f);
            InRegion("assistPlate", areas.Left, 0.065f, 1f, 0.125f);
            // Boss 信息也留在右栏，不再挤在棋盘上沿和回合标题之间。
            InRegion("bossFrame", areas.Right, 0.70f, 0.90f, 0.018f);
            InRegion("bossBar", areas.Right, 0.70f, 0.90f, 0.018f);
            InRegion("bossText", areas.Right, 0.675f, 0.94f, 0.025f);
        }

        private void InRegion(string path, Rect area, float y, float width, float height)
        {
            PlaceAbsolute(path, GameBattleLayout.InRegion(area, y, width, height));
        }

        /// <summary>每次由原始区域计算绝对位置，不在上次位置上继续加偏移，反复缩放不会越移越远。</summary>
        private void PlaceAbsolute(string path, Rect area)
        {
            Place(path, 0f, 0f, area.center.x, area.center.y, area.width, area.height);
            Transform node = transform.Find(path);
            Text text = node == null ? null : node.GetComponent<Text>();
            if (text != null)
            {
                text.resizeTextForBestFit = true;
                text.resizeTextMinSize = 14;
                text.resizeTextMaxSize = text.fontSize;
                text.horizontalOverflow = HorizontalWrapMode.Wrap;
                text.verticalOverflow = VerticalWrapMode.Truncate;
            }
        }

        /// <summary>统一设置锚点和中心坐标；不改字体/美术引用，换屏幕后仍可复用同一预制体。</summary>
        private void Place(string path, float ax, float ay, float x, float y, float width, float height)
        {
            RectTransform rect = transform.Find(path) as RectTransform;
            if (rect == null)
            {
                return;
            }
            rect.anchorMin = rect.anchorMax = new Vector2(ax, ay);
            rect.pivot = Vector2.one * 0.5f;
            rect.anchoredPosition = new Vector2(x, y);
            rect.sizeDelta = new Vector2(width, height);
        }
    }
}
