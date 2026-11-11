using UnityEngine;
using UnityEngine.EventSystems;

namespace RevGame.View
{
    // 决斗左手摇杆。场景持有它，采样只输出 -1、0、1 供现有输入缓冲封帧。
    public sealed class DuelMoveStick : MonoBehaviour, IPointerDownHandler, IInitializePotentialDragHandler,
        IDragHandler, IPointerUpHandler, IEndDragHandler
    {
        // 启动阈值，按底座半径的比例计算。竖直滑动不会启动左右移动。
        private const float PressThreshold = .24f;
        // 已经移动时缩小回中阈值，避免临界处轻微抖动造成反复停走。
        private const float ReleaseThreshold = .14f;
        // 允许旋钮移动的最大距离，按底座半径的比例计算。
        private const float KnobTravel = .55f;
        // 预绑定的旋钮，只沿本地横轴移动。
        public RectTransform Knob;
        // 本次按下至松手期间由同一触点持有，第二根手指可以独立点技能。
        private int? _owner;
        // 供决斗视图每帧采样的横向意图。
        public int Direction { get; private set; }

        // 首个左键或触摸触点获取摇杆。位置相对于固定圆心换算。
        public void OnPointerDown(PointerEventData eventData)
        {
            if (_owner.HasValue || eventData.button != PointerEventData.InputButton.Left)
            {
                return;
            }
            _owner = eventData.pointerId;
            Sample(eventData);
        }

        // 达到摇杆自身死区就可以走动，UGUI 不再额外等待通用拖拽阈值。
        public void OnInitializePotentialDrag(PointerEventData eventData)
        {
            eventData.useDragThreshold = false;
        }

        // 拖出圆形区域后仍由原触点控制，直到它松开。
        public void OnDrag(PointerEventData eventData)
        {
            if (_owner == eventData.pointerId)
            {
                Sample(eventData);
            }
        }

        // 只释放当前持有触点，其他手指松开不会清零移动。
        public void OnPointerUp(PointerEventData eventData)
        {
            if (_owner == eventData.pointerId)
            {
                ResetInput();
            }
        }

        // 系统结束拖拽时同样复位，重复收到抬手事件也安全。
        public void OnEndDrag(PointerEventData eventData)
        {
            OnPointerUp(eventData);
        }

        // 把屏幕坐标投到摇杆局部空间，仅将横向偏移量化为原生方向码。
        private void Sample(PointerEventData eventData)
        {
            var rect = (RectTransform)transform;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(rect, eventData.position, eventData.pressEventCamera, out Vector2 point))
            {
                return;
            }
            float radius = Mathf.Max(1, rect.rect.width * .5f);
            float horizontal = Mathf.Clamp((point.x - rect.rect.center.x) / radius, -1, 1);
            float rightThreshold = Direction == 1 ? ReleaseThreshold : PressThreshold;
            float leftThreshold = Direction == -1 ? ReleaseThreshold : PressThreshold;
            Direction = horizontal >= rightThreshold ? 1 : horizontal <= -leftThreshold ? -1 : 0;
            Knob.anchoredPosition = new Vector2(horizontal * radius * KnobTravel, 0);
        }

        // 松手、失焦、退房时清空持有者，后续触点必须重新按下才能接管。
        public void ResetInput()
        {
            _owner = null;
            Direction = 0;
            if (Knob != null)
            {
                Knob.anchoredPosition = Vector2.zero;
            }
        }

        // 场景销毁或对象禁用时归中，避免快速重进带入上局状态。
        private void OnDisable()
        {
            ResetInput();
        }
    }
}
