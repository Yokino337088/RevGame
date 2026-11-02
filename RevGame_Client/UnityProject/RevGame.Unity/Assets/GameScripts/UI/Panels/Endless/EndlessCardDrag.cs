using UnityEngine;
using UnityEngine.EventSystems;

namespace RevGame.Endless
{
    /// <summary>
    /// 每个手牌槽的薄指针适配。卡牌RectTransform始终不移动，只有面板拥有的曲线箭头跟手。
    /// UGUI原生鼠标和触屏都走同一Begin/Drag/End事件；真正扣牌/升级只在面板松手转发一次。
    /// </summary>
    public sealed class EndlessCardDrag : MonoBehaviour, IBeginDragHandler, IDragHandler, IEndDragHandler
    {
        /// <summary>仅借当前打开面板和固定槽号；面板关闭后解绑，避免指向已回池面板。</summary>
        private EndlessHudPanel _panel;
        /// <summary>预制体中固定的0～3槽号；手牌内容由面板查询，组件不保存卡牌业务状态。</summary>
        private int _index;
        /// <summary>面板每次打开时借用自身，关闭时传null解绑；组件随原手牌槽一起复用。</summary>
        public void Bind(EndlessHudPanel panel, int index)
        {
            _panel = panel;
            _index = index;
        }
        /// <summary>只转发起始指针，面板检查槽号、战斗阶段并创建箭头。</summary>
        public void OnBeginDrag(PointerEventData eventData)
        {
            _panel?.BeginCardDrag(_index, eventData);
        }
        /// <summary>移动只更新显示端点，不改变卡牌Transform，也不提前扣牌。</summary>
        public void OnDrag(PointerEventData eventData)
        {
            _panel?.MoveCardDrag(eventData);
        }
        /// <summary>松手由面板统一判断落点并提交一次；已经解绑时自然忽略旧事件。</summary>
        public void OnEndDrag(PointerEventData eventData)
        {
            _panel?.EndCardDrag(eventData);
        }
    }
}
