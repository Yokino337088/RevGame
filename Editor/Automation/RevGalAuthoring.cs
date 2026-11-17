using System;
using System.Collections.Generic;
using System.Linq;
using Revolution.Editor;
using UnityEditor;
using UnityEngine;

namespace Revolution.Gal.Editor
{
    /// <summary>RevCLI、RevMCP 和编辑器共用 Story SO 操作入口。</summary>
    public static class RevGalAuthoring
    {
        [Serializable]
        private sealed class Request
        {
            public string path;
            public string id;
            public string title;
            public string document;
            public string font;
            public string theme;
            public string bundle;
            public string entry;
            public string[] operations;
            public bool checkOnly;
            public bool overwrite;
        }

        [RevAutomationAction("gal.story.create", Environment = "edit", Keys = new[] { "path", "id", "title", "bundle" },
            Description = "创建带可选分支和结局的 Gal 剧情 SO 模板")]
        public static object Create(RevAutomationRequest request)
        {
            var args = Read(request);
            ValidateBundle(args.bundle);
            var story = RevGalAssets.Write(args.path, RevGalAssets.Template(args.id, args.title ?? args.id), false);
            MarkBundle(args.path, args.bundle);
            return Describe(args.path, story.Definition);
        }

        [RevAutomationAction("gal.story.validate", Environment = "edit", Keys = new[] { "path" }, Description = "检查剧情节点与引用")]
        public static object Validate(RevAutomationRequest request)
        {
            var args = Read(request);
            return Describe(args.path, RevGalAssets.Read(args.path).Definition);
        }

        [RevAutomationAction("gal.story.import", Environment = "edit",
            Keys = new[] { "path", "document", "checkOnly", "overwrite", "bundle" },
            Description = "检查或导入内联剧情 JSON；允许覆盖时保留 SO 的 GUID")]
        public static object Import(RevAutomationRequest request)
        {
            var args = Read(request);
            var definition = RevGalJson.Import(args.document);
            if (!args.checkOnly)
            {
                ValidateBundle(args.bundle);
                RevGalAssets.Write(args.path, definition, args.overwrite);
                MarkBundle(args.path, args.bundle);
            }
            return Describe(args.path, definition);
        }

        /// <summary>一次生成可打包的剧情 SO，并回传默认路径、包名及运行时逻辑键。</summary>
        [RevAutomationAction("gal.story.generate", Environment = "edit",
            Keys = new[] { "document", "path", "bundle", "checkOnly", "overwrite" },
            Description = "校验剧情 JSON，按剧情 ID 生成 SO 并回传逻辑路径和 AB 名")]
        public static object Generate(RevAutomationRequest request)
        {
            var args = Read(request);
            var definition = RevGalJson.Import(args.document);
            var result = Describe(args.path, definition);
            if (!(bool)result["valid"])
            {
                return result;
            }

            string path = string.IsNullOrEmpty(args.path)
                ? $"{RevGalAssets.ResourceRoot()}/Gal/Stories/{definition.Id}.asset"
                : RevGalAssets.AssetPath(args.path, ".asset");
            string logicalPath = RevGalAssets.LogicalPath(path);
            if (!RevGalValidation.IsLogicalPath(logicalPath))
            {
                throw new ArgumentException("请指定可用作剧情逻辑路径的 SO 文件名：" + path);
            }
            string oldBundle = AssetImporter.GetAtPath(path)?.assetBundleName;
            string bundle = !string.IsNullOrEmpty(args.bundle) ? args.bundle : oldBundle;
            if (string.IsNullOrEmpty(bundle))
            {
                bundle = "gal_story_" + definition.Id.ToLowerInvariant().Replace('.', '_').Replace('-', '_');
                ValidateBundle(bundle);
            }
            else if (!string.IsNullOrEmpty(args.bundle))
            {
                ValidateBundle(bundle);
            }
            if (!args.checkOnly)
            {
                RevGalAssets.Write(path, definition, args.overwrite);
                if (!string.Equals(oldBundle, bundle, StringComparison.Ordinal))
                {
                    MarkBundle(path, bundle);
                }
            }
            result["path"] = path;
            result["logicalPath"] = logicalPath;
            result["bundle"] = bundle;
            result["written"] = !args.checkOnly;
            return result;
        }

        [RevAutomationAction("gal.story.export", Environment = "edit", Keys = new[] { "path" }, Description = "导出剧情 SO 的可往返 JSON")]
        public static object Export(RevAutomationRequest request)
        {
            var args = Read(request);
            var story = RevGalAssets.Read(args.path);
            var result = Describe(args.path, story.Definition);
            result["document"] = RevGalJson.Export(story.Definition);
            return result;
        }

