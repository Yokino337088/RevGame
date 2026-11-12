using RevGame.Pvp;
using RevGame.Pvp.Duel;
using Revolution;
using UnityEngine;
using UnityEngine.UI;

namespace RevGame.View
{
    // 选角结果只重绑表现，席位、输入、胜负和镜头仍读取正式决斗快照。
    public sealed partial class PvpDuelView
    {
        // 原有两套角色资源可独立使用，两个座位都能选鸦鸦或绯焰。
        private bool _blackYaya;
        private bool _whiteYaya = true;
        // 仅镜像局存在的席位徽记，和角色同场景同生命周期。
        private RectTransform _blackBadge;
        private RectTransform _whiteBadge;
        // 跟随角色上方的设计像素偏移，与美术画布绑定。
        private static readonly Vector2 BadgeOffset = new Vector2(0, 190);

        // 以锁定阵容配置换帧、头像、特效和声音。先捕获源素材再覆盖，两个相同选择不会互相抢引用。
        private void BindDuelRoster()
        {
            var service = RevServices.Get<PvpService>();
            _blackYaya = service?.DuelRoleFor(1) == DuelRole.Yaya;
            _whiteYaya = service == null || service.DuelRoleFor(2) == DuelRole.Yaya;
            var ember = FirstAnimator.CaptureRoster();
            var yaya = SecondAnimator.CaptureRoster();
            if (_blackYaya)
            {
                FirstAnimator.UseRoster(yaya);
            }
            if (!_whiteYaya)
            {
                SecondAnimator.UseRoster(ember);
            }
            Effects.SetRoles(_blackYaya, _whiteYaya);
            if (_blackYaya == _whiteYaya)
            {
                _blackBadge = BuildMirrorBadge(First, "◆ 1P 黑方", new Color(1f, .53f, .36f));
                _whiteBadge = BuildMirrorBadge(Second, "◇ 2P 白方", new Color(.44f, .92f, 1f));
            }
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

        // 徽记跟随绘制坐标但不继承角色镜像。非镜像局没有额外控件或每帧分配。
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
