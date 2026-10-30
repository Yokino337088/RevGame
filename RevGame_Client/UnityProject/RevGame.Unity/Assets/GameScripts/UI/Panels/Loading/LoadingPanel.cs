using DG.Tweening;
using Revolution;
using UnityEngine;
using UnityEngine.UI;

namespace RevGame.UI
{
    /// <summary>
    /// 跨场景的全屏过场面板：只显示进度和播放动画，场景与资源的加载由GameFlow负责。
    /// 它由RevUI创建、缓存和关闭；流程会把它从旧业务域Detach，防止退出大厅时遮挡层也被关掉。
    /// 背景复用绿色星雾共享材质，没有Sprite依赖，因此不会等UI图集时自己先变成白块。
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

        protected override void OnBindView()
        {
            _cover = GetComponent<CanvasGroup>();
        }
        /// <summary>缓存面板再次使用时必须归零；不能把上次100%和透明状态带进下一次加载。</summary>
        public void ResetCover()
        {
            KillAnimations();
            _displayed = 0f;
            _progress.value = 0f;
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
        protected override void OnClose()
        {
            KillAnimations();
        }
        protected override void OnRelease()
        {
            KillAnimations();
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
