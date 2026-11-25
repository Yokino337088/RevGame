using System;
using DG.Tweening;
using RevGame.Core;
using RevGame.Flow;
using RevGame.Services;
using RevGame.View;
using Revolution;
using UnityEngine;
using UnityEngine.UI;

namespace RevGame.UI
{
    public sealed partial class HomePanel
    {
        [RevBind("btnMemories/Unread")] private Image _memoryBadge;
        [RevBind("btnMemories/Halo")] private Image _memoryHalo;
        [RevBind("MemorySpeech")] private CanvasGroup _memorySpeech;

        private Tween _memoryPulse;
        private Tween _memorySpeechTween;
        private LobbySceneView _memoryLobby;
        private int _spokenUnread;
        private bool _openingBook;

        /// <summary>打开大厅时装配收藏提示、邀请监听与浏览器入口预热，前台打开条件由原流程判断。</summary>
        protected override void OnOpen()
        {
            _spokenUnread = 0;
            _memoryLobby = FindObjectOfType<LobbySceneView>();
            RevEvent.AddEventListener(YayaMemoryProgress.Changed, OnMemoryProgressChanged, owner: this);
            RefreshMemoryEntry(true);
            RefreshCottageStoryEntry();
            OpenLobbyLanAsync().Forget();
            QueueBrowserEntryWarmup();
        }

        protected override void OnCovered(bool covered)
        {
            if (covered)
            {
                HideCottageStoryTip();
            }
            if (State == RevUIPanelState.Opened)
            {
                RefreshMemoryEntry(!covered);
            }
        }

        /// <summary>关闭大厅时收回入口提示和在途请求，再停止收藏表现及邀请监听。</summary>
        protected override void OnClose()
        {
            _modeRequestVersion++;
            RestoreModeLabel();
            _modeError?.Dispose();
            _modeError = null;
            CloseLobbyLan();
            StopMemoryPulse();
            HideMemorySpeech();
            HideCottageStoryTip();
            _memoryLobby = null;
        }

        protected override void OnRelease()
        {
            OnClose();
            if (_cottageStory != null)
            {
                _cottageStory.onClick.RemoveListener(OpenCottageStory);
            }
        }

        [RevButtonClick("btnMemories")]
        private void OpenMemoryBook()
        {
            OpenLobbyBook<YayaMemoryBookPanel>(YayaMemoryBookPanel.ThemeAtlases).Forget();
        }

        [RevButtonClick("btnProjectOpenSource")]
        private void OpenProjectBook()
        {
            OpenLobbyBook<ProjectOpenSourcePanel>(ProjectOpenSourcePanel.ThemeAtlases).Forget();
        }

        /// <summary>两个书本入口共享加载锁，且尊重当前在途的模式菜单。</summary>
        private async RevTask OpenLobbyBook<TBook>(string[] atlases) where TBook : RevUIPanel
        {
            var flow = RevServices.GetRequired<GameFlow>();
            if (_openingBook || _loadingModeLabel != null || flow.IsGameplayBlocked || flow.Screen != GameScreen.Home)
            {
                return;
            }
            _openingBook = true;
            HideMemorySpeech();
            try
            {
                await RevServices.GetRequired<GameSpriteAtlasService>().PreloadAsync(atlases);
                if (this != null && State == RevUIPanelState.Opened && !IsCovered && !flow.IsGameplayBlocked && flow.Screen == GameScreen.Home)
                {
                    await RevUI.OpenAsync<TBook>();
                }
            }
            catch (Exception error)
            {
                RevLog.Exception(error, typeof(TBook).Name + " 打开失败", "RevGame");
            }
            finally
            {
                _openingBook = false;
            }
        }

        private void OnMemoryProgressChanged()
        {
            RefreshMemoryEntry(false);
            RefreshCottageStoryEntry();
        }

        /// <summary>红点只跟随存档未阅位。鸦鸦在回到大厅或启动时提示一次。</summary>
        private void RefreshMemoryEntry(bool speak)
        {
            int unread = RevServices.GetRequired<YayaMemoryProgress>().Unread;
            _memoryBadge.gameObject.SetActive(unread != 0);
            _memoryHalo.gameObject.SetActive(unread != 0);
            if (unread == 0 || IsCovered)
            {
                StopMemoryPulse();
                HideMemorySpeech();
                return;
            }
            if (_memoryPulse == null)
            {
                _memoryHalo.rectTransform.localScale = Vector3.one;
                _memoryHalo.color = new Color(1f, .73f, .31f, .45f);
                _memoryPulse = DOTween.Sequence().SetUpdate(true).SetRecyclable(false)
                    .Append(_memoryHalo.rectTransform.DOScale(1.75f, .85f).SetEase(Ease.OutQuad))
                    .Join(_memoryHalo.DOFade(0f, .85f)).AppendInterval(.38f).SetLoops(-1);
            }
            if (speak && _spokenUnread != unread)
            {
                _spokenUnread = unread;
                ShowMemorySpeech();
            }
        }

        /// <summary>播放收藏提示。横向留在中栏，高度可随立绘站位调整，关闭面板时停止动画。</summary>
        private void ShowMemorySpeech()
        {
            HideMemorySpeech();
            _memorySpeech.gameObject.SetActive(true);
            if (_memoryLobby != null && _memoryLobby.TryGetYayaSpeechPoint(out Vector2 screen))
            {
                var rect = (RectTransform)_memorySpeech.transform;
                var parent = (RectTransform)rect.parent;
                var canvas = rect.GetComponentInParent<Canvas>();
                Camera camera = canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
                if (RectTransformUtility.ScreenPointToLocalPointInRectangle(parent, screen, camera, out Vector2 local))
                {
                    float heightInFrame = (local.y - parent.rect.yMin) / parent.rect.height;
                    rect.anchorMin = rect.anchorMax = new Vector2(GameLobbyLayout.HintLeft, Mathf.Clamp(heightInFrame, .57f, .625f));
                    rect.anchoredPosition = Vector2.zero;
                }
            }
            _memorySpeech.alpha = 0f;
            _memorySpeechTween = DOTween.Sequence().SetUpdate(true).SetRecyclable(false)
                .Append(_memorySpeech.DOFade(1f, .24f)).AppendInterval(6.5f)
                .Append(_memorySpeech.DOFade(0f, .30f)).OnComplete(() =>
                {
                    _memorySpeechTween = null;
                    _memorySpeech.gameObject.SetActive(false);
                });
        }

        private void StopMemoryPulse()
        {
            _memoryPulse?.Kill();
            _memoryPulse = null;
        }

        private void HideMemorySpeech()
        {
            _memorySpeechTween?.Kill();
            _memorySpeechTween = null;
            if (_memorySpeech != null)
            {
                _memorySpeech.gameObject.SetActive(false);
            }
        }
    }
}
