using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Revolution.Editor;
using UnityEditor;
using UnityEngine;

namespace Revolution.Gal.Editor
{
    /// <summary>通过稳定控件名查询和调整主题布局。坐标以父控件左上角为原点，单位为设计像素。</summary>
    public static class RevGalLayoutAuthoring
    {
        /// <summary>编辑器桥的参数，document 使用内联 JSON，CLI 帮用户读取本地文件。</summary>
        [Serializable]
        private sealed class Request
        {
            public string path;
            public string query;
            public string document;
            public string expectedHash;
            public bool checkOnly;
        }

        /// <summary>读取控件名称、布局覆盖以及当前测得的尺寸，供 AI 在修改前找目标。</summary>
        [RevAutomationAction("gal.ui.layout", Environment = "edit", Keys = new[] { "path", "query" }, Description = "按控件名称查询主题布局与设计像素尺寸")]
        public static object Inspect(RevAutomationRequest request)
        {
            var args = JsonUtility.FromJson<Request>(request.payloadJson);
            return Describe(Read(args.path), args.query);
        }

        /// <summary>打开所选主题的可视布局设计器，供人工检查 AI 修改后的矩形。</summary>
        [RevAutomationAction("gal.ui.designer", Environment = "edit", Keys = new[] { "path" }, Description = "打开主题的可视化面板布局设计器")]
        public static object OpenDesigner(RevAutomationRequest request)
        {
            var args = JsonUtility.FromJson<Request>(request.payloadJson);
            var theme = Read(args.path);
            RevGalThemeWindow.Open(theme);
            return new Dictionary<string, object> { ["path"] = AssetDatabase.GetAssetPath(theme) };
        }

        /// <summary>预检一批布局并一次保存同一主题资产。坏字段、旧摘要、未保存编辑都不会提前写入。</summary>
        [RevAutomationAction("gal.ui.layout-set", Environment = "edit", Keys = new[] { "path", "document", "expectedHash", "checkOnly" }, Description = "按控件名调整大小位置，支持预检和内容摘要保护")]
        public static object Set(RevAutomationRequest request)
        {
            var args = JsonUtility.FromJson<Request>(request.payloadJson);
            var theme = Read(args.path);
            if (!string.IsNullOrEmpty(args.expectedHash) && Hash(theme) != args.expectedHash)
            {
                throw new InvalidOperationException("主题已变更，请重新读取 gal.ui.layout 的 hash");
            }
            if (EditorUtility.IsDirty(theme) && !args.checkOnly)
            {
                throw new InvalidOperationException("主题有未保存编辑，请先在编辑器保存");
            }
            var document = RevAutomationJson.Object(RevAutomationJson.Parse(args.document));
            OnlyKeys(document, "parts");
            if (!document.TryGetValue("parts", out var values) || !(values is List<object> parts) || parts.Count == 0)
            {
                throw new ArgumentException("布局文档需要非空 parts 数组");
            }
            var draft = UnityEngine.Object.Instantiate(theme);
            draft.name = theme.name;
            try
            {
                var seen = new HashSet<RevGalThemePart>();
                foreach (var value in parts)
                {
                    var edit = RevAutomationJson.Object(value);
                    OnlyKeys(edit, "part", "rect", "override", "choiceHeight");
                    if (!edit.TryGetValue("part", out var name) || !(name is string text) ||
                        !Enum.TryParse(text, true, out RevGalThemePart role) || !Enum.IsDefined(typeof(RevGalThemePart), role) || !seen.Add(role))
                    {
                        throw new ArgumentException("控件 part 必须为有效且不重复的名称");
                    }
                    var style = draft.Parts.SingleOrDefault(part => part != null && part.Part == role)
                        ?? throw new ArgumentException("主题中找不到控件：" + role);
                    if (edit.TryGetValue("rect", out var raw))
                    {
                        if (!RevGalPartStyle.UsesRectLayout(role))
                        {
                            throw new ArgumentException(role + " 的几何由全屏遮罩或选项列表管理");
                        }
                        var rect = RevAutomationJson.Object(raw);
                        OnlyKeys(rect, "x", "y", "width", "height");
                        float x = Number(rect, "x"), y = Number(rect, "y");
                        float width = Number(rect, "width"), height = Number(rect, "height");
                        if (width <= 0 || height <= 0)
                        {
                            throw new ArgumentException(role + " 的宽高必须大于零");
                        }
                        style.Layout ??= new RevGalPartLayout();
                        style.Layout.Override = true;
                        style.Layout.AnchorMin = style.Layout.AnchorMax = style.Layout.Pivot = new Vector2(0, 1);
                        style.Layout.OffsetMin = new Vector2(x, -y - height);
                        style.Layout.OffsetMax = new Vector2(x + width, -y);
                    }
                    if (edit.TryGetValue("override", out var enabled))
                    {
                        if (!(enabled is bool flag) || !RevGalPartStyle.UsesRectLayout(role))
                        {
                            throw new ArgumentException(role + " 的 override 需要可调整控件和布尔值");
                        }
                        style.Layout ??= new RevGalPartLayout();
                        style.Layout.Override = flag;
                    }
                    if (edit.ContainsKey("choiceHeight"))
                    {
                        float height = Number(edit, "choiceHeight");
                        if (role != RevGalThemePart.ChoiceButton || height < 0)
                        {
                            throw new ArgumentException("choiceHeight 只用于 ChoiceButton，且需要非负数");
                        }
                        style.ChoiceHeight = height;
                    }
                    if (edit.Count == 1)
                    {
                        throw new ArgumentException(role + " 没有提供任何布局修改");
                    }
                }
                string original = Hash(theme);
                bool changed = JsonUtility.ToJson(theme) != JsonUtility.ToJson(draft);
                var report = Describe(draft, null);
                if (changed && !args.checkOnly)
                {
                    Undo.RecordObject(theme, "调整 Gal 面板布局");
                    EditorUtility.CopySerialized(draft, theme);
                    EditorUtility.SetDirty(theme);
                    AssetDatabase.SaveAssetIfDirty(theme);
                }
                report["path"] = args.path;
                report["hash"] = args.checkOnly ? original : Hash(theme);
                report["changed"] = changed;
                report["written"] = changed && !args.checkOnly;
                return report;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(draft);
            }
        }

