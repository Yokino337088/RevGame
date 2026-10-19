// ============================================================
// BoardCellView.cs —— 一格的图像和点击/长按转发器。
// 每格直接记住 BoardPos，点击时无需用浮点世界坐标反推，避免边界点错格。
// 它只保存显示状态，不保存棋子或修改棋盘；落子/查看范围都交给上层回调。
// ============================================================
using System;
using DG.Tweening;
using RevGame.Core;
using Revolution;
using UnityEngine;
using UnityEngine.EventSystems;

namespace RevGame.View
{
    [RevPoolPrefab(RevResPath.Board_Tile, "BoardCell", Group = RevResGroup.Battle)]
    public sealed class BoardCellView : MonoBehaviour, IPointerClickHandler, IPointerDownHandler, IPointerUpHandler, IRevPoolable
    {
        [SerializeField]
        private SpriteRenderer starRenderer;
        [SerializeField]
        private SpriteRenderer highlightRenderer;
        [SerializeField]
        private SpriteRenderer eyeRenderer;
        /// <summary>攻击范围用的独立渲染器：不能复用 highlightRenderer，否则"可落点"和范围会互相关掉。</summary>
        [SerializeField]
        private SpriteRenderer rangeRenderer;
        [Tooltip("按住多久算长按（秒）。纯表现手感，不影响战斗规则。")]
        [SerializeField]
        private float longPressSeconds = 0.35f;
        private Action<BoardPos> _onClick;
        private Action<BoardPos> _onLongPress;
        private bool _pressed;
        // 长按已经触发后，松手时系统仍会补一次 OnPointerClick；不拦掉就会顺手落子/换位。
        private bool _longPressFired;
        private Color _rangeBaseColor = new Color(1f, 0.95f, 0.66f, 0.45f);
        private Tween _rangeTween;
        public BoardPos Position { get; private set; }

        public bool IsHighlighted
        {
            get
            {
                return highlightRenderer != null && highlightRenderer.enabled;
            }
        }

        /// <summary>范围是否正在显示；验收直接读这个字段，不用去猜渲染器状态。</summary>
        public bool IsRangeVisible
        {
            get
            {
                return rangeRenderer != null && rangeRenderer.enabled;
            }
        }

        private void Awake()
        {
            // 颜色以预制体为准（策划可在 Inspector 直接调成别的警示色），运行时只改透明度做弹入。
            if (rangeRenderer != null)
            {
                _rangeBaseColor = rangeRenderer.color;
            }
        }

        /// <summary>绑定显示位置与两个交互去向；不在这里判断游戏规则。</summary>
        public void Bind(BoardPos pos, bool isStar, Action<BoardPos> onClick, Action<BoardPos> onLongPress)
        {
            Position = pos;
            _onClick = onClick;
            _onLongPress = onLongPress;
            _pressed = false;
            _longPressFired = false;
            transform.position = pos.ToWorldPosition(Consts.CellSize, Consts.BoardOriginOffset);
            if (starRenderer != null)
            {
                starRenderer.enabled = isStar;
            }

            SetHighlight(false);
            SetEye(false);
            SetRange(false);
        }

        public void SetHighlight(bool visible)
        {
            if (highlightRenderer != null)
            {
                highlightRenderer.enabled = visible;
            }
        }

        public void SetEye(bool visible)
        {
            if (eyeRenderer != null)
            {
                eyeRenderer.enabled = visible;
            }
        }

        public void SetRange(bool visible)
        {
            if (!visible)
            {
                HideRange();
                return;
            }

            if (rangeRenderer != null)
            {
                rangeRenderer.enabled = true;
            }
        }

        /// <summary>
        /// 水波式弹一下：从格子中心先胀到 1.12 倍再回落到 1 倍，同时淡入到预制体设定的黄色。
        /// 用 DOTween 而不是计时器，是因为这里只关心"到点长什么样"，不需要逐帧读数据。
        /// </summary>
        public void PlayRangePulse()
        {
            if (rangeRenderer == null)
            {
                return;
            }

            Transform target = rangeRenderer.transform;
            _rangeTween?.Kill(false);
            rangeRenderer.enabled = true;
            Color color = _rangeBaseColor;
            color.a = 0f;
            rangeRenderer.color = color;
            target.localScale = Vector3.one * 0.35f;
            _rangeTween = DOTween.Sequence()
                .Append(target.DOScale(1.12f, 0.13f).SetEase(Ease.OutQuad))
                .Join(rangeRenderer.DOFade(_rangeBaseColor.a, 0.1f))
                .Append(target.DOScale(1f, 0.08f).SetEase(Ease.InOutQuad));
        }

        /// <summary>立即收起范围格：先停动画再清状态，避免淡出动画在回池后又改了一次颜色。</summary>
        public void HideRange()
        {
            Tween previous = _rangeTween;
            _rangeTween = null;
            previous?.Kill(false);
            if (rangeRenderer == null)
            {
                return;
            }

            rangeRenderer.transform.localScale = Vector3.one;
            rangeRenderer.color = _rangeBaseColor;
            rangeRenderer.enabled = false;
        }

        public void OnPointerDown(PointerEventData eventData)
        {
            if (eventData.button != PointerEventData.InputButton.Left)
            {
                return;
            }

            // 每次按下重新计时：上一轮的标记必须先清掉，否则这一次的点击会被吞。
            _pressed = true;
            _longPressFired = false;
            RevTimer.CancelAllOf(this);
            RevTimer.After(longPressSeconds, OnLongPressElapsed, owner: this);
        }

        public void OnPointerUp(PointerEventData eventData)
        {
            if (eventData.button != PointerEventData.InputButton.Left)
            {
                return;
            }

            _pressed = false;
            // 松手时停掉还没到点的长按计时器；已经触发过的计时器是空操作。
            RevTimer.CancelAllOf(this);
        }

        private void OnLongPressElapsed()
        {
            // 物体可能在计时期间被回池/禁用；没有按住状态就不该再弹范围。
            if (!_pressed || _onLongPress == null)
            {
                return;
            }

            _longPressFired = true;
            _onLongPress(Position);
        }

        public void OnPointerClick(PointerEventData eventData)
        {
            if (eventData.button != PointerEventData.InputButton.Left)
            {
                return;
            }

            // 长按已展示范围：这次松手只负责结束手势，不能再当成一次点击去选棋/落子。
            if (_longPressFired)
            {
                _longPressFired = false;
                return;
            }

            if (_onClick != null)
            {
                _onClick(Position);
            }
        }

        public void OnPoolGet()
        {
        // 实际位置由 Bind 设置，出池时不要读取上一局的服务。
        }

        public void OnPoolReturn()
        {
            // ★ 委托会引用棋盘宿主，回池时必须摘掉，否则旧场景仍可能被保留在内存中。
            RevTimer.CancelAllOf(this);
            _onClick = null;
            _onLongPress = null;
            _pressed = false;
            _longPressFired = false;
            SetHighlight(false);
            SetEye(false);
            HideRange();
        }
    }
}
