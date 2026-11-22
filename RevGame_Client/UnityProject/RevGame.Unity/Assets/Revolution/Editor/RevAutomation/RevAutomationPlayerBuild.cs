// Player构建适配：输出目录由调用者明确给出，临时设置在finally还原；失败保留BuildReport，不重放构建。
using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Revolution.Editor
{
    internal static class RevAutomationPlayerBuild
    {
        [Serializable]
        private sealed class Options
        {
            public string target, path, backend = "current";
            public bool development, automation, frameTiming;
        }

        [Serializable]
        private sealed class Result
        {
            public string path, target, backend, result, buildId, unityVersion, reportPath;
            public bool development, automation, frameTiming;
            public ulong bytes;
            public int errors, warnings;
            public double seconds;
        }

        /// <summary>同步构建指定目标，发布 BuildReport 并恢复此次临时修改的 Unity 设置及工具链环境。</summary>
        internal static object Execute(RevAutomationRequest request)
        {
            RevAutomationActions.RequireEditMode();
            var p = JsonUtility.FromJson<Options>(request.payloadJson);
            BuildTarget target = RevAutomationActions.Target(p.target);
            BuildTargetGroup group = BuildPipeline.GetBuildTargetGroup(target);
            if (target != EditorUserBuildSettings.activeBuildTarget)
            {
                throw new InvalidOperationException("请先build.target并等待平台切换完成");
            }
            if (!BuildPipeline.IsBuildTargetSupported(group, target))
            {
                throw new InvalidOperationException("当前Unity未安装目标平台支持");
            }
            if (Enumerable.Range(0, UnityEngine.SceneManagement.SceneManager.sceneCount).Any(i => UnityEngine.SceneManagement.SceneManager.GetSceneAt(i).isDirty))
            {
                throw new InvalidOperationException("存在未保存场景，请先保存，不能在自动构建时丢弃修改");
            }
            string[] scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray();
            if (scenes.Length == 0 || scenes.Any(s => AssetDatabase.LoadAssetAtPath<SceneAsset>(s) == null))
            {
                throw new InvalidOperationException("需要有效且启用的Build Settings场景");
            }
            ScriptingImplementation before = PlayerSettings.GetScriptingBackend(group);
            ScriptingImplementation backend = before;
            switch (p.backend)
            {
                case "current": break;
                case "il2cpp": backend = ScriptingImplementation.IL2CPP; break;
                case "mono": backend = ScriptingImplementation.Mono2x; break;
                default: throw new ArgumentException("backend只能是current/il2cpp/mono");
            }
            string extension = target == BuildTarget.StandaloneWindows64 || target == BuildTarget.StandaloneWindows ? ".exe" :
                target == BuildTarget.Android ? (EditorUserBuildSettings.buildAppBundle ? ".aab" : ".apk") : target == BuildTarget.StandaloneOSX ? ".app" : "";
            string path = string.IsNullOrEmpty(p.path) ? Path.GetFullPath(Path.Combine("Builds", "RevAutomation", request.id, "Player" + extension)) : Path.GetFullPath(p.path);
            ValidateOutput(path, extension);
            bool timingBefore = PlayerSettings.enableFrameTimingStats;
            bool androidAutomation = target == BuildTarget.Android && (p.automation || p.development);
            bool internetBefore = androidAutomation && PlayerSettings.Android.forceInternetPermission;
            var compilerBefore = PlayerSettings.GetIl2CppCompilerConfiguration(group);
            // Unity WebGL 自带 Python 在 Windows 中文环境可能按 GBK 读取 LLVM 的 UTF-8 输出。
            // 改动只作用于此 Editor 的构建子进程，进程退出或构建结束后都不会残留到系统环境。
            bool webGlUtf8 = target == BuildTarget.WebGL && Application.platform == RuntimePlatform.WindowsEditor;
            string oldPythonUtf8 = webGlUtf8 ? Environment.GetEnvironmentVariable("PYTHONUTF8") : null;
            try
            {
                if (webGlUtf8)
                {
                    Environment.SetEnvironmentVariable("PYTHONUTF8", "1");
                }
                PlayerSettings.SetScriptingBackend(group, backend);
                PlayerSettings.enableFrameTimingStats = p.frameTiming;
                if (androidAutomation)
                {
                    // 只给本次安卓测试包确保回环 HTTP 所需的网络权限。
                    PlayerSettings.Android.forceInternetPermission = true;
                }
                if (backend == ScriptingImplementation.IL2CPP)
                {
                    PlayerSettings.SetIl2CppCompilerConfiguration(group, Il2CppCompilerConfiguration.Release);
                }
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                // extraScriptingDefines只作用于这一次构建，不把测试宏残留到项目的正式发布设置中。
                BuildReport report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
                {
                    scenes = scenes, target = target, locationPathName = path,
                    options = p.development ? BuildOptions.Development : BuildOptions.None,
                    extraScriptingDefines = p.automation ? new[] { "REV_AUTOMATION" } : Array.Empty<string>()
                });
                if (report == null)
                {
                    throw new InvalidOperationException("Unity没有返回BuildReport；请检查Console和IL2CPP工具链");
                }
                var s = report.summary;
                var result = new Result
                {
                    path = path, target = target.ToString(), backend = backend.ToString(), result = s.result.ToString(),
                    buildId = s.guid.ToString(), unityVersion = Application.unityVersion, development = p.development,
                    automation = p.automation, frameTiming = p.frameTiming, bytes = s.totalSize,
                    errors = (int)s.totalErrors, warnings = (int)s.totalWarnings, seconds = s.totalTime.TotalSeconds,
                    reportPath = Path.Combine(RevAutomationBridge.JobDirectory(request.id), "build-result.json")
                };
                RevAutomationBridge.WriteJson(result.reportPath, result);
                bool success = s.result == BuildResult.Succeeded && s.totalErrors == 0;
                if (success)
                {
                    // 包旁的清单不含凭据，CLI启动时用它核对本次构建；原始Profiler数据不导出。
                    RevAutomationBridge.WriteJson(Path.Combine(Path.GetDirectoryName(path), "rev-build.json"), result);
                }
                return new Completed(result, success);
            }
            finally
            {
                if (webGlUtf8)
                {
                    Environment.SetEnvironmentVariable("PYTHONUTF8", oldPythonUtf8);
                }
                PlayerSettings.SetScriptingBackend(group, before);
                PlayerSettings.enableFrameTimingStats = timingBefore;
                if (androidAutomation)
                {
                    PlayerSettings.Android.forceInternetPermission = internetBefore;
                }
                PlayerSettings.SetIl2CppCompilerConfiguration(group, compilerBefore);
                // BuildPipeline会保存临时设置；只恢复内存值还不够，必须把还原后的设置落盘。
                AssetDatabase.SaveAssets();
            }
        }

        internal static void ValidateOutput(string path, string extension)
        {
            string project = Path.GetFullPath(".");
            string parent = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(parent) || Path.GetPathRoot(path) == parent || !path.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("请指定专用空目录下的完整Player路径及正确扩展名");
            }
            foreach (string name in new[] { "Assets", "Packages", "ProjectSettings", "Library", "Temp", "Logs", ".git" })
            {
                string forbidden = Path.Combine(project, name);
                if (path.Equals(forbidden, StringComparison.OrdinalIgnoreCase) || path.StartsWith(forbidden + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    throw new ArgumentException("构建输出不得写入工程源文件或缓存目录：" + name);
                }
            }
            for (var dir = new DirectoryInfo(parent); dir != null; dir = dir.Parent)
            {
                if (dir.Exists && (dir.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new ArgumentException("构建路径不能经过目录链接");
                }
            }
            if (File.Exists(path) || Directory.Exists(path) || (Directory.Exists(parent) && Directory.EnumerateFileSystemEntries(parent).Any()))
            {
                throw new IOException("构建必须使用新建空目录，不能覆盖旧包或其它文件：" + parent);
            }
        }

        /// <summary>已有真实 BuildReport 的同步构建结果，由桥接立即交付。</summary>
        private sealed class Completed : RevAutomationOperation
        {
            /// <summary>携带构建结果和 Unity 的成功判定。</summary>
            internal Completed(object result, bool success)
            {
                Result = result;
                Success = success;
                Message = success ? "Player构建完成；原构建设置已恢复" : "Player构建失败；检查BuildReport与工具链，禁止盲目重放";
            }
            /// <summary>Player 构建在返回前已经结束。</summary>
            internal override bool CompletedOnReturn => true;

            /// <summary>兼容检查到此对象的调用方，它无需逐帧推进。</summary>
            public override bool Tick()
            {
                return true;
            }
        }
    }
}
