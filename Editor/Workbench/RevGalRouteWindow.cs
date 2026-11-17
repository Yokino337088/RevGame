using System.Collections.Generic;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace Revolution.Gal.Editor
{
    /// <summary>工作台的离屏路线回归报告。复用 CLI 的结果字段，策划可以复制到调试记录里。</summary>
    internal sealed class RevGalRouteWindow : EditorWindow
    {
        /// <summary>当前用例的序列化报告，域重载后窗口仍可显示结果。</summary>
        [SerializeField] private string _report;

        /// <summary>打开或复用一个结果窗口，使用本次路线的最新结果。</summary>
        public static void Open(Dictionary<string, object> report)
        {
            var window = GetWindow<RevGalRouteWindow>();
            window.titleContent = new GUIContent("Gal 路线验证");
            window.minSize = new Vector2(460, 320);
            window._report = RevAutomationJson.Write(report);
            window.CreateGUI();
            window.Show();
        }

        /// <summary>将每条路线的结果和失败字段交给用户。这里仅展示已执行的结果。</summary>
        public void CreateGUI()
        {
            rootVisualElement.Clear();
            if (string.IsNullOrEmpty(_report))
            {
                return;
            }
            var report = RevAutomationJson.Object(RevAutomationJson.Parse(_report));
            var toolbar = new Toolbar();
            toolbar.Add(new ToolbarButton(() => GUIUtility.systemCopyBuffer = _report) { text = "复制报告 JSON" });
            rootVisualElement.Add(toolbar);
            var body = new ScrollView();
            body.style.flexGrow = 1;
            body.style.paddingLeft = 12;
            body.style.paddingRight = 12;
            rootVisualElement.Add(body);
            body.Add(new Label($"{report["storyId"]} · {report["passed"]} 通过 · {report["failed"]} 失败"));
            foreach (var item in (List<object>)report["cases"])
            {
                var row = RevAutomationJson.Object(item);
                var box = new HelpBox($"{row["name"]}\n{row["message"]}", (bool)row["passed"] ? HelpBoxMessageType.Info : HelpBoxMessageType.Error);
                box.style.marginTop = 7;
                body.Add(box);
            }
        }
    }
}
