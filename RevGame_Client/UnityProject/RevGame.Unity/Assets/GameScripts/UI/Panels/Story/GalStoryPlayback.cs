using System;
using Revolution;
using Revolution.Gal;
using UnityEngine;
using UnityEngine.EventSystems;

namespace RevGame.UI
{
    /// <summary>RevGame 专用的 Gal 交互。使用默认面板的舞台与打字机，按主线习惯推进并交由流程返回大厅。</summary>
    internal sealed class GalStoryPlayback : MonoBehaviour, IPointerClickHandler
    {
        private readonly StoryTypingSound _typingSound = new StoryTypingSound();
        private RevGalPanel _panel;
        private RevGalView _view;
        private RevGalSession _session;
        private Action _returnHome;
        private IDisposable _observation;
        private UnityEngine.UI.Button _advance;
        private UnityEngine.UI.Button _close;
        private UnityEngine.UI.Text _advanceLabel;
        private UnityEngine.UI.Image _background;
        private UnityEngine.UI.Image _left;
        private UnityEngine.UI.Image _center;
        private UnityEngine.UI.Image _right;
        private RevGalStage _stage;
        private string _advanceText;
        private int _visibleLength;
        private int _lastClickFrame = -1;
        private bool _typing;
        private bool _visible;
        private bool _firstSoundPending;

        /// <summary>供流程判断首幕图片是否已交给可绘制的 Image。</summary>
        public bool StageReady => _stage != null && HasRequiredSprite(_background, _stage.Background)
            && HasRequiredSprite(_left, _stage.Left) && HasRequiredSprite(_center, _stage.Center)
            && HasRequiredSprite(_right, _stage.Right);

        /// <summary>首声获得实际播放句柄后为 true，换句时清零。</summary>
        public bool SubtitleSoundStarted => _typingSound.Started;

        /// <summary>默认面板打开并注册自身监听后接管两个业务按钮。View 原有整屏 Graphic 提供背景点击区域。</summary>
        public static GalStoryPlayback Attach(RevGalPanel panel, Action returnHome)
        {
            Transform view = panel.transform.Find("View");
            Transform story = view?.Find("Story");
            var advance = story?.Find("DialogueDock/Advance")?.GetComponent<UnityEngine.UI.Button>();
            var close = story?.Find("Header/Close")?.GetComponent<UnityEngine.UI.Button>();
            var label = advance?.GetComponentInChildren<UnityEngine.UI.Text>(true);
            if (advance == null || close == null || label == null)
            {
                throw new InvalidOperationException("Gal 面板缺少继续或返回所需的按钮绑定");
            }

            var playback = view.gameObject.AddComponent<GalStoryPlayback>();
            playback._panel = panel;
            playback._view = view.GetComponent<RevGalView>();
            playback._session = panel.Data;
            playback._returnHome = returnHome;
            playback._background = story.Find("Background").GetComponent<UnityEngine.UI.Image>();
            playback._left = story.Find("Left").GetComponent<UnityEngine.UI.Image>();
            playback._center = story.Find("Center").GetComponent<UnityEngine.UI.Image>();
            playback._right = story.Find("Right").GetComponent<UnityEngine.UI.Image>();
            playback._advance = advance;
            playback._close = close;
            playback._advanceLabel = label;
            playback._advanceText = label.text;
            advance.onClick.RemoveAllListeners();
            advance.onClick.AddListener(playback.OnAdvance);
            close.onClick.RemoveAllListeners();
            close.onClick.AddListener(playback.OnClose);
            // 首幕预加载和遮挡淡出时暂停 View 的打字机，关闭遮挡后才开始播放字幕。
            playback._view.enabled = false;
            playback._observation = playback._session.Observe(playback.OnFrame);
            return playback;
        }

        /// <summary>由场景流程在关闭过场后调用。已经落屏的首句字符也在这时播放第一声。</summary>
        public void BeginVisiblePlayback()
        {
            _visible = true;
            _firstSoundPending = _session.Frame.Kind == RevGalNodeKind.Line && _session.Options.CharactersPerSecond > 0;
            _view.enabled = true;
        }

        /// <summary>按钮消费自己的点击，其余区域由 View 接收。选择、设置和历史区域保持各自的交互。</summary>
        public void OnPointerClick(PointerEventData eventData)
        {
            if (eventData.button == PointerEventData.InputButton.Left && _session?.Frame.Kind == RevGalNodeKind.Line)
            {
                OnAdvance();
            }
        }

        private void OnAdvance()
        {
            if (!CanReceiveInput() || _lastClickFrame == Time.frameCount)
            {
                return;
            }
            _lastClickFrame = Time.frameCount;
            var frame = _session.Frame;
            if (frame.Kind == RevGalNodeKind.End)
            {
                _returnHome();
            }
            else if (frame.Kind == RevGalNodeKind.Line)
            {
                try
                {
                    _session.Advance(frame.Token);
                }
                catch (Exception error)
                {
                    RevLog.Exception(error, "Gal 推进失败", "RevGame");
                }
            }
        }

        private void OnClose()
        {
            if (CanReceiveInput())
            {
                _returnHome();
            }
        }

        private bool CanReceiveInput()
        {
            return _visible && _panel != null && _panel.State == RevUIPanelState.Opened && !_panel.IsCovered
                && _session != null && !_session.IsDisposed && !_panel.SettingsOpen && !_panel.HistoryOpen;
        }

        /// <summary>框架先显示当前帧，宿主随后把结尾的继续按钮改为显式返回大厅。</summary>
        private void OnFrame(RevGalFrame frame)
        {
            _typingSound.Reset();
            _visibleLength = 0;
            _firstSoundPending = false;
            _stage = frame.Stage;
            bool ended = frame.Kind == RevGalNodeKind.End;
            _typing = frame.Kind == RevGalNodeKind.Line && !_panel.TextComplete;
            _advanceLabel.text = ended ? "返回大厅" : _advanceText;
            _advance.interactable = ended || frame.Kind == RevGalNodeKind.Line;
        }

        private static bool HasRequiredSprite(UnityEngine.UI.Image image, string path)
        {
            return string.IsNullOrEmpty(path) || (image != null && image.enabled && image.sprite != null);
        }

        private void Update()
        {
            if (!CanReceiveInput())
            {
                _typingSound.Stop();
                return;
            }
            if (!_typing && !_firstSoundPending)
            {
                return;
            }

            int visibleLength = _panel.VisibleDialogue.Length;
            if (visibleLength > _visibleLength)
            {
                _typingSound.OnTextWritten();
            }
            _firstSoundPending = false;
            _visibleLength = visibleLength;
            _typing = !_panel.TextComplete;
        }

        private void OnDisable()
        {
            _typingSound.Stop();
        }

        private void OnDestroy()
        {
            _observation?.Dispose();
            _typingSound.Reset();
            if (_advance != null)
            {
                _advance.onClick.RemoveListener(OnAdvance);
            }
            if (_close != null)
            {
                _close.onClick.RemoveListener(OnClose);
            }
        }
    }
}
