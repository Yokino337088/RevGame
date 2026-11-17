using System;
using UnityEngine;

namespace Revolution.Gal
{
    /// <summary>RevUI 生命周期宿主。持有会话和资源，接收 View 用户意图，并处理自动推进与宿主衔接。</summary>
    [RevUIPanel(RevGal.PanelRoot, RevUILayer.Popup, CacheMode = RevUICacheMode.DestroyOnClose)]
    public sealed class RevGalPanel : RevUIPanel<RevGalSession>
    {
        private enum PlayMode
        {
            Manual, Auto, ReadSkip
        }

        private const string QuickSlot = "quick";
        [RevBind("View")] private RevGalView _view;
        [RevBind("Music")] private AudioSource _music;
        [RevBind("Voice")] private AudioSource _voice;
        [RevBind("Sound")] private AudioSource _sound;
        private RevGalSession _session;
        private RevGalMedia _media;
        private PlayMode _mode;
        private int _frameToken;
        private float _timer;

        public string VisibleDialogue => _view == null ? "" : _view.VisibleDialogue;
        public bool TextComplete => _view != null && _view.TextComplete;
        public bool HistoryOpen => _view != null && _view.HistoryOpen;
        internal bool BackgroundVisible => _view != null && _view.BackgroundVisible;

        protected override void OnBindView()
        {
            _view.InputRequested += OnInput;
            _view.ChoiceRequested += OnChoice;
            _view.Revealed += OnRevealed;
        }

        protected override void OnDataChanged(RevGalSession oldData, RevGalSession newData)
        {
            if (_session == newData)
            {
                return;
            }
            Detach();
            if (newData != null)
            {
                _session = newData;
                _media = new RevGalMedia(_view, _music, _voice, _sound);
                _session.Changed += OnChanged;
            }
        }

        protected override void OnRefreshView()
        {
            if (_session != null && !_session.IsDisposed && _session.Frame.Token != _frameToken)
            {
                OnChanged(_session.Frame);
            }
        }

        protected override void OnClose()
        {
            Detach();
        }

        protected override void OnRelease()
        {
            Detach();
            if (_view != null)
            {
                _view.InputRequested -= OnInput;
                _view.ChoiceRequested -= OnChoice;
                _view.Revealed -= OnRevealed;
            }
        }

        private void OnChanged(RevGalFrame frame)
        {
            _frameToken = frame.Token;
            _timer = 0;
            _view.ShowNotice(null);
            if (frame.Kind != RevGalNodeKind.Line || (_mode == PlayMode.ReadSkip && !frame.WasRead))
            {
                ChangeMode(PlayMode.Manual);
            }
            bool quiet = _mode == PlayMode.ReadSkip;
            _view.Present(_session.Title, frame, quiet ? 0 : _session.Options.CharactersPerSecond, _session.CanRollback,
                _session.CanSave && _session.Options.Storage != null, _session.CanSave && _session.Options.Storage != null);
            _media.Present(frame, _session.Options, quiet);
            if (frame.Signal != null)
            {
                if (_session.Options.HandleSignal != null)
                {
                    RunSignal(_session, frame.Signal).Forget();
                }
                else
                {
                    _view.ShowNotice("等待宿主处理：" + frame.Signal.Name);
                }
            }
            else if (frame.Kind == RevGalNodeKind.End)
            {
                Guard(_session.FlushProgress);
            }
        }

        private async RevTask RunSignal(RevGalSession session, RevGalSignal signal)
        {
            // 会话事件在提交内部发出。下一帧处理，兼容立即完成的宿主实现。
            await RevTask.Yield();
            if (!StillWaiting(session, signal.Token))
            {
                return;
            }
            try
            {
                await session.Options.HandleSignal(session, signal);
                if (StillWaiting(session, signal.Token))
                {
                    session.CompleteSignal(signal.Token);
                }
            }
            catch (Exception error)
            {
                if (StillWaiting(session, signal.Token))
                {
                    _view.ShowNotice("剧情信号失败：" + error.Message);
                }
                RevLog.Exception(error, "Gal 信号处理失败", "RevGal");
            }
        }

