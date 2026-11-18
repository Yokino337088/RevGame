using DG.Tweening;
using RevGame.Core;
using RevGame.Flow;
using RevGame.Services;
using Revolution;
using Revolution.Gal;
using UnityEngine;

namespace RevGame.UI
{
    public sealed partial class HomePanel
    {
        private const string CottageStoryPath = "Gal/Stories/yaya_days_together";
        private UnityEngine.UI.Button _cottageStory;
        private Transform _cottageStoryBadge;
        private CanvasGroup _cottageStoryTip;
        private Tween _cottageStoryTipTween;

        /// <summary>由新版大厅 Prefab 提供入口。旧平台资源仍可正常打开大厅。</summary>
        private void BindCottageStoryEntry()
        {
            var entry = transform.Find("btnCottageStory");
            if (entry == null)
            {
                return;
            }
            _cottageStory = entry.GetComponent<UnityEngine.UI.Button>();
            _cottageStory.onClick.AddListener(OpenCottageStory);
            _cottageStoryBadge = entry.Find("Unread");
            _cottageStoryTip = transform.Find("CottageStoryTip")?.GetComponent<CanvasGroup>();
        }

        private void RefreshCottageStoryEntry()
        {
            if (_cottageStoryBadge != null)
            {
                _cottageStoryBadge.gameObject.SetActive(RevServices.GetRequired<YayaMemoryProgress>().IsCottageStoryUnread);
            }
        }

        /// <summary>锁定时入口仍可点击，由大厅展示提示；只有剧情实际打开才清掉红点。</summary>
        private void OpenCottageStory()
        {
            var flow = RevServices.GetRequired<GameFlow>();
            if (_openingBook || flow.IsGameplayBlocked || flow.Screen != GameScreen.Home)
            {
                return;
            }
            var progress = RevServices.GetRequired<YayaMemoryProgress>();
            if (!progress.IsCottageStoryUnlocked)
            {
                ShowCottageStoryTip();
                return;
            }
            HideCottageStoryTip();
            EnterCottageStoryAsync(flow, progress).Forget();
        }

        private static async RevTask EnterCottageStoryAsync(GameFlow flow, YayaMemoryProgress progress)
        {
            var story = RevServices.GetRequired<StoryProgress>();
            var options = story.IsIsolated ? new RevGalOptions { UseDefaultStorage = false } : null;
            if (await flow.OpenGalAsync(CottageStoryPath, options))
            {
                progress.MarkCottageStoryVisited();
            }
        }

        private void ShowCottageStoryTip()
        {
            if (_cottageStoryTip == null)
            {
                return;
            }
            HideCottageStoryTip();
            _cottageStoryTip.alpha = 0f;
            _cottageStoryTip.gameObject.SetActive(true);
            _cottageStoryTipTween = DOTween.Sequence().SetUpdate(true).SetRecyclable(false)
                .Append(_cottageStoryTip.DOFade(1f, .16f)).AppendInterval(2.4f)
                .Append(_cottageStoryTip.DOFade(0f, .22f)).OnComplete(() =>
                {
                    _cottageStoryTipTween = null;
                    _cottageStoryTip.gameObject.SetActive(false);
                });
        }

        private void HideCottageStoryTip()
        {
            _cottageStoryTipTween?.Kill();
            _cottageStoryTipTween = null;
            if (_cottageStoryTip != null)
            {
                _cottageStoryTip.gameObject.SetActive(false);
            }
        }
    }
}
