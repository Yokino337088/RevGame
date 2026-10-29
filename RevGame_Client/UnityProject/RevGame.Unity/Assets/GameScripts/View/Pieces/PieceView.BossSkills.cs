// 棋子上的蓄力、冰壳、黑洞下沉共用原身体Shader；动画只表现服务事实，不负责解除业务状态。
using DG.Tweening;
using RevGame.Core;
using UnityEngine;
namespace RevGame.View
{
    public sealed partial class PieceView
    {
        private Tween _bossCastTween, _bossSinkTween;
        private float _bossCharge, _bossIce;
        private Color _bossTint;
        private bool _bossSunk;
        private Vector3 _bossRootScale;
        public bool BossHiddenVisual => _bossSunk;
        public float BossIceVisual => _bossIce;

        /// <summary>专属招式的身体涌能随章节变色；冰封终焉保持冰壳直到真实爆炸事件到达。</summary>
        public void PlayBossCharge(BossSkillKind kind, float seconds, bool windingUp = true)
        {
            _bossCastTween?.Kill(false);
            _bossTint = BossSkillFxView.ExclusiveTint(kind);
            _bossIce = windingUp && kind == BossSkillKind.GlacialAegis ? 1f : 0f;
            _bossCharge = 1f;
            ApplyBossShader();
            _bossCastTween = DOTween.To(() => _bossCharge, value => { _bossCharge = value; ApplyBossShader(); }, 0f, seconds)
                .SetEase(Ease.InQuad).OnComplete(() => { _bossCastTween = null; _bossIce = 0f; ApplyBossShader(); });
        }

        /// <summary>逻辑已隐藏仍保留身份；整颗视图缩入地面，收到服务返场事件后重新升起，不删除池对象。</summary>
        public void SetBossStatus(bool hidden, bool frozen, bool weakened)
        {
            _bossIce = frozen ? 1f : 0f;
            if (weakened) { _bossTint = new Color(.75f, .65f, .2f); }
            if (_bossSunk != hidden)
            {
                _bossSinkTween?.Kill(false);
                if (!_bossSunk) { _bossRootScale = transform.localScale; }
                _bossSunk = hidden;
                _bossSinkTween = transform.DOScale(hidden ? Vector3.zero : _bossRootScale, .38f)
                    .SetEase(hidden ? Ease.InBack : Ease.OutBack).OnComplete(() => _bossSinkTween = null);
            }
            ApplyBossShader();
        }

        private void ApplyBossShader()
        {
            if (bodyRenderer == null || bodyRenderer.sprite == null || _visualProperties == null || _bossDeathActive) { return; }
            SetVisualUv(bodyRenderer.sprite);
            _visualProperties.SetFloat("_BossCharge", _bossCharge);
            _visualProperties.SetFloat("_BossIce", _bossIce);
            _visualProperties.SetColor("_BossTint", _bossTint);
            bodyRenderer.SetPropertyBlock(_visualProperties);
        }

        /// <summary>回池及离场必须撤销Tween和材质参数，否则同一池实例会把冰壳/零缩放带给下颗棋。</summary>
        private void ClearBossSkillVisual()
        {
            _bossCastTween?.Kill(false); _bossCastTween = null;
            _bossSinkTween?.Kill(false); _bossSinkTween = null;
            if (_bossSunk) { transform.localScale = _bossRootScale; }
            _bossSunk = false; _bossCharge = 0f; _bossIce = 0f;
            ApplyBossShader();
        }
    }
}
