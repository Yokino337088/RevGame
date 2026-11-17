using System.Collections.Generic;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace Revolution.Gal.Editor
{
    /// <summary>呈现自有界面接入需要的路径、变量、信号和媒体，可复制为 JSON。</summary>
    internal sealed class RevGalHostWindow : EditorWindow
    {
        [SerializeField] private RevGalStory _story;

        internal static void Open(RevGalStory story)
        {
            var window = GetWindow<RevGalHostWindow>();
            window.titleContent = new GUIContent("Gal 自定义接入");
            window.minSize = new Vector2(460, 300);
            window._story = story;
            window.CreateGUI();
            window.Show();
        }

        public void CreateGUI()
        {
            rootVisualElement.Clear();
            if (_story == null)
            {
                rootVisualElement.Add(new HelpBox("从剧情工作台选择一份剧情后打开接入检查。", HelpBoxMessageType.Info));
                return;
            }
            var report = RevGalHostContract.Describe(_story);
            var toolbar = new Toolbar();
            toolbar.Add(new ToolbarButton(CreateGUI) { text = "刷新" });
            toolbar.Add(new ToolbarButton(() => GUIUtility.systemCopyBuffer = RevAutomationJson.Write(report)) { text = "复制清单 JSON" });
            rootVisualElement.Add(toolbar);
            var body = new ScrollView();
            body.style.flexGrow = 1;
            body.style.paddingLeft = 12;
            body.style.paddingRight = 12;
            rootVisualElement.Add(body);
            body.Add(Line($"{_story.name} · 起点 {report["start"]} · 修订 {report["revision"]}"));
            string path = report["logicalPath"] as string;
            if (path != null)
            {
                body.Add(new TextField("剧情逻辑路径") { value = path, isReadOnly = true });
            }
            else
            {
                body.Add(new HelpBox("可将此 SO 直接交给 RevGalPlayback.Create。需要按逻辑路径加载时，将 SO 放入项目的 RevAB 资源根。", HelpBoxMessageType.Info));
            }
            if (!(bool)report["valid"])
            {
                body.Add(new HelpBox("剧情结构存在错误，请通过工作台的问题列表定位后刷新。", HelpBoxMessageType.Warning));
            }
            AddRows(body, "变量", report, "variables", row => $"{row["id"]} = {row["initial"]}");
            AddRows(body, "宿主信号", report, "signals", row => $"{row["node"]} · {row["name"]} ({row["argument"]})");
            AddRows(body, "媒体与本地解析", report, "media", row => $"{row["path"]} · {row["type"]} · {LocalStatus(row["status"] as string)}");
        }

        private static void AddRows(VisualElement parent, string title, Dictionary<string, object> report, string key, System.Func<Dictionary<string, object>, string> text)
        {
            var rows = (Dictionary<string, object>[])report[key];
            var header = Line($"{title} ({rows.Length})");
            header.style.unityFontStyleAndWeight = FontStyle.Bold;
            header.style.marginTop = 8;
            parent.Add(header);
            foreach (var row in rows)
            {
                parent.Add(Line(text(row)));
            }
        }

        private static Label Line(string value)
        {
            return new Label(value) { style = { whiteSpace = WhiteSpace.Normal } };
        }

        private static string LocalStatus(string status)
        {
            return status switch
            {
                "found" => "已找到",
                "not-found" => "本地未找到",
                "type-mismatch" => "资源类型不符",
                "ambiguous" => "同路径有多个素材",
                "invalid-path" => "路径无效",
                _ => "待配置资源根"
            };
        }
    }
}