        /// <summary>用实际生成层级计算相对父控件的矩形，再转成左上角坐标。</summary>
        internal static Dictionary<string, object> Describe(RevGalPanelTheme theme, string query)
        {
            var preview = RevGalPanelBuilder.BuildLayoutPreview(theme);
            try
            {
                var items = new List<Dictionary<string, object>>();
                foreach (var style in theme.Parts.Where(part => part != null))
                {
                    string label = Label(style.Part);
                    if (!string.IsNullOrWhiteSpace(query) && style.Part.ToString().IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0 && !label.Contains(query))
                    {
                        continue;
                    }
                    var rect = RevGalPanelBuilder.FindPreviewTarget(preview, style.Part);
                    if (rect == null)
                    {
                        continue;
                    }
                    Vector3[] corners = new Vector3[4];
                    rect.GetWorldCorners(corners);
                    var parent = (RectTransform)rect.parent;
                    var topLeft = parent.InverseTransformPoint(corners[1]);
                    items.Add(new Dictionary<string, object>
                    {
                        ["part"] = style.Part.ToString(), ["label"] = label,
                        ["editable"] = RevGalPartStyle.UsesRectLayout(style.Part),
                        ["override"] = style.Layout?.Override ?? false, ["choiceHeight"] = style.ChoiceHeight,
                        ["layout"] = style.Layout,
                        ["rect"] = new Dictionary<string, object>
                        {
                            ["x"] = topLeft.x - parent.rect.xMin, ["y"] = parent.rect.yMax - topLeft.y,
                            ["width"] = rect.rect.width, ["height"] = rect.rect.height
                        },
                        ["canvasRect"] = new Dictionary<string, object>
                        {
                            ["x"] = ((RectTransform)preview.transform).InverseTransformPoint(corners[1]).x + 960,
                            ["y"] = 540 - ((RectTransform)preview.transform).InverseTransformPoint(corners[1]).y,
                            ["width"] = rect.rect.width, ["height"] = rect.rect.height
                        }
                    });
                }
                return new Dictionary<string, object>
                {
                    ["path"] = AssetDatabase.GetAssetPath(theme), ["hash"] = Hash(theme),
                    ["referenceSize"] = new Vector2(1920, 1080), ["items"] = items, ["total"] = items.Count,
                    ["dirty"] = EditorUtility.IsDirty(theme)
                };
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(preview);
            }
        }

        /// <summary>可从当前默认面板推断主题，也可显式传入任何已有主题 SO。</summary>
        internal static RevGalPanelTheme Read(string path)
        {
            var theme = string.IsNullOrEmpty(path) ? RevGalPanelBuilder.FindTheme()
                : AssetDatabase.LoadAssetAtPath<RevGalPanelTheme>(RevGalAssets.AssetPath(path, ".asset"));
            return theme != null ? theme : throw new ArgumentException("请先生成面板主题，或用 path 指定主题 SO");
        }

        /// <summary>读数时保留有限浮点语义，避免无穷大破坏 RectTransform。</summary>
        private static float Number(Dictionary<string, object> values, string key)
        {
            if (!values.TryGetValue(key, out var raw) || !(raw is double number) || double.IsNaN(number) ||
                double.IsInfinity(number) || Math.Abs(number) > float.MaxValue)
            {
                throw new ArgumentException(key + " 需要有限数值");
            }
            return (float)number;
        }

        private static void OnlyKeys(Dictionary<string, object> values, params string[] allowed)
        {
            if (values.Keys.Except(allowed).Any())
            {
                throw new ArgumentException("未知布局字段：" + string.Join(", ", values.Keys.Except(allowed)));
            }
        }

        /// <summary>供设计器与 CLI 共用的中文控件名。</summary>
        internal static string Label(RevGalThemePart part)
        {
            var field = typeof(RevGalThemePart).GetField(part.ToString());
            return ((InspectorNameAttribute)Attribute.GetCustomAttribute(field, typeof(InspectorNameAttribute)))?.displayName ?? part.ToString();
        }

        private static string Hash(RevGalPanelTheme theme)
        {
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(JsonUtility.ToJson(theme)))).Replace("-", "").ToLowerInvariant();
        }
    }
}
