using System;
using DG.Tweening;
using Revolution;
using UnityEngine;
using UnityEngine.UI;

namespace RevGame.Pvp
{
    public sealed partial class PvpPanel
    {
        [RevBind("boardToast")] private CanvasGroup _toast;
        [RevBind("boardToast/text")] private Text _toastText;
        [RevBind("yayaPortrait")] private Image _portrait;
        [RevBind("yayaBubble")] private CanvasGroup _bubble;
        [RevBind("yayaBubble/text")] private Text _speech;
        [RevBind("result")] private CanvasGroup _resultGroup;

        // 这些时长只控制镜头内反馈，取消动画不会改变时钟、棋谱或胜负。
        private const float TauntGapSeconds = 11f;
        private const string PlaceSound = "Pvp/PvpPlace";
        private const string CaptureSound = "Pvp/PvpCapture";
        private const string EyeSound = "Pvp/PvpEye";
        private const string ResultSound = "Pvp/PvpResult";
        private static readonly string[] MatchSounds = { PlaceSound, CaptureSound, EyeSound, ResultSound };

        private PvpYayaLines _yayaLines;
        private Sprite _idlePortrait;
        private RevSoundScope _matchSound;
        private Tween _toastTween, _speechTween, _countTween;
        private GoCore _lastPresented, _countCore, _shownDefeat;
        [RevBind("defeat")] private GameObject _defeat;

        internal bool DefeatVisible => _defeat.activeInHierarchy;

        [RevButtonClick("closeDefeat")]
        private void CloseDefeat()
        {
            Show(_defeat, false);
        }
        private int _feedbackVersion, _speechVersion, _hintVersion, _moveCursor, _captures, _eyeVersion;
        private float _nextTaunt;
        private bool _counting, _settling;
        private Text _outcomeTitle;
        private CanvasGroup _outcomeGroup;
        private const float OutcomeHoldSeconds = 1.5f;
        private const float ScoreNoticeSeconds = 1.2f;
#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
        private readonly RevSoundHandle[] _soundEvidence = new RevSoundHandle[MatchSounds.Length];
        internal int CompletedSoundMask
        {
            get;
            private set;
        }
        internal string LastCue
        {
            get;
            private set;
        } = "";
#endif

        // 测试查询直接读取视图当前的可见事实，避免把播放请求当成渲染成功。
        internal bool PortraitVisible => _portrait.gameObject.activeInHierarchy && _portrait.sprite != null;
        internal bool BubbleVisible => _bubble.gameObject.activeInHierarchy && _bubble.alpha > .01f;
        internal bool ToastVisible => _toast.gameObject.activeInHierarchy && _toast.alpha > .01f;
        internal string VisibleSpeech => BubbleVisible ? _speech.text : "";
        internal string VisibleToast => ToastVisible ? _toastText.text : "";
        internal bool Counting => _counting;
        internal bool Settling => _settling;
        internal string VisibleOutcome => _outcomeGroup != null && _outcomeGroup.gameObject.activeInHierarchy && _outcomeGroup.alpha > .01f ? _outcomeTitle.text : "";
        internal bool ScoreConfirmationVisible => _scoring.activeInHierarchy;
        internal bool ReplayVisible => !DefeatVisible && _result.activeInHierarchy && _previous.gameObject.activeInHierarchy;
        internal string VisibleResult => _result.activeInHierarchy ? _resultText.text : "";
        internal PvpBoardView BoardForCheck => _board;
        internal string PortraitSpriteForCheck => PortraitVisible ? _portrait.sprite.name : "";
        internal bool PortraitPackedForCheck => PortraitVisible && _portrait.sprite.packed;
        internal float BubbleWidthFraction => ((RectTransform)_bubble.transform).rect.width / ((RectTransform)transform).rect.width;
        internal float BubbleHeightFraction => ((RectTransform)_bubble.transform).rect.height / ((RectTransform)transform).rect.height;
        internal bool BubbleHasTitle => _bubble.transform.Find("name") != null;
        internal int SpeechMaxFontSize => _speech.resizeTextMaxSize;
        internal bool SpeechFits => !BubbleVisible || _speech.preferredHeight <= _speech.rectTransform.rect.height + 1;

        private void PrepareMatchSound()
        {
            _matchSound = RevSound.OpenScope();
            RevSound.Preload(MatchSounds);
#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
            RevSound.VoiceFinished += ObserveSoundFinished;
#endif
        }

