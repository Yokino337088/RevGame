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
            /// <summary>流程持有的只读剧情资源，面板不能写入配置或存档。</summary>
            public StoryBook Book;
            /// <summary>本次播放篇章，由GameFlow按ID选择。</summary>
            public StoryBook.Episode Episode;
            /// <summary>当前对白下标，范围0～Lines.Length-1。</summary>
            public int Index;
            /// <summary>播放代次，缓存面板据此区分同一篇章的两次重播。</summary>
            public int Version;
        }
        /// <summary>绑定预制体storyView节点，负责立绘、文字与按钮事件。</summary>
        [RevBind] private StoryView _storyView;
        /// <summary>全屏背景的单面板租约；打开后等待可画才允许玩家翻页，关闭时释放。</summary>
        [RevBind] private RevGame.View.StoryBackdrop _storyBackdrop;
        /// <summary>宿主等待本篇背景可画后才允许翻页；章节背景仍由场景拥有。</summary>
        public RevTask PrepareBackdropAsync()
        {
            return _storyBackdrop.PrepareAsync(Data.Episode.BackgroundChapter);
        }
        /// <summary>节点由RevBind自动装配，不在绑定时启动剧情或加载资源。</summary>
        protected override void OnBindView()
        {
        }
        /// <summary>只把当前帧落屏，换句必须由流程SetData驱动。</summary>
        protected override void OnRefreshView()
        {
            if (Data != null)
            {
                _storyView.Show(Data);
            }
        }
        /// <summary>子视图点击通知只转发唯一流程，不在UI里改进度。</summary>
        protected override void OnPartChanged(RevUIPart part)
        {
            if (part == _storyView)
            {
                RevServices.GetRequired<GameFlow>().AdvanceStory();
            }
        }
        /// <summary>回池前清掉背景引用，下一次打开重新取得所需背景。</summary>
        protected override void OnClose()
        {
            _storyBackdrop.Release();
        }
#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
        /// <summary>只读真实UI状态；表情、覆盖、排序与对话框不重叠可由MCP断言，不参与剧情控制。</summary>
        public static object YayaSnapshot()
        {
            StoryPanel panel = RevUI.Get<StoryPanel>();
            if (!RevUI.IsOpen<StoryPanel>() || panel == null || panel.Data == null)
            {
                return new System.Collections.Generic.Dictionary<string, object> { ["open"] = false };
            }
            Transform view = panel.transform.Find("storyView");
            var portrait = view.Find("guide").GetComponent<UnityEngine.UI.Image>();
            var card = view.Find("DialogueCard").GetComponent<RectTransform>();
            var rect = portrait.rectTransform;
            var dialogue = card.Find("dialogue").GetComponent<UnityEngine.UI.Text>();
            StoryBook.Line line = panel.Data.Episode.Lines[panel.Data.Index];
            // 检查整句的排版高度，不能因打字刚开始只有几个字就误判长对白能放下。
            var settings = dialogue.GetGenerationSettings(dialogue.rectTransform.rect.size);
            float required = dialogue.cachedTextGeneratorForLayout.GetPreferredHeight(line.Text, settings) / dialogue.pixelsPerUnit;
            return new System.Collections.Generic.Dictionary<string, object>
            {
                ["open"] = true,
                ["expression"] = line.Expression,
                ["portrait"] = portrait.sprite == null ? "" : portrait.sprite.name,
                ["portraitTop"] = portrait.transform.GetSiblingIndex() == view.childCount - 1,
                ["separate"] = rect.anchorMax.x <= card.anchorMin.x,
                ["backdropReady"] = panel._storyBackdrop.Ready,
                ["backgroundChapter"] = panel._storyBackdrop.Chapter,
                ["speaker"] = line.Speaker,
                ["text"] = line.Text,
                ["textFits"] = required <= dialogue.rectTransform.rect.height + 1,
                ["skipActive"] = view.Find("btnSkip").gameObject.activeInHierarchy
            };
        }
#endif
    }
}
