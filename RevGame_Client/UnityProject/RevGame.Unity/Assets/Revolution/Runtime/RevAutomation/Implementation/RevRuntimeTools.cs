// 编辑器与开发版 Player 共用的观测/UI 事件；HTTP 线程不触碰 Unity 对象。
#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Revolution
{
    internal static class RevRuntimeTools
    {
        [Serializable] private sealed class TextResult { public string text; }
        [Serializable] private sealed class Options
        {
            public string id, objectPath, scene, text, query;
            public bool includeInactive, submit;
            public int offset, limit = 200;
            public void CheckPage() { if (offset < 0 || limit < 1 || limit > 1000) { throw new ArgumentException("offset >= 0，limit 为 1..1000"); } }
        }
        internal static IEnumerable<GameObject> Objects()
        {
            return Resources.FindObjectsOfTypeAll<GameObject>().Where(go => go.scene.IsValid() && go.scene.isLoaded && (go.hideFlags & HideFlags.HideAndDontSave) == 0);
        }
        internal static string Identity(UnityEngine.Object obj) => "instance:" + obj.GetInstanceID();
        internal static string HierarchyPath(Transform t) => t.parent == null ? t.name : HierarchyPath(t.parent) + "/" + t.name;
        private static GameObject Find(Options p)
        {
            var matches = Objects().Where(go => string.IsNullOrEmpty(p.scene) || go.scene.path == p.scene)
                .Where(go => !string.IsNullOrEmpty(p.id) ? Identity(go) == p.id : !string.IsNullOrEmpty(p.objectPath) && HierarchyPath(go.transform) == p.objectPath).ToArray();
            if (matches.Length != 1) { throw new ArgumentException("UI 目标需要唯一 id/objectPath；匹配数：" + matches.Length); }
            return matches[0];
        }
        private const string Empty = "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}";
        private const string TargetProperties = "\"id\":{\"type\":\"string\"},\"objectPath\":{\"type\":\"string\"},\"scene\":{\"type\":\"string\"}";
        private static string Schema(string fields) => "{\"type\":\"object\",\"properties\":{" + fields + "},\"additionalProperties\":false}";
        internal static void Register()
        {
            RevRuntimeAutomation.Register("runtime.state", "场景、帧、域、服务、计时器及框架 UI 状态", Empty, State, true);
            RevRuntimeAutomation.Register("ui.dump", "UGUI/TMP 控件、文本、可交互与遮挡状态", Schema("\"query\":{\"type\":\"string\"},\"offset\":{\"type\":\"integer\",\"minimum\":0},\"limit\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":1000},\"includeInactive\":{\"type\":\"boolean\"}"), Dump, true);
            RevRuntimeAutomation.Register("ui.click", "通过 EventSystem 注入指针事件，拒绝遮挡/禁用目标", Schema(TargetProperties), Click);
            RevRuntimeAutomation.Register("ui.input", "填写 UGUI/TMP 输入框；submit 触发 onEndEdit", Schema(TargetProperties + ",\"text\":{\"type\":\"string\"},\"submit\":{\"type\":\"boolean\"}"), Input);
            RevRuntimeAutomation.Register("ui.back", "RevUI.Back 返回上一界面", Empty, Back);
            RevRuntimeAutomation.Register("ui.close", "RevUI.Close 关闭指定面板", Schema(TargetProperties), Close);
            RevRuntimeAutomation.Register("runtime.screenshot", "等待下一帧完整 PNG，返回路径和 base64 视觉证据", Empty, Screenshot, true);
            RevRuntimeAutomation.Register("runtime.ping", "运行实例身份", Empty, _ => new TextResult { text = Application.productName + " / " + Application.platform }, true);
            RevRuntimeAutomation.RegisterPerformance();
            RevRuntimeAutomation.Register("runtime.logs", "最近 128 条运行日志及本局累计 Error/Exception 数", Empty, _ => RevRuntimeAutomation.RecentLogs(), true);
        }
        [Serializable] private sealed class PanelInfo { public string id, key, state, layer; public bool covered; }
        [Serializable] private sealed class RuntimeState
        {
            public string scene, scopes, services, ui;
            public int frame, timerCount, errorCount;
            public float timeScale;
            public PanelInfo[] panels;
        }
        [Serializable] private sealed class UIElement
        {
            public string id, path, type, text;
            public bool active, interactable, raycastReachable;
            public Vector2 screenPoint;
        }
        [Serializable] private sealed class UIPage { public int total, offset; public bool truncated; public UIElement[] items; }
        [Serializable] private sealed class ClickResult { public string id, hitPath; public bool dispatched; }
        [Serializable] private sealed class CaptureResult { public string path; public long bytes; public string source = "GameView"; }

        public static object State(string json)
        {
            return new RuntimeState { scene = SceneManager.GetActiveScene().path, frame = Time.frameCount, timeScale = Time.timeScale,
                timerCount = RevTimer.Count, errorCount = RevRuntimeAutomation.ErrorCount, scopes = RevScope.Dump(), services = RevServices.Dump(), ui = RevUI.DumpStats(),
                panels = Objects().SelectMany(go => go.GetComponents<RevUIPanel>()).Select(panel => new PanelInfo
                { id = Identity(panel.gameObject), key = panel.PanelKey, state = panel.State.ToString(), layer = panel.Layer.ToString(), covered = panel.IsCovered }).ToArray() };
        }

        private static string TextOf(GameObject go)
        {
            var input = go.GetComponent<InputField>();
            if (input != null) { return input.text; }
            var text = go.GetComponentInChildren<Text>(true);
            if (text != null) { return text.text; }
            // TMP 为可选依赖，只读公开 text 属性，不要求框架引用 TMP 程序集。
            var tmp = go.GetComponentsInChildren<Component>(true).FirstOrDefault(c => c != null &&
                (c.GetType().FullName == "TMPro.TextMeshProUGUI" || c.GetType().FullName == "TMPro.TMP_InputField"));
            return tmp?.GetType().GetProperty("text")?.GetValue(tmp) as string;
        }

        private static Vector2 ScreenPoint(GameObject go)
        {
            var rect = go.GetComponent<RectTransform>();
            if (rect == null) { throw new ArgumentException("UI 目标缺少 RectTransform"); }
            var canvas = go.GetComponentInParent<Canvas>();
            if (canvas == null) { throw new ArgumentException("UI 目标不在 Canvas 中"); }
            return RectTransformUtility.WorldToScreenPoint(canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera,
                rect.TransformPoint(rect.rect.center));
        }

        private static RaycastResult Raycast(Vector2 point)
        {
            if (EventSystem.current == null) { return default; }
            var hits = new List<RaycastResult>();
            EventSystem.current.RaycastAll(new PointerEventData(EventSystem.current) { position = point }, hits);
            return hits.FirstOrDefault();
        }

        private static bool Reaches(GameObject target, RaycastResult hit)
        {
            if (hit.gameObject == null) { return false; }
            var handler = ExecuteEvents.GetEventHandler<IPointerClickHandler>(hit.gameObject);
            return handler != null && handler == ExecuteEvents.GetEventHandler<IPointerClickHandler>(target);
        }

        public static object Dump(string json)
        {
            var p = JsonUtility.FromJson<Options>(json); p.CheckPage();
            Canvas.ForceUpdateCanvases();
            var items = Objects().Where(go => (p.includeInactive || go.activeInHierarchy) && go.GetComponent<Selectable>() != null)
                .Select(go =>
                {
                    var selectable = go.GetComponent<Selectable>();
                    Vector2 point = ScreenPoint(go);
                    return new UIElement { id = Identity(go), path = HierarchyPath(go.transform),
                        type = selectable.GetType().FullName, text = TextOf(go), active = go.activeInHierarchy,
                        interactable = selectable.IsActive() && selectable.IsInteractable(), screenPoint = point,
                        raycastReachable = go.activeInHierarchy && Reaches(go, Raycast(point)) };
                }).Where(e => string.IsNullOrEmpty(p.query) || (e.path + " " + e.text).IndexOf(p.query, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderBy(e => e.path).ToArray();
            return new UIPage { total = items.Length, offset = p.offset, truncated = items.Length > p.offset + p.limit,
                items = items.Skip(p.offset).Take(p.limit).ToArray() };
        }

        public static object Click(string json)
        {
            // 不能直接 Button.onClick.Invoke()：那会绕过禁用状态和遮挡，测出“玩家点不到却通过”。
            // 先按 Canvas 算屏幕中心，再用 EventSystem 的真实 Raycast 结果找到事件接收者。
            var go = Find(JsonUtility.FromJson<Options>(json));
            if (EventSystem.current == null || !go.activeInHierarchy) { throw new InvalidOperationException("没有 EventSystem 或目标未激活"); }
            var selectable = go.GetComponent<Selectable>();
            if (selectable != null && (!selectable.IsActive() || !selectable.IsInteractable())) { throw new InvalidOperationException("目标不可交互"); }
            Canvas.ForceUpdateCanvases();
            Vector2 point = ScreenPoint(go);
            RaycastResult hit = Raycast(point);
            if (!Reaches(go, hit)) { throw new InvalidOperationException("目标中心被遮挡或没有可接收点击的 Raycast 结果"); }
            GameObject handler = ExecuteEvents.GetEventHandler<IPointerClickHandler>(hit.gameObject);
            string id = Identity(go), hitPath = HierarchyPath(hit.gameObject.transform);
            var data = new PointerEventData(EventSystem.current) { position = point, pressPosition = point, pointerId = -1,
                button = PointerEventData.InputButton.Left, pointerCurrentRaycast = hit, pointerPressRaycast = hit,
                eligibleForClick = true, clickCount = 1, clickTime = Time.unscaledTime, rawPointerPress = hit.gameObject };
            EventSystem.current.SetSelectedGameObject(go, data);
            ExecuteEvents.ExecuteHierarchy(hit.gameObject, data, ExecuteEvents.pointerEnterHandler);
            GameObject down = ExecuteEvents.ExecuteHierarchy(hit.gameObject, data, ExecuteEvents.pointerDownHandler);
            data.pointerPress = down != null ? down : handler;
            if (data.pointerPress != null) { ExecuteEvents.Execute(data.pointerPress, data, ExecuteEvents.pointerUpHandler); }
            bool dispatched = handler != null && ExecuteEvents.Execute(handler, data, ExecuteEvents.pointerClickHandler);
            if (!dispatched) { throw new InvalidOperationException("点击事件没有接收者"); }
            return new ClickResult { id = id, hitPath = hitPath, dispatched = true };
        }

        public static object Input(string json)
        {
            // 输入使用控件公开 text/setter，正常触发值变更；submit 明确表示再触发结束编辑。
            // 不模拟键盘逐字输入和输入法；需要这类测试时应提供项目专用能力。
            var p = JsonUtility.FromJson<Options>(json);
            var go = Find(p);
            var selectable = go.GetComponent<Selectable>();
            if (!go.activeInHierarchy || selectable == null || !selectable.IsActive() || !selectable.IsInteractable()) { throw new InvalidOperationException("输入框不可交互"); }
            var input = go.GetComponent<InputField>();
            if (input != null)
            {
                if (input.readOnly) { throw new InvalidOperationException("输入框为只读"); }
                input.text = p.text ?? "";
                if (p.submit) { input.onEndEdit.Invoke(input.text); }
            }
            else
            {
                var tmp = go.GetComponents<Component>().FirstOrDefault(c => c != null && c.GetType().FullName == "TMPro.TMP_InputField");
                if (tmp == null) { throw new ArgumentException("目标不是 UGUI/TMP 输入框"); }
                if ((bool)tmp.GetType().GetProperty("readOnly").GetValue(tmp)) { throw new InvalidOperationException("输入框为只读"); }
                tmp.GetType().GetProperty("text").SetValue(tmp, p.text ?? "");
                if (p.submit)
                {
                    object evt = tmp.GetType().GetProperty("onEndEdit").GetValue(tmp);
                    evt.GetType().GetMethod("Invoke", new[] { typeof(string) }).Invoke(evt, new object[] { p.text ?? "" });
                }
            }
            return new TextResult { text = TextOf(go) };
        }

        public static object Back(string json)
        {
            if (!RevUI.Back()) { throw new InvalidOperationException("当前没有可返回的界面"); }
            return State(json);
        }

        public static object Close(string json)
        {
            var panel = Find(JsonUtility.FromJson<Options>(json)).GetComponent<RevUIPanel>();
            if (panel == null || !RevUI.Close(panel)) { throw new InvalidOperationException("目标不是可关闭的 RevUIPanel"); }
            return new TextResult { text = "已请求关闭；界面动画完成请查询 runtime.state" };
        }

        public static object Screenshot(string json)
        {
            string folder = Path.Combine(Application.persistentDataPath, "RevolutionAutomation", "screenshots");
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".png");
            ScreenCapture.CaptureScreenshot(path);
            return new CaptureOperation(path);
        }
        private sealed class CaptureOperation : RevRuntimeOperation
        {
            private readonly string _path;
            private readonly int _frame = Time.frameCount;
            public CaptureOperation(string path) { _path = path; }
            public override bool Tick()
            {
                if (!Application.isPlaying) { Success = false; Message = "游戏已停止"; return true; }
                if (Time.frameCount <= _frame || !File.Exists(_path)) { return false; }
                byte[] png;
                try { png = File.ReadAllBytes(_path); } catch (IOException) { return false; }
                if (png.Length < 32 || png[0] != 137 || png[1] != 80 || png[png.Length - 8] != 73 || png[png.Length - 5] != 68) { return false; }
                if (png.Length > 8 * 1024 * 1024) { Success = false; Message = "截图超过 8 MiB，请降低游戏分辨率"; return true; }
                Result = new Dictionary<string, object> { ["path"] = _path, ["bytes"] = png.Length, ["mimeType"] = "image/png", ["base64"] = Convert.ToBase64String(png) };
                return true;
            }
        }
    }
}
#endif
