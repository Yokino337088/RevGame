using UnityEngine;

namespace RevGame.UI
{
    /// <summary>背景按原画比例覆盖视口，控件的安全区独立计算。</summary>
    public static class GameBackgroundLayout
    {
        /// <summary>计算铺满画幅的等比尺寸。使用较大的轴缩放量，让另一轴超出的部分被视口裁掉。</summary>
        public static Vector2 CoverSize(Vector2 viewport, Vector2 artwork)
        {
            if (artwork.x <= 0f || artwork.y <= 0f)
            {
                return viewport;
            }
            return artwork * Mathf.Max(viewport.x / artwork.x, viewport.y / artwork.y);
        }

        /// <summary>在原始图片或图集子区域中居中裁切，令满屏 RawImage 上的图像保留原比例。</summary>
        public static Rect CoverUv(Vector2 viewport, float artworkAspect, Rect source)
        {
            if (viewport.x <= 0f || viewport.y <= 0f || artworkAspect <= 0f)
            {
                return source;
            }
            float viewportAspect = viewport.x / viewport.y;
            var size = new Vector2(source.width * Mathf.Min(1f, viewportAspect / artworkAspect),
                source.height * Mathf.Min(1f, artworkAspect / viewportAspect));
            return new Rect(source.center - size * .5f, size);
        }
    }
}
