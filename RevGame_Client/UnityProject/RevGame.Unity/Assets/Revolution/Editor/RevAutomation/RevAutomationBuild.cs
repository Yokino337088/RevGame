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

        [RevAutomationAction("build.player", Keys = new[] { "target", "path", "development", "backend", "automation", "frameTiming" }, Description = "构建Player到指定空目录，支持IL2CPP与非开发自动化包，返回真实BuildReport")]
        public static object Player(RevAutomationRequest request)
        {
            return RevAutomationPlayerBuild.Execute(request);
        }

        private sealed class CompletedBuild : RevAutomationOperation
        {
            public CompletedBuild(object result, bool success) { Result = result; Success = success; Message = success ? "Player 构建完成" : "Player 构建失败，请检查 build-result.json 与 Console"; }
            public override bool Tick() => true;
        }
    }
}
