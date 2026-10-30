using DG.Tweening;
using RevGame.Core;
using Revolution;
using UnityEngine;
using UnityEngine.UI;

namespace RevGame.UI
{
    /// <summary>胜利弹幕只显示服务快照：击败章节和章节累计积分；动画用非缩放时钟，不在UI计分。</summary>
    [RevUIPanel("UI/Panel/Victory", RevUILayer.Guide, InBackStack = false, CacheMode = RevUICacheMode.DestroyOnClose, Mask = RevUIMaskMode.ClickBlock)]
    public sealed class BossVictoryPanel : RevUIPanel<BossDefeatedInfo>
    {
        [RevBind] private Text _headline;
        [RevBind] private Text _chapterScore;
        [RevBind] private RectTransform _banner;
        private Tween _animation;
        protected override void OnBindView()
        {
        }
        protected override void OnRefreshView()
        {
            if (Data == null)
            {
                return;
            }
            _headline.text = "第 " + Data.Chapter + " 章 · BOSS 已击败！";
            _chapterScore.text = "本章总积分  " + Data.ChapterScore.ToString("N0");
        }
        protected override void PlayOpenTransition(System.Action onDone)
        {
            _banner.localScale = Vector3.one * .55f;
            _animation = DOTween.Sequence().SetUpdate(true)
                .Append(_banner.DOScale(1.06f, .27f).SetEase(Ease.OutBack))
                .Append(_banner.DOShakeAnchorPos(.28f, new Vector2(13, 6), 12, 80, false, true))
                .Append(_banner.DOScale(1f, .15f))
                .OnComplete(() => { _animation = null; onDone(); });
        }
        protected override void PlayCloseTransition(System.Action onDone)
        {
            RevUIAnim.Play(this, RevUIAnimPreset.FadeOut, .2f, onDone, owner: this);
        }
        protected override void OnClose()
        {
            _animation?.Kill(false);
            _animation = null;
        }
        private void OnDisable()
        {
            _animation?.Kill(false);
            _animation = null;
        }
    }
}
