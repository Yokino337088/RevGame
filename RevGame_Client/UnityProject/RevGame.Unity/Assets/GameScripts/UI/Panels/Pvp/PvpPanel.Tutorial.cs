using System;
using System.Collections.Generic;
using RevGame.Flow;
using Revolution;
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
        /// <summary>普通实操完成后保留反馈的秒数。</summary>
        private const float TutorialFeedbackSeconds = 1.75f;
        /// <summary>确认数子后保留最终盘面和分数的秒数。</summary>
        private const float TutorialScoreSeconds = 3f;
        /// <summary>这些句读或右括号应当跟随前一个字，避免被自动换行拆到行首。</summary>
        private const string TutorialTrailingPunctuation = "，。！？；：、…％）】》」』”’";
        /// <summary>左括号和起始引号应当跟随下一字，避免单独留在行尾。</summary>
        private const string TutorialLeadingPunctuation = "（【《「『“‘";
        /// <summary>本课自动推进的非缩放时间，负数表示当前没有等待中的推进。</summary>
        private double _tutorialAdvanceAt = -1;
        /// <summary>说明页采用本课台词配置的秒数，换课时更新。</summary>
        private float _tutorialReadSeconds;
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
        /// <summary>上次排版时的对白顶部，单位为面板设计坐标。</summary>
        private float _tutorialBubbleTop;
        /// <summary>上次排版所用的面板高度，单位为设计坐标。</summary>
        private float _tutorialFrameHeight;
        /// <summary>普通对局气泡的纵向锚点，退出教学时恢复。</summary>
        private Vector2 _regularBubbleVerticalAnchors;
        /// <summary>普通对局立绘的顶部锚点，教学退出时恢复原来的站姿空间。</summary>
        private float _regularPortraitTop;
        /// <summary>气泡与立绘在设计稿里的横向偏移差，入场用它清除残留动画位移。</summary>
        private float _regularBubbleOffsetFromPortrait;
        /// <summary>进入教学前的对白缩放模式，退场后还原。</summary>
        private bool _regularSpeechBestFit;
        /// <summary>普通对局的对白字号，教学独立排版后恢复。</summary>
        private int _regularSpeechFontSize;
        /// <summary>普通对局的对白对齐方式，教学独立排版后恢复。</summary>
        private TextAnchor _regularSpeechAlignment;
        /// <summary>当前课程已经展示数子效果，避免每次刷新重播。</summary>
        private bool _tutorialCountShown;

        /// <summary>真实练习盘已加载、已显示且教学视图能够接单。</summary>
        internal bool TutorialReady => Menu == "Tutorial" && _battleVisible && _board != null
            && _board.Ready && _tutorialControls != null && _tutorialControls.activeInHierarchy;

        /// <summary>复制练习所需的按钮素材，缓存对局立绘和对白的基准，便于退场还原。</summary>
        private void BindTutorialHud()
        {
            _normalControls = transform.Find("controls").gameObject;
            var bubble = (RectTransform)_bubble.transform;
            _regularBubbleVerticalAnchors = new Vector2(bubble.anchorMin.y, bubble.anchorMax.y);
            _regularPortraitTop = _portrait.rectTransform.anchorMax.y;
            _regularBubbleOffsetFromPortrait = bubble.anchoredPosition.x - _portrait.rectTransform.anchoredPosition.x;
            _regularSpeechBestFit = _speech.resizeTextForBestFit;
            _regularSpeechFontSize = _speech.fontSize;
            _regularSpeechAlignment = _speech.alignment;
            RectTransform controls = PvpTutorialControls.Rect("tutorialControls", transform, 0, 0, 1, 1);
            _tutorialControls = controls.gameObject;
            // 按钮和开发态 GM 共用同一套处理，GM 另外读取返回值来判断是否被拒绝。
            _tutorialPass = PvpTutorialControls.Button("tutorialPass", _pass, controls, "停一手",
                .435f, .025f, .565f, .095f, () => PassTutorialFromPanel());
            _tutorialConfirm = PvpTutorialControls.Button("tutorialConfirmScore", _hint, controls, "确认数子",
                .435f, .025f, .565f, .095f, () => ConfirmTutorialScoreFromPanel());
            PvpTutorialControls.Button("tutorialRetry", _back, controls, "重练本节",
                .025f, .12f, .174f, .185f, () => RetryTutorialFromPanel());
            PvpTutorialControls.Button("tutorialExit", _back, controls, "退出教学",
                .025f, .03f, .174f, .095f, () => ExitTutorialFromPanel());
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
                // 用同一侧栏立绘的实际偏移恢复气泡位置，保留设备安全区的横向扩展。
                var bubble = (RectTransform)_bubble.transform;
                bubble.anchoredPosition = new Vector2(_portrait.rectTransform.anchoredPosition.x + _regularBubbleOffsetFromPortrait, 0f);
                _speech.resizeTextForBestFit = false;
                _speech.alignment = TextAnchor.UpperLeft;
            }

            Set(_title, "围棋教学  " + tutorial.StepNumber + "/" + tutorial.StepCount);
            Show(_tutorialPass.gameObject, tutorial.CanPass);
            Show(_tutorialConfirm.gameObject, tutorial.CanConfirmScore);
            bool lessonChanged = _tutorialCue != tutorial.Cue;
            RefreshTutorialDialogue(tutorial, lessonChanged);
            RefreshTutorialBoard(tutorial.Core, lessonChanged);
        }

        /// <summary>读取 Excel 台词和表情。正文、反馈或可用几何变化时才重新换行。</summary>
        private void RefreshTutorialDialogue(PvpTutorial tutorial, bool lessonChanged)
        {
            var rect = (RectTransform)_bubble.transform;
            var root = (RectTransform)rect.parent;
            if (root.rect.height <= 0f || rect.rect.width <= 0f)
            {
                return;
            }
            float top = TutorialStatusBottom(root) - 18f;
            int point = tutorial.HintPoint;
            string feedback = tutorial.Feedback;
            if (!lessonChanged && feedback == _tutorialFeedback && point == _tutorialPoint &&
                Mathf.Approximately(_tutorialBubbleTop, top) && Mathf.Approximately(_tutorialFrameHeight, root.rect.height)
                && Mathf.Approximately(_tutorialBubbleWidth, rect.rect.width))
            {
                return;
            }
            if (lessonChanged)
            {
                _tutorialAdvanceAt = -1;
                var line = _yayaLines.Take(tutorial.Cue);
                _tutorialCue = tutorial.Cue;
                _tutorialLine = line.Line;
                _tutorialReadSeconds = line.Seconds;
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
            _tutorialBubbleTop = top;
            _tutorialFrameHeight = root.rect.height;
            Set(_speech, LayoutTutorialDialogue(root, rect, top, speech));
        }

        /// <summary>读取顶部状态栏在面板设计坐标中的实际下边缘，气泡由这里向下排。</summary>
        private float TutorialStatusBottom(RectTransform root)
        {
            var status = (RectTransform)_title.transform.parent;
            return root.InverseTransformPoint(status.TransformPoint(new Vector3(0f, status.rect.yMin, 0f))).y;
        }

        /// <summary>为立绘留空间，优先按大字号布满正文，长反馈需要时才缩小字号。</summary>
        private string LayoutTutorialDialogue(RectTransform root, RectTransform bubble, float top, string speech)
        {
            const int MaxFont = 30;
            const int MinFont = 24;
            const float PortraitGap = 12f;
            float frameHeight = root.rect.height;
            RectTransform portrait = _portrait.rectTransform;
            float portraitBottom = root.rect.yMin + portrait.anchorMin.y * frameHeight;
            // 气泡上沿固定，最长也要保留至少四成设计高度给鸦鸦站姿。
            float maxHeight = Mathf.Max(0f, top - portraitBottom - .40f * frameHeight - PortraitGap);
            float padding = _speech.rectTransform.offsetMin.y - _speech.rectTransform.offsetMax.y;
            var settings = _speech.GetGenerationSettings(new Vector2(_speech.rectTransform.rect.width, 0f));
            settings.fontSize = MaxFont;
            settings.verticalOverflow = VerticalWrapMode.Overflow;
            TextGenerator generator = _speech.cachedTextGeneratorForLayout;
            float height;
            string wrapped;
            while (true)
            {
                wrapped = WrapTutorialPunctuation(speech, generator, settings);
                height = Mathf.Ceil(generator.GetPreferredHeight(wrapped, settings) /
                    _speech.pixelsPerUnit + padding + 2f);
                if (height <= maxHeight || settings.fontSize == MinFont)
                {
                    break;
                }
                settings.fontSize--;
            }
            _speech.fontSize = settings.fontSize;
            float bottom = top - Mathf.Min(maxHeight, Mathf.Max(108f, height));
            SetVerticalAnchors(bubble, (bottom - root.rect.yMin) / frameHeight, (top - root.rect.yMin) / frameHeight);
            SetVerticalAnchors(portrait, portrait.anchorMin.y,
                Mathf.Min(_regularPortraitTop, (bottom - PortraitGap - root.rect.yMin) / frameHeight));
            return wrapped;
        }

        /// <summary>以当前字体实测分行为准，提前换行把句读和相邻正文留在同一行。</summary>
        private static string WrapTutorialPunctuation(string text, TextGenerator generator, TextGenerationSettings settings)
        {
            // 每次只修正最早一个自动断点，再重新测量余下正文。短台词按有界次数处理。
            const int MaxCorrections = 12;
            for (int i = 0; i < MaxCorrections && generator.Populate(text, settings); i++)
            {
                int split = TutorialPunctuationBreak(text, generator.lines);
                if (split < 0)
                {
                    break;
                }
                text = text.Insert(split, "\n");
            }
            return text;
        }

        /// <summary>寻找首个可前移的自动断点，保留工作簿原有的段落换行。</summary>
        private static int TutorialPunctuationBreak(string text, IList<UILineInfo> lines)
        {
            for (int i = 1; i < lines.Count; i++)
            {
                int next = lines[i].startCharIdx;
                int previous = lines[i - 1].startCharIdx;
                if (next <= previous + 1 || next >= text.Length || text[next - 1] == '\n')
                {
                    continue;
                }

                int split = next;
                if (TutorialTrailingPunctuation.IndexOf(text[next]) >= 0)
                {
                    do
                    {
                        split--;
                    } while (split > previous + 1 && TutorialTrailingPunctuation.IndexOf(text[split]) >= 0);
                }
                while (split > previous + 1 && TutorialLeadingPunctuation.IndexOf(text[split - 1]) >= 0)
                {
                    split--;
                }
                if (split < next)
                {
                    return split;
                }
            }
            return -1;
        }

        /// <summary>只调整纵向范围，保留安全区已经计算好的横向锚点和偏移。</summary>
        private static void SetVerticalAnchors(RectTransform rect, float bottom, float top)
        {
            rect.anchorMin = new Vector2(rect.anchorMin.x, bottom);
            rect.anchorMax = new Vector2(rect.anchorMax.x, top);
            rect.offsetMin = new Vector2(rect.offsetMin.x, 0f);
            rect.offsetMax = new Vector2(rect.offsetMax.x, 0f);
        }

        /// <summary>只在可操作的教学画面计时，错点、重练、切场和遮挡都不会留下迟到推进。</summary>
        private void TickTutorialAdvance(PvpTutorial tutorial)
        {
            if (tutorial?.CanContinue != true || !TutorialReady || IsModalOpen ||
                RevServices.GetRequired<GameFlow>().IsGameplayBlocked)
            {
                _tutorialAdvanceAt = -1;
                return;
            }

            double now = Time.unscaledTimeAsDouble;
            if (_tutorialAdvanceAt < 0)
            {
                float seconds = tutorial.IsReadingLesson ? _tutorialReadSeconds :
                    tutorial.Core.EndReason == GoEndReason.Scored ? TutorialScoreSeconds : TutorialFeedbackSeconds;
                _tutorialAdvanceAt = now + seconds;
                return;
            }
            if (now >= _tutorialAdvanceAt)
            {
                AdvanceTutorialFromPanel();
            }
        }

#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
        /// <summary>读已落屏的气泡和文字实际边界，用于校验状态栏间距、立绘避让和文字换行。</summary>
        internal object TutorialDialogueSnapshot()
        {
            if (!TutorialReady || !BubbleVisible)
            {
                return null;
            }
            var bubble = (RectTransform)_bubble.transform;
            var root = (RectTransform)bubble.parent;
            RectTransform portrait = _portrait.rectTransform;
            float top = root.InverseTransformPoint(bubble.TransformPoint(new Vector3(0f, bubble.rect.yMax, 0f))).y;
            float bottom = root.InverseTransformPoint(bubble.TransformPoint(new Vector3(0f, bubble.rect.yMin, 0f))).y;
            float portraitTop = root.InverseTransformPoint(portrait.TransformPoint(new Vector3(0f, portrait.rect.yMax, 0f))).y;
            return new Dictionary<string, object>
            {
                ["statusGap"] = TutorialStatusBottom(root) - top,
                ["portraitGap"] = bottom - portraitTop,
                ["fontSize"] = _speech.fontSize,
                ["width"] = bubble.rect.width,
                ["height"] = bubble.rect.height,
                ["speechFits"] = SpeechFits,
                ["advancePending"] = _tutorialAdvanceAt >= 0
            };
        }
#endif

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

        /// <summary>清除待推进时间，并恢复普通棋局的控件、对白和立绘尺寸。</summary>
        private void HideTutorialHud()
        {
            if (_tutorialControls == null || !_tutorialControls.activeSelf)
            {
                return;
            }
            Show(_tutorialControls, false);
            Show(_normalControls, true);
            _tutorialAdvanceAt = -1;
            _speech.resizeTextForBestFit = _regularSpeechBestFit;
            _speech.fontSize = _regularSpeechFontSize;
            _speech.alignment = _regularSpeechAlignment;
            SetVerticalAnchors((RectTransform)_bubble.transform, _regularBubbleVerticalAnchors.x, _regularBubbleVerticalAnchors.y);
            SetVerticalAnchors(_portrait.rectTransform, _portrait.rectTransform.anchorMin.y, _regularPortraitTop);
            _tutorialFeedback = _tutorialCue = _tutorialLine = string.Empty;
            _tutorialPoint = -1;
            ResetMatchFeedback();
        }

        /// <summary>本节允许时由计时器推进，开发态也可立即推进。结课经业务回调再请求模式菜单。</summary>
        internal bool AdvanceTutorialFromPanel()
        {
            if (!Service.AdvanceTutorial())
            {
                return false;
            }
            _tutorialAdvanceAt = -1;
            if (Service.Tutorial == null)
            {
                ReturnFromTutorial();
            }
            RefreshView();
            return true;
        }

        /// <summary>只有停一手按钮可用时才提交真实停手，成功后刷新练习盘。</summary>
        internal bool PassTutorialFromPanel()
        {
            if (Service.Tutorial?.CanPass != true || !Service.PassTutorial())
            {
                return false;
            }
            RefreshView();
            return true;
        }

        /// <summary>只有当前课允许确认才提交真实数子，成功后展示最终盘面。</summary>
        internal bool ConfirmTutorialScoreFromPanel()
        {
            if (Service.Tutorial?.CanConfirmScore != true || !Service.ConfirmTutorialScore())
            {
                return false;
            }
            RefreshView();
            return true;
        }

        /// <summary>恢复本课初始盘面，并取消上一份作答反馈的推进计时。</summary>
        internal bool RetryTutorialFromPanel()
        {
            if (!Service.RetryTutorial())
            {
                return false;
            }
            _tutorialAdvanceAt = -1;
            RefreshView();
            return true;
        }

        /// <summary>放弃当前练习并请求回到模式选择，不提交结课进度。</summary>
        internal bool ExitTutorialFromPanel()
        {
            if (!Service.ExitTutorial())
            {
                return false;
            }
            ReturnFromTutorial();
            return true;
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
