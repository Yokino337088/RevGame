// ============================================================
// RevUIViewport.cs —— 为面板和场景相机统一计算屏幕视口。
// 归一化矩形可跨画布和相机使用，写入控件时再换算成父级的布局单位。
// ============================================================
using UnityEngine;

namespace Revolution
{
    /// <summary>屏幕安全区和设计画幅的共用计算入口。归一化坐标以视口左下角为原点，右上角为 (1, 1)。</summary>
    public static class RevUIViewport
    {
        /// <summary>整个视口，用于没有内缩的显示区域和尺寸初始化的回退。</summary>
        public static Rect Full => new Rect(0f, 0f, 1f, 1f);

        /// <summary>读取主玩家窗口的归一化安全区。设备模拟器的尺寸和安全区取自同一个设备接口。</summary>
        public static Rect GetScreenSafeArea()
        {
            var window = new Rect(0f, 0f, UnityEngine.Device.Screen.width, UnityEngine.Device.Screen.height);
            return NormalizeSafeArea(UnityEngine.Device.Screen.safeArea, window);
        }

        /// <summary>将同一坐标系中的安全区裁到视口并归一化。尺寸尚未就绪或没有交集时使用完整视口。</summary>
        public static Rect NormalizeSafeArea(Rect safeArea, Rect viewport)
        {
            if (!HasArea(safeArea) || !HasArea(viewport))
            {
                return Full;
            }
            float left = Mathf.Clamp01((safeArea.xMin - viewport.xMin) / viewport.width);
            float right = Mathf.Clamp01((safeArea.xMax - viewport.xMin) / viewport.width);
            float bottom = Mathf.Clamp01((safeArea.yMin - viewport.yMin) / viewport.height);
            float top = Mathf.Clamp01((safeArea.yMax - viewport.yMin) / viewport.height);
            return right > left && top > bottom ? Rect.MinMaxRect(left, bottom, right, top) : Full;
        }

        /// <summary>在归一化安全区内居中放入设计尺寸，宽高使用同一个缩放值。视口尺寸和设计尺寸可分别使用像素、画布单位。</summary>
        public static Rect FitDesignFrame(Vector2 viewportSize, Rect safeArea, Vector2 designResolution)
        {
            if (!HasSize(viewportSize))
            {
                return Full;
            }
            Rect safe = NormalizeSafeArea(safeArea, Full);
            if (!HasSize(designResolution))
            {
                return safe;
            }

            float scale = Mathf.Min(safe.width * viewportSize.x / designResolution.x, safe.height * viewportSize.y / designResolution.y);
            var size = new Vector2(designResolution.x * scale / viewportSize.x, designResolution.y * scale / viewportSize.y);
            return HasSize(size) ? new Rect(safe.center - size * .5f, size) : safe;
        }

        /// <summary>把 FitDesignFrame 算出的矩形应用到内容容器，保留枢轴与子控件坐标。返回实际缩放值；容器尚未就绪时返回 0。</summary>
        public static float ApplyDesignFrame(RectTransform content, Rect frame, Vector2 designResolution)
        {
            if (content == null || !(content.parent is RectTransform parent) || !HasSize(parent.rect.size) ||
                !HasArea(frame) || !HasSize(designResolution))
            {
                return 0f;
            }

            float scale = frame.height * parent.rect.height / designResolution.y;
            if (!IsPositiveFinite(scale))
            {
                return 0f;
            }

            Vector2 anchor = frame.position + Vector2.Scale(frame.size, content.pivot);
            content.anchorMin = content.anchorMax = anchor;
            content.sizeDelta = designResolution;
            content.anchoredPosition = Vector2.zero;
            content.localScale = new Vector3(scale, scale, content.localScale.z);
            return scale;
        }

        // 统一过滤尚未初始化的尺寸，避免几何计算输出 NaN 或无穷大。
        internal static bool HasSize(Vector2 size)
        {
            return IsPositiveFinite(size.x) && IsPositiveFinite(size.y);
        }

        // 既检查宽高也检查实际边界，有效矩形才能用于裁切。
        private static bool HasArea(Rect area)
        {
            return HasSize(area.size) && IsFinite(area.xMin) && IsFinite(area.xMax) &&
                   IsFinite(area.yMin) && IsFinite(area.yMax);
        }

        // 尺寸分量要求正且有限，比较也会拒绝 NaN。
        private static bool IsPositiveFinite(float value)
        {
            return value > 0f && value < float.PositiveInfinity;
        }

        // 位置可以是负数，裁切前仅过滤非有限数。
        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }
}
