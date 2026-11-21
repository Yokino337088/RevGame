using System;
using DG.Tweening;
using RevGame.View;
using Revolution;
using UnityEngine;
using UnityEngine.UI;

namespace RevGame.UI
{
    /// <summary>
    /// 跨场景的全屏过场面板：只显示进度和播放动画，场景与资源的加载由GameFlow负责。
    /// 它由 RevUI 创建和关闭。流程从旧域解绑使遮挡持续到交接完成，关闭时由框架销毁并归还资源。
    /// 程序背景先提供全屏遮挡，等专用小图集准备完成后再显示进度条。
    /// </summary>
    [RevUIPanel(RevResPath.UI_Panel_Loading, RevUILayer.Top, InBackStack = false, Mask = RevUIMaskMode.None)]
    public sealed class LoadingPanel : RevUIPanel<LoadingPanel.ProgressData>
    {
        /// <summary>流程传给界面的不可变快照。Progress为0~1真实阶段进度；Error非空时显示返回入口。</summary>
        public sealed class ProgressData
        {
            /// <summary>实际工作完成比例，不是按时间自动增长的假进度。</summary>
            public readonly float Progress;
            /// <summary>给玩家看的当前步骤；不暴露对象池、预制体等工程细节。</summary>
            public readonly string Message;
            /// <summary>失败原因，空字符串代表正常加载。非空时允许玩家点击返回入口。</summary>
            public readonly string Error;
            public ProgressData(float progress, string message, string error = "")
            {
                Progress = Mathf.Clamp01(progress);
                Message = message;
                Error = error;
            }
        }
        [RevBind] private Slider _progress; // 只读进度条：玩家不能拖拽改变加载状态。
        [RevBind] private Text _message; // 当前阶段文字，加粗居中，与进度条一起复用。
        [RevBind] private Text _percent; // 显示经过缓动的百分比，避免数字跳动与填充条不同步。
        [RevBind] private Button _btnReturn; // 只在失败时显示，回调只转发给GameFlow。
        /// <summary>整面板的透明度；只在资源、实例及UI都就绪后才从1缓动到0。</summary>
        private CanvasGroup _cover;
        /// <summary>仅持有本面板的Tween，关闭时Kill(false)，不会误停战斗中其他动画。</summary>
        private Tween _progressTween;
        private Tween _fadeTween;
        private float _displayed;

        /// <summary>LoadingPanel 专用图集标签，随这次过场单独加载与归还。</summary>
        private const string ProgressAtlasName = "UILoading";
        /// <summary>预制体的底槽图层，绑定后用于显示当前图集里的 Sprite。</summary>
        private Image _progressTrack;
        /// <summary>Slider 填充图层，按进度裁切一部分固定宽度的图。</summary>
        private Image _progressFill;
        /// <summary>当前打开过程持有的图集租约，也用来识别过期的加载结果。</summary>
        private GameSpriteAtlasService.ScopedAtlasLease _progressAtlas;
        /// <summary>本次过场取得的底槽 Sprite 副本，和租约一并释放。</summary>
        private Sprite _packedTrack;
        /// <summary>本次过场取得的填充 Sprite 副本，和租约一并释放。</summary>
        private Sprite _packedFill;

        /// <summary>只绑定当前预制体的控制和排版，资源等待交给打开过程。</summary>
        protected override void OnBindView()
        {
            RevGame.UI.GameMobileLayout.EnableSafePanel(transform, "screenBackground");
            _cover = GetComponent<CanvasGroup>();
            _progressTrack = _progress.GetComponent<Image>();
            _progressFill = _progress.fillRect.GetComponent<Image>();
        }

        /// <summary>真实 AB 先取得进度条图集，再结束打开流程；旧预制体与直读模式沿用各自已有图层。</summary>
        protected override void PlayOpenTransition(Action onDone)
        {
#if UNITY_EDITOR
            if (!RevResBootstrap.UseABInEditor)
            {
                onDone?.Invoke();
                return;
            }
#endif
            if (_progressTrack.sprite == null || _progressFill.sprite == null)
            {
                onDone?.Invoke();
                return;
            }
            // AB 首次打开可能早于 Sprite 自动绑定。先避开白块，保留程序背景提供遮挡。
            _progressTrack.enabled = _progressFill.enabled = false;
            LoadProgressArtAsync(onDone).Forget();
        }

