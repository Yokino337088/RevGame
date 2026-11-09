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
            /// <summary>开发态预览可在大厅单独加载章背景。背景编号始终来自篇章配置。</summary>
            public bool Preview;
        }
        /// <summary>绑定预制体storyView节点，负责立绘、文字与按钮事件。</summary>
        [RevBind] private StoryView _storyView;
        /// <summary>全屏背景的单面板租约；打开后等待可画才允许玩家翻页，关闭时释放。</summary>
        [RevBind] private RevGame.View.StoryBackdrop _storyBackdrop;
        /// <summary>宿主等待本篇背景可画后才允许翻页；面板只负责归还自己取得的租约。</summary>
        public RevTask PrepareBackdropAsync()
        {
            return _storyBackdrop.PrepareAsync(Data.Episode.BackgroundChapter, Data.Preview);
        }
        /// <summary>旧预制体的根是固定设计尺寸。把背景和翻页区一起铺满 UI 层，内容由 StoryView 适配安全区。</summary>
        protected override void OnBindView()
        {
            GameMobileLayout.StretchToParent((RectTransform)transform);
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
            var layer = (RectTransform)panel.transform.parent;
            Rect layerBounds = ScreenBounds(layer);
            Rect safe = GameBattleLayout.SafeViewport();
            Rect safeBounds = new Rect(layerBounds.position + Vector2.Scale(safe.position, layerBounds.size),
                Vector2.Scale(safe.size, layerBounds.size));
            var skip = (RectTransform)view.Find("btnSkip");
            StoryBook.Line line = panel.Data.Episode.Lines[panel.Data.Index];
            // 检查整句的排版高度，不能因打字刚开始只有几个字就误判长对白能放下。
            var settings = dialogue.GetGenerationSettings(dialogue.rectTransform.rect.size);
            float required = dialogue.cachedTextGeneratorForLayout.GetPreferredHeight(line.Text, settings) / dialogue.pixelsPerUnit;
            var background = panel._storyBackdrop.GetComponent<UnityEngine.UI.RawImage>();
            Material material = background.material;
            string textureProperty = panel._storyBackdrop.Chapter == 1 ? "_MainTex" : "_ChapterTex";
            return new System.Collections.Generic.Dictionary<string, object>
            {
                ["open"] = true,
                ["expression"] = line.Expression,
                ["portrait"] = portrait.sprite == null ? "" : portrait.sprite.name,
                ["portraitSource"] = line.Portrait != null ? "line" : panel.Data.Episode.Portrait != null ? "episode" : line.Expression == 0 ? "default" : "expression",
                ["portraitPacked"] = portrait.sprite != null && portrait.sprite.packed,
                ["portraitTop"] = portrait.transform.GetSiblingIndex() == view.childCount - 1,
                ["separate"] = rect.anchorMax.x <= card.anchorMin.x,
                ["backdropReady"] = panel._storyBackdrop.Ready,
                ["backdropCoversScreen"] = Contains(ScreenBounds((RectTransform)panel._storyBackdrop.transform), layerBounds),
                ["clickAreaCoversScreen"] = Contains(ScreenBounds((RectTransform)view.Find("btnNext")), layerBounds),
                ["contentInSafeArea"] = Contains(safeBounds, ScreenBounds(rect)) &&
                    Contains(safeBounds, ScreenBounds(card)) && Contains(safeBounds, ScreenBounds(skip)),
                ["mobilePreview"] = UnityEngine.Device.Application.isMobilePlatform,
                ["deviceModel"] = UnityEngine.Device.SystemInfo.deviceModel,
                ["backgroundChapter"] = panel._storyBackdrop.Chapter,
                ["backgroundShader"] = material.shader.name,
                ["backgroundTexture"] = background.texture == null ? "" : background.texture.name,
                ["backgroundTextureBound"] = background.texture != null && material.HasProperty(textureProperty) && material.GetTexture(textureProperty) == background.texture,
                ["title"] = card.Find("title").GetComponent<UnityEngine.UI.Text>().text,
                ["speaker"] = card.Find("speaker").GetComponent<UnityEngine.UI.Text>().text,
                ["text"] = line.Text,
                ["displayedText"] = dialogue.text,
                ["textFits"] = required <= dialogue.rectTransform.rect.height + 1,
                ["skipActive"] = skip.gameObject.activeInHierarchy
            };
        }

        private static Rect ScreenBounds(RectTransform rect)
        {
            var corners = new Vector3[4];
            rect.GetWorldCorners(corners);
            Vector2 min = RectTransformUtility.WorldToScreenPoint(null, corners[0]);
            Vector2 max = RectTransformUtility.WorldToScreenPoint(null, corners[2]);
            return Rect.MinMaxRect(min.x, min.y, max.x, max.y);
        }

        private static bool Contains(Rect outer, Rect inner)
        {
            const float Tolerance = 1f;
            return outer.xMin <= inner.xMin + Tolerance && outer.xMax >= inner.xMax - Tolerance &&
                outer.yMin <= inner.yMin + Tolerance && outer.yMax >= inner.yMax - Tolerance;
        }
#endif
    }
}
