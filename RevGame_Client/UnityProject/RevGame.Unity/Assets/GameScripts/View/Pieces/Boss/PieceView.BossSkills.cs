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
        /// <summary>上次写给身体渲染器的序列帧，空值表示共用属性块或贴图已经需要重新核对。</summary>
        private Sprite _submittedBossSprite;
        /// <summary>上次写入的图集页。即使 Sprite 相同，图集重新绑定也需要更新它。</summary>
        private Texture2D _submittedBossTexture;
        /// <summary>上次提交的 Boss 蓄力强度，范围 0 到 1，只有提交成功才更新。</summary>
        private float _submittedBossCharge;
        /// <summary>上次提交的冰壳强度，0 无冰壳，1 显示冰壳。</summary>
        private float _submittedBossIce;
        /// <summary>上次提交的技能染色，随技能和虚弱状态变化。</summary>
        private Color _submittedBossTint;
#if UNITY_EDITOR
        /// <summary>此实例累计真正写入过几次 Boss 属性块，供相同输入及回池复用断言读取。</summary>
        public int BossShaderWrites { get; private set; }
#endif
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
        /// <summary>同步冰壳、虚弱与下沉状态；同一隐藏状态不重开动画，下沉与返场仍保留本棋身份。</summary>
        public void SetBossStatus(bool hidden, bool frozen, bool weakened)
        {
            _bossIce = frozen ? 1f : 0f;

            if (weakened)
            {
                _bossTint = new Color(.75f, .65f, .2f);
            }
            if (_bossSunk != hidden)
            {
                _bossSinkTween?.Kill(false);

                if (!_bossSunk)
                {
                    _bossRootScale = transform.localScale;
                }
                _bossSunk = hidden;
                _bossSinkTween = transform.DOScale(hidden ? Vector3.zero : _bossRootScale, .38f)
                    .SetEase(hidden ? Ease.InBack : Ease.OutBack).OnComplete(() => _bossSinkTween = null);
            }
            ApplyBossShader();
        }
        /// <summary>身体帧、图集与技能参数相同就沿用已提交的属性块；演出接管后由收尾入口失效缓存。</summary>
        private void ApplyBossShader()
        {
            if (bodyRenderer == null || _visualProperties == null || _bossDeathActive)
            {
                return;
            }

            Sprite sprite = bodyRenderer.sprite;
            if (sprite == null)
            {
                return;
            }

            Texture2D texture = sprite.texture;
            if (ReferenceEquals(_submittedBossSprite, sprite) && ReferenceEquals(_submittedBossTexture, texture)
                && _submittedBossCharge == _bossCharge && _submittedBossIce == _bossIce && _submittedBossTint.Equals(_bossTint))
            {
                return;
            }

            SetVisualUv(sprite);
            _visualProperties.SetFloat("_BossCharge", _bossCharge);
            _visualProperties.SetFloat("_BossIce", _bossIce);
            _visualProperties.SetColor("_BossTint", _bossTint);
            bodyRenderer.SetPropertyBlock(_visualProperties);
            // 先提交再记录，避免图集或其它表现重绑后误认为渲染器仍持有旧的参数。
            _submittedBossSprite = sprite;
            _submittedBossTexture = texture;
            _submittedBossCharge = _bossCharge;
            _submittedBossIce = _bossIce;
            _submittedBossTint = _bossTint;
#if UNITY_EDITOR
            BossShaderWrites++;
#endif
        }

        /// <summary>在换绑定、重写共用图集参数和清除渲染器属性块时作废旧提交，释放上一次的图集引用。</summary>
        private void InvalidateBossShaderSubmission()
        {
            _submittedBossSprite = null;
            _submittedBossTexture = null;
        }
        /// <summary>回池及离场必须撤销Tween和材质参数，否则同一池实例会把冰壳/零缩放带给下颗棋。</summary>
        private void ClearBossSkillVisual()
        {
            _bossCastTween?.Kill(false);
            _bossCastTween = null;
            _bossSinkTween?.Kill(false);
            _bossSinkTween = null;

            if (_bossSunk)
            {
                transform.localScale = _bossRootScale;
            }
            _bossSunk = false;
            _bossCharge = 0f;
            _bossIce = 0f;
            ApplyBossShader();
        }
    }
}
