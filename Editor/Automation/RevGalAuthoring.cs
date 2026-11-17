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

        /// <summary>从工作台模板一键建立可试读剧情，参数可覆盖默认路径、名称和资源包。</summary>
        [RevAutomationAction("gal.story.quickstart", Environment = "edit", Keys = new[] { "path", "id", "title", "bundle", "checkOnly" },
            Description = "根据现存资源根创建或重新打开两条路线的入门剧情 SO")]
        public static object Quickstart(RevAutomationRequest request)
        {
            var args = Read(request);
            return Quickstart(args.path, args.id, args.title, args.bundle, args.checkOnly);
        }

        /// <summary>工具栏和 CLI 共用的入门路径。重复执行只返回同 ID 的资产，已经写过的剧情保持由作者管理。</summary>
        internal static Dictionary<string, object> Quickstart(string path, string id, string title, string bundle, bool checkOnly)
        {
            id = string.IsNullOrEmpty(id) ? "first_story" : id;
            if (!RevGalValidation.IsId(id) || id == "." || id == "..")
            {
                throw new ArgumentException("入门剧情需要合法的故事 ID：" + id);
            }
            string root = RevGalAssets.ConfiguredResourceRoot();
            path = RevGalAssets.AssetPath(string.IsNullOrEmpty(path) ? $"{root ?? "Assets"}/Gal/Stories/{id}.asset" : path, ".asset");
            string logicalPath = RevGalAssets.LogicalPathInRoot(path, root);
            if (logicalPath != null && !RevGalValidation.IsLogicalPath(logicalPath))
            {
                throw new ArgumentException("剧情的资源逻辑路径无效：" + path);
            }
            var existing = AssetDatabase.LoadMainAssetAtPath(path);
            var story = existing as RevGalStory;
            if ((existing != null || System.IO.File.Exists(path)) && story?.Definition?.Id != id)
            {
                throw new InvalidOperationException("目标位置已有其他资产或不同 ID 的剧情：" + path);
            }
            var definition = story == null ? RevGalAssets.Template(id, string.IsNullOrWhiteSpace(title) ? "初次见面" : title) : story.Definition;
            var result = Describe(path, definition);
            string mark = story != null ? AssetImporter.GetAtPath(path)?.assetBundleName
                : string.IsNullOrEmpty(bundle) && logicalPath != null ? DefaultStoryBundle(id) : bundle;
            if (story == null)
            {
                ValidateBundle(mark);
            }

            // 路径、ID、标签都已检查。标签写入失败时撤销本次新建的 SO。
            bool written = !checkOnly && story == null && (bool)result["valid"];
            if (written)
            {
                RevGalAssets.Write(path, definition, false);
                try
                {
                    MarkBundle(path, mark);
                }
                catch
                {
                    AssetDatabase.DeleteAsset(path);
                    throw;
                }
            }
            result["logicalPath"] = logicalPath;
            result["bundle"] = mark;
            result["reused"] = story != null;
            result["written"] = written;
            return result;
        }

        [RevAutomationAction("gal.story.validate", Environment = "edit", Keys = new[] { "path" }, Description = "检查剧情节点与引用")]
        public static object Validate(RevAutomationRequest request)
        {
            var args = Read(request);
            return Describe(args.path, RevGalAssets.Read(args.path).Definition);
        }

        [RevAutomationAction("gal.story.contract", Environment = "edit", Keys = new[] { "path" },
            Description = "读取自定义界面接入清单，包括路径、变量、宿主信号和媒体资源")]
        public static object HostContract(RevAutomationRequest request)
        {
            return RevGalHostContract.Describe(RevGalAssets.Read(Read(request).path));
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
                bundle = DefaultStoryBundle(definition.Id);
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
            var result = RevGalTrial.Run(RevGalAssets.Read(args.path), args.entry, args.operations ?? Array.Empty<string>());
            if (result.RejectedStep != null)
            {
                throw new InvalidOperationException(result.Rejection);
            }
            return result.Snapshot;
        }

        /// <summary>使用 JSON 中的路线与断言对现有剧情做回归。返回全部用例，任一失败会使工单失败。</summary>
        [RevAutomationAction("gal.story.test", Environment = "edit", Keys = new[] { "path", "document" },
            Description = "运行命名路线用例集，验证节点、变量、选项与预期拒绝")]
        public static object TestRoutes(RevAutomationRequest request)
        {
            var args = Read(request);
            var result = RevGalRouteSuite.Run(RevGalAssets.Read(args.path), args.document);
            return (int)result["failed"] == 0 ? (object)result : new FailedRouteSuite(result);
        }

        /// <summary>用工单失败表达断言失败，同时让 CLI 和 RevMCP 读到每条路线的实际结果。</summary>
        private sealed class FailedRouteSuite : RevAutomationOperation
        {
            /// <summary>把断言报告交给编辑器桥，失败时保留各条路线的结果。</summary>
            public FailedRouteSuite(Dictionary<string, object> result)
            {
                Success = false;
                Message = "Gal 路线用例失败，请查 data.cases";
                Result = result;
            }

            /// <summary>断言已执行，下一次自动化轮询直接返回报告。</summary>
            public override bool Tick()
            {
                return true;
            }
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

        /// <summary>剧情逻辑 ID 保持原样，包名再转换为 RevAB 支持的小写下划线格式。</summary>
        private static string DefaultStoryBundle(string storyId)
        {
            return "gal_story_" + storyId.ToLowerInvariant().Replace('.', '_').Replace('-', '_');
        }

        private static Request Read(RevAutomationRequest request)
        {
            return JsonUtility.FromJson<Request>(request.payloadJson);
        }
    }
}
