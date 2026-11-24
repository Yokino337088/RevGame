using UnityEngine;
using UnityEngine.UI;

namespace RevGame.View
{
    // 决斗双方共用鸦鸦美术，此处只建立和更新两席的辨认标记。
    public sealed partial class PvpDuelView
    {
        // 黑方席位徽记，随决斗场景销毁。
        private RectTransform _blackBadge;
        // 白方席位徽记，随决斗场景销毁。
        private RectTransform _whiteBadge;
        // 跟随角色上方的设计像素偏移，与美术画布绑定。
        private static readonly Vector2 BadgeOffset = new Vector2(0, 190);

        // HUD 字体就绪后建立席位徽记。两席的角色和特效已由场景装配。
        private void BuildMirrorBadges()
        {
            _blackBadge = BuildMirrorBadge(First, "◆ 1P 黑方", new Color(1f, .53f, .36f));
            _whiteBadge = BuildMirrorBadge(Second, "◇ 2P 白方", new Color(.44f, .92f, 1f));
        }

        // 徽记用场景既有字体。颜色加轮廓、实心/空心符号和文字三重区别，不要求玩家辨认颜色。
        private RectTransform BuildMirrorBadge(Image actor, string label, Color color)
        {
            var outline = actor.gameObject.AddComponent<Outline>();
            outline.effectColor = new Color(color.r, color.g, color.b, .85f);
            outline.effectDistance = new Vector2(3, -3);
            var node = new GameObject(label, typeof(RectTransform), typeof(Text), typeof(Outline));
            var badge = (RectTransform)node.transform;
            badge.SetParent(actor.transform.parent, false);
            badge.anchorMin = actor.rectTransform.anchorMin;
            badge.anchorMax = actor.rectTransform.anchorMax;
            badge.sizeDelta = new Vector2(180, 38);
            var text = node.GetComponent<Text>();
            text.font = HudPanel.FirstName.font;
            text.fontSize = 24;
            text.fontStyle = FontStyle.Bold;
            text.alignment = TextAnchor.MiddleCenter;
            text.color = color;
            text.raycastTarget = false;
            text.text = label;
            node.GetComponent<Outline>().effectColor = new Color(.03f, .03f, .06f, 1f);
            return badge;
        }

        // 徽记直接跟随角色绘制坐标，文字保持正向，绑定前跳过更新。
        private void PaintMirrorBadges()
        {
            if (_blackBadge == null)
            {
                return;
            }
            _blackBadge.anchoredPosition = First.rectTransform.anchoredPosition + BadgeOffset;
            // 两席贴身时错开标签，避免同一横坐标下文字重叠。
            float separation = Mathf.Abs(First.rectTransform.anchoredPosition.x - Second.rectTransform.anchoredPosition.x);
            float rise = Mathf.InverseLerp(180, 40, separation) * 42;
            _whiteBadge.anchoredPosition = Second.rectTransform.anchoredPosition + BadgeOffset + new Vector2(0, rise);
        }

#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
        // 只读显示证据；测试不能用该入口造出选角结果。
        internal bool HasMirrorBadges => _blackBadge != null && _whiteBadge != null;
#endif
    }
}
