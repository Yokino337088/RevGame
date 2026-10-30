// ============================================================
// BattleEventNoticeView.cs —— 可复用的增益提示零件，不是 RevUI 面板。
// RevUI 管面板生命周期，RevPool 管短命提示；DOTween 只做滑入、冲击、停留和淡出。
// ============================================================
using System;
using DG.Tweening;
using Revolution;
using RevGame.UI;
using UnityEngine;
using UnityEngine.UI;

namespace RevGame.View
{
    [RevPoolPrefab(RevResPath.UI_Item_Notice, "BattleEventNotice", Group = RevResGroup.Battle)]
    public sealed class BattleEventNoticeView : MonoBehaviour, IRevPoolable
    {
        [Tooltip("整体透明度；不阻挡玩家点击棋盘。")]
        [SerializeField] private CanvasGroup group;
        [Tooltip("大字标题：内容、字号与颜色由 BattleText 表决定。")]
        [SerializeField] private Text titleText;
        [Tooltip("第二行效果说明；跟随同一条配置，无需在脚本重复维护文案。")]
        [SerializeField] private Text detailText;
        [Tooltip("新版提示图标；装饰图不阻挡棋盘点击。")]
        [SerializeField] private Image icon;
        // 当前对象只拥有这条序列；入池停止并丢弃完成回调，防止旧回调收走已经复用的提示。
        private Tween _animation;
        private Action<BattleEventNoticeView> _onFinished;
        // 动画只改变局部偏移；布局每帧由安全区重新计算，缩放窗口时不保留旧屏幕上的坐标。
        private float _slide;
        private float _peakScale = 1f;
        // 准备横幅用归一化行程，窗口改变尺寸时重新算实际屏幕边界，绝不沿用旧像素坐标。
        private bool _preparationMode;
        // 敌落子使用右栏下方独立一行，不与战斗增益争抢同一个位置；都是布局比例，不是玩法数值。
        private bool _enemyDeploymentMode;
        // 长规则文案使用横幅留足阅读宽度，仍复用底板和池，不挤在右侧小栏内缩成14号字。
        private bool _boardRuleMode;
        public bool HasActiveAnimation
        {
            get
            {
                return _animation != null && _animation.IsActive();
            }
        }
        /// <summary>调用者拥有当前提示，关闭 HUD 时须归还；动画自己结束也会回池。</summary>
        public void Show(string title, Sprite sprite, Action<BattleEventNoticeView> onFinished)
        {
            BattleText config = GameTextConfig.Require("Event_" + title);
            Show(config, config.Text, sprite, onFinished);
        }
        /// <summary>升级和增益共用池化提示，标题替换占位符，其余参数直接来自 Excel。</summary>
        public void Show(BattleText config, string title, Sprite sprite, Action<BattleEventNoticeView> onFinished, string detail = null, bool enemyDeployment = false)
        {
            StopAnimation();
            _preparationMode = false;
            _enemyDeploymentMode = enemyDeployment;
            _boardRuleMode = title.Contains("吞噬") || title.Contains("远程棋子最多");
            _onFinished = onFinished;
            titleText.text = title;
            detailText.text = detail ?? config.Detail;
            // 每次出池显式恢复字体状态，不能依赖上一次显示的是准备横幅还是升级提示。
            titleText.fontStyle = detailText.fontStyle = FontStyle.Bold;
            titleText.alignment = detailText.alignment = TextAnchor.MiddleCenter;
            detailText.fontSize = 27;
            detailText.color = Color.white;
            titleText.fontSize = config.FontSize;
            titleText.color = GameTextConfig.ColorOf(config);
            icon.sprite = sprite;
            icon.enabled = sprite != null;
            RectTransform rect = (RectTransform)transform;
            rect.anchorMin = rect.anchorMax = Vector2.zero;
            // 对象池可能保留上次世界坐标的 Z 值；UI 必须回到画布平面，否则会在相机裁剪范围之外。
            rect.localRotation = Quaternion.identity;
            rect.localPosition = Vector3.zero;
            _peakScale = Mathf.Max(1f, config.PeakScale);
            _slide = 1f;
            ApplySafeLayout();
            rect.localScale = Vector3.one * 0.68f;
            group.alpha = 0f;
            group.blocksRaycasts = false;
            group.interactable = false;

            // 入场同时放大、下落和亮起；短暂回弹制造冲击，再留足两秒阅读效果。
            Sequence sequence = DOTween.Sequence().SetUpdate(true);
            sequence.Append(DOTween.To(() => _slide, value => _slide = value, 0f, 0.32f).SetEase(Ease.OutCubic));
            sequence.Join(DOTween.To(() => rect.localScale, value => rect.localScale = value, Vector3.one * config.PeakScale, 0.24f).SetEase(Ease.OutBack));
            sequence.Join(DOTween.To(() => group.alpha, value => group.alpha = value, 1f, 0.12f));
            sequence.Append(DOTween.To(() => rect.localScale, value => rect.localScale = value, Vector3.one, 0.14f));

            if (title == "能量巨大")
            {
                sequence.Append(titleText.rectTransform.DOShakeAnchorPos(0.42f, new Vector2(14f, 9f), 24, 90f, false, true));
            }
            if (title.Contains("吞噬") || title.Contains("远程棋子最多"))
            {
                // 规则提示短促弹跳+闪烁，借用现有共享底板；Tween仅归当前池对象所有。
                sequence.Append(rect.DOPunchScale(Vector3.one * .12f, .32f, 3, .5f));
                sequence.Append(group.DOFade(.35f, .1f).SetLoops(4, LoopType.Yoyo));
            }
            sequence.AppendInterval(Mathf.Max(0.1f, config.Duration - 0.86f - (title == "能量巨大" ? 0.42f : 0f)));
            sequence.Append(DOTween.To(() => group.alpha, value => group.alpha = value, 0f, 0.4f));
            sequence.Join(DOTween.To(() => _slide, value => _slide = value, -1f, 0.4f));
            _animation = sequence.OnComplete(Finish);
        }
        /// <summary>准备增益：右侧进入中央后立即弹跳0.5秒，再停0.25秒，最后飞出左侧；对象由框架池回收。</summary>
        public void ShowPreparation(string title, string detail, Action<BattleEventNoticeView> onFinished)
        {
            StopAnimation();
            _preparationMode = true;
            _boardRuleMode = false;
            _onFinished = onFinished;
            titleText.text = title;
            detailText.text = detail;
            titleText.fontSize = 46;
            detailText.fontSize = 27;
            titleText.color = new Color(1f, 0.94f, 0.28f, 1f);
            detailText.color = Color.white;
            titleText.fontStyle = detailText.fontStyle = FontStyle.Bold;
            titleText.alignment = detailText.alignment = TextAnchor.MiddleCenter;
            // 美术预制体已有描边时直接复用；给旧资源补一次即可，绝不每次出池重复添加组件。
            ConfigureOutline(titleText);
            ConfigureOutline(detailText);
            icon.enabled = false;
            RectTransform rect = (RectTransform)transform;
            rect.anchorMin = rect.anchorMax = Vector2.zero;
            rect.localRotation = Quaternion.identity;
            rect.localPosition = Vector3.zero;
            rect.localScale = Vector3.one;
            _peakScale = 1.12f;
            _slide = 1f;
            group.alpha = 1f;
            group.blocksRaycasts = group.interactable = false;
            ApplySafeLayout();
            Sequence sequence = DOTween.Sequence().SetUpdate(true);
            sequence.Append(DOTween.To(() => _slide, x => _slide = x, 0f, 0.46f).SetEase(Ease.OutCubic));
            // 到中央立刻开始冲击：0.2秒放大+0.3秒回弹，总共0.5秒；回弹后仅停0.25秒便离场。
            sequence.Append(DOTween.To(() => rect.localScale, x => rect.localScale = x, Vector3.one * _peakScale, 0.2f).SetEase(Ease.OutQuad));
            sequence.Append(DOTween.To(() => rect.localScale, x => rect.localScale = x, Vector3.one, 0.3f).SetEase(Ease.OutBack));
            sequence.AppendInterval(0.25f);
            sequence.Append(DOTween.To(() => _slide, x => _slide = x, -1f, 0.46f).SetEase(Ease.InCubic));
            _animation = sequence.OnComplete(Finish);
        }
        private static void ConfigureOutline(Text text)
        {
            Outline outline = text.GetComponent<Outline>();

            if (outline == null)
            {
                outline = text.gameObject.AddComponent<Outline>();
            }
            outline.effectColor = new Color(0.04f, 0.07f, 0.12f, 1f);
            outline.effectDistance = new Vector2(2f, -2f);
        }
        private void LateUpdate()
        {
            ApplySafeLayout();
        }
        /// <summary>提示独占右栏中部空位，不盖住棋盘、信息栏或准备按钮；连同冲击放大的范围一起预留。</summary>
        private void ApplySafeLayout()
        {
            RectTransform rect = (RectTransform)transform;
            RectTransform parent = rect.parent as RectTransform;

            if (parent == null)
            {
                return;
            }
            GameBattleLayout.Regions areas = GameBattleLayout.Calculate(parent.rect.size, GameBattleLayout.SafeViewport());

            if (_boardRuleMode)
            {
                float bannerWidth = Mathf.Min(1080f, parent.rect.width * .68f);
                rect.sizeDelta = new Vector2(bannerWidth, 94f);
                rect.anchoredPosition = new Vector2(parent.rect.width * .5f, parent.rect.height * .76f);
                SetChildRect(titleText.rectTransform, Vector2.zero, new Vector2(bannerWidth * .93f, 72f));
                SetChildRect(detailText.rectTransform, new Vector2(0, -35), new Vector2(bannerWidth * .9f, 20));
                titleText.resizeTextForBestFit = true;
                titleText.resizeTextMinSize = 27;
                titleText.resizeTextMaxSize = 39;
                return;
            }
            if (_preparationMode)
            {
                float bannerWidth = Mathf.Min(1120f, parent.rect.width * 0.72f);
                rect.sizeDelta = new Vector2(bannerWidth, 146f);
                // 半屏宽+横幅半宽+回弹余量，确保整块文字确实从可见区域之外进入/退出。
                float travel = parent.rect.width * 0.5f + bannerWidth * _peakScale * 0.5f + 40f;
                rect.anchoredPosition = new Vector2(parent.rect.width * 0.5f + _slide * travel, parent.rect.height * 0.60f);
                SetChildRect(titleText.rectTransform, new Vector2(0f, 28f), new Vector2(bannerWidth, 64f));
                SetChildRect(detailText.rectTransform, new Vector2(0f, -36f), new Vector2(bannerWidth, 66f));
                titleText.resizeTextForBestFit = detailText.resizeTextForBestFit = true;
                titleText.resizeTextMinSize = 28;
                titleText.resizeTextMaxSize = 46;
                detailText.resizeTextMinSize = 20;
                detailText.resizeTextMaxSize = 27;
                return;
            }
            Rect slot = GameBattleLayout.InRegion(areas.Right, _enemyDeploymentMode ? 0.38f : 0.56f, 0.86f, 0.14f);
            // 除以峰值：哪怕放大到 Excel 配置的最大比例，也不会超出预留范围。
            rect.sizeDelta = slot.size / _peakScale;
            rect.anchoredPosition = slot.center + Vector2.right * (areas.Right.width * 0.03f * _slide);
            float width = rect.sizeDelta.x;
            float height = rect.sizeDelta.y;
            SetChildRect(icon.rectTransform, new Vector2(-width * 0.40f, height * 0.15f), new Vector2(width * 0.12f, height * 0.35f));
            // 标题的几何中心必须就是底板中心；只把Text设为居中，仍会在偏移的文字框内“假居中”。
            SetChildRect(titleText.rectTransform, new Vector2(0f, height * 0.19f), new Vector2(width * 0.78f, height * 0.50f));
            SetChildRect(detailText.rectTransform, new Vector2(0f, -height * 0.27f), new Vector2(width * 0.94f, height * 0.34f));
            titleText.resizeTextForBestFit = detailText.resizeTextForBestFit = true;
            titleText.resizeTextMinSize = detailText.resizeTextMinSize = 14;
            titleText.resizeTextMaxSize = titleText.fontSize;
            detailText.resizeTextMaxSize = detailText.fontSize;
        }
        private static void SetChildRect(RectTransform rect, Vector2 center, Vector2 size)
        {
            rect.anchorMin = rect.anchorMax = Vector2.one * 0.5f;
            rect.pivot = Vector2.one * 0.5f;
            rect.anchoredPosition = center;
            rect.sizeDelta = size;
        }
        private void Finish()
        {
            _animation = null;
            Action<BattleEventNoticeView> callback = _onFinished;
            _onFinished = null;
            // 先回池再通知队列，下一条可复用同一个对象；回调之后不再写任何本对象字段。
            RevPool.Return(this, 0);
            callback?.Invoke(this);
        }
        private void StopAnimation()
        {
            Tween previous = _animation;
            _animation = null;
            previous?.Kill(false);
        }
        public void OnPoolGet()
        {
            StopAnimation();
        }
        public void OnPoolReturn()
        {
            StopAnimation();
            _onFinished = null;
            _preparationMode = false;
            _enemyDeploymentMode = false;
            titleText.text = detailText.text = string.Empty;
            group.alpha = 0f;
        }
        private void OnDisable()
        {
            StopAnimation();
        }
        private void OnDestroy()
        {
            StopAnimation();
        }
    }
}
