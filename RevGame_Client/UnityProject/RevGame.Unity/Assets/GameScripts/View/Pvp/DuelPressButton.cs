using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace RevGame.View
{
    // 战斗按键在触点按下时提交一次意图，返回大厅等菜单按钮继续使用普通 Button。
    public sealed class DuelPressButton : Button
    {
        // 按下时先进入 UGUI 按压状态，再给决斗输入缓冲一次边沿。
        public override void OnPointerDown(PointerEventData eventData)
        {
            base.OnPointerDown(eventData);
            if (eventData.button == PointerEventData.InputButton.Left && IsActive() && IsInteractable())
            {
                onClick.Invoke();
            }
        }

        // 同一触点随后抬起时只恢复视觉状态，避免重复提交招式。
        public override void OnPointerClick(PointerEventData eventData)
        {
        }
    }
}
