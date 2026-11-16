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
        private bool _openingMemoryBook;

        protected override void OnOpen()
        {
            _spokenUnread = 0;
            _memoryLobby = FindObjectOfType<LobbySceneView>();
            RevEvent.AddEventListener(YayaMemoryProgress.Changed, OnMemoryProgressChanged, owner: this);
            RefreshMemoryEntry(true);
        }

        protected override void OnCovered(bool covered)
        {
            if (State == RevUIPanelState.Opened)
            {
                RefreshMemoryEntry(!covered);
            }
        }

        protected override void OnClose()
        {
            StopMemoryPulse();
            HideMemorySpeech();
            _memoryLobby = null;
        }

        protected override void OnRelease()
        {
            OnClose();
        }

        [RevButtonClick("btnMemories")]
        private async void OpenMemoryBook()
        {
            var flow = RevServices.GetRequired<GameFlow>();
            if (_openingMemoryBook || flow.IsGameplayBlocked || flow.Screen != GameScreen.Home)
            {
                return;
            }
            _openingMemoryBook = true;
            HideMemorySpeech();
            try
            {
                await RevServices.GetRequired<GameSpriteAtlasService>().PreloadAsync(YayaMemoryBookPanel.ThemeAtlases);
                if (this != null && State == RevUIPanelState.Opened && !IsCovered && !flow.IsGameplayBlocked && flow.Screen == GameScreen.Home)
                {
                    await RevUI.OpenAsync<YayaMemoryBookPanel>();
                }
            }
            catch (Exception error)
            {
                RevLog.Exception(error, "收藏书打开失败", "RevGame");
            }
            finally
            {
                _openingMemoryBook = false;
            }
        }

        private void OnMemoryProgressChanged()
        {
            RefreshMemoryEntry(false);
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
                    Vector2 normalized = (local - parent.rect.min) / parent.rect.size;
                    rect.anchorMin = rect.anchorMax = new Vector2(Mathf.Clamp(normalized.x, .29f, .49f), Mathf.Clamp(normalized.y, .58f, .84f));
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
