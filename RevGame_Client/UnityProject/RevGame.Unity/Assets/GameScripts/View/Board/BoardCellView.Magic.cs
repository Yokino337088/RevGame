using UnityEngine;

namespace RevGame.View
{
    /// <summary>格子的轻量表现。共享章节材质由BoardView租用；眼睛帧由预制体引用、BoardStatus图集绑定。</summary>
    public sealed partial class BoardCellView
    {
        [Tooltip("正视眼睛的开、半闭、近闭、闭合四帧，按此顺序装配。")]
        [SerializeField] private Sprite[] eyeBlinkFrames;
        private SpriteRenderer _surfaceRenderer;
        private Material _originalSurface;
        private int _eyeFrame = -1;
        public int EyeFrame { get { return _eyeFrame; } }
        public int EyeFrameCount { get { return eyeBlinkFrames == null ? 0 : eyeBlinkFrames.Length; } }
        public bool IsEyeVisible { get { return eyeRenderer != null && eyeRenderer.enabled; } }
        public SpriteRenderer SurfaceRenderer { get { return _surfaceRenderer; } }
        public SpriteRenderer PlacementRenderer { get { return highlightRenderer; } }

        /// <summary>只替换sharedMaterial引用，不访问renderer.material，81格共享一份章节材质。</summary>
        public void ApplySurface(Material material)
        {
            if (_surfaceRenderer == null)
            {
                _surfaceRenderer = GetComponent<SpriteRenderer>();
                _originalSurface = _surfaceRenderer.sharedMaterial;
            }
            _surfaceRenderer.sharedMaterial = material;
        }

        /// <summary>回池先摘掉章节材质，再由棋盘宿主归还资源租约，避免池中对象保活上章AB。</summary>
        private void RestoreSurface()
        {
            if (_surfaceRenderer != null) { _surfaceRenderer.sharedMaterial = _originalSurface; }
            _eyeFrame = -1;
        }

        /// <summary>由棋盘单一时钟每秒最多20次驱动。绝大部分时间睁眼，只有短窗口眨眼，按坐标错开。</summary>
        public void AnimateEye(float seconds)
        {
            if (!IsEyeVisible || EyeFrameCount != 4) { return; }
            float age = Mathf.Repeat(seconds + Position.X * .37f + Position.Y * .61f, 3.8f);
            int frame = age < 3.25f ? 0 : age < 3.35f ? 1 : age < 3.43f ? 2 : age < 3.55f ? 3 : age < 3.63f ? 2 : age < 3.73f ? 1 : 0;
            if (_eyeFrame == frame) { return; }
            _eyeFrame = frame;
            eyeRenderer.sprite = eyeBlinkFrames[frame];
        }
    }
}
