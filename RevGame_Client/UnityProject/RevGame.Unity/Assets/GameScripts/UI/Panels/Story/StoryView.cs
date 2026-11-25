using System.Globalization;
using DG.Tweening;
using Revolution;
using UnityEngine;
using UnityEngine.UI;

namespace RevGame.UI
{
    /// <summary>框架节点级View（RevUIPart）：负责画像、打字与点击通知，不读存档、不改变场景。</summary>
    public sealed class StoryView : RevUIPart
    {
        /// <summary>默认对话卡上缘在安全区中的位置，给正文留出更高的连续区域。</summary>
        private const float DefaultCardTop = .55f;
        /// <summary>长句最多扩张到此处，留出右上方的跳过按钮区域。</summary>
        private const float MaximumCardTop = .82f;
        /// <summary>安全区变窄时仍尽量保持正文清晰的设计字号下限。</summary>
        private const int MinimumDialogueFontSize = 24;
        /// <summary>给字形边缘和取整留出的垂直余量，单位为画布坐标。</summary>
        private const float DialoguePadding = 4f;
        /// <summary>绑定对话卡标题、说话人、正文与阅读进度，均仅显示当前帧。</summary>
        [RevBind] private Text _title;
        [RevBind] private Text _speaker;
        [RevBind] private Text _dialogue;
        [RevBind] private Text _counter;
        /// <summary>剧情表情立绘，面板关闭必须清Sprite引用再归还表情图集。</summary>
        [RevBind] private Image _guide;
        [RevBind("DialogueCard")] private RectTransform _card;
        [RevBind("btnSkip")] private RectTransform _skip;
        private Rect _guideAnchors;
        private Rect _cardAnchors;
        private Rect _skipAnchors;
        private Rect _lastSafeArea;
        /// <summary>上次排版的视图尺寸，等比例调整窗口时也需要重新测量正文。</summary>
        private Vector2 _lastViewSize;
        /// <summary>预制体正文的正常字号。每句都从这一尺寸尝试排版。</summary>
        private int _normalDialogueFontSize;
        /// <summary>本次正在打字的整句，只在换句或关闭时更新。</summary>
        private string _fullDialogue = string.Empty;
        /// <summary>本句完整的 Unicode 文字元素数，用于瞬间显示整句时保持观察值一致。</summary>
        private int _dialogueElementCount;
        /// <summary>当前页码，换句后生成一次，打字完成时只更新阅读提示。</summary>
        private string _counterPrefix = string.Empty;
        private bool _layoutReady;
        /// <summary>只保存自己创建的Tween/音效句柄，换句、关闭、销毁均收尾，绝不KillAll。</summary>
        private Tween _typing;
        /// <summary>当前表情在Book集合里的索引，仅用于检查切换，与大厅待机帧分开。</summary>
        public int GuideFrame
        {
            get; private set;
        }
        public bool GuidePacked => _guide != null && _guide.sprite != null && _guide.sprite.packed;
        /// <summary>换句或离开面板时回收本视图的字幕音效。</summary>
        private readonly StoryTypingSound _typingSound = new StoryTypingSound();
        /// <summary>缓存当前播放代次与行号，重复落屏不重启动画，新篇章同下标仍能刷新。</summary>
        private int _version = -1;
        private int _index = -1;
        /// <summary>已显示Unicode文字元素数量，用于自动化观察打字进度。</summary>
        public int VisibleCharacters
        {
            get; private set;
        }
        /// <summary>可观察的播放证据：成功获得声音句柄才算发声；用于区分“调用了Play”和“确实开始播放”。</summary>
        public bool SoundStarted
        {
            get
            {
                return _typingSound.Started;
            }
        }
        /// <summary>打字仍在进行时为真，点击先显示整句，阅读完毕后才通知宿主翻页。</summary>
        public bool IsTyping
        {
            get
            {
                return _typing != null && _typing.IsActive();
            }
        }
        /// <summary>只装配文字显示规则，禁用富文本避免对白被当标记解释。</summary>
        protected override void OnBindView()
        {
            _dialogue.supportRichText = false;
            _dialogue.alignment = TextAnchor.UpperLeft;
            _normalDialogueFontSize = _dialogue.fontSize;
            // 标题、署名、正文与页脚各有明确的竖向区间，正文可以占用卡片的主要高度。
            SetVerticalAnchors(_title.rectTransform, .86f, .95f);
            SetVerticalAnchors(_speaker.rectTransform, .75f, .85f);
            SetVerticalAnchors(_dialogue.rectTransform, .17f, .74f);
            SetVerticalAnchors(_counter.rectTransform, .035f, .145f);
            // 标题靠近卡片上缘，神殿高光会压低浅金字的对比。只在绑定时配置一次深色描边，翻页不重复创建。
            if (!_title.TryGetComponent(out Outline titleOutline))
            {
                titleOutline = _title.gameObject.AddComponent<Outline>();
            }
            titleOutline.effectColor = new Color(0.04f, 0.07f, 0.12f, 1f);
            titleOutline.effectDistance = new Vector2(2f, -2f);
            _guideAnchors = ReadAnchors(_guide.rectTransform);
            _cardAnchors = ReadAnchors(_card);
            _cardAnchors.yMax = Mathf.Max(_cardAnchors.yMax, DefaultCardTop);
            _skipAnchors = ReadAnchors(_skip);
            _layoutReady = true;
            ApplySafeArea();
        }

