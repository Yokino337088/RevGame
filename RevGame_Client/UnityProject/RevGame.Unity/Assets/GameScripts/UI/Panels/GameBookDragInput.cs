#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
using System;
using System.Collections.Generic;
using Revolution;
using UnityEngine;
using UnityEngine.EventSystems;

namespace RevGame.Automation
{
    /// <summary>从可点击的书角构造连续指针，仍经过真实射线命中与 UGUI 拖页事件。</summary>
    public sealed class GameBookDragInput
    {
        public const string Schema = "{\"type\":\"object\",\"properties\":{\"direction\":{\"type\":\"integer\"},\"amount\":{\"type\":\"number\"},\"release\":{\"type\":\"boolean\"}},\"required\":[\"direction\",\"amount\",\"release\"],\"additionalProperties\":false}";

        [Serializable]
        private sealed class DragArgs
        {
            public int direction;
            public float amount;
            public bool release;
        }

        private PointerEventData _gesture;
        private RevUIPanel _panel;
        private int _direction;

        public void Send(RevUIPanel panel, string json)
        {
            DragArgs args = JsonUtility.FromJson<DragArgs>(json);
            Send(panel, args.direction, args.amount, args.release);
        }

        public void Send(RevUIPanel panel, int direction, float amount, bool release)
        {
            if (panel == null || EventSystem.current == null || (direction != -1 && direction != 1) || !(amount >= 0f && amount <= 1f))
            {
                throw new InvalidOperationException("需要已打开的书本，direction 取 ±1，amount 取 0～1");
            }
            if (_gesture != null && _panel == panel && _direction != direction)
            {
                throw new InvalidOperationException("请先松开当前书页");
            }
            var paper = (RectTransform)panel.transform.Find("Paper");
            var canvas = paper.GetComponentInParent<Canvas>();
            Camera camera = canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
            float startX = direction > 0 ? .92f : .08f;
            Vector2 current = ScreenPoint(paper, camera, startX - direction * amount * .72f);
            if (_panel != panel || _gesture == null)
            {
                Vector2 start = ScreenPoint(paper, camera, startX);
                var gesture = new PointerEventData(EventSystem.current)
                {
                    pointerId = -42, button = PointerEventData.InputButton.Left, position = start, pressPosition = start
                };
                var hits = new List<RaycastResult>();
                EventSystem.current.RaycastAll(gesture, hits);
                if (hits.Count == 0 || ExecuteEvents.GetEventHandler<IBeginDragHandler>(hits[0].gameObject) != panel.gameObject)
                {
                    Reset();
                    throw new InvalidOperationException("书页的拖拽起点被其他界面遮挡");
                }
                _panel = panel;
                _direction = direction;
                _gesture = gesture;
                _gesture.pointerPressRaycast = hits[0];
                _gesture.position = current;
                ExecuteEvents.Execute(panel.gameObject, _gesture, ExecuteEvents.beginDragHandler);
            }
            _gesture.position = current;
            ExecuteEvents.Execute(panel.gameObject, _gesture, ExecuteEvents.dragHandler);
            if (release)
            {
                ExecuteEvents.Execute(panel.gameObject, _gesture, ExecuteEvents.endDragHandler);
                Reset();
            }
        }

        public void Reset()
        {
            _gesture = null;
            _panel = null;
        }

        private static Vector2 ScreenPoint(RectTransform paper, Camera camera, float normalizedX)
        {
            Rect rect = paper.rect;
            var local = new Vector3(rect.xMin + normalizedX * rect.width, rect.yMin + rect.height * .22f, 0f);
            return RectTransformUtility.WorldToScreenPoint(camera, paper.TransformPoint(local));
        }
    }
}
#endif
