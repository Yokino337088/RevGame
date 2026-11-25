// ============================================================
// GameBattleLayout.cs —— 横屏战斗共用的空间分配算法。
// 相机、手牌、HUD 和增益提示必须使用同一份区域，否则各自适配后仍可能互相遮挡。
// 返回的坐标以画布左下角为原点；它不是另一套 UI 管理器，只计算几个矩形。
// ============================================================
using Revolution;
using UnityEngine;

namespace RevGame.UI
{
    public static class GameBattleLayout
    {
        /// <summary>项目唯一 UI 设计基准；屏幕与窗口尺寸变化时，画布和棋盘都保持这套比例。</summary>
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
        /// <summary>读取 Revolution 的屏幕安全区，设备模拟器和真机使用同一归一化坐标口径。</summary>
        public static Rect SafeViewport()
        {
            return RevUIViewport.GetScreenSafeArea();
        }
        /// <summary>棋盘相机、手牌和侧栏在所有屏幕上使用同一个安全设计框，背景仍覆盖完整画幅。</summary>
        public static Rect BoardViewport(Vector2 size)
        {
            return FitBoardViewport(size, SafeViewport());
        }

        /// <summary>在真实安全区内等比内接设计矩形。单边刘海只扣除一次，中心随安全区平移。</summary>
        public static Rect FitBoardViewport(Vector2 size, Rect safe)
        {
            return RevUIViewport.FitDesignFrame(size, safe, DesignResolution);
        }

        /// <summary>将棋盘设计框之外的安全空间分给两侧控件。返回设计坐标中的偏移，按钮和字体仍等比缩放。</summary>
        public static Vector2 SidebarOffsets(Rect safe, Rect frame)
        {
            // 用掉八成额外宽度，超宽窗口也保留相邻关系和外侧余量。
            const float Spread = .8f;
            float limit = DesignResolution.x * .12f;
            Vector2 offsets = RevUIViewport.GetSafeEdgeOffsets(safe, frame, DesignResolution).Horizontal * Spread;
            return new Vector2(Mathf.Max(-limit, offsets.x), Mathf.Min(limit, offsets.y));
        }

        /// <summary>返回贴底、贴顶控件要补偿的纵向距离，单位为设计坐标。棋盘仍使用原设计框。</summary>
        public static Vector2 VerticalEdgeOffsets(Rect safe, Rect frame)
        {
            return RevUIViewport.GetSafeEdgeOffsets(safe, frame, DesignResolution).Vertical;
        }
        /// <summary>独立纯计算入口，方便验证刘海、左右旋转和非法的零尺寸输入。</summary>
        public static Rect NormalizeSafeArea(Rect safe, Vector2 size)
        {
            return RevUIViewport.NormalizeSafeArea(safe, new Rect(Vector2.zero, size));
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