        /// <summary>从已有结果文字复用字体和材质。大字独立居中且不挡点击，随主面板回池。</summary>
        private void BindOutcomeTitle()
        {
            _outcomeTitle = Instantiate(_resultText, transform, false);
            _outcomeTitle.name = "outcomeTitle";
            _outcomeTitle.raycastTarget = false;
            _outcomeTitle.alignment = TextAnchor.MiddleCenter;
            _outcomeTitle.resizeTextForBestFit = false;
            _outcomeTitle.fontSize = 112;
            var rect = _outcomeTitle.rectTransform;
            rect.anchorMin = new Vector2(.2f, .36f);
            rect.anchorMax = new Vector2(.8f, .64f);
            rect.offsetMin = rect.offsetMax = Vector2.zero;
            if (!_outcomeTitle.TryGetComponent(out Outline outline))
            {
                outline = _outcomeTitle.gameObject.AddComponent<Outline>();
            }
            outline.effectColor = new Color(.07f, .05f, .08f, 1f);
            outline.effectDistance = new Vector2(4f, -4f);
            _outcomeGroup = _outcomeTitle.gameObject.AddComponent<CanvasGroup>();
            _outcomeGroup.blocksRaycasts = false;
            Show(_outcomeTitle.gameObject, false);
        }

        private void ReleaseMatchSound()
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
            RevSound.VoiceFinished -= ObserveSoundFinished;
#endif
            _matchSound?.Dispose();
            _matchSound = null;
            foreach (string sound in MatchSounds)
            {
                RevSound.Unload(sound);
            }
        }

        /// <summary>面板和池租约均可复用。取消各自的Tween后恢复透明度与位移，旧完成回调不能隐藏新气泡。</summary>
        private void ResetMatchFeedback()
        {
            ResetAnalysisHud();
            _toastTween?.Kill(false);
            _speechTween?.Kill(false);
            _countTween?.Kill(false);
            _toastTween = _speechTween = _countTween = null;
            _lastPresented = _countCore = _shownDefeat = null;
            Show(_defeat, false);
            _moveCursor = _captures = _eyeVersion = 0;
            _feedbackVersion = _speechVersion = _hintVersion = 0;
            _nextTaunt = 0;
            _counting = _settling = false;
            Show(_outcomeTitle.gameObject, false);
#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
            CompletedSoundMask = 0;
            LastCue = "";
            Array.Clear(_soundEvidence, 0, _soundEvidence.Length);
#endif
            _resultGroup.alpha = 1;
            ((RectTransform)_resultGroup.transform).anchoredPosition = Vector2.zero;
            Show(_toast.gameObject, false);
            Show(_bubble.gameObject, false);
            _portrait.sprite = _idlePortrait;
            Show(_portrait.gameObject, false);
            if (_board != null)
            {
                _board.ResetScoreCount();
            }
        }

        private void RefreshMatchFeedback(PvpService service, bool playing)
        {
            GoCore core = service.Core;
            if (!_battleVisible || core == null || _yayaLines == null)
            {
                if (_lastPresented != null)
                {
                    ResetMatchFeedback();
                }
                return;
            }

            bool starting = _lastPresented == null || core.Sequence < _lastPresented.Sequence;
            if (starting)
            {
                ResetMatchFeedback();
                _moveCursor = service.Moves.Count;
                _captures = core.BlackCaptures + core.WhiteCaptures;
                _eyeVersion = _board.EyeVersion;
                _feedbackVersion = service.FeedbackVersion;
                _speechVersion = service.SpeechVersion;
                _hintVersion = service.HintRevision;
            }
            _lastPresented = core;

            if (_feedbackVersion != service.FeedbackVersion)
            {
                _feedbackVersion = service.FeedbackVersion;
                PopToast(service.Feedback);
            }
            RefreshSounds(service, core);

            Show(_portrait.gameObject, playing);
            if (playing)
            {
                RefreshSpeech(service, starting);
            }
            else
            {
                _speechTween?.Kill(false);
                _speechTween = null;
                Show(_bubble.gameObject, false);
                _portrait.sprite = _idlePortrait;
            }

            if (core.Phase == GoPhase.Finished && !ReferenceEquals(_countCore, core))
            {
                BeginResult(service, core);
            }
            // 大字退场后才展示败北面板。关闭后同一局不会再次弹出。
            if (!_settling && core.Phase == GoPhase.Finished && service.MySide != GoSide.Empty &&
                core.Winner == GoCore.Opponent(service.MySide) && !ReferenceEquals(_shownDefeat, core))
            {
                _shownDefeat = core;
                Show(_defeat, true);
            }
        }

