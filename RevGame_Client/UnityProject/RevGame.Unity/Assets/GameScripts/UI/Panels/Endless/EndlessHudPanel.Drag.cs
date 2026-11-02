using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace RevGame.Endless
{
    public sealed partial class EndlessHudPanel
    {
        /// <summary>预制体绑定的单个曲线控件；面板拥有它的动画生命周期，关闭时立即取消。</summary>
        [SerializeField] private EndlessCurveArrow cardArrow;
        /// <summary>固定手牌槽的事件适配，装配时缓存，打开/关闭时成对绑定和解绑。</summary>
        private readonly EndlessCardDrag[] _cardDrags = new EndlessCardDrag[4];
        /// <summary>拖曳期间记住槽号和棋种，自动补牌/换牌/退出时失效，避免旧手势消费新牌。</summary>
        private int _dragIndex = -1, _dragKind = -1, _dragPointer, _dragPlaySerial;
        /// <summary>拖曳结束后的一个UI帧屏蔽点击，防止同一次手势又触发Button选择。</summary>
        private bool _dragSuppressClick;
        private int _suppressUntil;
        /// <summary>松手射线由本面板独占复用；只在提交时查询，不逐帧创建结果列表。</summary>
        private readonly List<RaycastResult> _dropHits = new List<RaycastResult>(16);
        /// <summary>只读当前手势状态，用于开发态断言，不允许外部推进拖曳流程。</summary>
        public bool IsCardDragging => _dragIndex >= 0;
        /// <summary>箭头控件实际已启用且有可见伸展量。</summary>
        public bool ArrowVisible => cardArrow != null && cardArrow.IsVisible;
        /// <summary>箭头实际网格偏离直线，不能仅凭启动Tween宣称曲线已出现。</summary>
        public bool ArrowCurved => cardArrow != null && cardArrow.HasCurve;

#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
        /// <summary>开发态只读显示回执；箭头缺失时返回null，测试必须据此失败。</summary>
        public object ArrowSnapshot()
        {
            return cardArrow?.Snapshot();
        }
#endif
        /// <summary>仅装配预制体组件；缺组件时明确报错，避免运行时临时造出另一套UI。</summary>
        private void BindCardDrag()
        {
            for (int i = 0; i < _cards.Length; i++)
            {
                _cardDrags[i] = _cards[i].GetComponent<EndlessCardDrag>();
                if (_cardDrags[i] == null)
                {
                    Revolution.RevLog.Error("手牌拖曳组件缺失，请执行无尽交互资源配方", "RevGame");
                    continue;
                }
            }
        }
        /// <summary>在每次面板打开/关闭时成对借出/收回引用，防止回池后旧指针事件调用业务。</summary>
        private void SetCardDragBinding(bool opening)
        {
            for (int i = 0; i < _cardDrags.Length; i++)
            {
                _cardDrags[i]?.Bind(opening ? this : null, i);
            }
        }
        /// <summary>选牌并记住本次手势身份；屏幕坐标统一转换到箭头画布，原卡牌保持在槽中。</summary>
        public void BeginCardDrag(int index, PointerEventData input)
        {
            if (_dragIndex >= 0 && input.pointerId != _dragPointer)
            {
                return;
            }
            CancelCardDrag();
            if (!_opening || cardArrow == null || !Service.SelectCard(index))
            {
                return;
            }
            _dragIndex = index;
            _dragKind = Service.Run.hand[index];
            _dragPointer = input.pointerId;
            _dragPlaySerial = Service.CardPlaySerial;
            _dragSuppressClick = true;
            RectTransform root = cardArrow.rectTransform;
            Camera camera = input.pressEventCamera;
            RectTransform cardRect = (RectTransform)_cards[index].transform;
            Vector2 cardCenter = RectTransformUtility.WorldToScreenPoint(camera, cardRect.TransformPoint(cardRect.rect.center));
            RectTransformUtility.ScreenPointToLocalPointInRectangle(root, cardCenter, camera, out Vector2 origin);
            RectTransformUtility.ScreenPointToLocalPointInRectangle(root, input.position, camera, out Vector2 destination);
            cardArrow.transform.SetAsLastSibling();
            cardArrow.Begin(origin, destination);
            RefreshView();
        }
        /// <summary>只接受拥有手势的指针；实时落点合法性决定箭头颜色，动画尖端不用于业务判定。</summary>
        public void MoveCardDrag(PointerEventData input)
        {
            if (_dragIndex < 0 || input.pointerId != _dragPointer)
            {
                return;
            }
            RectTransformUtility.ScreenPointToLocalPointInRectangle(cardArrow.rectTransform, input.position, input.pressEventCamera, out Vector2 target);
            bool valid = ScreenCell(input.position, out int x, out int y) && Service.CanPlaceAt(x, y);
            cardArrow.Aim(target, valid);
        }
        /// <summary>松手只提交一次；取消/遮罩/棋种变化不扣牌。松手坐标直接转棋格，不用滞后的动画尖端。</summary>
        public void EndCardDrag(PointerEventData input)
        {
            if (_dragIndex < 0 || input.pointerId != _dragPointer)
            {
                return;
            }
            int index = _dragIndex;
            _dragIndex = -1;
            _suppressUntil = Time.frameCount + 1;
            if (_opening && Service.CardPlaySerial == _dragPlaySerial && index < Service.Run.hand.Count && Service.Run.hand[index] == _dragKind && CanDropThroughUi(input.position) && ScreenCell(input.position, out int x, out int y))
            {
                Service.PlayCardAt(index, x, y);
            }
            cardArrow.Retract();
            RefreshView();
        }
        /// <summary>外部补牌/模态页/关闭会使旧手势失效；在下一帧撤销，避免误消费新槽位中的牌。</summary>
        private void LateUpdate()
        {
            if (_dragIndex < 0 && _dragSuppressClick && Time.frameCount > _suppressUntil)
            {
                _dragSuppressClick = false;
            }
            if (_dragIndex >= 0 && (_modalShown || !_opening || Service.Run == null || _dragIndex >= Service.Run.hand.Count || Service.Run.hand[_dragIndex] != _dragKind || Service.CardPlaySerial != _dragPlaySerial))
            {
                CancelCardDrag();
            }
        }
        /// <summary>只接收相机内棋盘坐标；UI遮罩在EventSystem上层时取消，箭头本身不参与射线。</summary>
        private bool CanDropThroughUi(Vector2 point)
        {
            if (EventSystem.current == null)
            {
                return false;
            }
            _dropHits.Clear();
            EventSystem.current.RaycastAll(new PointerEventData(EventSystem.current) { position = point }, _dropHits);
            return _dropHits.Count == 0;
        }
        /// <summary>棋盘格固定在世界坐标0～8；屏幕外或相机失效返回false，调用方保留手牌。</summary>
        private static bool ScreenCell(Vector2 screen, out int x, out int y)
        {
            x = y = -1;
            Camera camera = Camera.main;
            if (camera == null || screen.x < 0 || screen.y < 0 || screen.x >= Screen.width || screen.y >= Screen.height)
            {
                return false;
            }
            Vector3 world = camera.ScreenToWorldPoint(screen);
            x = Mathf.RoundToInt(world.x);
            y = Mathf.RoundToInt(world.y);
            return x >= 0 && x < 9 && y >= 0 && y < 9;
        }
        /// <summary>结束显示租约，不提交牌；面板关闭和测试finally共用，可重复调用。</summary>
        public void CancelCardDrag()
        {
            _dragIndex = -1;
            _dragSuppressClick = false;
            cardArrow?.Cancel();
        }
    }
}
