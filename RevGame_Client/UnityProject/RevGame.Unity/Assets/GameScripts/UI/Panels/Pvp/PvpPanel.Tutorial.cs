using System;
using UnityEngine;
using UnityEngine.UI;

namespace RevGame.Pvp
{
    /// <summary>教学使用既有九路围棋 HUD，所有练习动作仍由业务判定。</summary>
    public sealed partial class PvpPanel
    {
        /// <summary>教学专用的按钮根。背景透明，棋盘区域继续接受射线。</summary>
        private GameObject _tutorialControls;
        /// <summary>普通对局操作根。在练习时关闭，退出后恢复。</summary>
        private GameObject _normalControls;
        /// <summary>继续当前课程，在最后一页结束教学。</summary>
        private Button _tutorialNext;
        /// <summary>继续按钮沿用的标题，装配时取一次，换到最后一节才更换文案。</summary>
        private Text _tutorialNextCaption;
        /// <summary>停一手示范按钮，只在课程允许时显示。</summary>
        private Button _tutorialPass;
        /// <summary>数子练习的确认按钮，只在该步骤允许时显示。</summary>
        private Button _tutorialConfirm;
        /// <summary>已经呈现的练习盘行动序号，用来保证音效只播一次。</summary>
        private int _tutorialSequence;
        /// <summary>已经呈现的提子总数，随练习盘重新取基线。</summary>
        private int _tutorialCaptures;
        /// <summary>最近呈现的反馈，变化时才重新拼接对白。</summary>
        private string _tutorialFeedback = string.Empty;
        /// <summary>最近呈现的建议格号，-1 表示当前无指定交叉点。</summary>
        private int _tutorialPoint = -1;
        /// <summary>已经取过立绘和台词的课程编号，变更后切换表情。</summary>
        private string _tutorialCue = string.Empty;
        /// <summary>当前课程的 Excel 原句。同一步中的操作反馈刷新不会推进台词轮换。</summary>
        private string _tutorialLine = string.Empty;
        /// <summary>当前对白用于换行的实际宽度，安全区变化时重新计算。</summary>
        private float _tutorialBubbleWidth;
        /// <summary>进入教学前的对白缩放模式，退场后还原。</summary>
        private bool _regularSpeechBestFit;
        /// <summary>当前课程已经展示数子效果，避免每次刷新重播。</summary>
        private bool _tutorialCountShown;

        /// <summary>真实练习盘已加载、已显示且教学视图能够接单。</summary>
        internal bool TutorialReady => Menu == "Tutorial" && _battleVisible && _board != null
            && _board.Ready && _tutorialControls != null && _tutorialControls.activeInHierarchy;

        /// <summary>复制当前 HUD 的按钮素材，沿用原棋盘和安全区的设计坐标。</summary>
        private void BindTutorialHud()
        {
            _normalControls = transform.Find("controls").gameObject;
            _regularSpeechBestFit = _speech.resizeTextForBestFit;
            RectTransform controls = PvpTutorialControls.Rect("tutorialControls", transform, 0, 0, 1, 1);
            _tutorialControls = controls.gameObject;
            _tutorialNext = PvpTutorialControls.Button("tutorialNext", _resign, controls, "继续",
                .44f, .025f, .56f, .095f, AdvanceTutorialFromPanel);
            _tutorialNextCaption = _tutorialNext.GetComponentInChildren<Text>(true);
            _tutorialPass = PvpTutorialControls.Button("tutorialPass", _pass, controls, "停一手",
                .31f, .025f, .43f, .095f, PassTutorialFromPanel);
            _tutorialConfirm = PvpTutorialControls.Button("tutorialConfirmScore", _hint, controls, "确认数子",
                .57f, .025f, .69f, .095f, ConfirmTutorialScoreFromPanel);
            PvpTutorialControls.Button("tutorialRetry", _back, controls, "重练本节",
                .025f, .12f, .174f, .185f, RetryTutorialFromPanel);
            PvpTutorialControls.Button("tutorialExit", _back, controls, "退出教学",
                .025f, .03f, .174f, .095f, ExitTutorialFromPanel);
            Show(_tutorialControls, false);
        }

