using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.U2D;
using UnityEngine.UI;

namespace Revolution.Gal
{
    /// <summary>默认剧情面板可配置的部位。枚举值随主题资产序列化，新增部位放在末尾。</summary>
    public enum RevGalThemePart
    {
        [InspectorName("舞台底板")] Backdrop,
        [InspectorName("剧情背景")] Background,
        [InspectorName("左侧立绘")] LeftPortrait,
        [InspectorName("中间立绘")] CenterPortrait,
        [InspectorName("右侧立绘")] RightPortrait,
        [InspectorName("顶部栏")] Header,
        [InspectorName("剧情标题")] Title,
        [InspectorName("播放状态")] Mode,
        [InspectorName("关闭剧情")] Close,
        [InspectorName("打开设置")] OpenSettings,
        [InspectorName("对白框")] DialogueBox,
        [InspectorName("说话人")] Speaker,
        [InspectorName("正文区域")] Dialogue,
        [InspectorName("继续")] Advance,
        [InspectorName("剧情提示")] Notice,
        [InspectorName("选项区域")] Choices,
        [InspectorName("选项按钮")] ChoiceButton,
        [InspectorName("设置遮罩")] SettingsShade,
        [InspectorName("设置面板")] SettingsPanel,
        [InspectorName("设置标题")] SettingsTitle,
        [InspectorName("设置播放状态")] SettingsMode,
        [InspectorName("设置操作提示")] SettingsNotice,
        [InspectorName("关闭设置")] CloseSettings,
        [InspectorName("自动播放")] Auto,
        [InspectorName("跳过已读")] ReadSkip,
        [InspectorName("回退")] Rollback,
        [InspectorName("回看")] History,
        [InspectorName("快速保存")] QuickSave,
        [InspectorName("快速读取")] QuickLoad,
        [InspectorName("回看遮罩")] HistoryShade,
        [InspectorName("回看标题")] HistoryTitle,
        [InspectorName("回看正文区域")] HistoryText,
        [InspectorName("回看页码")] HistoryPage,
        [InspectorName("回看上一页")] HistoryOlder,
        [InspectorName("回看下一页")] HistoryNewer,
        [InspectorName("关闭回看")] HistoryClose
    }

    /// <summary>相对 Prefab 父节点设置布局。关闭覆盖时使用 Prefab 的 RectTransform。</summary>
    [Serializable]
    public sealed class RevGalPartLayout
    {
        public bool Override;
        public Vector2 AnchorMin;
        public Vector2 AnchorMax = Vector2.one;
        public Vector2 Pivot = new Vector2(.5f, .5f);
        public Vector2 OffsetMin;
        public Vector2 OffsetMax;

        /// <summary>从生成面板取初值，打开覆盖开关时从原本位置开始调整。</summary>
        public void ReadFrom(RectTransform rect)
        {
            AnchorMin = rect.anchorMin;
            AnchorMax = rect.anchorMax;
            Pivot = rect.pivot;
            OffsetMin = rect.offsetMin;
            OffsetMax = rect.offsetMax;
        }

        /// <summary>运行面板和编辑器预览使用同一组锚点、轴心与边距。</summary>
        public void ApplyTo(RectTransform rect)
        {
            rect.anchorMin = AnchorMin;
            rect.anchorMax = AnchorMax;
            rect.pivot = Pivot;
            rect.offsetMin = OffsetMin;
            rect.offsetMax = OffsetMax;
        }
    }

    /// <summary>单个部位的布局和外观。图集切片按原名匹配，单独指定的 Sprite 优先。</summary>
    [Serializable]
    public sealed class RevGalPartStyle
    {
        public RevGalThemePart Part;
        public RevGalPartLayout Layout = new RevGalPartLayout();
        [Tooltip("选项列表由布局组排版。正数覆盖每行高度，0 沿用 Prefab。")]
        [Min(0f)] public float ChoiceHeight;
        public Sprite Sprite;
        public string AtlasSprite;
        [Tooltip("使用 Sprite Border 九宫格拉伸。")]
        public bool Sliced;
        public bool OverrideColor;
        public Color Tint = Color.white;

