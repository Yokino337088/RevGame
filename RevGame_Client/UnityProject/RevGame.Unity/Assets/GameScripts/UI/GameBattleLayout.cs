// ============================================================
// GameBattleLayout.cs —— 横屏战斗共用的空间分配算法。
// 相机、手牌、HUD 和增益提示必须使用同一份区域，否则各自适配后仍可能互相遮挡。
// 返回的坐标以画布左下角为原点；它不是另一套 UI 管理器，只计算几个矩形。
// ============================================================
using UnityEngine;

namespace RevGame.UI
{
    public static class GameBattleLayout
    {
        /// <summary>项目唯一 UI 设计基准；手机像素不必相同，由 CanvasScaler 按此基准等比适配。</summary>
        public static readonly Vector2 DesignResolution = new Vector2(1920f, 1080f);

        /// <summary>各区互不重叠；左右区再按高度比例分配控件，屏幕缩放时不会积累位移。</summary>
        public struct Regions
        {
            public Rect Safe;
            public Rect Left;
            public Rect Right;
            public Rect Board;
            public Rect Header;
        }

        /// <summary>先用设备自己的宽高归一化安全区，不能拿模拟手机的像素直接套到 Game 窗口。</summary>
        public static Rect SafeViewport()
        {
            // UnityEngine.Device.Screen 在 Simulator 中代表模拟手机，真机上与 Screen 相同。
            // 普通 Screen.width 在编辑器可能仍代表另一个 Game 窗口，混用会把侧栏推到屏幕外。
            Vector2 deviceSize = new Vector2(UnityEngine.Device.Screen.width, UnityEngine.Device.Screen.height);
            return NormalizeSafeArea(UnityEngine.Device.Screen.safeArea, deviceSize);
        }

        /// <summary>独立纯计算入口，方便验证刘海、左右旋转和非法的零尺寸输入。</summary>
        public static Rect NormalizeSafeArea(Rect safe, Vector2 size)
        {
            if (size.x <= 0f || size.y <= 0f || safe.width <= 0f || safe.height <= 0f)
            {
                return new Rect(0f, 0f, 1f, 1f);
            }
            float left = Mathf.Clamp01(safe.xMin / size.x);
            float right = Mathf.Clamp01(safe.xMax / size.x);
            float bottom = Mathf.Clamp01(safe.yMin / size.y);
            float top = Mathf.Clamp01(safe.yMax / size.y);
            if (right <= left || top <= bottom) { return new Rect(0f, 0f, 1f, 1f); }
            return Rect.MinMaxRect(left, bottom, right, top);
        }

        /// <summary>先预留左右操作区和顶栏，再给棋盘尽可能大的正方形；窄横屏也不会挤进手牌。</summary>
        public static Regions Calculate(Vector2 size, Rect viewport)
        {
            Rect safe = new Rect(viewport.x * size.x, viewport.y * size.y, viewport.width * size.x, viewport.height * size.y);
            float margin = Mathf.Min(safe.width, safe.height) * 0.018f;
            safe = Rect.MinMaxRect(safe.xMin + margin, safe.yMin + margin, safe.xMax - margin, safe.yMax - margin);
            float headerHeight = safe.height * 0.12f;
            float availableHeight = safe.height - headerHeight - margin;
            float side = Mathf.Min(availableHeight, safe.width * 0.54f);
            Rect board = new Rect(safe.center.x - side * 0.5f, safe.yMin + (availableHeight - side) * 0.5f, side, side);
            float sidebarWidth = board.xMin - margin - safe.xMin;
            return new Regions
            {
                Safe = safe,
                Board = board,
                Left = new Rect(safe.xMin, safe.yMin, sidebarWidth, safe.height),
                Right = new Rect(board.xMax + margin, safe.yMin, sidebarWidth, safe.height),
                Header = new Rect(board.xMin, safe.yMax - headerHeight, side, headerHeight)
            };
        }

        /// <summary>把“某区域中的比例”换成画布坐标，参数 (0.5, 0.5) 就是该区域中心。</summary>
        public static Rect InRegion(Rect region, float centerY, float widthRatio, float heightRatio)
        {
            Vector2 size = new Vector2(region.width * widthRatio, region.height * heightRatio);
            Vector2 center = new Vector2(region.center.x, region.yMin + region.height * centerY);
            return new Rect(center - size * 0.5f, size);
        }

        /// <summary>计算两列三行中的一张牌；返回手牌区域内的绝对矩形，供真实布局与边界检查共用。</summary>
        public static Rect HandCard(Rect hand, float safeHeight, int index)
        {
            // ★ 横向和纵向的间距要分开：三行需要节省高度，但两列之间需要留出清楚的空隙。
            // 以侧栏宽度的 16% 作为列间距，并限制在屏幕高度的 6% 内，宽屏上也不会把两列拉得过远。
            float verticalGap = safeHeight * 0.012f;
            float horizontalGap = Mathf.Min(hand.width * 0.16f, safeHeight * 0.06f);
            // 两边各留一份小边距；卡片宽度同时受两列总宽和三行总高限制，窄横屏也不能挤进棋盘。
            float widthLimit = (hand.width - verticalGap * 2f - horizontalGap) * 0.5f;
            float heightLimit = (hand.height - verticalGap * 2f) / 3f;
            float cardWidth = Mathf.Min(widthLimit, heightLimit * 0.75f);
            Vector2 cardSize = new Vector2(cardWidth, cardWidth / 0.75f);
            Vector2 offset = new Vector2((index % 2 - 0.5f) * (cardSize.x + horizontalGap), (1 - index / 2) * (cardSize.y + verticalGap));
            return new Rect(hand.center + offset - cardSize * 0.5f, cardSize);
        }
    }
}
