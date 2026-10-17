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
    [RevPoolPrefab("Effects/", "BattleEventNotice", Group = RevResGroup.Battle)]
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
        public bool HasActiveAnimation { get { return _animation != null && _animation.IsActive(); } }

        /// <summary>调用者拥有当前提示，关闭 HUD 时须归还；动画自己结束也会回池。</summary>
        public void Show(string title, Sprite sprite, Action<BattleEventNoticeView> onFinished)
        {
            BattleText config = GameTextConfig.Require("Event_" + title);
            Show(config, config.Text, sprite, onFinished);
        }

        /// <summary>升级和增益共用池化提示，标题替换占位符，其余参数直接来自 Excel。</summary>
        public void Show(BattleText config, string title, Sprite sprite, Action<BattleEventNoticeView> onFinished)
        {
            StopAnimation();
            _onFinished = onFinished;
            titleText.text = title;
            detailText.text = config.Detail;
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
            sequence.AppendInterval(Mathf.Max(0.1f, config.Duration - 0.86f));
            sequence.Append(DOTween.To(() => group.alpha, value => group.alpha = value, 0f, 0.4f));
            sequence.Join(DOTween.To(() => _slide, value => _slide = value, -1f, 0.4f));
            _animation = sequence.OnComplete(Finish);
        }

        private void LateUpdate() { ApplySafeLayout(); }

        /// <summary>提示独占右栏中部空位，不盖住棋盘、信息栏或准备按钮；连同冲击放大的范围一起预留。</summary>
        private void ApplySafeLayout()
        {
            RectTransform rect = (RectTransform)transform;
            RectTransform parent = rect.parent as RectTransform;
            if (parent == null) { return; }
            GameBattleLayout.Regions areas = GameBattleLayout.Calculate(parent.rect.size, GameBattleLayout.SafeViewport());
            Rect slot = GameBattleLayout.InRegion(areas.Right, 0.56f, 0.86f, 0.14f);
            // 除以峰值：哪怕放大到 Excel 配置的最大比例，也不会超出预留范围。
            rect.sizeDelta = slot.size / _peakScale;
            rect.anchoredPosition = slot.center + Vector2.right * (areas.Right.width * 0.03f * _slide);
            float width = rect.sizeDelta.x;
            float height = rect.sizeDelta.y;
            SetChildRect(icon.rectTransform, new Vector2(-width * 0.40f, height * 0.15f), new Vector2(width * 0.12f, height * 0.35f));
            SetChildRect(titleText.rectTransform, new Vector2(width * 0.07f, height * 0.19f), new Vector2(width * 0.79f, height * 0.50f));
            SetChildRect(detailText.rectTransform, new Vector2(0f, -height * 0.27f), new Vector2(width * 0.94f, height * 0.34f));
            titleText.resizeTextForBestFit = detailText.resizeTextForBestFit = true;
            titleText.resizeTextMinSize = detailText.resizeTextMinSize = 14;
            titleText.resizeTextMaxSize = titleText.fontSize;
            detailText.resizeTextMaxSize = detailText.fontSize;
        }

        private static void SetChildRect(RectTransform rect, Vector2 center, Vector2 size)
        {
            rect.anchorMin = rect.anchorMax = Vector2.one * 0.5f;
            rect.anchoredPosition = center;
            rect.sizeDelta = size;
        }

        private void Finish()
        {
            _animation = null;
            Action<BattleEventNoticeView> callback = _onFinished;
            _onFinished = null;
            callback?.Invoke(this);
            RevPool.Return(this, 0);
        }

        private void StopAnimation()
        {
            Tween previous = _animation;
            _animation = null;
            previous?.Kill(false);
        }

        public void OnPoolGet() { StopAnimation(); }
        public void OnPoolReturn()
        {
            StopAnimation();
            _onFinished = null;
            titleText.text = detailText.text = string.Empty;
            group.alpha = 0f;
        }
        private void OnDisable() { StopAnimation(); }
        private void OnDestroy() { StopAnimation(); }
    }
}
