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
    /// 已选背景先提供全屏遮挡，等专用小图集准备完成后再显示进度条。
    /// </summary>
    [RevUIPanel(RevResPath.UI_Panel_Loading, RevUILayer.Top, InBackStack = false, Mask = RevUIMaskMode.None)]
    public sealed class LoadingPanel : RevUIPanel<LoadingPanel.ProgressData>
    {
        /// <summary>流程传给界面的不可变快照。Progress为0~1真实阶段进度；Error非空时显示返回入口。</summary>
        public sealed class ProgressData
        {
            /// <summary>已完成的分段工作比例，范围 0~1。</summary>
            public readonly float Progress;
            /// <summary>给玩家看的当前步骤；不暴露对象池、预制体等工程细节。</summary>
            public readonly string Message;
            /// <summary>失败原因，空字符串代表正常加载。非空时允许玩家点击返回入口。</summary>
            public readonly string Error;
            /// <summary>创建一次加载快照，将进度限制在 0~1，空错误表示继续正常显示。</summary>
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
        /// <summary>整面板的透明度；只在资源、实例及 UI 都就绪后才从 1 缓动到 0。</summary>
        private CanvasGroup _cover;
        /// <summary>本面板持有的淡出动画，关闭时停止且不触发完成回调。</summary>
        private Tween _fadeTween;
        /// <summary>本次过场已报告的最高进度，范围 0~1；重置遮挡时归零。</summary>
        private float _targetProgress;
        /// <summary>当前已经绘到进度条上的比例，范围 0~1；只向本次过场的目标追赶。</summary>
        private float _displayed;
        /// <summary>上次写到文本的整数百分比，-1 表示尚未写入，用来跳过重复字形刷新。</summary>
        private int _displayedPercent = -1;
        /// <summary>每真实秒至少可追赶的进度，防止靠近检查点时长时间停在整数百分比下方。</summary>
        private const float MinimumProgressSpeed = 0.25f;
        /// <summary>每真实秒按进度差乘上的追赶系数，检查点跨度大时能及时跟上。</summary>
        private const float ProgressCatchUpRate = 12f;

        /// <summary>LoadingPanel 专用图集标签，随这次过场单独加载与归还。</summary>
        private const string ProgressAtlasName = "UILoading";
        /// <summary>预制体选择。默认使用传送门，取消勾选即可切回原彩虹漩涡。</summary>
        [SerializeField, Tooltip("勾选使用新版传送门背景，取消勾选切回彩虹漩涡")]
        private bool usePortalBackdrop = true;
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
            RevGame.UI.GameMobileLayout.EnableSafePanel(transform, "screenBackground", "portalBackground");
            _cover = GetComponent<CanvasGroup>();
            _progressTrack = _progress.GetComponent<Image>();
            _progressFill = _progress.fillRect.GetComponent<Image>();
        }

        /// <summary>按预制体选择启用一层背景。旧包里没有新节点时仍由原漩涡承接全屏遮挡。</summary>
        private void ApplyBackdrop()
        {
            Transform rainbow = transform.Find("screenBackground");
            RawImage portal = transform.Find("portalBackground")?.GetComponent<RawImage>();
            bool showPortal = usePortalBackdrop && portal != null && portal.texture != null;
            if (rainbow != null)
            {
                rainbow.gameObject.SetActive(!showPortal);
            }
            if (portal != null)
            {
                portal.gameObject.SetActive(showPortal);
            }
        }

        /// <summary>先启用已选的全屏背景，再等真实 AB 进度图集结束打开流程。直读模式使用预制体图片。</summary>
        protected override void PlayOpenTransition(Action onDone)
        {
            ApplyBackdrop();
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
            // AB 首次打开可能早于 Sprite 自动绑定。先避开白块，已选背景继续提供遮挡。
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
            _targetProgress = 0f;
            SetDisplayed(0f);
            _cover.alpha = 1f;
            _cover.blocksRaycasts = true;
            _cover.interactable = true;
            _btnReturn.gameObject.SetActive(false);
        }
        /// <summary>消费流程快照。阶段与错误变更直接上屏，小批进度合并为这一帧的追赶目标。</summary>
        protected override void OnRefreshView()
        {
            if (Data == null)
            {
                return;
            }
            bool failed = !string.IsNullOrEmpty(Data.Error);
            string message = failed ? Data.Error : Data.Message;
            if (_message.text != message)
            {
                _message.text = message;
            }
            if (_btnReturn.gameObject.activeSelf != failed)
            {
                _btnReturn.gameObject.SetActive(failed);
            }
            _targetProgress = Mathf.Max(_targetProgress, Data.Progress);
        }

        /// <summary>在绘制前用非缩放时间追赶已报告的进度，直到到达本轮目标。一次更新承接同帧的所有小批回报。</summary>
        private void LateUpdate()
        {
            if (_displayed >= _targetProgress)
            {
                return;
            }

            float remaining = _targetProgress - _displayed;
            float speed = Mathf.Max(MinimumProgressSpeed, remaining * ProgressCatchUpRate);
            SetDisplayed(Mathf.MoveTowards(_displayed, _targetProgress, speed * Time.unscaledDeltaTime));
        }

        /// <summary>用同一个显示值驱动 Slider 和百分比，只在跨过整数时更新文字。</summary>
        private void SetDisplayed(float value)
        {
            _displayed = value;
            _progress.SetValueWithoutNotify(value);
            int percent = Mathf.FloorToInt(value * 100f);
            if (percent != _displayedPercent)
            {
                _displayedPercent = percent;
                _percent.text = percent + "%";
            }
        }
        /// <summary>先让进度条到100%，再淡出0.35秒；使用非缩放时间，暂停或慢动作不会卡死遮挡层。</summary>
        public async RevTask FadeOutAsync()
        {
            while (this != null && _displayed < _targetProgress)
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
        /// <summary>结束本次追赶并取消淡出；关闭或重置遮挡都从这里清理本面板的动画状态。</summary>
        private void KillAnimations()
        {
            _targetProgress = _displayed;
            _fadeTween?.Kill(false);
            _fadeTween = null;
        }
    }
}