        /// <summary>接收本帧的建议或事件，按 Excel 台词组轮换，同一报告只播一次。</summary>
        private void RefreshSpeech(PvpService service, bool starting)
        {
            if (RefreshAnalysisSpeech(service))
            {
                _hintVersion = service.HintRevision;
                _speechVersion = service.SpeechVersion;
                return;
            }
            string cue = "";
            string point = "";
            if (starting && service.IsComputerGame)
            {
                cue = "Opening";
            }
            else if (_hintVersion != service.HintRevision)
            {
                cue = service.HintSource;
                if (service.SuggestionCount > 0)
                {
                    var hint = service.Suggestions[0];
                    point = PvpService.FormatPoint(hint.X, hint.Y);
                }
            }
            else if (_speechVersion != service.SpeechVersion)
            {
                cue = service.SpeechCue;
                point = service.SpeechPoint;
            }
            else if (service.IsComputerGame && Time.unscaledTime >= _nextTaunt && !_bubble.gameObject.activeSelf)
            {
                cue = "Taunt";
            }
            _hintVersion = service.HintRevision;
            _speechVersion = service.SpeechVersion;
            if (!string.IsNullOrEmpty(cue))
            {
                _analysisSpeech = false;
                _hintSpeech = cue.StartsWith("Hint", StringComparison.Ordinal);
                ShowYayaLine(cue, point);
            }
        }

        /// <summary>自动和手动建议使用同一气泡，整行读取停留时间和表情，坐标只填入最终着手。</summary>
        private void ShowYayaLine(string cue, string point)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
            LastCue = cue;
#endif
            var line = _yayaLines.Take(cue);
            Set(_speech, line.Line.Replace("{point}", point));
            _speechTween?.Kill(false);
            // 先停旧动画再换表情，避免旧回调把新台词的立绘切回默认图。
            _portrait.sprite = portraitCatalog.Find(line.Expression);
            _speechTween = SlideMessage(_bubble, 50, line.Seconds, () => _portrait.sprite = _idlePortrait);
            _nextTaunt = Time.unscaledTime + TauntGapSeconds;
        }

        private void PopToast(string text)
        {
            if (_battleVisible && !string.IsNullOrEmpty(text))
            {
                Set(_toastText, text);
                _toastTween?.Kill(false);
                _toastTween = SlideMessage(_toast, -92, 1.7f);
            }
        }

        private static Tween SlideMessage(CanvasGroup group, float offset, float seconds, Action onHidden = null)
        {
            var rect = (RectTransform)group.transform;
            group.alpha = 0;
            rect.anchoredPosition = new Vector2(offset, 0);
            Show(group.gameObject, true);
            return DOTween.Sequence().SetUpdate(true)
                .Append(rect.DOAnchorPosX(0, .34f).SetEase(Ease.OutBack))
                .Join(group.DOFade(1, .20f))
                .AppendInterval(seconds)
                .Append(group.DOFade(0, .24f))
                .Join(rect.DOAnchorPosX(offset * .30f, .24f))
                .OnComplete(() =>
                {
                    Show(group.gameObject, false);
                    onHidden?.Invoke();
                });
        }

        private void RefreshSounds(PvpService service, GoCore core)
        {
            // 只消费已提交棋谱；悬停、试下、复盘不会触发落子和提子音效。
            for (; _moveCursor < service.Moves.Count; _moveCursor++)
            {
                if ((GoAction)service.Moves[_moveCursor].kind == GoAction.Play)
                {
                    PlayMatchSound(PlaceSound, .55f);
                }
            }
            int captures = core.BlackCaptures + core.WhiteCaptures;
            if (captures > _captures)
            {
                PlayMatchSound(CaptureSound, .49f);
            }
            _captures = captures;
            if (_eyeVersion != _board.EyeVersion && core.Phase == GoPhase.Playing)
            {
                PlayMatchSound(EyeSound, .42f);
            }
            _eyeVersion = _board.EyeVersion;
        }

        private void PlayMatchSound(string sound, float volume)
        {
            RevSoundHandle handle = _matchSound?.Play(sound, volume) ?? RevSoundHandle.Empty;
#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
            if (!handle.IsEmpty)
            {
                _soundEvidence[Array.IndexOf(MatchSounds, sound)] = handle;
            }
#endif
        }

#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
        /// <summary>试听验收记录框架真实播完回调，而不是按钮、落子或资源加载次数。</summary>
        private void ObserveSoundFinished(RevSoundHandle handle)
        {
            for (int i = 0; i < _soundEvidence.Length; i++)
            {
                if (handle.Equals(_soundEvidence[i]))
                {
                    CompletedSoundMask |= 1 << i;
                }
            }
        }
