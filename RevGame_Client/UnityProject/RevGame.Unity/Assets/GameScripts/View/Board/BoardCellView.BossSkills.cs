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

        /// <summary>
        /// 技能格面双跳：首次冲击抬起，回落后再轻弹一次。延迟由到技能中心的距离决定，形成逐格传播的波浪。
        /// duration/delay仅决定表现，玩家能否被命中仍由服务判断；每格只保留一条Tween，后来的冲击替换旧冲击。
        /// </summary>
        public void PlayBossTile(Color tint, float duration, float delay = 0f)
        {
            ClearBossTile();
            if (_surfaceRenderer == null) { return; }
            _bossTileProperties ??= new MaterialPropertyBlock();
            _surfaceRenderer.GetPropertyBlock(_bossTileProperties);
            // 自定义参数块必须携带真实图集页；仅写颜色可能让某些SpriteRenderer路径退回材质的白色默认贴图。
            if (_surfaceRenderer.sprite != null) { _bossTileProperties.SetTexture("_MainTex", _surfaceRenderer.sprite.texture); }
            _bossTileProperties.SetColor("_BossTint", tint);
            _bossTilePulse = 0f;
            _bossTileTween = DOTween.To(() => _bossTilePulse, value =>
            {
                _bossTilePulse = value;
                float flash = (1f - value) * (.74f + .26f * Mathf.Cos(value * Mathf.PI * 8f));
                _bossTileProperties.SetFloat("_BossPulse", flash);
                _surfaceRenderer.SetPropertyBlock(_bossTileProperties);
                // 复用已有的纯视觉起伏层：只动Sprite，绝不移动根节点上的格子碰撞器。
                float wave = value < .65f ? value / .65f : (value - .65f) / .35f;
                SetVictoryWave(Mathf.Clamp01(wave), value < .65f ? .62f : .23f);
                if (_victorySurface != null && _victorySurface.enabled) { _victorySurface.SetPropertyBlock(_bossTileProperties); }
            }, 1f, Mathf.Clamp(duration, .65f, 1.25f)).SetDelay(delay).SetEase(Ease.Linear)
                .OnComplete(() => { _bossTileTween = null; ClearBossTile(); });
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
