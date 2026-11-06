using DG.Tweening;
using Revolution;
using UnityEngine;
using UnityEngine.UI;

namespace RevGame.Pvp
{
    /// <summary>匹配/邀请/失败统一顶部提示。DOTween使用非缩放时间，关闭时终止动画和定时器。</summary>
    [RevUIPanel("UI/Panel/Pvp/Menu", RevUILayer.Top, InBackStack = false, Mask = RevUIMaskMode.None)]
    public sealed class PvpNoticePanel : RevUIPanel<string>
    {
        protected override void OnBindView()
        {
            // 视图由RevBind装配；控件路由使用RevButtonClick，不在刷新时重复注册。
        }

        [RevBind("banner")] private RectTransform _banner;
        [RevBind("banner/text")] private Text _text;
        private Tween _motion;
        private bool _queueMode;
        public static void ShowQueue(int seconds)
        {
            string text = "正在匹配棋友  ·  已等待 " + (seconds / 60).ToString("00") + ":" + (seconds % 60).ToString("00");
            var current = RevUI.Get<PvpNoticePanel>();
            if (current != null && current.gameObject.activeInHierarchy && current._queueMode)
            {
                current.SetData(text);
                return;
            }
            RevUI.Open<PvpNoticePanel>(panel =>
            {
                panel.ShowMessageNow(text);
                panel._queueMode = true;
                RevTimer.CancelAllOf(panel);
            });
        }
        public static void ShowMessage(string message)
        {
            RevUI.Open<PvpNoticePanel>(panel => panel.ShowMessageNow(message));
        }
        private void ShowMessageNow(string message)
        {
            _queueMode = false;
            SetData(message);
            _motion?.Kill(false);
            RevTimer.CancelAllOf(this);
            _banner.anchoredPosition = new Vector2(0, 150);
            _motion = _banner.DOAnchorPosY(-26, .38f).SetEase(Ease.OutCubic).SetUpdate(true);
            RevTimer.After(3, Hide, domain: RevTimeDomain.Unscaled, owner: this);
        }
        private void Hide()
        {
            _motion?.Kill(false);
            _motion = _banner.DOAnchorPosY(150, .25f).SetEase(Ease.InCubic).SetUpdate(true).OnComplete(() => CloseSelf());
        }
        protected override void OnRefreshView()
        {
            _text.text = Data ?? "";
        }
        protected override void OnClose()
        {
            _queueMode = false;
            _motion?.Kill(false);
            _motion = null;
            RevTimer.CancelAllOf(this);
        }
    }
}
