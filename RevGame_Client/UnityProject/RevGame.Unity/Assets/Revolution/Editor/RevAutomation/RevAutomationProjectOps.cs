// ============================================================
// RevAutomationProjectOps —— 烘焙、包管理、以及框架全链路里缺的结构化入口。
// 【为什么】这些都是"发起之后要等一会儿"或"窗口里能看、脚本拿不到"的能力：
//          光照烘焙、Package Manager 是异步的，必须用跨帧工单等到真实结果；
//          Excel 表预览、AB 检查页的问题清单以前只画在窗口里，AI 只能拿到一句失败文本。
// 【怎么用】长操作返回 RevAutomationOperation，桥接持续 Tick，调用方像普通动作一样等结果。
// 【边界】包的增删会改 manifest 并触发域重载，所以必须 confirm；不替用户换包源。
//        NavMesh 用内置 NavMeshBuilder，AI Navigation 包的 NavMeshSurface 不在此列。
// ============================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Revolution.Editor.ExcelTool;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;

namespace Revolution.Editor
{
    public static class RevAutomationProjectOps
    {
        // ---------------------------------------------------------------- 烘焙

        [Serializable] public sealed class BakeStatus { public bool lightingRunning; public int lightmaps, navMeshTriangles; public float lightingProgress; }

        private static void RequireSavedScenes()
        {
            RevAutomationActions.RequireEditMode();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene scene = SceneManager.GetSceneAt(i);
                if (scene.isLoaded && (scene.isDirty || string.IsNullOrEmpty(scene.path))) { throw new InvalidOperationException("烘焙前必须保存场景，否则结果无处存放：" + scene.name); }
            }
        }

        private static BakeStatus Status() => new BakeStatus
        {
            lightingRunning = Lightmapping.isRunning, lightingProgress = Lightmapping.isRunning ? Lightmapping.buildProgress : 0,
            lightmaps = LightmapSettings.lightmaps.Length, navMeshTriangles = NavMesh.CalculateTriangulation().indices.Length / 3
        };

        private sealed class LightingBake : RevAutomationOperation
        {
            // isRunning 变 false 不等于烘成功：失败、被别处取消也会停下。只有完成事件才说明结果有效。
            private bool _completed;
            internal LightingBake() { Lightmapping.bakeCompleted += OnCompleted; }
            private void OnCompleted() => _completed = true;
            public override bool Tick()
            {
                if (Lightmapping.isRunning) { return false; }
                Lightmapping.bakeCompleted -= OnCompleted;
                Result = Status();
                Success = _completed;
                Message = _completed ? "光照烘焙完成" : "烘焙停止但没有完成（失败或被取消），请读 editor.console";
                return true;
            }
            public override void Cancel() { Lightmapping.bakeCompleted -= OnCompleted; Lightmapping.Cancel(); base.Cancel(); }
            public override void Dispose() { Lightmapping.bakeCompleted -= OnCompleted; }
        }

        [RevAutomationAction("lighting.bake", Environment = "live", Keys = new string[0], Description = "烘焙已打开场景的光照并等待结束，取消或超时会停止烘焙；场景必须已保存，只有 Contribute GI 的静态物体参与")]
        public static object BakeLighting(RevAutomationRequest request)
        {
            RequireSavedScenes();
            if (!Lightmapping.BakeAsync()) { throw new InvalidOperationException("Unity 没有启动烘焙，请读 editor.console"); }
            return new LightingBake();
        }

        [RevAutomationAction("bake.status", Environment = "either", Keys = new string[0], Description = "读取光照烘焙进度、光照贴图数量与 NavMesh 三角形数量")]
        public static object ReadBakeStatus(RevAutomationRequest request) => Status();

        [RevAutomationAction("lighting.clear", Keys = new string[0], Confirm = true, Description = "清除已打开场景的烘焙光照数据，需要 confirm")]
        public static object ClearLighting(RevAutomationRequest request)
        {
            Lightmapping.Clear();
            return Status();
        }