        /// <summary>教学本身没有房间或竞技控制。只展示口诀、练习进度和当前操作。</summary>
        private void RefreshTutorialHud(PvpTutorial tutorial)
        {
            if (!_tutorialControls.activeSelf)
            {
                ResetMatchFeedback();
                Show(_normalControls, false);
                Show(_tutorialControls, true);
                Show(_title.transform.parent.gameObject, true);
                Show(_scoring, false);
                Show(_result, false);
                Show(_invite, false);
                Show(_portrait.gameObject, true);
                Show(_bubble.gameObject, true);
                _bubble.alpha = 1;
                // 战斗对白退场会留下水平动画偏移，教学对白固定停在右侧。
                var bubble = (RectTransform)_bubble.transform;
                bubble.anchoredPosition = new Vector2(0, bubble.anchoredPosition.y);
                _speech.resizeTextForBestFit = true;
                _speech.resizeTextMinSize = 26;
                _speech.resizeTextMaxSize = _speech.fontSize;
            }

            Set(_title, "围棋教学  " + tutorial.StepNumber + "/" + tutorial.StepCount);
            Show(_tutorialNext.gameObject, tutorial.CanContinue);
            Show(_tutorialPass.gameObject, tutorial.CanPass);
            Show(_tutorialConfirm.gameObject, tutorial.CanConfirmScore);
            Set(_tutorialNextCaption, tutorial.StepNumber == tutorial.StepCount ? "完成教学" : "继续");
            bool lessonChanged = _tutorialCue != tutorial.Cue;
            RefreshTutorialDialogue(tutorial, lessonChanged);
            RefreshTutorialBoard(tutorial.Core, lessonChanged);
        }

        /// <summary>读取 Excel 台词和表情。同一条话持续显示，读完或做对后由玩家前进。</summary>
        private void RefreshTutorialDialogue(PvpTutorial tutorial, bool lessonChanged)
        {
            var rect = (RectTransform)_bubble.transform;
            int point = tutorial.HintPoint;
            string feedback = tutorial.Feedback;
            if (!lessonChanged && feedback == _tutorialFeedback && point == _tutorialPoint
                && Mathf.Approximately(_tutorialBubbleWidth, rect.rect.width))
            {
                return;
            }
            if (lessonChanged)
            {
                var line = _yayaLines.Take(tutorial.Cue);
                _tutorialCue = tutorial.Cue;
                _tutorialLine = line.Line;
                _portrait.sprite = GetMatchPortrait(line.Expression);
#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
                LastCue = tutorial.Cue;
#endif
            }
            string coord = point < 0 ? "" : PvpService.FormatPoint(point % GoCore.BoardSize, point / GoCore.BoardSize);
            string speech = _tutorialLine.Replace("{point}", coord);
            if (!string.IsNullOrEmpty(feedback))
            {
                speech += "\n\n" + feedback;
            }
            _tutorialFeedback = feedback;
            _tutorialPoint = point;
            _tutorialBubbleWidth = rect.rect.width;
            Set(_speech, speech);
            // 顺着既有气泡从下往上扩展，并限制在屏幕内。窄侧栏时才用预设最低字号。
            float totalHeight = ((RectTransform)transform).rect.height;
            float normal = totalHeight * (rect.anchorMax.y - rect.anchorMin.y);
            float available = totalHeight * (.975f - rect.anchorMax.y);
            float padding = _speech.rectTransform.offsetMin.y - _speech.rectTransform.offsetMax.y;
            _speech.resizeTextForBestFit = false;
            float extra = Mathf.Ceil(_speech.preferredHeight + padding - normal);
            _speech.resizeTextForBestFit = true;
            rect.sizeDelta = new Vector2(rect.sizeDelta.x, Mathf.Clamp(extra, 0, Mathf.Max(0, available)));
        }

        /// <summary>展示实际行动、提子和数子结果。练习换盘时先归还上一节的计分表现。</summary>
        private void RefreshTutorialBoard(GoCore core, bool lessonChanged)
        {
            int captures = core.BlackCaptures + core.WhiteCaptures;
            // 业务提交可能换上新克隆，只有换课程或回退序号才应重置音效游标。
            if (lessonChanged || core.Sequence < _tutorialSequence || (_tutorialCountShown && core.Phase != GoPhase.Finished))
            {
                _tutorialSequence = core.Sequence;
                _tutorialCaptures = captures;
                _tutorialCountShown = false;
                _board.ResetScoreCount();
            }
            if (captures > _tutorialCaptures)
            {
                PlayMatchSound(CaptureSound, .49f);
            }
            else if (core.Sequence != _tutorialSequence && core.Phase == GoPhase.Playing)
            {
                PlayMatchSound(PlaceSound, .55f);
            }
            _tutorialSequence = core.Sequence;
            _tutorialCaptures = captures;
            if (core.Phase == GoPhase.Finished && core.EndReason == GoEndReason.Scored)
            {
                GoScore score = core.Score;
                Set(_black, "黑方\n" + score.BlackStones + " 子 + " + score.BlackTerritory + " 空");
                Set(_white, "白方\n" + score.WhiteStones + " 子 + " + score.WhiteTerritory + " 空");
                Set(_turn, "白贴 " + (score.KomiHalfPoints / 2f).ToString("0.#") + " 目");
                if (!_tutorialCountShown)
                {
                    _tutorialCountShown = true;
                    _board.BeginScoreCount(core);
                    _board.SetScoreProgress(GoCore.PointCount + 4);
                    PlayMatchSound(ResultSound, .52f);
                }
                return;
            }
            Set(_black, "黑方\n提 " + core.BlackCaptures + " 子");
            Set(_white, "白方\n提 " + core.WhiteCaptures + " 子");
            Set(_turn, core.Phase == GoPhase.Scoring ? "双方停一手 · 确认死子" :
                core.Turn == GoSide.Black ? "轮到黑棋" : "轮到白棋");
        }

