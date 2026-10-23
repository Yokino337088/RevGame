// ============================================================
// RevAutomationBuild —— Build Settings、切换平台和 Player 构建的无人值守入口。
// 【为什么】点击 Build 后“没有抛异常”不能证明成功，必须读取真实 BuildReport。
// 【时序】切平台可能域重载，只恢复等待；已开始的 Build 不能自动重放。
// 【输出】Builds 下唯一目录保存产物，Library 工单只保存报告，避免覆盖旧包。
// ============================================================
using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Revolution.Editor
{
    public static class RevAutomationBuild
    {
        [Serializable] private sealed class SettingsResult { public string target; public RevAutomationSceneEntry[] scenes; }
        [Serializable] private sealed class BuildResultData { public string path, target, result, reportPath; public ulong bytes; public int errors, warnings; public double seconds; }

        [RevAutomationAction("build.settings", Keys = new string[0], Description = "读取 Build Settings 的场景列表与活动平台")]
        public static object Settings(RevAutomationRequest request) => new SettingsResult { target = EditorUserBuildSettings.activeBuildTarget.ToString(),
            scenes = EditorBuildSettings.scenes.Select(s => new RevAutomationSceneEntry { path = s.path, enabled = s.enabled }).ToArray() };

        [RevAutomationAction("build.configure", Keys = new[] { "scenes" }, Description = "替换 Build Settings 场景清单；不切换当前场景")]
        public static object Configure(RevAutomationRequest request)
        {
            var p = RevAutomationEditorOptions.Read(request);
            if (p.scenes == null || p.scenes.Any(s => s == null) || p.scenes.Select(s => s.path).Distinct().Count() != p.scenes.Length)
            { throw new ArgumentException("需要 scenes 数组且不能含重复场景"); }
            foreach (var scene in p.scenes)
            {
                RevAutomationEditorModel.AssetPath(scene.path, true);
                if (AssetDatabase.LoadAssetAtPath<SceneAsset>(scene.path) == null) { throw new ArgumentException("场景不存在：" + scene.path); }
            }
            EditorBuildSettings.scenes = p.scenes.Select(s => new EditorBuildSettingsScene(s.path, s.enabled)).ToArray();
            return Settings(request);
        }

        [RevAutomationAction("build.target", Environment = "live", Keys = new[] { "target" }, ResumeAfterReload = true, Description = "异步切换已安装的构建平台，重载后继续等待")]
        public static object Target(RevAutomationRequest request)
        {
            var p = RevAutomationEditorOptions.Read(request);
            if (string.IsNullOrEmpty(p.target)) { throw new ArgumentException("需要 target"); }
            BuildTarget target = RevAutomationActions.Target(p.target);
            string marker = Path.Combine(RevAutomationBridge.JobDirectory(request.id), "target-started");
            if (!File.Exists(marker))
            {
                RevAutomationActions.RequireEditMode();
                BuildTargetGroup group = BuildPipeline.GetBuildTargetGroup(target);
                if (!BuildPipeline.IsBuildTargetSupported(group, target)) { throw new InvalidOperationException("当前 Unity 未安装目标平台支持：" + target); }
                File.WriteAllText(marker, p.target);
                if (EditorUserBuildSettings.activeBuildTarget != target && !EditorUserBuildSettings.SwitchActiveBuildTargetAsync(group, target))
                { throw new InvalidOperationException("无法开始切换平台：" + target); }
            }
            return new TargetOperation(target);
        }

        private sealed class TargetOperation : RevAutomationOperation
        {
            private readonly BuildTarget _target;
            private int _stable;
            public TargetOperation(BuildTarget target) { _target = target; }
            public override bool Tick()
            {
                if (EditorApplication.isCompiling || EditorApplication.isUpdating || EditorUserBuildSettings.activeBuildTarget != _target) { _stable = 0; return false; }
                if (++_stable < 3) { return false; }
                Success = !EditorUtility.scriptCompilationFailed;
                Message = Success ? "已切换构建平台：" + _target : "目标平台脚本编译失败，请读取 editor.compilation/editor.console";
                Result = new RevAutomationActions.RevAutomationText(_target.ToString());
                return true;
            }
        }

        [RevAutomationAction("build.player", Keys = new[] { "target", "path", "development" }, Description = "按已配置场景构建 Player，返回真实 BuildReport 摘要")]
        public static object Player(RevAutomationRequest request)
        {
            var p = RevAutomationEditorOptions.Read(request);
            BuildTarget target = RevAutomationActions.Target(p.target);
            if (target != EditorUserBuildSettings.activeBuildTarget) { throw new InvalidOperationException("请先 build.target 并等待平台切换完成"); }
            if (!BuildPipeline.IsBuildTargetSupported(BuildPipeline.GetBuildTargetGroup(target), target)) { throw new InvalidOperationException("未安装构建平台支持"); }
            if (Enumerable.Range(0, UnityEngine.SceneManagement.SceneManager.sceneCount).Any(i => UnityEngine.SceneManagement.SceneManager.GetSceneAt(i).isDirty))
            { throw new InvalidOperationException("构建前请先 scene.save 保存修改"); }
            string[] scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray();
            if (scenes.Length == 0) { throw new InvalidOperationException("没有启用的构建场景，请先 build.configure"); }
            string extension = target == BuildTarget.StandaloneWindows || target == BuildTarget.StandaloneWindows64 ? ".exe" :
                target == BuildTarget.Android ? (EditorUserBuildSettings.buildAppBundle ? ".aab" : ".apk") : target == BuildTarget.StandaloneOSX ? ".app" : "";
            // Unity 禁止把 Player 构建到 Library；工单只存报告，发布产物放 Builds 的唯一子目录。
            string path = string.IsNullOrEmpty(p.path) ? Path.GetFullPath(Path.Combine("Builds", "RevAutomation", request.id, "Player" + extension)) : Path.GetFullPath(p.path);
            {
                string allowed = Path.GetFullPath("Builds") + Path.DirectorySeparatorChar;
                if (!path.StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) { throw new ArgumentException("自定义构建输出必须位于当前工程 Builds/ 内"); }
                for (var dir = new DirectoryInfo(Path.GetDirectoryName(path)); dir != null; dir = dir.Parent)
                { if (dir.Exists && (dir.Attributes & FileAttributes.ReparsePoint) != 0) { throw new ArgumentException("构建输出不能经过目录链接"); } }
            }
            if (File.Exists(path) || Directory.Exists(path)) { throw new IOException("构建目标已存在，请使用新的输出路径：" + path); }
            if (!string.IsNullOrEmpty(p.path) && Directory.Exists(Path.GetDirectoryName(path)) && Directory.EnumerateFileSystemEntries(Path.GetDirectoryName(path)).Any())
            { throw new IOException("自定义构建必须使用全新的空目录，防止覆盖同目录的旧 Player 文件"); }
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            BuildReport report = BuildPipeline.BuildPlayer(new BuildPlayerOptions { scenes = scenes, target = target, locationPathName = path, options = p.development ? BuildOptions.Development : BuildOptions.None });
            if (report == null) { throw new InvalidOperationException("Unity 未返回 BuildReport；请读取 editor.console 的构建错误，不能判为构建成功"); }
            var summary = report.summary;
            var result = new BuildResultData { path = path, target = target.ToString(), result = summary.result.ToString(), bytes = summary.totalSize,
                errors = (int)summary.totalErrors, warnings = (int)summary.totalWarnings, seconds = summary.totalTime.TotalSeconds,
                reportPath = Path.Combine(RevAutomationBridge.JobDirectory(request.id), "build-result.json") };
            RevAutomationBridge.WriteJson(result.reportPath, result);
            return new CompletedBuild(result, summary.result == BuildResult.Succeeded && summary.totalErrors == 0);
        }

        private sealed class CompletedBuild : RevAutomationOperation
        {
            public CompletedBuild(object result, bool success) { Result = result; Success = success; Message = success ? "Player 构建完成" : "Player 构建失败，请检查 build-result.json 与 Console"; }
            public override bool Tick() => true;
        }
    }
}