        [RevAutomationAction("gal.story.simulate", Environment = "edit", Keys = new[] { "path", "entry", "operations" },
            Description = "离屏试读：advance、choose:ID、rollback、capture、restore、complete、read")]
        public static object Simulate(RevAutomationRequest request)
        {
            var args = Read(request);
            var steps = args.operations ?? Array.Empty<string>();
            if (steps.Length > 512)
            {
                throw new ArgumentException("一次试读最多 512 步");
            }
            using var session = RevGal.Preview(RevGalAssets.Read(args.path), args.entry);
            var trace = new List<Dictionary<string, object>> { Trace(session, "start") };
            RevGalSave bookmark = null;
            foreach (string step in steps)
            {
                bool accepted;
                switch (step)
                {
                    case "advance":
                        accepted = session.Advance();
                        break;
                    case "rollback":
                        accepted = session.Rollback();
                        break;
                    case "capture":
                        bookmark = session.Capture();
                        accepted = true;
                        break;
                    case "restore":
                        accepted = session.Restore(bookmark);
                        break;
                    case "complete":
                        accepted = session.CompleteSignal(session.Frame.Token);
                        break;
                    case "read":
                        accepted = session.MarkRead(session.Frame.Token);
                        break;
                    default:
                        if (step == null || !step.StartsWith("choose:", StringComparison.Ordinal))
                        {
                            throw new ArgumentException("不认识的试读操作：" + step);
                        }
                        accepted = session.Choose(step.Substring("choose:".Length));
                        break;
                }
                if (!accepted)
                {
                    throw new InvalidOperationException("节点 " + session.Frame.Id + " 不接受操作 " + step);
                }
                trace.Add(Trace(session, step));
            }
            return new Dictionary<string, object>
            {
                ["node"] = session.Frame.Id, ["kind"] = session.Frame.Kind.ToString(), ["stage"] = session.Frame.Stage,
                ["choices"] = session.Frame.Choices.Select(x => new Dictionary<string, object>
                { ["id"] = x.Id, ["text"] = x.Text, ["enabled"] = x.Enabled }).ToArray(),
                ["variables"] = session.Variables().ToDictionary(x => x.Id, x => (object)x.Value),
                ["progress"] = session.Progress(), ["history"] = session.History(),
                ["trace"] = trace
            };
        }

        private static Dictionary<string, object> Trace(RevGalSession session, string step)
        {
            return new Dictionary<string, object>
            {
                ["step"] = step, ["node"] = session.Frame.Id, ["kind"] = session.Frame.Kind.ToString()
            };
        }

        [RevAutomationAction("gal.ui.create", Environment = "edit", Keys = new[] { "font", "theme", "overwrite", "bundle" },
            Description = "生成默认 Gal 面板、设置弹层及共用主题")]
        public static object CreatePanel(RevAutomationRequest request)
        {
            var args = Read(request);
            ValidateBundle(args.bundle);
            var created = RevGalPanelBuilder.Create(args.font, args.theme, args.overwrite, args.bundle);
            return new Dictionary<string, object>
            {
                ["path"] = created.Path, ["themePath"] = created.ThemePath,
                ["logicalPath"] = RevGal.PanelRoot + "/RevGalPanel"
            };
        }

        [RevAutomationAction("gal.editor.open", Environment = "edit", Keys = new[] { "path" }, Description = "打开 Gal 剧情工作台")]
        public static object Open(RevAutomationRequest request)
        {
            var args = Read(request);
            RevGalWorkbench.Open(string.IsNullOrEmpty(args.path) ? null : RevGalAssets.Read(args.path));
            return new Dictionary<string, object> { ["path"] = args.path };
        }

        internal static Dictionary<string, object> Describe(string path, RevGalDefinition definition)
        {
            var issues = RevGalValidation.Validate(definition);
            return new Dictionary<string, object>
            {
                ["path"] = path,
                ["id"] = definition?.Id,
                ["start"] = definition?.Start,
                ["nodes"] = definition?.Nodes?.Count ?? 0,
                ["valid"] = !issues.Any(issue => !issue.Warning),
                ["issues"] = issues.Select(issue => new Dictionary<string, object>
                { ["node"] = issue.Node, ["message"] = issue.Message, ["warning"] = issue.Warning }).ToArray()
            };
        }

        internal static void MarkBundle(string path, string bundle)
        {
            if (string.IsNullOrEmpty(bundle))
            {
                return;
            }
            ValidateBundle(bundle);
            AssetImporter.GetAtPath(path).SetAssetBundleNameAndVariant(bundle, "");
        }

        private static void ValidateBundle(string bundle)
        {
            if (!string.IsNullOrEmpty(bundle) && bundle.Any(c => !(c >= 'a' && c <= 'z' || c >= '0' && c <= '9' || c == '_')))
            {
                throw new ArgumentException("包名使用小写字母、数字与下划线");
            }
        }

        private static Request Read(RevAutomationRequest request)
        {
            return JsonUtility.FromJson<Request>(request.payloadJson);
        }
    }
}
