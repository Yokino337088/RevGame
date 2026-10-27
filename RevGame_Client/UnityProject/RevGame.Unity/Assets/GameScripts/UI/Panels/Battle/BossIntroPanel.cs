using DG.Tweening;
using RevGame.Core;
using Revolution;
using UnityEngine;
using UnityEngine.UI;

namespace RevGame.UI
{
    /// <summary>登场弹幕消费主将快照，闪烁只作用本横幅，不让整个屏幕白闪；不参与战斗或资源加载。</summary>
    [RevUIPanel("UI/Panel/Victory",RevUILayer.Guide,InBackStack=false,CacheMode=RevUICacheMode.DestroyOnClose,Mask=RevUIMaskMode.ClickBlock)]
    public sealed class BossIntroPanel : RevUIPanel<BossDefeatedInfo>
    {
        [RevBind] private Text _headline;
        [RevBind] private Text _subtitle;
        [RevBind] private RectTransform _banner;
        [RevBind] private Image _flare;
        private Tween _animation;
        protected override void OnBindView() { }
        protected override void OnRefreshView()
        {
            if (Data == null) { return; }
            _headline.text = "第 "+Data.Chapter+" 章 · BOSS 已出现！";
            _subtitle.text = "强敌降临 · 布阵迎战";
        }
        protected override void PlayOpenTransition(System.Action onDone)
        {
            _banner.localScale = Vector3.one*.4f;
            _animation = DOTween.Sequence().SetUpdate(true)
                .Append(_banner.DOScale(1.12f,.3f).SetEase(Ease.OutBack))
                .Append(_banner.DOShakeAnchorPos(.25f,new Vector2(18,8),14,80,false,true))
                .Append(_banner.DOScale(1,.18f)).OnComplete(()=>{_animation=null;onDone();});
        }
        /// <summary>0～1由入场序列传入。闪光每秒约两次，亮度/Alpha限制在横幅内，缩放让提示更有冲击。</summary>
        public void SetFlashProgress(float progress)
        {
            float flash = .5f+.5f*Mathf.Sin(progress*Mathf.PI*10);
            _flare.color = new Color(1,.82f,.3f,.12f+flash*.36f);
            _headline.color = Color.Lerp(new Color(.32f,.16f,.06f),new Color(.92f,.42f,.08f),flash);
            _banner.localScale = Vector3.one*(1+flash*.035f);
        }
        protected override void PlayCloseTransition(System.Action onDone) { RevUIAnim.Play(this,RevUIAnimPreset.FadeOut,.2f,onDone,owner:this); }
        protected override void OnClose() { _animation?.Kill(false); _animation=null; }
        private void OnDisable() { _animation?.Kill(false); _animation=null; }
    }
}
