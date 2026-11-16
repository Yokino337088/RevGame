using System;
using System.Collections.Generic;
using DG.Tweening;
using RevGame.Data;
using RevGame.Services;
using Revolution;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace RevGame.UI
{
    /// <summary>收藏书的打开、拖页、落页与关闭。资源、纸面材质和转场全部随面板回收。</summary>
    [RevUIPanel("UI/Panel/YayaMemories", RevUILayer.Popup, CacheMode = RevUICacheMode.DestroyOnClose)]
    public sealed class YayaMemoryBookPanel : RevUIPanel, IBeginDragHandler, IDragHandler, IEndDragHandler
    {
        internal static readonly string[] ThemeAtlases = { "UIYayaMemories" };
        private enum Phase { Opening, Idle, Dragging, Turning, Closing }

        [SerializeField] private YayaMemoryBook book;
        [SerializeField] private Material sceneTemplate;

        [RevBind("Paper")] private RawImage _paper;
        [RevBind("Cover")] private RawImage _cover;
        [RevBind("CoverTitle")] private CanvasGroup _coverTitle;
        [RevBind("Words")] private CanvasGroup _words;
        [RevBind("Words/Chapter")] private Text _title;
        [RevBind("Words/Caption")] private Text _caption;
        [RevBind("Words/Unavailable")] private Text _unavailable;
        [RevBind("PageNumber")] private Text _pageNumber;
        [RevBind("btnPrevious")] private Button _previousButton;
        [RevBind("btnNext")] private Button _nextButton;
        [RevBind("Notice")] private CanvasGroup _notice;
        [RevBind("Notice/Label")] private Text _noticeText;

        private static readonly int Progress = Shader.PropertyToID("_Progress");
        private static readonly int Direction = Shader.PropertyToID("_Direction");
        private static readonly int Tilt = Shader.PropertyToID("_Tilt");
        private static readonly int Current = Shader.PropertyToID("_Current");
        private static readonly int Next = Shader.PropertyToID("_Next");
        private static readonly int HasCurrent = Shader.PropertyToID("_HasCurrent");
        private static readonly int HasNext = Shader.PropertyToID("_HasNext");

        private readonly Dictionary<int, YayaMemoryIllustration> _chapters = new Dictionary<int, YayaMemoryIllustration>();
        private YayaMemoryProgress _progress;
        private Material _paperMaterial;
        private Material _coverMaterial;
        private Material _sceneMaterial;
        private Phase _phase;
        private Tween _transition;
        private Tween _barrage;
        private int _version;
        private int _chapter;
        private int _target;
        private int _dragPointer;
        private int _direction;
        private float _amount;
        private float _dragStart;
        private float _clockStart;

        protected override void OnBindView()
        {
            GameMobileLayout.EnableSafePanel(transform, "Backdrop");
            _paperMaterial = new Material(_paper.material);
            _coverMaterial = new Material(_cover.material);
            _sceneMaterial = new Material(sceneTemplate);
            _paper.material = _paperMaterial;
            _cover.material = _coverMaterial;
        }

        protected override void PlayOpenTransition(Action onDone)
        {
            _phase = Phase.Opening;
            _clockStart = Time.unscaledTime;
            _progress = RevServices.GetRequired<YayaMemoryProgress>();
            _chapter = _progress.OpeningChapter();
            PrepareCover(0f);
            SetNavigation();
            WaitAndOpen(++_version, onDone).Forget();
        }

        private async RevTask WaitAndOpen(int version, Action onDone)
        {
            var page = Page(_chapter);
            while (version == _version && page.Waiting)
            {
                await RevTask.Yield();
            }
            if (version != _version)
            {
                return;
            }
            UpdatePaper();
            ShowWords();
            _transition = DOTween.To(() => 0f, PrepareCover, 1f, .95f).SetDelay(.12f)
                .SetUpdate(true).SetEase(Ease.InOutCubic).SetRecyclable(false).OnComplete(() =>
                {
                    _transition = null;
                    _cover.gameObject.SetActive(false);
                    onDone();
                });
        }

        protected override void OnOpen()
        {
            LandOnPage();
        }

        protected override void PlayCloseTransition(Action onDone)
        {
            ++_version;
            StopAnimations();
            _phase = Phase.Closing;
            SetNavigation();
            PrepareCover(1f);
            _transition = DOTween.To(() => 1f, PrepareCover, 0f, .65f)
                .SetUpdate(true).SetEase(Ease.InOutCubic).SetRecyclable(false).OnComplete(() =>
                {
                    _transition = null;
                    onDone();
                });
        }

        protected override void OnClose()
        {
            ReleasePages();
        }

        protected override void OnRelease()
        {
            ReleasePages();
            Destroy(_paperMaterial);
            Destroy(_coverMaterial);
            Destroy(_sceneMaterial);
        }

        private void PrepareCover(float amount)
        {
            _cover.gameObject.SetActive(true);
            _coverMaterial.SetFloat(Progress, amount);
            _coverMaterial.SetFloat(Tilt, .24f);
            _coverTitle.alpha = 1f - Mathf.SmoothStep(0f, 1f, amount * 5f);
        }

        private void LateUpdate()
        {
            if (_progress != null)
            {
                UpdatePaper();
                // 已经打开的锁页可以在 GM 解锁并绘制成功后原地显示，不必重新开书。
                if (_phase == Phase.Idle && _unavailable.gameObject.activeSelf && Page(_chapter).Image != null)
                {
                    StopBarrage();
                    ShowWords();
                    _progress.MarkRead(_chapter);
                }
            }
        }

        private void UpdatePaper()
        {
            float now = Time.unscaledTime - _clockStart;
            Texture current = Page(_chapter).Draw(_sceneMaterial, now);
            Texture next = _target == 0 ? null : Page(_target).Draw(_sceneMaterial, now);
            _paperMaterial.SetTexture(Current, current);
            _paperMaterial.SetTexture(Next, next);
            _paperMaterial.SetFloat(HasCurrent, current == null ? 0f : 1f);
            _paperMaterial.SetFloat(HasNext, next == null ? 0f : 1f);
        }

        private YayaMemoryIllustration Page(int chapter)
        {
            bool unlocked = _progress.IsUnlocked(chapter);
            if (!_chapters.TryGetValue(chapter, out var page) || page.Unlocked != unlocked)
            {
                page?.Dispose();
                page = new YayaMemoryIllustration(chapter, unlocked);
                _chapters[chapter] = page;
            }
            return page;
        }

        /// <summary>只把已经完整落稳且有像素的插画记为已读。</summary>
        private void LandOnPage()
        {
            _target = 0;
            _phase = Phase.Idle;
            SetAmount(0f);
            SetNavigation();
            UpdatePaper();
            ShowWords();

            YayaMemoryIllustration page = Page(_chapter);
            if (!page.Unlocked)
            {
                ShowBarrage("请先通关第" + _chapter + "章，解锁这张收藏图");
            }
            else if (page.Image != null)
            {
                _progress.MarkRead(_chapter);
            }
            else
            {
                ShowBarrage("插画加载失败，请合上书后再试");
            }

            // 前后页保持可拖起，已离开的合成图本帧即可释放。
            for (int chapter = 1; chapter <= YayaMemoryProgress.ChapterCount; chapter++)
            {
                if (chapter != _chapter && _chapters.TryGetValue(chapter, out var adjacent))
                {
                    adjacent.ReleaseImage();
                    if (Mathf.Abs(chapter - _chapter) > 1)
                    {
                        adjacent.Dispose();
                        _chapters.Remove(chapter);
                    }
                }
            }
            if (_chapter > 1)
            {
                Page(_chapter - 1);
            }
            if (_chapter < YayaMemoryProgress.ChapterCount)
            {
                Page(_chapter + 1);
            }
        }

        private void ShowWords()
        {
            var entry = book.Chapters[_chapter - 1];
            var page = Page(_chapter);
            _title.text = "第" + _chapter + "章  ·  " + entry.Title;
            _caption.text = page.Image != null ? entry.Caption : "";
            _unavailable.text = page.Unlocked ? "插画暂时无法展开" : "通关第" + _chapter + "章后，这一页就会写满回忆";
            _unavailable.gameObject.SetActive(page.Image == null);
            _pageNumber.text = _chapter.ToString("D2") + "  /  " + YayaMemoryProgress.ChapterCount.ToString("D2");
        }

        private void SetNavigation()
        {
            _previousButton.interactable = _phase == Phase.Idle && _chapter > 1;
            _nextButton.interactable = _phase == Phase.Idle && _chapter < YayaMemoryProgress.ChapterCount;
        }

        [RevButtonClick("btnPrevious")]
        private void Previous()
        {
            Nudge(-1);
        }

        [RevButtonClick("btnNext")]
        private void NextPage()
        {
            Nudge(1);
        }

        private void Nudge(int direction)
        {
            if (BeginTurn(direction, direction > 0 ? -.28f : .28f))
            {
                FinishTurn(true, ++_version).Forget();
            }
        }

        [RevButtonClick("btnClose")]
        private void CloseBook()
        {
            RevUI.Close(this);
        }

        private bool BeginTurn(int direction, float tilt)
        {
            int target = _chapter + direction;
            if (_phase != Phase.Idle || target < 1 || target > YayaMemoryProgress.ChapterCount)
            {
                return false;
            }

            _direction = direction;
            _target = target;
            _phase = Phase.Dragging;
            StopBarrage();
            _paperMaterial.SetFloat(Direction, direction);
            _paperMaterial.SetFloat(Tilt, tilt);
            SetNavigation();
            return true;
        }

        public void OnBeginDrag(PointerEventData eventData)
        {
            if (eventData.button != PointerEventData.InputButton.Left ||
                !PagePoint(eventData.pressPosition, eventData, out Vector2 uv))
            {
                return;
            }

            int direction = uv.x >= .5f ? 1 : -1;
            if ((uv.x < .43f || uv.x > .57f) && BeginTurn(direction, (uv.y - .5f) * .65f))
            {
                _dragPointer = eventData.pointerId;
                _dragStart = uv.x;
                OnDrag(eventData);
            }
        }

        public void OnDrag(PointerEventData eventData)
        {
            if (_phase == Phase.Dragging && eventData.pointerId == _dragPointer &&
                PagePoint(eventData.position, eventData, out Vector2 uv))
            {
                SetAmount(Mathf.Clamp01((_dragStart - uv.x) * _direction / .72f));
            }
        }

        public void OnEndDrag(PointerEventData eventData)
        {
            if (_phase == Phase.Dragging && eventData.pointerId == _dragPointer)
            {
                FinishTurn(_amount >= .32f, ++_version).Forget();
            }
        }

        private bool PagePoint(Vector2 screenPoint, PointerEventData eventData, out Vector2 uv)
        {
            var rect = _paper.rectTransform;
            bool success = RectTransformUtility.ScreenPointToLocalPointInRectangle(rect, screenPoint, eventData.pressEventCamera, out var local);
            uv = (local - rect.rect.min) / rect.rect.size;
            return success;
        }

        private async RevTask FinishTurn(bool commit, int version)
        {
            _phase = Phase.Turning;
            bool failed = false;
            if (commit)
            {
                var target = Page(_target);
                while (version == _version && target.Waiting)
                {
                    await RevTask.Yield();
                }
                if (version != _version)
                {
                    return;
                }
                UpdatePaper();
                failed = target.Unlocked && target.Image == null;
                commit = !failed;
            }

            float end = commit ? 1f : 0f;
            float duration = commit ? Mathf.Lerp(.22f, .74f, 1f - _amount) : .29f;
            _transition = DOTween.To(() => _amount, SetAmount, end, duration)
                .SetUpdate(true).SetEase(Ease.OutCubic).SetRecyclable(false).OnComplete(() =>
                {
                    _transition = null;
                    if (commit)
                    {
                        _chapter = _target;
                        LandOnPage();
                    }
                    else
                    {
                        var unused = Page(_target);
                        unused.ReleaseImage();
                        if (failed)
                        {
                            unused.Dispose();
                            _chapters.Remove(_target);
                        }
                        _target = 0;
                        _phase = Phase.Idle;
                        SetNavigation();
                        if (failed)
                        {
                            ShowBarrage("这一页暂时无法展开，请再试一次");
                        }
                    }
                });
        }

        private void SetAmount(float amount)
        {
            _amount = amount;
            _paperMaterial.SetFloat(Progress, amount);
            _words.alpha = 1f - Mathf.SmoothStep(0f, 1f, amount * 4f);
        }

        private void ShowBarrage(string message)
        {
            StopBarrage();
            _noticeText.text = message;
            _notice.gameObject.SetActive(true);
            _notice.alpha = 0f;
            var rect = (RectTransform)_notice.transform;
            rect.anchoredPosition = new Vector2(260f, 0f);
            _barrage = DOTween.Sequence().SetUpdate(true).SetRecyclable(false)
                .Append(rect.DOAnchorPosX(0f, .33f).SetEase(Ease.OutCubic))
                .Join(_notice.DOFade(1f, .24f)).AppendInterval(2.3f)
                .Append(rect.DOAnchorPosX(-190f, .32f).SetEase(Ease.InCubic))
                .Join(_notice.DOFade(0f, .32f)).OnComplete(() =>
                {
                    _barrage = null;
                    _notice.gameObject.SetActive(false);
                });
        }

        private void StopBarrage()
        {
            _barrage?.Kill();
            _barrage = null;
            _notice.gameObject.SetActive(false);
        }

        private void StopAnimations()
        {
            _transition?.Kill();
            _transition = null;
            StopBarrage();
        }

        private void ReleasePages()
        {
            ++_version;
            StopAnimations();
            _progress = null;
            if (_paperMaterial != null)
            {
                _paperMaterial.SetTexture(Current, null);
                _paperMaterial.SetTexture(Next, null);
            }
            foreach (var page in _chapters.Values)
            {
                page.Dispose();
            }
            _chapters.Clear();
        }

#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
        [RevBind("Words/ChapterPlate")] private Image _chapterPlate;
        [RevBind("btnClose")] private Button _closeButton;

        /// <summary>只读落屏证据。通过真实按钮、真实拖拽和实际资源拿到这些状态。</summary>
        public Dictionary<string, object> Inspect()
        {
            var page = _chapters.TryGetValue(_chapter, out var found) ? found : null;
            var previous = ((Image)_previousButton.targetGraphic).sprite;
            var next = ((Image)_nextButton.targetGraphic).sprite;
            var close = ((Image)_closeButton.targetGraphic).sprite;
            Vector4 photoRect = _paperMaterial.GetVector("_PhotoRect");
            return new Dictionary<string, object>
            {
                ["chapter"] = _chapter, ["target"] = _target, ["phase"] = _phase.ToString(),
                ["amount"] = _amount, ["artReady"] = page?.Image != null, ["unlocked"] = page?.Unlocked == true,
                ["title"] = _title.text, ["caption"] = _caption.text,
                ["notice"] = _notice.gameObject.activeInHierarchy ? _noticeText.text : "",
                ["residentPages"] = _chapters.Count, ["coverVisible"] = _cover.gameObject.activeInHierarchy,
                ["shader"] = _sceneMaterial.shader.name,
                ["prevSprite"] = previous.name, ["nextSprite"] = next.name, ["closeSprite"] = close.name,
                ["titleSprite"] = _chapterPlate.sprite.name,
                ["themePacked"] = previous.packed && next.packed && close.packed && _chapterPlate.sprite.packed,
                ["photoWidth"] = Mathf.RoundToInt((photoRect.z - photoRect.x) * 1000f),
                ["photoHeight"] = Mathf.RoundToInt((photoRect.w - photoRect.y) * 1000f)
            };
        }
#endif
    }
}