        /// <summary>恢复普通棋局的控件、对白与棋盘计分画面。</summary>
        private void HideTutorialHud()
        {
            if (_tutorialControls == null || !_tutorialControls.activeSelf)
            {
                return;
            }
            Show(_tutorialControls, false);
            Show(_normalControls, true);
            _speech.resizeTextForBestFit = _regularSpeechBestFit;
            ((RectTransform)_bubble.transform).sizeDelta = Vector2.zero;
            _tutorialFeedback = _tutorialCue = _tutorialLine = string.Empty;
            _tutorialPoint = -1;
            ResetMatchFeedback();
        }

        /// <summary>推进已完成的练习或完成整个教学。</summary>
        private void AdvanceTutorialFromPanel()
        {
            if (Service.AdvanceTutorial() && Service.Tutorial == null)
            {
                ReturnFromTutorial();
            }
            RefreshView();
        }

        /// <summary>点击课程专用按钮提交停一手。</summary>
        private void PassTutorialFromPanel()
        {
            Service.PassTutorial();
            RefreshView();
        }

        /// <summary>点击课程专用按钮确认当前盘面，展示实际计分。</summary>
        private void ConfirmTutorialScoreFromPanel()
        {
            Service.ConfirmTutorialScore();
            RefreshView();
        }

        /// <summary>重置这一节的盘面，在原地再练一次。</summary>
        private void RetryTutorialFromPanel()
        {
            Service.RetryTutorial();
            RefreshView();
        }

        /// <summary>离开练习并回到模式选择，由业务决定首次教学进度。</summary>
        private void ExitTutorialFromPanel()
        {
            if (Service.ExitTutorial())
            {
                ReturnFromTutorial();
            }
        }

        /// <summary>先收起棋盘和专用控件，再请求同一场景中的模式菜单。</summary>
        private void ReturnFromTutorial()
        {
            HideTutorialHud();
            _board.gameObject.SetActive(false);
            _battleVisible = false;
            ShowDestination("Entry");
            RefreshView();
        }
    }

    /// <summary>教学临时控件的约定。只复制已有素材和字体，由宿主面板管理寿命。</summary>
    internal static class PvpTutorialControls
    {
        /// <summary>创建没有图形的布局根，供棋盘上的教学按钮共用。</summary>
        internal static RectTransform Rect(string name, Transform parent, float x0, float y0, float x1, float y1)
        {
            var rect = (RectTransform)new GameObject(name, typeof(RectTransform)).transform;
            rect.SetParent(parent, false);
            SetRect(rect, x0, y0, x1, y1);
            return rect;
        }

        /// <summary>以父节点归一化坐标定位控件，保留父级的安全框和缩放。</summary>
        internal static void SetRect(RectTransform rect, float x0, float y0, float x1, float y1)
        {
            rect.anchorMin = new Vector2(x0, y0);
            rect.anchorMax = new Vector2(x1, y1);
            rect.offsetMin = rect.offsetMax = Vector2.zero;
        }

        /// <summary>复制现有按钮的美术并明确换成教学事件，避免触发原对局动作。</summary>
        internal static Button Button(string name, Button source, Transform parent, string caption,
            float x0, float y0, float x1, float y1, Action click)
        {
            Button button = UnityEngine.Object.Instantiate(source, parent, false);
            button.name = name;
            SetRect((RectTransform)button.transform, x0, y0, x1, y1);
            button.interactable = true;
            button.onClick = new Button.ButtonClickedEvent();
            button.onClick.AddListener(() => click());
            button.GetComponentInChildren<Text>(true).text = caption;
            button.gameObject.SetActive(true);
            return button;
        }
    }
}
