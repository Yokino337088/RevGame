using DG.Tweening;
using RevGame.Core;
using RevGame.Flow;
using RevGame.Services;
using Revolution;
using Revolution.Gal;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace RevGame.UI
{
    public sealed partial class HomePanel
    {
        private UnityEngine.UI.Button _cottageStory;
        private Transform _cottageStoryBadge;
        private CanvasGroup _cottageStoryTip;
        private Tween _cottageStoryTipTween;

        /// <summary>绑定小屋剧情，并在默认直进大厅的浏览器版提供序章和教学回看入口。</summary>
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
            if (Consts.WebContentOpenByDefault)
            {
                CreateBrowserStoryButton("btnReplayOpening", "回看序章", 1, () => RevServices.GetRequired<GameFlow>().ReplayOpeningStory());
                CreateBrowserStoryButton("btnReplayTutorial", "重玩教学", 2, () => RevServices.GetRequired<GameFlow>().ReplayTutorial());
            }
        }

        /// <summary>沿大厅底边复用小屋入口样式，按钮及监听与大厅面板一起释放。</summary>
        private void CreateBrowserStoryButton(string name, string title, int slot, UnityAction onClick)
        {
            var source = (RectTransform)_cottageStory.transform;
            var rect = Instantiate(source, source.parent);
            rect.name = name;
            // 保持原锚点，使用设计坐标在现有按钮右侧排列。它们同样交给大厅安全区适配。
            rect.anchoredPosition = source.anchoredPosition + Vector2.right * slot * (source.sizeDelta.x + 20f);
            var badge = rect.Find("Unread");
            if (badge != null)
            {
                badge.gameObject.SetActive(false);
            }
            rect.GetComponentInChildren<Text>().text = title;
            var button = rect.GetComponent<Button>();
            // 克隆仅复用画面，点击进入对应的正式重玩入口。
            button.onClick = new Button.ButtonClickedEvent();
            button.onClick.AddListener(() =>
            {
                if (!_openingBook && !IsCovered)
                {
                    onClick();
                }
            });
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
            RevGalOptions options = null;
#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
            if (progress.IsCottageStoryPreviewOnly || RevServices.GetRequired<StoryProgress>().IsIsolated)
            {
                options = GameFlow.CreateGalPreviewOptions(true);
            }
#endif
            if (await flow.OpenGalAsync(GameFlow.CottageStoryPath, options))
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
