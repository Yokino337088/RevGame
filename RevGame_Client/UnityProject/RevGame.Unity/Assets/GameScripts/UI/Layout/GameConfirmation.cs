using System;
using Revolution;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace RevGame.UI
{
    /// <summary>
    /// 由当前业务面板持有的一次性确认框。复用宿主按钮的图和字体，遮罩覆盖整个视口，正文和按钮在安全区内。
    /// 确认和取消都会先收起遮挡；宿主退场或被销毁时可直接 Dispose。
    /// </summary>
    internal sealed class GameConfirmation : IDisposable
    {
        /// <summary>挂在当前 UI 根画布末尾的整屏遮罩，关闭后置空。</summary>
        private GameObject _overlay;
        /// <summary>只在玩家点击确认时执行一次的业务请求。</summary>
        private Action _confirm;
        /// <summary>打开时取得的事件系统，关窗时用于恢复本次移动过的导航焦点。</summary>
        private readonly EventSystem _events;
        /// <summary>打开前选中的控件，已销毁或隐藏时无需恢复。</summary>
        private readonly GameObject _previousSelection;

        /// <summary>业务用来去重和拦截底层玩法输入。关闭后立即为 false。</summary>
        public bool IsOpen => _overlay != null && _overlay.activeSelf;

        /// <summary>在宿主所在画布打开确认框，按钮仍走 Unity EventSystem 的实际点击与导航。</summary>
        public GameConfirmation(Button style, string title, string message, string confirmLabel, string cancelLabel, Action confirm)
        {
            _confirm = confirm;
            _events = EventSystem.current;
            _previousSelection = _events == null ? null : _events.currentSelectedGameObject;

            // 使用实际根画布，使遮罩比常驻面板、手牌和教学提示都靠前。宿主收尾时立即撤掉它。
            Canvas canvas = style.GetComponentInParent<Canvas>().rootCanvas;
            RectTransform overlay = AddRect("GameConfirmation", canvas.transform, 0f, 0f, 1f, 1f);
            _overlay = overlay.gameObject;
            overlay.gameObject.AddComponent<Image>().color = new Color(.035f, .055f, .08f, .78f);

            RectTransform safe = AddRect("safeFrame", overlay, 0f, 0f, 1f, 1f);
            safe.gameObject.AddComponent<RevUIAdaptiveLayout>()
                .Configure(RevUIAdaptiveLayoutMode.FitDesignFrame, GameBattleLayout.DesignResolution);
            RectTransform card = AddRect("card", safe, .23f, .29f, .77f, .71f);
            Image background = card.gameObject.AddComponent<Image>();
            background.color = new Color32(22, 38, 54, 255);
            Outline border = card.gameObject.AddComponent<Outline>();
            border.effectColor = new Color32(212, 175, 105, 218);
            border.effectDistance = new Vector2(2f, -2f);

            Text reference = style.GetComponentInChildren<Text>(true);
            AddText("title", card, reference.font, title, new Color32(255, 221, 150, 255), 47, .08f, .72f, .92f, .94f);
            AddText("message", card, reference.font, message, new Color32(247, 244, 232, 255), 34, .09f, .33f, .91f, .71f);
            Button cancel = AddButton("cancel", card, style, reference, cancelLabel,
                confirm == null ? .32f : .10f, confirm == null ? .68f : .45f, Dispose);
            if (confirm != null)
            {
                Button accepted = AddButton("confirm", card, style, reference, confirmLabel, .55f, .90f, Accept);
                cancel.navigation = new Navigation { mode = Navigation.Mode.Explicit, selectOnRight = accepted };
                accepted.navigation = new Navigation { mode = Navigation.Mode.Explicit, selectOnLeft = cancel };
            }
            else
            {
                // 只读战绩窗口只提供关闭；导航不能穿透到后面的新开按钮。
                cancel.navigation = new Navigation { mode = Navigation.Mode.None };
            }
            if (_events != null)
            {
                _events.SetSelectedGameObject(cancel.gameObject);
            }
        }

        /// <summary>先清除本次确认框再提交业务，避免连点重复消费。</summary>
        private void Accept()
        {
            Action confirm = _confirm;
            Dispose();
            confirm?.Invoke();
        }

        /// <summary>关闭时先取消射线遮挡再销毁临时节点。重复调用安全，退场清理不会执行确认回调。</summary>
        public void Dispose()
        {
            _confirm = null;
            if (_overlay == null)
            {
                return;
            }

            GameObject selected = _events == null ? null : _events.currentSelectedGameObject;
            if (selected != null && selected.transform.IsChildOf(_overlay.transform))
            {
                _events.SetSelectedGameObject(_previousSelection != null && _previousSelection.activeInHierarchy ? _previousSelection : null);
            }
            _overlay.SetActive(false);
            UnityEngine.Object.Destroy(_overlay);
            _overlay = null;
        }

        /// <summary>复用按钮的绘制样式，只注册本次弹窗自己的点击动作。</summary>
        private static Button AddButton(string name, Transform parent, Button style, Text reference, string label,
            float left, float right, Action click)
        {
            RectTransform rect = AddRect(name, parent, left, .095f, right, .29f);
            var source = (Image)style.targetGraphic;
            Image image = rect.gameObject.AddComponent<Image>();
            image.sprite = source.overrideSprite;
            image.type = source.type;
            image.color = source.color;
            Button button = rect.gameObject.AddComponent<Button>();
            button.targetGraphic = image;
            button.colors = style.colors;
            button.onClick.AddListener(() => click());
            AddText("label", rect, reference.font, label, reference.color, 32, .07f, .08f, .93f, .92f);
            return button;
        }

        /// <summary>文字使用已有的游戏字体，并在既定矩形内换行缩放。</summary>
        private static void AddText(string name, Transform parent, Font font, string content, Color color, int size,
            float left, float bottom, float right, float top)
        {
            Text text = AddRect(name, parent, left, bottom, right, top).gameObject.AddComponent<Text>();
            text.font = font;
            text.text = content;
            text.color = color;
            text.fontSize = size;
            text.alignment = TextAnchor.MiddleCenter;
            text.fontStyle = FontStyle.Bold;
            text.resizeTextForBestFit = true;
            text.resizeTextMinSize = 22;
            text.resizeTextMaxSize = size;
            text.supportRichText = false;
            text.raycastTarget = false;
        }

        /// <summary>用父节点归一化锚点装配临时控件，并继承当前画布的 Unity 图层。</summary>
        private static RectTransform AddRect(string name, Transform parent, float left, float bottom, float right, float top)
        {
            var node = new GameObject(name, typeof(RectTransform));
            node.layer = parent.gameObject.layer;
            var rect = (RectTransform)node.transform;
            rect.SetParent(parent, false);
            rect.anchorMin = new Vector2(left, bottom);
            rect.anchorMax = new Vector2(right, top);
            rect.offsetMin = rect.offsetMax = Vector2.zero;
            return rect;
        }
    }
}