#endif

        private void BeginResult(PvpService service, GoCore core)
        {
            _countCore = core;
            _countTween?.Kill(false);
            _settling = true;
            Sequence sequence = DOTween.Sequence().SetUpdate(true);
            _countTween = sequence;
            if (core.EndReason == GoEndReason.Scored)
            {
                if (core.AutomaticScoring)
                {
                    string notice = core.PlacementLimitReached ? "落子已满 · 强制结算" : "双方停一手 · 自动计分";
                    PvpNoticePanel.ShowMessage(notice, ScoreNoticeSeconds);
                    // 顶部弹幕先进入并退场，再开始逐棋子表现。规则在本手已经完成结算。
                    sequence.AppendInterval(ScoreNoticeSeconds + .25f);
                }
                AppendScoreCount(sequence, core);
            }
            else
            {
                Set(_resultText, service.ResultText());
            }
            AppendOutcome(sequence, service, core);
        }

        private void AppendScoreCount(Sequence sequence, GoCore core)
        {
            sequence.AppendCallback(() =>
            {
                _counting = true;
                _board.BeginScoreCount(core);
                RenderCount(0);
                PlayMatchSound(ResultSound, .52f);
                RefreshView();
            });
            // 同一进度先逐子、再逐空、最后滚入贴目。盘面闪光与面板小计由同一份进度驱动。
            float progress = 0;
            sequence.Append(DOTween.To(() => progress, value =>
            {
                progress = value;
                RenderCount(value);
            }, GoCore.PointCount, 4.05f).SetEase(Ease.Linear));
            float komi = 0;
            sequence.Append(DOTween.To(() => komi, value =>
            {
                komi = value;
                RenderCount(GoCore.PointCount, value);
            }, core.Score.KomiHalfPoints / 2f, .72f).SetEase(Ease.OutCubic));
            sequence.AppendCallback(() =>
            {
                _counting = false;
                RenderCount(GoCore.PointCount, komi);
                // 扫光只经过最近四格，结束后推进表现游标，让中立格高亮退去。
                _board.SetScoreProgress(GoCore.PointCount + 4);
                RefreshView();
            });
        }

        /// <summary>所有结果先弹入、静止 1.5 秒，再弹回消失。Tween只管理可见性，绝不提交第二次计分。</summary>
        private void AppendOutcome(Sequence sequence, PvpService service, GoCore core)
        {
            bool draw = core.Winner == GoSide.Empty;
            bool won = !draw && core.Winner == service.MySide;
            _outcomeTitle.text = draw ? "和棋" : service.MySide == GoSide.Empty
                ? core.Winner == GoSide.Black ? "黑方胜" : "白方胜"
                : won ? "胜利" : "败北";
            _outcomeTitle.color = draw || won || service.MySide == GoSide.Empty ? new Color(1f, .83f, .45f) : new Color(1f, .64f, .70f);
            RectTransform rect = _outcomeTitle.rectTransform;
            rect.localScale = Vector3.one * .25f;
            _outcomeGroup.alpha = 0;
            sequence.AppendCallback(() =>
            {
                rect.SetAsLastSibling();
                Show(_outcomeTitle.gameObject, true);
                if (core.EndReason != GoEndReason.Scored)
                {
                    PlayMatchSound(ResultSound, .52f);
                }
            });
            sequence.Append(rect.DOScale(1f, .34f).SetEase(Ease.OutBack))
                .Join(_outcomeGroup.DOFade(1f, .18f))
                .AppendInterval(OutcomeHoldSeconds)
                .Append(rect.DOScale(.25f, .25f).SetEase(Ease.InBack))
                .Join(_outcomeGroup.DOFade(0f, .23f))
                .OnComplete(() =>
                {
                    Show(_outcomeTitle.gameObject, false);
                    _settling = false;
                    RefreshView();
                });
        }

        private void RenderCount(float progress, float komi = 0)
        {
            _board.SetScoreProgress(progress);
            GoScore score = _board.CountedScore;
            float whiteTotal = score.WhiteArea + komi;
            string stage = _counting
                ? progress < _board.CountStoneTotal ? "逐子清点" : progress < GoCore.PointCount ? "逐格清点" : "白方贴目"
                : _countCore.Winner == GoSide.Empty ? "和棋" :
                    (_countCore.Winner == GoSide.Black ? "黑方胜  " : "白方胜  ") + (Math.Abs(_countCore.Score.DifferenceHalfPoints) / 2f).ToString("0.0") + " 目";
            Set(_resultText, "<color=#FFD586><b>" + stage + "</b></color>\n" +
                "黑  " + score.BlackStones + " 子 + " + score.BlackTerritory + " 空 = " + score.BlackArea + "\n" +
                "白  " + score.WhiteStones + " 子 + " + score.WhiteTerritory + " 空 + " + komi.ToString("0.0") + "\n" +
                "<color=#BDEEFF>白方合计 " + whiteTotal.ToString("0.0") + " 目</color>  ·  中立 " + score.Neutral);
        }
    }
}