        /// <summary>只检测窗口和安全区变化，尺寸稳定时不重复测量打字中的内容。</summary>
        private void LateUpdate()
        {
            if (_layoutReady)
            {
                ApplySafeArea();
            }
        }

        /// <summary>预制体的比例作为基准。旋转或换预览设备时只调整内容区，翻页点击区始终铺满。</summary>
        private void ApplySafeArea()
        {
            Rect safe = GameBattleLayout.SafeViewport();
            Vector2 size = ((RectTransform)transform).rect.size;
            if (safe == _lastSafeArea && size == _lastViewSize)
            {
                return;
            }

            _lastSafeArea = safe;
            _lastViewSize = size;
            SetSafeAnchors(_guide.rectTransform, _guideAnchors, safe);
            SetSafeAnchors(_skip, _skipAnchors, safe);
            FitDialogue();
        }

        /// <summary>用整句测量所需空间，先扩卡片，再在小画幅下按需缩小正文。</summary>
        private void FitDialogue()
        {
            Rect design = _cardAnchors;
            SetSafeAnchors(_card, design, _lastSafeArea);
            _dialogue.fontSize = _normalDialogueFontSize;
            if (_fullDialogue.Length == 0 || _lastViewSize.y <= 0f || _dialogue.rectTransform.rect.width <= 0f)
            {
                return;
            }

            RectTransform body = _dialogue.rectTransform;
            var settings = _dialogue.GetGenerationSettings(new Vector2(body.rect.width, 0f));
            float required = MeasureDialogue(settings);
            if (required > body.rect.height && _lastSafeArea.height > 0f)
            {
                // 正文高度按锚点比例随卡片增大。保持卡片在安全区内并避开跳过按钮。
                float bodyScale = body.anchorMax.y - body.anchorMin.y;
                float additional = (required - body.rect.height) / (bodyScale * _lastViewSize.y * _lastSafeArea.height);
                design.yMax = Mathf.Min(Mathf.Max(design.yMax, MaximumCardTop), design.yMax + additional);
                SetSafeAnchors(_card, design, _lastSafeArea);
            }
            while (required > body.rect.height && settings.fontSize > MinimumDialogueFontSize)
            {
                settings.fontSize--;
                required = MeasureDialogue(settings);
            }
            _dialogue.fontSize = settings.fontSize;
        }

        /// <summary>计算整句换行所需的高度，包含字形留白。</summary>
        private float MeasureDialogue(TextGenerationSettings settings)
        {
            return Mathf.Ceil(_dialogue.cachedTextGeneratorForLayout.GetPreferredHeight(_fullDialogue, settings) /
                _dialogue.pixelsPerUnit + DialoguePadding);
        }

        /// <summary>调整卡片内部的纵向区间，保留预制体原有横向边距。</summary>
        private static void SetVerticalAnchors(RectTransform rect, float bottom, float top)
        {
            rect.anchorMin = new Vector2(rect.anchorMin.x, bottom);
            rect.anchorMax = new Vector2(rect.anchorMax.x, top);
        }

