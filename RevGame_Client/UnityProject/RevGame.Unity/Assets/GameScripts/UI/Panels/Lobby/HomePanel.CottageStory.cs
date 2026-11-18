using RevGame.Flow;
using RevGame.Services;
using Revolution;
using Revolution.Gal;

namespace RevGame.UI
{
    public sealed partial class HomePanel
    {
        private const string CottageStoryPath = "Gal/Stories/yaya_days_together";
        private UnityEngine.UI.Button _cottageStory;

        /// <summary>由新版大厅 Prefab 提供入口。旧平台资源仍可正常打开大厅。</summary>
        private void BindCottageStoryEntry()
        {
            var entry = transform.Find("btnCottageStory");
            if (entry != null)
            {
                _cottageStory = entry.GetComponent<UnityEngine.UI.Button>();
                _cottageStory.onClick.AddListener(OpenCottageStory);
            }
        }

        /// <summary>续篇沿用六章通关位图。Editor 的插画预览也能进入，方便读稿和演出检查。</summary>
        private static bool CanReadCottageStory()
        {
            return RevServices.GetRequired<YayaMemoryProgress>().Unlocked == (1 << YayaMemoryProgress.ChapterCount) - 1;
        }

        private void RefreshCottageStoryEntry()
        {
            if (_cottageStory != null)
            {
                _cottageStory.gameObject.SetActive(CanReadCottageStory());
            }
        }

        private void OpenCottageStory()
        {
            if (!_openingBook && CanReadCottageStory())
            {
                var progress = RevServices.GetRequired<StoryProgress>();
                var options = progress.IsIsolated ? new RevGalOptions { UseDefaultStorage = false } : null;
                RevServices.GetRequired<GameFlow>().OpenGalAsync(CottageStoryPath, options).Forget();
            }
        }
    }
}