        [RevAutomationAction("navmesh.bake", Keys = new string[0], Description = "用内置 NavMeshBuilder 同步烘焙已打开场景；场景必须已保存，只有 Navigation Static 的物体参与，结果三角形为 0 时判失败")]
        public static object BakeNavMesh(RevAutomationRequest request)
        {
            RequireSavedScenes();
            UnityEditor.AI.NavMeshBuilder.BuildNavMesh();
            BakeStatus status = Status();
            if (status.navMeshTriangles == 0) { throw new InvalidOperationException("烘焙结果为空：没有可行走的静态几何，先用 gameobject.configure isStatic=true 标记地面"); }
            return status;
        }

        [RevAutomationAction("navmesh.clear", Keys = new string[0], Confirm = true, Description = "清除已打开场景的 NavMesh，需要 confirm")]
        public static object ClearNavMesh(RevAutomationRequest request)
        {
            UnityEditor.AI.NavMeshBuilder.ClearAllNavMeshes();
            return Status();
        }

        // ---------------------------------------------------------------- 包管理

        [Serializable] public sealed class PackageRow { public string name, version, source, displayName; }
        [Serializable] public sealed class PackageList { public PackageRow[] packages; }
        [Serializable] private sealed class PackageOptions { public string package; }

        private sealed class PackageWait : RevAutomationOperation
        {
            private readonly Request _request;
            private readonly Func<object> _complete;
            internal PackageWait(Request request, Func<object> complete) { _request = request; _complete = complete; }
            // Package Manager 的请求发出后无法撤回。不能让调用方以为"取消了就没改"，manifest 可能照样会变。
            public override void Cancel() { base.Cancel(); Message = "已提交给 Package Manager，无法撤回；结果以 package.list 和 Packages/manifest.json 为准"; }
            public override bool Tick()
            {
                if (!_request.IsCompleted) { return false; }
                Success = _request.Status == StatusCode.Success;
                Message = Success ? "完成" : "Package Manager 失败：" + _request.Error?.message;
                if (Success) { Result = _complete(); }
                return true;
            }
        }

        private static PackageRow Row(UnityEditor.PackageManager.PackageInfo info) => new PackageRow { name = info.name, version = info.version, source = info.source.ToString(), displayName = info.displayName };

        [RevAutomationAction("package.list", Keys = new string[0], Description = "列出工程当前解析出的全部包（名称、版本、来源），只读且不联网")]
        public static object ListPackages(RevAutomationRequest request)
        {
            ListRequest list = Client.List(true, true);
            return new PackageWait(list, () => new PackageList { packages = list.Result.OrderBy(x => x.name).Select(Row).ToArray() });
        }

        private static string RequirePackage(RevAutomationRequest request)
        {
            string package = JsonUtility.FromJson<PackageOptions>(request.payloadJson ?? "{}").package;
            if (string.IsNullOrWhiteSpace(package)) { throw new ArgumentException("需要 package，例如 com.unity.timeline 或 com.unity.timeline@1.7.6"); }
            return package;
        }

        [RevAutomationAction("package.add", Keys = new[] { "package" }, Confirm = true, Description = "安装包（名称、名称@版本或 git 地址），会改 manifest 并触发编译，需要 confirm；之后用 editor.wait 与 package.list 确认")]
        public static object AddPackage(RevAutomationRequest request)
        {
            AddRequest add = Client.Add(RequirePackage(request));
            return new PackageWait(add, () => Row(add.Result));
        }

        [RevAutomationAction("package.remove", Keys = new[] { "package" }, Confirm = true, Description = "移除包，会改 manifest 并触发编译，需要 confirm；之后用 editor.wait 与 package.list 确认")]
        public static object RemovePackage(RevAutomationRequest request)
        {
            string package = RequirePackage(request);
            RemoveRequest remove = Client.Remove(package);
            return new PackageWait(remove, () => new RevAutomationActions.RevAutomationText("已移除：" + package));
        }