        /// <summary>显式取到图集像素后一次赋给两张图。资源失败时百分比和原有遮挡流程仍可用。</summary>
        private async RevTask LoadProgressArtAsync(Action onDone)
        {
            GameSpriteAtlasService.ScopedAtlasLease lease = null;
            try
            {
                lease = RevServices.GetRequired<GameSpriteAtlasService>().AcquireScopedAtlas(ProgressAtlasName);
                _progressAtlas = lease;
                await lease.WaitReadyAsync();
                if (this == null || State != RevUIPanelState.Opening || !ReferenceEquals(_progressAtlas, lease))
                {
                    return;
                }
                _packedTrack = lease.GetPackedSprite(_progressTrack.sprite.name);
                _packedFill = lease.GetPackedSprite(_progressFill.sprite.name);
                _progressTrack.overrideSprite = _packedTrack;
                _progressFill.overrideSprite = _packedFill;
                _progressTrack.enabled = _progressFill.enabled = true;
            }
            catch (Exception error)
            {
                if (this == null || State != RevUIPanelState.Opening || !ReferenceEquals(_progressAtlas, lease))
                {
                    return;
                }
                RevLog.Warn("加载进度条图集不可用：" + error.Message, "LoadingPanel");
                ReleaseProgressArt();
            }
            onDone?.Invoke();
        }
        /// <summary>开始一次过场时归零，恢复遮挡、输入规则与默认按钮状态。</summary>
        public void ResetCover()
        {
            KillAnimations();
            SetDisplayed(0f);
            _cover.alpha = 1f;
            _cover.blocksRaycasts = true;
            _cover.interactable = true;
            _btnReturn.gameObject.SetActive(false);
        }
        protected override void OnRefreshView()
        {
            if (Data == null)
            {
                return;
            }
            _message.text = string.IsNullOrEmpty(Data.Error) ? Data.Message : Data.Error;
            _btnReturn.gameObject.SetActive(!string.IsNullOrEmpty(Data.Error));
            _progressTween?.Kill(false);
            // 只让视觉值追赶真实值，不靠定时器假装加载完成；慢机器可停在某个阶段等待真实结果。
            float target = Mathf.Max(_displayed, Data.Progress);
            _progressTween = DOTween.To(() => _displayed, SetDisplayed, target, 0.22f)
                .SetEase(Ease.OutCubic).SetUpdate(true).OnComplete(() => _progressTween = null);
        }
        /// <summary>用同一个缓动值驱动 Slider 的裁切范围和百分比。</summary>
        private void SetDisplayed(float value)
        {
            _displayed = value;
            _progress.value = value;
            _percent.text = Mathf.FloorToInt(value * 100f) + "%";
        }
        /// <summary>先让进度条到100%，再淡出0.35秒；使用非缩放时间，暂停或慢动作不会卡死遮挡层。</summary>
        public async RevTask FadeOutAsync()
        {
            while (this != null && _progressTween != null && _progressTween.IsActive())
            {
                await RevTask.Yield();
            }
            if (this == null)
            {
                return;
            }
            _fadeTween = _cover.DOFade(0f, 0.35f).SetEase(Ease.InOutSine).SetUpdate(true).OnComplete(() => _fadeTween = null);
            while (this != null && _fadeTween != null && _fadeTween.IsActive())
            {
                await RevTask.Yield();
            }
            // 最后一帧淡出前仍拦截点击；流程解除输入门禁后才真正关闭。
        }
        [RevButtonClick("btnReturn")]
        private void OnReturn()
        {
            RevServices.GetRequired<Flow.GameFlow>().DismissLoadingFailure();
        }
        /// <summary>关闭本轮过场时停止动画并归还图集，供后续重新打开。</summary>
        protected override void OnClose()
        {
            KillAnimations();
            ReleaseProgressArt();
        }
        /// <summary>直接销毁或取消打开时也执行同样的清理。</summary>
        protected override void OnRelease()
        {
            KillAnimations();
            ReleaseProgressArt();
        }
        /// <summary>摘下当前图层引用，再销毁 Sprite 副本及归还租约；重复关闭安全。</summary>
        private void ReleaseProgressArt()
        {
            if (_packedTrack != null)
            {
                _progressTrack.overrideSprite = null;
                Destroy(_packedTrack);
                _packedTrack = null;
            }
            if (_packedFill != null)
            {
                _progressFill.overrideSprite = null;
                Destroy(_packedFill);
                _packedFill = null;
            }
            _progressAtlas?.Dispose();
            _progressAtlas = null;
        }
        private void KillAnimations()
        {
            _progressTween?.Kill(false);
            _fadeTween?.Kill(false);
            _progressTween = null;
            _fadeTween = null;
        }
    }
}