        /// <summary>遮罩始终铺满以拦截点击，选项行由列表布局组管理。</summary>
        public static bool UsesRectLayout(RevGalThemePart part)
        {
            return part != RevGalThemePart.Backdrop && part != RevGalThemePart.SettingsShade
                && part != RevGalThemePart.HistoryShade && part != RevGalThemePart.ChoiceButton;
        }

        /// <summary>剧情背景和立绘的 Sprite 来自当前剧情帧。文字部位可设置颜色。</summary>
        public static bool UsesThemeSprite(RevGalThemePart part)
        {
            switch (part)
            {
                case RevGalThemePart.Background:
                case RevGalThemePart.LeftPortrait:
                case RevGalThemePart.CenterPortrait:
                case RevGalThemePart.RightPortrait:
                case RevGalThemePart.Title:
                case RevGalThemePart.Mode:
                case RevGalThemePart.Speaker:
                case RevGalThemePart.Notice:
                case RevGalThemePart.SettingsTitle:
                case RevGalThemePart.SettingsMode:
                case RevGalThemePart.SettingsNotice:
                case RevGalThemePart.HistoryTitle:
                case RevGalThemePart.HistoryPage:
                    return false;
                default:
                    return true;
            }
        }

        /// <summary>先装配选项模板再实例化分支按钮，所有选项共享同一式样。</summary>
        internal void ApplyTo(RectTransform rect, Sprite sprite)
        {
            if (UsesRectLayout(Part) && Layout != null && Layout.Override)
            {
                Layout.ApplyTo(rect);
            }
            if (Part == RevGalThemePart.ChoiceButton && ChoiceHeight > 0 && rect.TryGetComponent<LayoutElement>(out var row))
            {
                row.preferredHeight = ChoiceHeight;
            }
            if (sprite != null && rect.TryGetComponent<Image>(out var image))
            {
                image.sprite = sprite;
                image.type = Sliced ? Image.Type.Sliced : Image.Type.Simple;
                image.color = OverrideColor ? Tint : Color.white;
            }
            else if (OverrideColor && rect.TryGetComponent<Graphic>(out var graphic))
            {
                graphic.color = Tint;
            }
        }
    }

    /// <summary>Prefab 引用的共享主题资源。所有使用该 Prefab 的剧情共用图集、布局和字体。</summary>
    [CreateAssetMenu(menuName = "Revolution/Gal/剧情面板主题", fileName = "RevGalPanelTheme")]
    public sealed class RevGalPanelTheme : ScriptableObject
    {
        [SerializeField] private SpriteAtlas atlas;
        [SerializeField] private Font font;
        /// <summary>默认背景使用的共享材质。Unity 从该资产读取 Shader 和参数。</summary>
        [SerializeField] private Material backgroundMaterial;
        /// <summary>适合程序化 Shader。勾选后，背景没有图片时也会以共享材质绘制。</summary>
        [SerializeField] private bool renderBackgroundMaterialWithoutSprite;
        [SerializeField] private RevGalPartStyle[] parts = CreateParts();

        public SpriteAtlas Atlas => atlas;
        public Font Font => font;
        /// <summary>默认背景材质，可由当前剧情节点覆盖。</summary>
        public Material BackgroundMaterial => backgroundMaterial;
        /// <summary>是否允许共享材质在没有 Sprite 时独立绘制。</summary>
        public bool RenderBackgroundMaterialWithoutSprite => renderBackgroundMaterialWithoutSprite;
        public IReadOnlyList<RevGalPartStyle> Parts => parts;

        private static RevGalPartStyle[] CreateParts()
        {
            var roles = (RevGalThemePart[])Enum.GetValues(typeof(RevGalThemePart));
            return Array.ConvertAll(roles, role => new RevGalPartStyle { Part = role });
        }
    }
}
