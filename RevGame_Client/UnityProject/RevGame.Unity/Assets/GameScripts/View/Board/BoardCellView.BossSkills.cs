// 格面层在共享章节Shader上写实例参数，DOTween只驱动短期脉冲。Collider和棋盘坐标始终不移动。
using DG.Tweening;
using UnityEngine;
namespace RevGame.View
{
    public sealed partial class BoardCellView
    {
        private Tween _bossTileTween;
        private MaterialPropertyBlock _bossTileProperties;
        private float _bossTilePulse;
        public bool HasBossSkillAnimation => _bossTileTween != null && _bossTileTween.IsActive();

        public void PlayBossTile(Color tint, float duration)
        {
            ClearBossTile();
            if (_surfaceRenderer == null) { return; }
            _bossTileProperties ??= new MaterialPropertyBlock();
            _surfaceRenderer.GetPropertyBlock(_bossTileProperties);
            _bossTileProperties.SetColor("_BossTint", tint);
            _bossTilePulse = 1f;
            _bossTileTween = DOTween.To(() => _bossTilePulse, value =>
            {
                _bossTilePulse = value;
                _bossTileProperties.SetFloat("_BossPulse", value);
                _surfaceRenderer.SetPropertyBlock(_bossTileProperties);
                // 复用已有的纯视觉起伏层：只动Sprite，绝不移动根节点上的格子碰撞器。
                SetVictoryWave(Mathf.Clamp01(1f - value));
                if (_victorySurface != null && _victorySurface.enabled) { _victorySurface.SetPropertyBlock(_bossTileProperties); }
            }, 0f, duration).SetEase(Ease.OutSine).OnComplete(() => { _bossTileTween = null; ClearBossTile(); });
        }
        public void ClearBossTile()
        {
            _bossTileTween?.Kill(false); _bossTileTween = null;
            if (_surfaceRenderer != null && _bossTileProperties != null)
            {
                _bossTileProperties.SetFloat("_BossPulse", 0f);
                _surfaceRenderer.SetPropertyBlock(_bossTileProperties);
            }
            SetVictoryWave(1f);
        }
    }
}
