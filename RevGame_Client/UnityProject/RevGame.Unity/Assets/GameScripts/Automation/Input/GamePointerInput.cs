#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System.Collections.Generic;
using RevGame.Core;
using RevGame.UI;
using RevGame.View;
using Revolution;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace RevGame.Automation
{
    /// <summary>
    /// 开发态单指输入适配器：屏幕坐标 → EventSystem 真实射线 → 按下 → 下一次轮询松开/点击。
    /// 不调用 Button.onClick.Invoke，不调用私有回调，也不直接调用落子服务，因此遮罩、禁用和错误接线都会暴露。
    /// 这是 Unity 指针事件层的模拟，不冒充操作系统鼠标/手机真机触摸；电脑插件另行验证真实编辑器操作。
    /// </summary>
    public sealed class GamePointerInput
    {
        /// <summary>本适配器独占的射线结果缓冲；每次清空复用，避免测试输入影响战斗热路径。</summary>
        private readonly List<RaycastResult> _hits = new List<RaycastResult>(32);
        /// <summary>当前按下手势；最长只保持一个轮询间隔，取消时只松开，绝不补一次点击。</summary>
        private PointerEventData _pointer;
        private GameObject _pressed;
        private Vector2 _position;
        /// <summary>最近一次失败原因，如被遮罩挡住或按钮不可交互；交给步骤超时报告，不吞掉。</summary>
        public string LastError { get; private set; }

        public bool IsPressed
        {
            get
            {
                return _pointer != null;
            }
        }
        /// <summary>从已打开的业务面板解析唯一按钮。每次重新解析，不能跨场景缓存可能已回收的面板。</summary>
        public Button FindButton(string target)
        {
            string[] parts = (target ?? "").Split('/');
            if (parts.Length != 2)
            {
                LastError = "目标必须是 Panel类型名/按钮节点名";
                return null;
            }
            RevUIPanel panel = FindPanel(parts[0]);
            if (panel == null || !panel.gameObject.activeInHierarchy)
            {
                LastError = "面板尚未打开：" + parts[0];
                return null;
            }
            Button result = null;
            foreach (Button button in panel.GetComponentsInChildren<Button>(false))
            {
                if (button.name != parts[1])
                {
                    continue;
                }
                if (result != null)
                {
                    LastError = "按钮名不唯一：" + target;
                    return null;
                }
                result = button;
            }
            if (result == null)
            {
                LastError = "找不到可见按钮：" + target;
            }
            return result;
        }
        /// <summary>显式面板白名单；新增页面时在此接项目面板，不使用反射执行任意类型。</summary>
        private static RevUIPanel FindPanel(string name)
        {
            switch (name)
            {
                case "HomePanel":
                    return RevUI.Get<HomePanel>();
                case "LevelSelectPanel":
                    return RevUI.Get<LevelSelectPanel>();
                case "PreparePanel":
                    return RevUI.Get<PreparePanel>();
                case "SettlePanel":
                    return RevUI.Get<SettlePanel>();
                case "RewardPanel":
                    return RevUI.Get<RewardPanel>();
                case "BattleHudPanel":
                    return RevUI.Get<BattleHudPanel>();
                default:
                    return null;
            }
        }
        /// <summary>按按钮可见矩形中心发送指针，保留 Canvas 相机换算，不依赖 1920×1080 的硬编码像素位置。</summary>
        public bool PressButton(Button button)
        {
            if (button == null || !button.isActiveAndEnabled || !button.IsInteractable())
            {
                LastError = "按钮未就绪或不可交互";
                return false;
            }
            RectTransform rect = button.transform as RectTransform;
            Canvas canvas = button.GetComponentInParent<Canvas>();
            Camera camera = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
            Vector2 point = RectTransformUtility.WorldToScreenPoint(camera, rect.TransformPoint(rect.rect.center));
            return Press(button.gameObject, point);
        }
        /// <summary>棋盘同样经过 Physics2DRaycaster；若格子被 UI 挡住就拒绝，不能直接调用 OnCellClicked 绕过遮挡。</summary>
        public bool PressCell(BoardCellView cell)
        {
            Camera camera = Camera.main;
            if (camera == null || cell == null)
            {
                LastError = "棋盘相机或格子尚未就绪";
                return false;
            }
            return Press(cell.gameObject, camera.WorldToScreenPoint(cell.transform.position));
        }
        /// <summary>仅检查这个格子此刻是否能被用户点到，用于选择候选落点；不发送输入，不修改棋盘。</summary>
        public bool CanReachCell(BoardCellView cell)
        {
            return cell != null && Camera.main != null && Hit(cell.gameObject, Camera.main.WorldToScreenPoint(cell.transform.position), out _);
        }
        private bool Press(GameObject target, Vector2 point)
        {
            if (IsPressed || !Hit(target, point, out RaycastResult hit))
            {
                return false;
            }
            _position = point;
            _pressed = ExecuteEvents.GetEventHandler<IPointerClickHandler>(target);
            _pointer = new PointerEventData(EventSystem.current)
            {
                pointerId = -100,
                button = PointerEventData.InputButton.Left,
                position = point,
                pressPosition = point,
                eligibleForClick = true,
                pointerCurrentRaycast = hit,
                pointerPressRaycast = hit,
                clickCount = 1,
                clickTime = Time.unscaledTime
            };
            ExecuteEvents.ExecuteHierarchy(target, _pointer, ExecuteEvents.pointerEnterHandler);
            GameObject down = ExecuteEvents.ExecuteHierarchy(target, _pointer, ExecuteEvents.pointerDownHandler);
            _pointer.pointerPress = down != null ? down : _pressed;
            _pointer.rawPointerPress = target;
            LastError = "";
            return true;
        }
        /// <summary>
        /// 复用 EventSystem 的排序结果，只接受最上层命中。不能从射线列表中挑自己想要的后排物体，
        /// 否则加载遮罩/结算页挡住棋盘时，测试仍能点穿并产生虚假的“通过”。
        /// </summary>
        private bool Hit(GameObject target, Vector2 point, out RaycastResult hit)
        {
            hit = default;
            if (EventSystem.current == null || point.x < 0 || point.y < 0 || point.x >= Screen.width || point.y >= Screen.height)
            {
                LastError = "没有 EventSystem，或目标不在游戏视口内";
                return false;
            }
            _hits.Clear();
            EventSystem.current.RaycastAll(new PointerEventData(EventSystem.current) { position = point }, _hits);
            if (_hits.Count > 0)
            {
                hit = _hits[0];
                GameObject actual = ExecuteEvents.GetEventHandler<IPointerClickHandler>(hit.gameObject);
                GameObject expected = ExecuteEvents.GetEventHandler<IPointerClickHandler>(target);
                if (actual != null && actual == expected)
                {
                    return true;
                }
            }
            LastError = "目标被遮挡或没有输入射线：" + target.name + "；最上层=" + (hit.gameObject != null ? hit.gameObject.name : "无");
            return false;
        }
        /// <summary>在后续轮询释放手势；再次检查最上层目标，防止按下后出现弹窗却仍点击旧按钮。</summary>
        public bool Release(bool click)
        {
            if (_pointer == null)
            {
                return false;
            }
            PointerEventData pointer = _pointer;
            GameObject pressed = _pressed;
            _pointer = null;
            _pressed = null;
            bool accepted = click && pressed != null && Hit(pressed, _position, out _);
            if (pointer.pointerPress != null)
            {
                ExecuteEvents.Execute(pointer.pointerPress, pointer, ExecuteEvents.pointerUpHandler);
            }
            if (accepted)
            {
                ExecuteEvents.Execute(pressed, pointer, ExecuteEvents.pointerClickHandler);
            }
            if (pressed != null)
            {
                ExecuteEvents.ExecuteHierarchy(pressed, pointer, ExecuteEvents.pointerExitHandler);
            }
            return accepted;
        }
    }
}
#endif
