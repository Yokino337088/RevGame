using System;
using DG.Tweening;
using RevGame.Data;
using Revolution;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace RevGame.UI
{
    /// <summary>在同一本纸书中查看项目资料和作者正文。纸卷使用收藏书的原画与材质。</summary>
    [RevUIPanel(RevResPath.UI_Panel + "ProjectOpenSource", RevUILayer.Popup, CacheMode = RevUICacheMode.DestroyOnClose)]
    public sealed partial class ProjectOpenSourcePanel : RevUIPanel, IBeginDragHandler, IDragHandler, IEndDragHandler
    {
        public const string RepositoryAddress = "github.com/Yokino337088/RevGame";
        public const string RepositoryUrl = "https://" + RepositoryAddress;
        public const string GroupNumber = "1130932807";
        internal const string GroupCardUrl = "mqqapi://card/show_pslcard?src_type=internal&version=1&uin=" + GroupNumber + "&card_type=group&source=external";
        internal static readonly string[] ThemeAtlases = { "UIProjectOpenSource", "UIYayaMemories" };

        private enum Phase
        {
            Opening, Idle, Dragging, Turning, Closing
        }

        [SerializeField] private ProjectOpenSourceConfig configuration;
        [RevBind("Paper")] private RawImage _paper;
        [RevBind("Cover")] private RawImage _cover;
        [RevBind("CoverTitle")] private CanvasGroup _coverTitle;
        [RevBind("Pages/Overview")] private CanvasGroup _overview;
        [RevBind("Pages/Author")] private CanvasGroup _author;
        [RevBind("Pages/Author/Message")] private ScrollRect _authorScroll;
        [RevBind("Pages/Author/Message/Body")] private Text _authorBody;
        [RevBind("Pages/Overview/Notice")] private Text _notice;
        [RevBind("Navigation/PageNumber")] private Text _pageNumber;
        [RevBind("Navigation/btnPrevious")] private Button _previous;
        [RevBind("Navigation/btnNext")] private Button _next;

        private static readonly int Progress = Shader.PropertyToID("_Progress");
        private static readonly int Direction = Shader.PropertyToID("_Direction");
        private static readonly int Tilt = Shader.PropertyToID("_Tilt");
        private static readonly int PhotoRect = Shader.PropertyToID("_PhotoRect");

        private Material _paperMaterial;
        private Material _coverMaterial;
        private Tween _transition;
        private Tween _noticeTimer;
        private Phase _phase;
        private int _page;
        private int _dragPointer;
        private float _dragStart;
        private float _amount;

        private CanvasGroup CurrentPage => _page == 0 ? _overview : _author;
        private CanvasGroup OtherPage => _page == 0 ? _author : _overview;
        private int TurnDirection => _page == 0 ? 1 : -1;

        protected override void OnBindView()
        {
            GameMobileLayout.EnableSafePanel(transform, "Backdrop");
            // 动画只修改当前实例。将相框区域移出 UV，保留纸张原图上的完整留白。
            _paperMaterial = new Material(_paper.material);
            _paperMaterial.SetVector(PhotoRect, new Vector4(2f, 2f, 3f, 3f));
            _coverMaterial = new Material(_cover.material);
            _paper.material = _paperMaterial;
            _cover.material = _coverMaterial;
            _authorBody.text = configuration.AuthorMessage ?? "";
        }

        protected override void PlayOpenTransition(Action onDone)
        {
            _phase = Phase.Opening;
            _page = 0;
            ShowPage();
            _cover.gameObject.SetActive(true);
            SetCover(0f);
            _transition = DOVirtual.Float(0f, 1f, .86f, SetCover).SetDelay(.10f)
                .SetEase(Ease.InOutCubic).SetUpdate(true).SetRecyclable(false).OnComplete(() =>
                {
                    _transition = null;
                    _cover.gameObject.SetActive(false);
                    _phase = Phase.Idle;
                    ShowPage();
                    onDone();
                });
        }

        protected override void PlayCloseTransition(Action onDone)
        {
            StopMotion();
            _phase = Phase.Closing;
            ShowPage();
            _cover.gameObject.SetActive(true);
            SetCover(1f);
            _transition = DOVirtual.Float(1f, 0f, .64f, SetCover)
                .SetEase(Ease.InOutCubic).SetUpdate(true).SetRecyclable(false).OnComplete(() =>
                {
                    _transition = null;
                    onDone();
                });
        }

        protected override void OnClose()
        {
            StopMotion();
        }

        protected override void OnRelease()
        {
            StopMotion();
            Destroy(_paperMaterial);
            Destroy(_coverMaterial);
        }

        private void SetCover(float amount)
        {
            _coverMaterial.SetFloat(Progress, amount);
            _coverMaterial.SetFloat(Tilt, .24f);
            _coverTitle.alpha = 1f - Mathf.SmoothStep(0f, 1f, amount * 5f);
        }

        private void ShowPage()
        {
            _amount = 0f;
            _paperMaterial.SetFloat(Progress, 0f);
            SetPageInput(CurrentPage, _phase == Phase.Idle);
            SetPageInput(OtherPage, false);
            CurrentPage.gameObject.SetActive(true);
            CurrentPage.alpha = 1f;
            OtherPage.gameObject.SetActive(false);
            _previous.gameObject.SetActive(_page == 1);
            _next.gameObject.SetActive(_page == 0);
            _previous.interactable = _next.interactable = _phase == Phase.Idle;
            _pageNumber.text = _page == 0 ? "1 / 2" : "2 / 2";
        }

        private static void SetPageInput(CanvasGroup page, bool enabled)
        {
            page.interactable = page.blocksRaycasts = enabled;
        }

        /// <summary>翻页过程中关闭两套按钮的输入。原页文字先离场，新页随纸张落稳显现。</summary>
        private void PrepareTurn(Phase phase, float tilt)
        {
            _phase = phase;
            ClearNotice();
            SetPageInput(CurrentPage, false);
            SetPageInput(OtherPage, false);
            OtherPage.gameObject.SetActive(true);
            _previous.interactable = _next.interactable = false;
            _paperMaterial.SetFloat(Direction, TurnDirection);
            _paperMaterial.SetFloat(Tilt, tilt);
            SetAmount(0f);
        }

        private void SetAmount(float amount)
        {
            _amount = amount;
            _paperMaterial.SetFloat(Progress, amount);
            CurrentPage.alpha = 1f - Mathf.SmoothStep(0f, 1f, amount / .22f);
            OtherPage.alpha = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(.68f, 1f, amount));
        }

        private void FinishTurn(bool commit, float duration)
        {
            _phase = Phase.Turning;
            _transition = DOVirtual.Float(_amount, commit ? 1f : 0f, duration, SetAmount)
                .SetEase(Ease.OutCubic).SetUpdate(true).SetRecyclable(false).OnComplete(() =>
                {
                    _transition = null;
                    if (commit)
                    {
                        _page = 1 - _page;
                    }
                    _phase = Phase.Idle;
                    ShowPage();
                    if (commit && _page == 1)
                    {
                        LayoutRebuilder.ForceRebuildLayoutImmediate(_authorBody.rectTransform);
                        _authorScroll.verticalNormalizedPosition = 1f;
                    }
                });
        }

        [RevButtonClick("btnNext")]
        private void NextPage()
        {
            TurnFromButton(0);
        }

        [RevButtonClick("btnPrevious")]
        private void PreviousPage()
        {
            TurnFromButton(1);
        }

        private void TurnFromButton(int originPage)
        {
            if (_phase == Phase.Idle && _page == originPage)
            {
                PrepareTurn(Phase.Turning, .21f);
                FinishTurn(true, .53f);
            }
        }

        public void OnBeginDrag(PointerEventData eventData)
        {
            if (_phase != Phase.Idle || eventData.button != PointerEventData.InputButton.Left
                || !PagePoint(eventData.pressPosition, eventData, out Vector2 uv)
                || uv.y < 0f || uv.y > 1f || uv.x < 0f || uv.x > 1f)
            {
                return;
            }
            if ((_page == 0 && uv.x < .75f) || (_page == 1 && uv.x > .25f))
            {
                return;
            }
            _dragPointer = eventData.pointerId;
            _dragStart = uv.x;
            PrepareTurn(Phase.Dragging, (uv.y - .5f) * .65f);
            OnDrag(eventData);
        }

        public void OnDrag(PointerEventData eventData)
        {
            if (_phase == Phase.Dragging && _dragPointer == eventData.pointerId && PagePoint(eventData.position, eventData, out Vector2 uv))
            {
                SetAmount(Mathf.Clamp01((_dragStart - uv.x) * TurnDirection / .72f));
            }
        }

        public void OnEndDrag(PointerEventData eventData)
        {
            if (_phase == Phase.Dragging && _dragPointer == eventData.pointerId)
            {
                bool commit = _amount >= .32f;
                FinishTurn(commit, commit ? Mathf.Lerp(.20f, .47f, 1f - _amount) : .27f);
            }
        }

        private bool PagePoint(Vector2 point, PointerEventData eventData, out Vector2 uv)
        {
            RectTransform rect = _paper.rectTransform;
            bool hit = RectTransformUtility.ScreenPointToLocalPointInRectangle(rect, point, eventData.pressEventCamera, out Vector2 local);
            uv = (local - rect.rect.min) / rect.rect.size;
            return hit;
        }

        [RevButtonClick("btnClose")]
        private void CloseBook()
        {
            CloseSelf();
        }

        [RevButtonClick("btnRepository")]
        private void OpenRepository()
        {
            OpenExternal(RepositoryUrl);
        }

        [RevButtonClick("btnCopyRepository")]
        private void CopyRepository()
        {
            GUIUtility.systemCopyBuffer = RepositoryUrl;
            ShowNotice("已复制仓库地址");
        }

        [RevButtonClick("btnJoinGroup")]
        private void JoinGroup()
        {
            if (Uri.TryCreate(configuration.GroupInviteUrl?.Trim(), UriKind.Absolute, out Uri invite) && invite.Scheme == Uri.UriSchemeHttps)
            {
                OpenExternal(invite.AbsoluteUri);
                return;
            }
            ShowNotice("若 QQ 没有打开，可复制群号后在 QQ 中搜索");
            OpenExternal(GroupCardUrl);
        }

        [RevButtonClick("btnCopyGroup")]
        private void CopyGroup()
        {
            GUIUtility.systemCopyBuffer = GroupNumber;
            ShowNotice("已复制群号");
        }

        private static void OpenExternal(string url)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
            if (ProjectOpenSourceAutomation.RecordExternalLaunch(url))
            {
                return;
            }
#endif
            Application.OpenURL(url);
        }

        private void ShowNotice(string message)
        {
            ClearNotice();
            _notice.text = message;
            _noticeTimer = DOVirtual.DelayedCall(3f, ClearNotice).SetRecyclable(false);
        }

        private void ClearNotice()
        {
            _noticeTimer?.Kill();
            _noticeTimer = null;
            _notice.text = "";
        }

        private void StopMotion()
        {
            _transition?.Kill();
            _transition = null;
            ClearNotice();
        }
    }
}
