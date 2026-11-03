// 手牌目标高亮只修改原身体Shader参数；图集页与已有身体动画共用参数块，回池必须清零。
using UnityEngine;

namespace RevGame.View
{
    /// <summary>原棋子身体的无尽卡牌高亮职责；服务提交战力，视图只接受强度并保留图集/回池契约。</summary>
    public sealed partial class PieceView
    {
        /// <summary>预计算属性Id；不在热帧解析字符串或访问renderer.material。</summary>
        private static readonly int CardHighlightId = Shader.PropertyToID("_CardHighlight");
        /// <summary>本租约已提交的0～1强度，值不变不上传；竞技场负责衰减，回池负责清零。</summary>
        private float _cardHighlight;
        /// <summary>
        /// 仅设置身体高亮，不改星级/HP。先同步当前图集页，保留其它动画参数。
        /// 普通六章不调用，缺Shader属性保持原画，不临时克隆材质。
        /// </summary>
        public void SetCardHighlight(float strength)
        {
            strength = Mathf.Clamp01(strength);
            if (Mathf.Approximately(strength, _cardHighlight) || bodyRenderer == null || bodyRenderer.sprite == null || bodyRenderer.sharedMaterial == null || !bodyRenderer.sharedMaterial.HasProperty(CardHighlightId))
            {
                return;
            }
            _cardHighlight = strength;
            SetVisualUv(bodyRenderer.sprite);
            _visualProperties.SetFloat(CardHighlightId, strength);
            bodyRenderer.SetPropertyBlock(_visualProperties);
        }
    }
}
