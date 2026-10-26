using RevGame.Data;
using RevGame.Flow;
using Revolution;
using UnityEngine;

namespace RevGame.UI
{
    /// <summary>全屏剧情宿主；内容交给可复用的StoryView，进度和存档由唯一GameFlow管理。</summary>
    [RevUIPanel("UI/Panel/Story", RevUILayer.Guide, InBackStack = false, Mask = RevUIMaskMode.None)]
    public sealed class StoryPanel : RevUIPanel<StoryPanel.Frame>
    {
        /// <summary>单句不可变快照；序号从0开始，序列号防止缓存面板把上次同句误当成这次。</summary>
        public sealed class Frame
        {
            public StoryBook Book;
            public StoryBook.Episode Episode;
            public int Index;
            public int Version;
        }
        [RevBind] private StoryView _storyView;
        protected override void OnBindView() { }
        protected override void OnRefreshView() { if (Data != null) { _storyView.Show(Data); } }
        protected override void OnPartChanged(RevUIPart part)
        {
            if (part == _storyView) { RevServices.GetRequired<GameFlow>().AdvanceStory(); }
        }
    }
}
