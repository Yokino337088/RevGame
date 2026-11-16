#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
using System;
using System.Collections.Generic;
using RevGame.Services;
using RevGame.UI;
using Revolution;
using UnityEngine;
using UnityEngine.EventSystems;

namespace RevGame.Bootstrap
{
    /// <summary>收藏书开发态取证。点击用框架的通用 UI 接口，拖页使用真实 EventSystem 事件。</summary>
    internal static class YayaMemoryAutomation
    {
        private static readonly object Owner = new object();
        private static PointerEventData _gesture;
        private static YayaMemoryBookPanel _gesturePanel;
        private static int _gestureDirection;

        [Serializable]
        private sealed class DragArgs
        {
            public int direction;
            public float amount;
            public bool release;
        }

        public static void Register()
        {
            RevRuntimeAutomation.Register("memories.state", "读取收藏书通关、已阅、入口与实际落屏状态",
                "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}",
                _ => State(), readOnly: true, owner: Owner);
            RevRuntimeAutomation.Register("memories.drag", "在隔离Play中从书页边缘发出并保持真实拖拽事件，release结束",
                "{\"type\":\"object\",\"properties\":{\"direction\":{\"type\":\"integer\"},\"amount\":{\"type\":\"number\"},\"release\":{\"type\":\"boolean\"}},\"required\":[\"direction\",\"amount\",\"release\"],\"additionalProperties\":false}",
                json =>
                {
                    DragArgs args = JsonUtility.FromJson<DragArgs>(json);
                    return Drag(args.direction, args.amount, args.release);
                }, owner: Owner);
        }

        public static void Unregister()
        {
            _gesture = null;
            _gesturePanel = null;
            RevRuntimeAutomation.UnregisterAllOf(Owner);
        }

        internal static Dictionary<string, object> State()
        {
            var progress = RevServices.GetRequired<YayaMemoryProgress>();
            var home = RevUI.Get<HomePanel>();
            var book = RevUI.Get<YayaMemoryBookPanel>();
            var speech = home == null ? null : home.transform.Find("MemorySpeech");
            var bubble = speech == null ? null : speech.Find("Background").GetComponent<UnityEngine.UI.Image>().sprite;
            bool bundleMode = true;
#if UNITY_EDITOR
            bundleMode = RevResBootstrap.UseABInEditor;
#endif
            var refs = new int[YayaMemoryProgress.ChapterCount];
            for (int i = 0; i < refs.Length; i++)
            {
                refs[i] = RevResManager.GetRefCount("YayaMemories/Chapter" + (i + 1).ToString("D2") + "/", "Illustration");
            }
            return new Dictionary<string, object>
            {
                ["cleared"] = progress.Cleared, ["unlocked"] = progress.Unlocked, ["read"] = progress.Read, ["unread"] = progress.Unread,
                ["abMode"] = bundleMode, ["book"] = book == null ? null : book.Inspect(),
                ["badge"] = home != null && home.transform.Find("btnMemories/Unread").gameObject.activeInHierarchy,
                ["speech"] = speech != null && speech.gameObject.activeInHierarchy,
                ["speechSprite"] = bubble == null ? "" : bubble.name, ["speechPacked"] = bubble != null && bubble.packed,
                ["refs"] = refs
            };
        }

        internal static Dictionary<string, object> Drag(int direction, float amount, bool release)
        {
            var book = RevUI.Get<YayaMemoryBookPanel>();
            if (!RevServices.GetRequired<StoryProgress>().IsIsolated || book == null ||
                EventSystem.current == null || (direction != -1 && direction != 1) || amount < 0 || amount > 1)
            {
                throw new InvalidOperationException("需要已打开收藏书的隔离Play，direction取±1，amount取0～1");
            }
            if (_gesturePanel == book && _gesture != null && direction != _gestureDirection)
            {
                throw new InvalidOperationException("请先结束正在拖拽的书页");
            }

            var paper = (RectTransform)book.transform.Find("Paper");
            var canvas = paper.GetComponentInParent<Canvas>();
            Camera camera = canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
            float startX = direction > 0 ? .92f : .08f;
            Vector2 start = Point(paper, camera, startX);
            Vector2 current = Point(paper, camera, startX - direction * amount * .72f);
            if (_gesturePanel != book || _gesture == null)
            {
                _gesturePanel = book;
                _gestureDirection = direction;
                _gesture = new PointerEventData(EventSystem.current)
                {
                    pointerId = -42, button = PointerEventData.InputButton.Left, position = start, pressPosition = start
                };
                var results = new List<RaycastResult>();
                EventSystem.current.RaycastAll(_gesture, results);
                if (results.Count == 0 || ExecuteEvents.GetEventHandler<IBeginDragHandler>(results[0].gameObject) != book.gameObject)
                {
                    _gesture = null;
                    throw new InvalidOperationException("书页的拖拽起点被其他界面遮挡");
                }
                _gesture.pointerPressRaycast = results[0];
                _gesture.position = current;
                ExecuteEvents.Execute(book.gameObject, _gesture, ExecuteEvents.beginDragHandler);
            }
            _gesture.position = current;
            ExecuteEvents.Execute(book.gameObject, _gesture, ExecuteEvents.dragHandler);
            if (release)
            {
                ExecuteEvents.Execute(book.gameObject, _gesture, ExecuteEvents.endDragHandler);
                _gesture = null;
                _gesturePanel = null;
            }
            return book.Inspect();
        }

        private static Vector2 Point(RectTransform paper, Camera camera, float normalizedX)
        {
            Rect rect = paper.rect;
            var local = new Vector3(rect.xMin + normalizedX * rect.width, rect.yMin + rect.height * .22f, 0);
            return RectTransformUtility.WorldToScreenPoint(camera, paper.TransformPoint(local));
        }
    }
}
#endif
