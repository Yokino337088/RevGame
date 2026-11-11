using UnityEngine;

namespace RevGame.View
{
    // 让决斗圆形控件只在可见圆内接收触点，透明方角不会抢占附近的技能。
    public sealed class DuelCircleHitArea : MonoBehaviour, ICanvasRaycastFilter
    {
        // Canvas 射线检测入口。以局部矩形的中心和半径计算圆形命中。
        public bool IsRaycastLocationValid(Vector2 screenPoint, Camera eventCamera)
        {
            var rect = (RectTransform)transform;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(rect, screenPoint, eventCamera, out Vector2 point))
            {
                return false;
            }
            float radius = Mathf.Min(rect.rect.width, rect.rect.height) * .5f;
            return (point - rect.rect.center).sqrMagnitude <= radius * radius;
        }
    }
}
