#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
using System;
using System.Collections.Generic;
using Revolution;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace RevGame.Endless
{
    /// <summary>
    /// 开发态单指拖曳：真实EventSystem射线找到手牌，发按下/开始拖曳/移动/松手。
    /// 不调用面板私有方法或Button.Invoke，能发现箭头遮挡、绑定失效与双重消费。
    /// 它模拟UGUI指针事件，不冒充手机真机触屏。每次新局/收尾显式Cancel。
    /// </summary>
    public static class EndlessDragInput
    {
        /// <summary>测试器独占一个模拟指针；finally取消后清空，不能跨局借用旧EventSystem。</summary>
        private static PointerEventData _pointer;
        /// <summary>真实射线命中的手牌槽，借用到松手/取消，不生成测试UI。</summary>
        private static GameObject _target;
        /// <summary>按下前的卡牌世界位置，用于核对原卡牌没有随手指移动。</summary>
        private static Vector3 _cardPosition;
        /// <summary>动作前成功打牌序号和手牌数，用差值验证一次消费，不能只检查API返回成功。</summary>
        private static int _playsBefore, _handBefore;
        /// <summary>测试器自有射线缓冲；每次Begin清空，避免旧UI命中混入新手势。</summary>
        private static readonly List<RaycastResult> Hits = new List<RaycastResult>(32);
        /// <summary>从卡牌Rect中心发真实UGUI事件，先验射线命中再拖向指定世界棋格；失败抛出原因。</summary>
        public static object Begin(int index, int x, int y)
        {
            Cancel();
            var service = RevServices.GetRequired<EndlessService>();
            var panel = RevUI.Get<EndlessHudPanel>();
            if (!service.Isolated || panel == null || EventSystem.current == null)
            {
                throw new InvalidOperationException("拖曳测试需要已打开的隔离无尽局");
            }
            Transform card = panel.transform.Find("bottom/card" + index);
            if (card == null || !card.gameObject.activeInHierarchy)
            {
                throw new InvalidOperationException("手牌槽当前不可见");
            }
            Canvas canvas = card.GetComponentInParent<Canvas>();
            Camera camera = canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
            var rectangle = (RectTransform)card;
            Vector2 origin = RectTransformUtility.WorldToScreenPoint(camera, rectangle.TransformPoint(rectangle.rect.center));
            _pointer = new PointerEventData(EventSystem.current) { pointerId = -1103, position = origin, pressPosition = origin, button = PointerEventData.InputButton.Left };
            Hits.Clear();
            EventSystem.current.RaycastAll(_pointer, Hits);
            if (Hits.Count == 0 || ExecuteEvents.GetEventHandler<IBeginDragHandler>(Hits[0].gameObject) != card.gameObject)
            {
                Cancel();
                string top = Hits.Count == 0 ? "无射线" : Hits[0].gameObject.name;
                throw new InvalidOperationException("手牌拖曳射线失败，最上层=" + top + "，目标=" + card.name + "，拖曳组件=" + (card.GetComponent<EndlessCardDrag>() != null));
            }
            _target = card.gameObject;
            _cardPosition = card.position;
            _playsBefore = service.CardPlaySerial;
            _handBefore = service.Run.hand.Count;
            _pointer.pointerPressRaycast = _pointer.pointerCurrentRaycast = Hits[0];
            _pointer.pointerDrag = _target;
            _pointer.pointerPress = ExecuteEvents.ExecuteHierarchy(_target, _pointer, ExecuteEvents.pointerDownHandler);
            ExecuteEvents.Execute(_target, _pointer, ExecuteEvents.initializePotentialDrag);
            ExecuteEvents.Execute(_target, _pointer, ExecuteEvents.beginDragHandler);
            _pointer.dragging = true;
            _pointer.eligibleForClick = false;
            Move(x, y);
            return State();
        }
        /// <summary>把棋格世界坐标转成屏幕指针并触发Drag；仅用于隔离测试，不直接调用面板方法。</summary>
        public static object Move(int x, int y)
        {
            if (_pointer == null || _target == null || Camera.main == null)
            {
                throw new InvalidOperationException("拖曳手势未开始");
            }
            _pointer.position = Camera.main.WorldToScreenPoint(new Vector3(x, y, 0));
            ExecuteEvents.Execute(_target, _pointer, ExecuteEvents.dragHandler);
            return State();
        }
        /// <summary>发送松手/结束事件，返回业务差值；没有开始的手势明确失败。</summary>
        public static object End()
        {
            if (_pointer == null || _target == null)
            {
                throw new InvalidOperationException("没有可松开的手势");
            }
            ExecuteEvents.Execute(_pointer.pointerPress, _pointer, ExecuteEvents.pointerUpHandler);
            ExecuteEvents.Execute(_target, _pointer, ExecuteEvents.endDragHandler);
            _pointer = null;
            var s = RevServices.GetRequired<EndlessService>();
            return new Dictionary<string, object> { ["playsDelta"] = s.CardPlaySerial - _playsBefore, ["handDelta"] = _handBefore - s.Run.hand.Count, ["cardStayed"] = _target != null && (_target.transform.position - _cardPosition).sqrMagnitude < .001f, ["action"] = s.LastCardAction };
        }
        /// <summary>读取实际面板/网格状态，不推进业务；供rev_run带期限轮询。</summary>
        public static object State()
        {
            EndlessHudPanel panel = RevUI.Get<EndlessHudPanel>();
            return new Dictionary<string, object> { ["dragging"] = panel != null && panel.IsCardDragging, ["cardStayed"] = _target != null && (_target.transform.position - _cardPosition).sqrMagnitude < .001f, ["arrowVisible"] = panel?.ArrowVisible ?? false, ["curve"] = panel?.ArrowCurved ?? false, ["arrow"] = panel?.ArrowSnapshot() };
        }
        /// <summary>用屏幕外落点释放旧事件，再清空引用和箭头；测试finally共用，禁止扣牌。</summary>
        public static void Cancel()
        {
            if (_pointer != null && _target != null)
            {
                _pointer.position = new Vector2(-100, -100);
                ExecuteEvents.Execute(_pointer.pointerPress, _pointer, ExecuteEvents.pointerUpHandler);
                ExecuteEvents.Execute(_target, _pointer, ExecuteEvents.endDragHandler);
            }
            _pointer = null;
            _target = null;
            RevUI.Get<EndlessHudPanel>()?.CancelCardDrag();
        }
    }
}
#endif
