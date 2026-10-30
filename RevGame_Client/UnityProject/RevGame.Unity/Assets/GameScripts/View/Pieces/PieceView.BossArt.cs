using UnityEngine;

namespace RevGame.View
{
    public sealed partial class PieceView
    {
        /// <summary>借用当前棋盘的章节配置，不自行加载；归池清引用后棋盘才释放AB。</summary>
        private BossChapterArtConfig _bossArt;
        private int _frameDirection = 1;
        private Vector3 _normalBodyScale;
        public int BossAppearanceChapter
        {
            get
            {
                return _bossArt == null ? 0 : _bossArt.Chapter;
            }
        }
        /// <summary>同一配置幂等，HUD/血量刷新不能把动画每次重置到首帧。</summary>
        public void BindBossArt(BossChapterArtConfig art)
        {
            if (ReferenceEquals(_bossArt, art))
            {
                return;
            }
            RestoreBossArt();

            if (art == null)
            {
                return;
            }
            art.Validate(art.Chapter);
            _bossArt = art;
            _normalBodyScale = _bodyRestScale;
            _bodyRestScale = _normalBodyScale * art.BodyScale;

            if (bodyRenderer != null)
            {
                bodyRenderer.transform.localScale = _bodyRestScale;
            }
            _frames = art.Frames;
            _frameInterval = art.FrameSeconds;
            _frameIndex = 0;
            _frameTimer = 0;
            _frameDirection = 1;

            if (bodyRenderer != null)
            {
                bodyRenderer.sprite = _frames[0];

                if (_birthProgress > 0f || _stormProgress > 0f || _eyeGarrison > 0f)
                {
                    SetVisualUv(bodyRenderer.sprite);
                    bodyRenderer.SetPropertyBlock(_visualProperties);
                }
            }

            if (art.BuiltInDecorations)
            {
                if (baseRenderer != null)
                {
                    baseRenderer.enabled = false;
                }
                if (crownRenderer != null)
                {
                    crownRenderer.enabled = false;
                }
            }
        }
        /// <summary>往返端点不重复停留，8张源图播放为0…7…1；普通棋子保持原循环行为。</summary>
        private void AdvanceAnimationIndex()
        {
            if (_bossArt == null || !_bossArt.PingPong)
            {
                _frameIndex = (_frameIndex + 1) % _frames.Length;
                return;
            }
            int next = _frameIndex + _frameDirection;

            if (next >= _frames.Length)
            {
                _frameDirection = -1;
                next = _frames.Length - 2;
            }
            else

                if (next < 0)
                {
                    _frameDirection = 1;
                    next = 1;
                }
            _frameIndex = next;
        }
        /// <summary>复用为普通棋子前恢复身体尺寸与普通帧，不把旧Boss引用留在池中。</summary>
        private void RestoreBossArt()
        {
            if (_bossArt == null)
            {
                return;
            }
            _bossArt = null;
            _bodyRestScale = _normalBodyScale;

            if (bodyRenderer != null)
            {
                bodyRenderer.transform.localScale = _bodyRestScale;
            }
            _frames = _entry == null ? null : _entry.idleFrames;
            _frameInterval = _entry == null ? .2f : _entry.frameInterval;
            _frameIndex = 0;
            _frameTimer = 0;
            _frameDirection = 1;

            if (bodyRenderer != null)
            {
                bodyRenderer.sprite = _frames == null || _frames.Length == 0 ? null : _frames[0];
            }
            if (baseRenderer != null && _entry != null)
            {
                baseRenderer.sprite = _entry.baseSprite;
                baseRenderer.enabled = _entry.baseSprite != null;
            }
        }
    }
}