        private static Rect ReadAnchors(RectTransform rect)
        {
            return new Rect(rect.anchorMin, rect.anchorMax - rect.anchorMin);
        }

        private static void SetSafeAnchors(RectTransform rect, Rect design, Rect safe)
        {
            rect.anchorMin = safe.position + Vector2.Scale(design.min, safe.size);
            rect.anchorMax = safe.position + Vector2.Scale(design.max, safe.size);
        }
        /// <summary>先排版整句再逐字显示，避免文字逼近边缘时改变行高或裁掉结尾。</summary>
        public void Show(StoryPanel.Frame frame)
        {
            if (_version == frame.Version && _index == frame.Index)
            {
                return;
            }
            StopTyping();
            _version = frame.Version;
            _index = frame.Index;
            var line = frame.Episode.Lines[frame.Index];
            _title.text = frame.Episode.Title;
            _speaker.text = line.Speaker;
            _counterPrefix = (frame.Index + 1) + " / " + frame.Episode.Lines.Length + "    ";
            _counter.text = _counterPrefix + "点击任意处 · 显示整句";
            // 每句配置明确指定表情，不把六张不同情绪当序列帧循环；立绘缩放保留原图比例。
            _guide.sprite = frame.Portrait;
            GuideFrame = frame.ExpressionIndex;
            _guide.preserveAspect = true;
            _guide.transform.SetAsLastSibling();
            _fullDialogue = line.Text;
            _dialogue.text = string.Empty;
            FitDialogue();
            int[] ends = StringInfo.ParseCombiningCharacters(line.Text);
            _dialogueElementCount = ends.Length;
            VisibleCharacters = 0;
            if (_dialogueElementCount == 0)
            {
                ShowCompleteLine();
                return;
            }
            _typing = DOTween.To(() => VisibleCharacters, count =>
            {
                if (count == VisibleCharacters)
                {
                    return;
                }
                VisibleCharacters = count;
                int end = count >= ends.Length ? line.Text.Length : ends[count];
                _dialogue.text = line.Text.Substring(0, end);

                _typingSound.OnTextWritten();
            }, ends.Length, ends.Length / frame.Book.CharactersPerSecond).SetEase(Ease.Linear).SetUpdate(true)
                .OnComplete(() =>
                {
                    _typing = null;
                    ShowCompleteLine();
                });
        }

        /// <summary>自然播完和提前读完都使用同一份完整正文与页脚。</summary>
        private void ShowCompleteLine()
        {
            VisibleCharacters = _dialogueElementCount;
            _dialogue.text = _fullDialogue;
            _counter.text = _counterPrefix + "点击任意处 · 下一句";
        }

        /// <summary>第一下补完仍在打字的文本。正文已经完整时，由宿主推进剧情。</summary>
        [RevButtonClick("btnNext")]
        private void OnNext()
        {
            if (IsTyping)
            {
                StopTyping(resetSound: false);
                ShowCompleteLine();
                return;
            }
            NotifyHost();
        }
        /// <summary>右上角跳过由宿主交给唯一GameFlow收尾，不能只隐藏UI留下剧情锁。</summary>
        [RevButtonClick("btnSkip")]
        private void OnSkip()
        {
            RevServices.GetRequired<RevGame.Flow.GameFlow>().SkipStory();
        }
        /// <summary>面板关闭释放打字、声音与立绘，同时清掉上一句的排版内容。</summary>
        protected override void OnPartClose()
        {
            StopTyping();
            _guide.sprite = null;
            _fullDialogue = string.Empty;
            _dialogueElementCount = 0;
            _counterPrefix = string.Empty;
            _version = -1;
            _index = -1;
        }
        /// <summary>真正销毁时仍执行同一收尾，绝不停止其它UI的Tween或声音。</summary>
        protected override void OnDestroy()
        {
            StopTyping();
            base.OnDestroy();
        }
        /// <summary>停止当前字效。显式补全文字时保留本句已经播过声音的观察状态。</summary>
        private void StopTyping(bool resetSound = true)
        {
            _typing?.Kill(false);
            _typing = null;
            if (resetSound)
            {
                _typingSound.Reset();
            }
            else
            {
                _typingSound.Stop();
            }
        }
    }
}