        // ---------------------------------------------------------------- 构建设置

        [Serializable] public sealed class TargetRow { public string target, group; public bool installed, active; }
        [Serializable] public sealed class TargetList { public TargetRow[] targets; }
        [Serializable] private sealed class SceneOptions { public string path; public bool enabled = true; }

        [RevAutomationAction("build.targets", Environment = "either", Keys = new string[0], Description = "列出常用构建平台及当前 Unity 是否装了对应支持，切平台前先查")]
        public static object ListTargets(RevAutomationRequest request)
        {
            var targets = new[] { BuildTarget.StandaloneWindows64, BuildTarget.StandaloneWindows, BuildTarget.StandaloneOSX, BuildTarget.StandaloneLinux64, BuildTarget.Android, BuildTarget.iOS, BuildTarget.WebGL };
            return new TargetList
            {
                targets = targets.Select(target =>
                {
                    BuildTargetGroup group = BuildPipeline.GetBuildTargetGroup(target);
                    return new TargetRow { target = target.ToString(), group = group.ToString(), installed = BuildPipeline.IsBuildTargetSupported(group, target), active = EditorUserBuildSettings.activeBuildTarget == target };
                }).ToArray()
            };
        }

        [RevAutomationAction("build.scene-add", Keys = new[] { "path", "enabled" }, Description = "把场景加入 Build Settings，已存在时只更新启用状态")]
        public static object AddBuildScene(RevAutomationRequest request)
        {
            var p = JsonUtility.FromJson<SceneOptions>(request.payloadJson ?? "{}");
            RevAutomationEditorModel.AssetPath(p.path, false);
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(p.path) == null) { throw new ArgumentException("场景不存在：" + p.path); }
            var scenes = EditorBuildSettings.scenes.ToList();
            int index = scenes.FindIndex(s => s.path == p.path);
            if (index >= 0) { scenes[index].enabled = p.enabled; } else { scenes.Add(new EditorBuildSettingsScene(p.path, p.enabled)); }
            EditorBuildSettings.scenes = scenes.ToArray();
            AssetDatabase.SaveAssets();
            return new RevAutomationActions.RevAutomationRows(EditorBuildSettings.scenes.Select(s => (s.enabled ? "+ " : "- ") + s.path));
        }

        [RevAutomationAction("build.scene-remove", Keys = new[] { "path" }, Description = "把场景移出 Build Settings，不删除场景文件；不在列表时不报错")]
        public static object RemoveBuildScene(RevAutomationRequest request)
        {
            string path = JsonUtility.FromJson<SceneOptions>(request.payloadJson ?? "{}").path;
            EditorBuildSettings.scenes = EditorBuildSettings.scenes.Where(s => s.path != path).ToArray();
            AssetDatabase.SaveAssets();
            return new RevAutomationActions.RevAutomationRows(EditorBuildSettings.scenes.Select(s => (s.enabled ? "+ " : "- ") + s.path));
        }

        // ---------------------------------------------------------------- Excel 结构化结果

        [Serializable] private sealed class ExcelOptions { public string[] sources; public bool recursive; public string table; public int offset, limit = 50; }

        private static object Check(RevExcelCheck check) => new Dictionary<string, object> { ["level"] = check.Type.ToString(), ["message"] = check.Message };

        [RevAutomationAction("excel.check", Environment = "either", Keys = new string[0], Description = "导表前体检：代码目录、容器目录、数据目录是否可写可用，以及数据目录所在 AB 包")]
        public static object CheckExcelOutputs(RevAutomationRequest request)
        {
            RevExcelProjectSettings settings = RevExcelProjectSettings.instance;
            string bundle = RevExcelService.DataDirBundle(out bool autoByFolder);
            return new Dictionary<string, object>
            {
                ["code"] = Check(RevExcelService.CheckCodeDir(settings.structDir)), ["container"] = Check(RevExcelService.CheckCodeDir(settings.containerDir)),
                ["data"] = Check(RevExcelService.CheckDataDir()), ["bundle"] = bundle, ["autoByFolder"] = autoByFolder
            };
        }

        [RevAutomationAction("excel.preview", Environment = "either", Keys = new[] { "sources", "recursive", "table", "offset", "limit" },
            Description = "预览 Excel：不给 table 时列出每张表的字段数、行数、是否忽略和错误；给 table 时返回字段、分页行和带行号的错误。坏表也会返回详情，不直接失败")]
        public static object PreviewExcel(RevAutomationRequest request)
        {
            var p = JsonUtility.FromJson<ExcelOptions>(request.payloadJson ?? "{}");
            if (p.offset < 0 || p.limit < 1 || p.limit > 500) { throw new ArgumentException("offset >= 0，limit 为 1..500"); }
            string[] sources = p.sources != null && p.sources.Length > 0 ? p.sources : RevExcelUserSettings.instance.sources.ToArray();
            RevExcelReadResult read = RevExcelService.Read(sources, p.recursive, false);
            var fileErrors = read.FileErrors.Select(x => x.Key + ": " + x.Value).ToArray();
            if (string.IsNullOrEmpty(p.table))
            {
                return new Dictionary<string, object>
                {
                    ["files"] = read.Files.Count, ["fileErrors"] = fileErrors,
                    ["tables"] = read.Tables.Select(t => new Dictionary<string, object>
                    {
                        ["name"] = t.Name, ["sourceFile"] = t.SourceFile, ["valid"] = t.IsValid, ["ignored"] = t.Ignored, ["ignoreReason"] = t.IgnoreReason,
                        ["fields"] = t.Fields.Count, ["rows"] = t.Rows.Count, ["errors"] = t.Errors.Count
                    }).ToArray()
                };
            }
            ExcelTable table = read.Tables.FirstOrDefault(t => t.Name == p.table) ?? throw new ArgumentException("表不存在：" + p.table);
            return new Dictionary<string, object>
            {
                ["name"] = table.Name, ["sourceFile"] = table.SourceFile, ["valid"] = table.IsValid, ["ignored"] = table.Ignored, ["ignoreReason"] = table.IgnoreReason,
                ["fields"] = table.Fields.Select(f => new Dictionary<string, object> { ["name"] = f.Name, ["type"] = f.TypeText, ["desc"] = f.Desc, ["isKey"] = f.IsKey, ["column"] = f.ColumnName }).ToArray(),
                ["total"] = table.Rows.Count, ["offset"] = p.offset,
                ["rows"] = table.Rows.Skip(p.offset).Take(p.limit).Select(r => new Dictionary<string, object> { ["row"] = r.ExcelRowNumber, ["cells"] = r.Cells ?? new string[0] }).ToArray(),
                ["errors"] = table.Errors.Select(e => new Dictionary<string, object> { ["row"] = e.Row, ["message"] = e.Message }).ToArray()
            };
        }

        // ---------------------------------------------------------------- AB 结构化结果

        [Serializable] private sealed class AbOptions { public string target, bundle; public string[] assets; public int limit = 50; }

        [RevAutomationAction("ab.issues", Keys = new[] { "target" }, Description = "返回 AB 检查页的结构化问题：校验错误与警告、同包重名、被多包重复引用的资源、循环依赖、映射一致性；有问题也正常返回，canBuild 表示能否打包")]
        public static object AssetBundleIssues(RevAutomationRequest request)
        {
            var p = JsonUtility.FromJson<AbOptions>(request.payloadJson ?? "{}");
            BuildTarget target = RevAutomationActions.Target(p.target);
            ABCollectResult collect = ABCollector.CollectReadOnly();
            ABValidateResult validation = ABValidator.Validate(collect);
            ABValidator.ValidateAndroidShaders(collect, validation, target);
            ABDependencyReport dependencies = ABDependencyAnalyzer.AnalyzeFromDatabase(collect);
            ABResMapCheckReport map = ABResMapChecker.Check(target);
            return new Dictionary<string, object>
            {
                ["canBuild"] = validation.CanBuild, ["errors"] = validation.errors.ToArray(), ["warnings"] = validation.warnings.ToArray(),
                ["sameName"] = collect.entries.GroupBy(e => e.bundle + " → " + e.asset).Where(g => g.Count() > 1)
                    .Select(g => new Dictionary<string, object> { ["key"] = g.Key, ["logics"] = g.Select(e => e.logic).ToArray() }).ToArray(),
                ["shared"] = dependencies.sharedAssets.OrderByDescending(x => x.Value.Count)
                    .Select(x => new Dictionary<string, object> { ["asset"] = x.Key, ["bundles"] = x.Value.ToArray(), ["bytes"] = ABSizeCache.OfAsset(x.Key) }).ToArray(),
                ["circular"] = dependencies.circularBundles.ToArray(), ["mapErrors"] = map.Errors.ToArray(), ["mapWarnings"] = map.Warnings.ToArray()
            };
        }

        [RevAutomationAction("ab.extract-shared", Keys = new[] { "bundle", "assets" }, Confirm = true, Description = "把被多个包重复引用的资源移进共享包，消除重复打包；不给 assets 时处理全部重复资源，需要 confirm")]
        public static object ExtractShared(RevAutomationRequest request)
        {
            var p = JsonUtility.FromJson<AbOptions>(request.payloadJson ?? "{}");
            string bundle = ABBundleEditor.Normalize(p.bundle);
            if (!ABBundleEditor.IsLegalBundleName(bundle, out string why)) { throw new ArgumentException(why); }
            ICollection<string> assets = p.assets != null && p.assets.Length > 0 ? p.assets : (ICollection<string>)ABDependencyAnalyzer.AnalyzeFromDatabase(ABCollector.CollectReadOnly()).sharedAssets.Keys;
            if (assets.Count == 0) { return new RevAutomationActions.RevAutomationText("没有重复引用的资源"); }
            foreach (string asset in assets) { RevAutomationActions.RequireAssetPath(asset); }
            int added = ABBundleEditor.AddAssets(bundle, assets, out _, out _);
            ABMarkerWatcher.RefreshNow("CLI 移进共享包");
            return new RevAutomationActions.RevAutomationText("已把 " + added + " 个资源移进包 " + bundle);
        }

        [RevAutomationAction("ab.top-assets", Keys = new[] { "bundle", "limit" }, Description = "按源文件体积列出最大的资源，可限定某个包，用于定位包体热点")]
        public static object TopAssets(RevAutomationRequest request)
        {
            var p = JsonUtility.FromJson<AbOptions>(request.payloadJson ?? "{}");
            if (p.limit < 1 || p.limit > 500) { throw new ArgumentException("limit 为 1..500"); }
            ABCollectResult collect = ABCollector.CollectReadOnly();
            if (!string.IsNullOrEmpty(p.bundle) && !collect.bundleToAssets.ContainsKey(p.bundle)) { throw new ArgumentException("包不存在：" + p.bundle); }
            var rows = collect.bundleToAssets.Where(pair => string.IsNullOrEmpty(p.bundle) || pair.Key == p.bundle)
                .SelectMany(pair => pair.Value.Select(logic => (bundle: pair.Key, asset: collect.logicToAssetPath[logic])))
                .Select(x => (x.bundle, x.asset, bytes: ABSizeCache.OfAsset(x.asset))).OrderByDescending(x => x.bytes).Take(p.limit);
            return new RevAutomationActions.RevAutomationRows(rows.Select(x => x.bytes + " bytes\t" + x.bundle + "\t" + x.asset));
        }
    }
}
