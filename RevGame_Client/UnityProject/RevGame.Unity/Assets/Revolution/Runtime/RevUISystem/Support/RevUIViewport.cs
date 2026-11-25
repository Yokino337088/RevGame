// ============================================================
// RevUIViewport.cs —— 为面板和场景相机统一计算屏幕视口。
// 归一化矩形可跨画布和相机使用，写入控件时再换算成父级的布局单位。
// ============================================================
using UnityEngine;

namespace Revolution
{
    /// <summary>等比原画在可用视口中的显示方式，由面板选择构图策略。</summary>
    public enum RevUIArtworkFit
    {
        /// <summary>放大到覆盖视口，超出的边沿由实际窗口或遮罩裁掉。</summary>
        Cover,

        /// <summary>把整张原画放入视口，剩余空间由面板底色显示。</summary>
        Contain
    }

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
            return TryNormalizeSafeArea(safeArea, viewport, out Rect normalized) ? normalized : Full;
        }

        /// <summary>计算实际交集。没有可用区域时让布局保留上次结果，直到窗口或内容边界就绪。</summary>
        internal static bool TryNormalizeSafeArea(Rect safeArea, Rect viewport, out Rect normalized)
        {
            normalized = default;
            if (!HasArea(safeArea) || !HasArea(viewport))
            {
                return false;
            }
            float left = Mathf.Clamp01((safeArea.xMin - viewport.xMin) / viewport.width);
            float right = Mathf.Clamp01((safeArea.xMax - viewport.xMin) / viewport.width);
            float bottom = Mathf.Clamp01((safeArea.yMin - viewport.yMin) / viewport.height);
            float top = Mathf.Clamp01((safeArea.yMax - viewport.yMin) / viewport.height);
            if (right <= left || top <= bottom)
            {
                return false;
            }
            normalized = Rect.MinMaxRect(left, bottom, right, top);
            return true;
        }

        /// <summary>取两个像素矩形的交集，供已指定内容区的面板限制可用范围。</summary>
        internal static Rect Intersect(Rect first, Rect second)
        {
            return Rect.MinMaxRect(Mathf.Max(first.xMin, second.xMin), Mathf.Max(first.yMin, second.yMin),
                Mathf.Min(first.xMax, second.xMax), Mathf.Min(first.yMax, second.yMax));
        }

        /// <summary>把轴对齐的屏幕空间 RectTransform 投影成实际屏幕像素矩形。</summary>
        internal static Rect GetScreenBounds(RectTransform target, Camera camera)
        {
            Rect rect = target.rect;
            Vector2 first = RectTransformUtility.WorldToScreenPoint(camera, target.TransformPoint(rect.min));
            Vector2 last = RectTransformUtility.WorldToScreenPoint(camera, target.TransformPoint(rect.max));
            return Rect.MinMaxRect(Mathf.Min(first.x, last.x), Mathf.Min(first.y, last.y),
                Mathf.Max(first.x, last.x), Mathf.Max(first.y, last.y));
        }

        /// <summary>让同一比例作用在原画宽和高上。视口或源图尺寸尚未就绪时返回零。</summary>
        public static Vector2 FitArtworkSize(Vector2 viewportSize, Vector2 artworkSize, RevUIArtworkFit fit)
        {
            if (!HasSize(viewportSize) || !HasSize(artworkSize))
            {
                return Vector2.zero;
            }
            float widthScale = viewportSize.x / artworkSize.x;
            float heightScale = viewportSize.y / artworkSize.y;
            float scale = fit == RevUIArtworkFit.Contain ? Mathf.Min(widthScale, heightScale) : Mathf.Max(widthScale, heightScale);
            Vector2 size = artworkSize * scale;
            return HasSize(size) ? size : Vector2.zero;
        }

        /// <summary>在固定全屏 RawImage 内居中铺满。传入原始 UV 和采样区宽高比，每次从源值重算。</summary>
        public static Rect CoverArtworkUv(Vector2 viewportSize, float artworkAspect, Rect sourceUv)
        {
            if (!HasSize(viewportSize) || !IsPositiveFinite(artworkAspect) || !HasArea(sourceUv))
            {
                return sourceUv;
            }
            float viewportAspect = viewportSize.x / viewportSize.y;
            var size = new Vector2(sourceUv.width * Mathf.Min(1f, viewportAspect / artworkAspect),
                sourceUv.height * Mathf.Min(1f, artworkAspect / viewportAspect));
            if (!HasSize(size) || size.Equals(sourceUv.size))
            {
                return sourceUv;
            }
            return new Rect(sourceUv.center - size * .5f, size);
        }

        /// <summary>把设计框外的可用空间换算为设计单位。横轴依次是左、右，纵轴依次是下、上。起始边偏移为负。</summary>
        public static (Vector2 Horizontal, Vector2 Vertical) GetSafeEdgeOffsets(Rect available, Rect designFrame,
            Vector2 designResolution)
        {
            if (!HasArea(available) || !HasArea(designFrame) || !HasSize(designResolution))
            {
                return default;
            }
            var horizontal = new Vector2(-Mathf.Max(0f, designFrame.xMin - available.xMin),
                Mathf.Max(0f, available.xMax - designFrame.xMax)) * (designResolution.x / designFrame.width);
            var vertical = new Vector2(-Mathf.Max(0f, designFrame.yMin - available.yMin),
                Mathf.Max(0f, available.yMax - designFrame.yMax)) * (designResolution.y / designFrame.height);
            return (horizontal, vertical);
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
        internal static bool HasArea(Rect area)
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

        /// <summary>保存布局接管前的锚点、尺寸与缩放，组件停用或回池时归还。</summary>
        internal readonly struct RectState
        {
            /// <summary>父级中的原锚点下界。</summary>
            private readonly Vector2 _anchorMin;
            /// <summary>父级中的原锚点上界。</summary>
            private readonly Vector2 _anchorMax;
            /// <summary>原锚点偏移，单位为父级布局单位。</summary>
            private readonly Vector2 _position;
            /// <summary>原尺寸增量，配合锚点恢复拉伸布局。</summary>
            private readonly Vector2 _size;
            /// <summary>原局部缩放，适配时可继续沿用 Z 分量。</summary>
            public readonly Vector3 Scale;

            /// <summary>在本轮首次接管前获取原始布局。</summary>
            public RectState(RectTransform target)
            {
                _anchorMin = target.anchorMin;
                _anchorMax = target.anchorMax;
                _position = target.anchoredPosition;
                _size = target.sizeDelta;
                Scale = target.localScale;
            }

            /// <summary>先恢复锚点，再恢复相对位置、尺寸和缩放。</summary>
            public void Restore(RectTransform target)
            {
                target.anchorMin = _anchorMin;
                target.anchorMax = _anchorMax;
                target.anchoredPosition = _position;
                target.sizeDelta = _size;
                target.localScale = Scale;
            }
        }
    }
}