        private bool StillWaiting(RevGalSession session, int token)
        {
            return this != null && _session == session && !session.IsDisposed && session.Frame.Token == token;
        }

        private void Update()
        {
            if (_session == null || _mode == PlayMode.Manual || State != RevUIPanelState.Opened || IsCovered || HistoryOpen || !TextComplete)
            {
                return;
            }
            if (_mode == PlayMode.Auto && _session.Options.WaitForVoice && _media.VoiceBusy)
            {
                return;
            }
            _timer += Time.unscaledDeltaTime;
            float delay = _mode == PlayMode.Auto ? Mathf.Max(.15f, _session.Options.AutoDelaySeconds) : .09f;
            if (_timer >= delay)
            {
                _timer = 0;
                Guard(() => _session.Advance());
            }
        }

        private void OnInput(RevGalInput input)
        {
            if (_session == null || State != RevUIPanelState.Opened || IsCovered)
            {
                return;
            }
            Guard(() =>
            {
                switch (input)
                {
                    case RevGalInput.Advance:
                        if (TextComplete)
                        {
                            _session.Advance();
                        }
                        else
                        {
                            _view.Reveal();
                        }
                        break;
                    case RevGalInput.Auto:
                        ChangeMode(_mode == PlayMode.Auto ? PlayMode.Manual : PlayMode.Auto);
                        break;
                    case RevGalInput.Skip:
                        if (_mode == PlayMode.ReadSkip)
                        {
                            ChangeMode(PlayMode.Manual);
                        }
                        else if (_session.Frame.WasRead)
                        {
                            ChangeMode(PlayMode.ReadSkip);
                            _view.Reveal();
                        }
                        else
                        {
                            _view.ShowNotice("已到未读对白");
                        }
                        break;
                    case RevGalInput.Rollback:
                        ChangeMode(PlayMode.Manual);
                        _session.Rollback();
                        break;
                    case RevGalInput.History:
                        _view.ToggleHistory(_session.History());
                        break;
                    case RevGalInput.Save:
                        _session.SaveSlot(QuickSlot);
                        _view.ShowNotice("已快速保存");
                        break;
                    case RevGalInput.Load:
                        ChangeMode(PlayMode.Manual);
                        _view.ShowNotice(_session.LoadSlot(QuickSlot) ? "已读取" : "没有可用的快速存档");
                        break;
                    case RevGalInput.Close:
                        CloseSelf();
                        break;
                }
            });
        }

        private void OnChoice(string id)
        {
            if (_session != null && State == RevUIPanelState.Opened && !IsCovered && !HistoryOpen)
            {
                Guard(() => _session.Choose(id));
            }
        }

        private void OnRevealed(int token)
        {
            _session?.MarkRead(token);
        }

        private void ChangeMode(PlayMode next)
        {
            _mode = next;
            _timer = 0;
            _view.ShowMode(next == PlayMode.Auto ? "自动" : next == PlayMode.ReadSkip ? "跳过已读" : "");
        }

        private void Detach()
        {
            var session = _session;
            _session = null;
            _frameToken = 0;
            _mode = PlayMode.Manual;
            _media?.Dispose();
            _media = null;
            if (_view != null)
            {
                _view.ResetView();
            }
            if (session != null)
            {
                session.Changed -= OnChanged;
                try
                {
                    session.Dispose();
                }
                catch (Exception error)
                {
                    RevLog.Exception(error, "保存 Gal 进度失败", "RevGal");
                }
            }
        }

        private void Guard(Action action)
        {
            try
            {
                action();
            }
            catch (Exception error)
            {
                ChangeMode(PlayMode.Manual);
                _view.ShowNotice(error.Message);
                RevLog.Exception(error, "Gal 播放操作失败", "RevGal");
            }
        }
    }
}
