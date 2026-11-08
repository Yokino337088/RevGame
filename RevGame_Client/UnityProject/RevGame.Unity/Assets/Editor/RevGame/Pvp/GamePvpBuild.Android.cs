using System;
using System.IO;
using System.Linq;
using RevGame.Pvp;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.SceneManagement;

namespace RevGame.Editor
{
    public static partial class GamePvpBuild
    {
        // Unity 2022 的安卓 Mono Player 只有 ARMv7。目录和导入器都明确限定对应 ABI。
        private const string AndroidDuelLibrary = "Assets/Plugins/Android/libs/armeabi-v7a/libRevGameCore.so";
        private const string AndroidReleaseLibrary = "Assets/Plugins/Android/libs/arm64-v8a/libRevGameCore.so";
        private const string ServerMenuPrefab = "Assets/GameRes/UI/Panel/Pvp/Menu/PvpServerPanel.prefab";

        // 异步切换平台。调用端通过编辑器状态等待重导入和编译结束，不依赖磁盘工单。
        public static string SelectAndroidDuelTarget()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode ||
                !BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.Android, BuildTarget.Android))
            {
                throw new InvalidOperationException("需要退出 Play 并安装 Android Build Support。");
            }
            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android &&
                !EditorUserBuildSettings.SwitchActiveBuildTargetAsync(BuildTargetGroup.Android, BuildTarget.Android))
            {
                throw new InvalidOperationException("无法启动 Android 平台切换。");
            }
            return "Android 平台切换已受理。";
        }

        // ASCII 路径的独立构建副本通过 Unity 命令行调用。输出留在副本内，验包后由调用方复制。
        public static void BuildAndroidDuelTestBatch()
        {
            if (!Application.isBatchMode)
            {
                throw new InvalidOperationException("仅供独立工程的 Unity 批处理构建。");
            }
            EditorUserBuildSettings.buildAppBundle = false;
            Debug.Log(BuildAndroidDuelTest("Builds/AndroidDuelTest/Player/RevGame-Android-Mono-DuelTest.apk"));
        }

        /// <summary>在 ASCII 构建副本中导出正式 ARM64 工程，Gradle 负责打包，签名凭据留在工程外。</summary>
        public static void ExportAndroidReleaseBatch()
        {
            if (!Application.isBatchMode || EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android ||
                Directory.Exists("Assets/StreamingAssets/PC") || !File.Exists("Assets/StreamingAssets/Android/Android"))
            {
                throw new InvalidOperationException("请使用仅含 Android AB 的独立 Android 构建副本。");
            }

            var nativeCore = AssetImporter.GetAtPath(AndroidReleaseLibrary) as PluginImporter;
            if (nativeCore == null || !nativeCore.GetCompatibleWithPlatform(BuildTarget.Android) ||
                nativeCore.GetPlatformData(BuildTarget.Android, "CPU") != "ARM64")
            {
                throw new InvalidOperationException("缺少 ARM64 决斗原生库或其导入配置错误。");
            }

            string[] scenes = EditorBuildSettings.scenes.Where(scene => scene.enabled).Select(scene => scene.path).ToArray();
            if (!scenes.Contains("Assets/Scenes/StartScene.unity") || !scenes.Contains("Assets/Scenes/PvpDuelScene.unity"))
            {
                throw new InvalidOperationException("Android 构建场景列表缺少大厅或决斗。");
            }

            // 副本中只启用 ARM64，避免 APK 同时装入两套引擎和决斗库。
            PlayerSettings.SetScriptingBackend(BuildTargetGroup.Android, ScriptingImplementation.IL2CPP);
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
            PlayerSettings.Android.targetSdkVersion = AndroidSdkVersions.AndroidApiLevelAuto;
            PlayerSettings.Android.useCustomKeystore = false;
            PlayerSettings.Android.forceInternetPermission = true;
            PlayerSettings.defaultInterfaceOrientation = UIOrientation.LandscapeLeft;
            PlayerSettings.defaultScreenWidth = 1920;
            PlayerSettings.defaultScreenHeight = 1080;
            EditorUserBuildSettings.buildAppBundle = false;
            EditorUserBuildSettings.exportAsGoogleAndroidProject = true;

            BuildReport report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = scenes,
                target = BuildTarget.Android,
                locationPathName = "Builds/AndroidRelease/Gradle",
                options = BuildOptions.AcceptExternalModificationsToPlayer | BuildOptions.CompressWithLz4HC | BuildOptions.StrictMode
            });
            var summary = report.summary;
            Debug.Log($"Android release export {summary.result}, errors={summary.totalErrors}, warnings={summary.totalWarnings}");
            if (summary.result != BuildResult.Succeeded || summary.totalErrors != 0)
            {
                throw new InvalidOperationException("Android 正式包的 Unity 导出失败。");
            }
        }

        // 固定构建范围是仓库的测试包目录。出包之前验证 Android 资源完整且 PC 目录已隔离。
        public static string BuildAndroidDuelTest(string requestedPath)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android ||
                Enumerable.Range(0, SceneManager.sceneCount).Any(i => SceneManager.GetSceneAt(i).isDirty))
            {
                throw new InvalidOperationException("请保存场景、退出 Play 并完成 Android 平台切换。");
            }
            if (Directory.Exists("Assets/StreamingAssets/PC") || !File.Exists("Assets/StreamingAssets/Android/Android") ||
                PlayerSettings.Android.targetArchitectures != AndroidArchitecture.ARMv7 ||
                !PlayerSettings.Android.forceInternetPermission || EditorUserBuildSettings.buildAppBundle)
            {
                throw new InvalidOperationException("安卓资源、ARMv7、APK 或网络权限尚未装配，或者 PC 资源仍在首包目录。");
            }
            string path = Path.GetFullPath(requestedPath);
            string allowed = Path.GetFullPath("../../../打包测试(不入库)") + Path.DirectorySeparatorChar;
            string localOutput = Path.GetFullPath("Builds/AndroidDuelTest") + Path.DirectorySeparatorChar;
            string parent = Path.GetDirectoryName(path);
            if ((!parent.StartsWith(allowed, StringComparison.OrdinalIgnoreCase) &&
                 !parent.StartsWith(localOutput, StringComparison.OrdinalIgnoreCase)) ||
                !path.EndsWith(".apk", StringComparison.OrdinalIgnoreCase) ||
                (Directory.Exists(parent) && Directory.EnumerateFileSystemEntries(parent).Any()))
            {
                throw new ArgumentException("请指定测试包目录下的专用空子目录与 APK 路径。", nameof(requestedPath));
            }
            string[] scenes = EditorBuildSettings.scenes.Where(scene => scene.enabled).Select(scene => scene.path).ToArray();
            if (!scenes.Contains("Assets/Scenes/StartScene.unity") || !scenes.Contains("Assets/Scenes/PvpDuelScene.unity"))
            {
                throw new InvalidOperationException("Android 构建场景列表缺少大厅或决斗。");
            }

            Directory.CreateDirectory(parent);
            ScriptingImplementation previous = PlayerSettings.GetScriptingBackend(BuildTargetGroup.Android);
            UIOrientation previousOrientation = PlayerSettings.defaultInterfaceOrientation;
            int previousWidth = PlayerSettings.defaultScreenWidth;
            int previousHeight = PlayerSettings.defaultScreenHeight;
            try
            {
                PlayerSettings.SetScriptingBackend(BuildTargetGroup.Android, ScriptingImplementation.Mono2x);
                PlayerSettings.defaultInterfaceOrientation = UIOrientation.LandscapeLeft;
                PlayerSettings.defaultScreenWidth = 1920;
                PlayerSettings.defaultScreenHeight = 1080;
                BuildReport report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
                {
                    scenes = scenes,
                    target = BuildTarget.Android,
                    locationPathName = path,
                    options = BuildOptions.Development
                });
                var summary = report.summary;
                var result = new AndroidDuelBuildResult { path = path, result = summary.result.ToString(),
                    bytes = (long)summary.totalSize, errors = (int)summary.totalErrors, warnings = (int)summary.totalWarnings };
                string json = JsonUtility.ToJson(result, true);
                File.WriteAllText(Path.Combine(parent, "rev-build.json"), json);
                if (summary.result != BuildResult.Succeeded || summary.totalErrors != 0)
                {
                    throw new InvalidOperationException("安卓测试包构建失败，查看 Unity Console 与 rev-build.json。");
                }
                return json;
            }
            finally
            {
                PlayerSettings.SetScriptingBackend(BuildTargetGroup.Android, previous);
                PlayerSettings.defaultInterfaceOrientation = previousOrientation;
                PlayerSettings.defaultScreenWidth = previousWidth;
                PlayerSettings.defaultScreenHeight = previousHeight;
                AssetDatabase.SaveAssets();
            }
        }

        // 旁车清单用于复核真实 BuildReport 的平台、后端、体积和错误数。
        [Serializable]
        private sealed class AndroidDuelBuildResult
        {
            public string target = "Android";
            public string backend = "Mono2x";
            public bool development = true;
            public string path;
            public string result;
            public long bytes;
            public int errors;
            public int warnings;
        }

        // 装配测试包需要的原生库和连接面板默认地址。只更新已有 Prefab 字段，保留 GUID 和布局。
        public static string PrepareAndroidDuelTest()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode ||
                PlayerSettings.Android.targetArchitectures != AndroidArchitecture.ARMv7)
            {
                throw new InvalidOperationException("请在编辑态使用 ARMv7 构建安卓 Mono 决斗测试包。");
            }
            var plugin = AssetImporter.GetAtPath(AndroidDuelLibrary) as PluginImporter ??
                throw new InvalidOperationException("缺少 ARMv7 原生决斗库。");
            if (plugin.GetCompatibleWithAnyPlatform() || plugin.GetCompatibleWithEditor() ||
                !plugin.GetCompatibleWithPlatform(BuildTarget.Android) || plugin.GetPlatformData(BuildTarget.Android, "CPU") != "ARMv7")
            {
                plugin.SetCompatibleWithAnyPlatform(false);
                plugin.SetCompatibleWithEditor(false);
                plugin.SetCompatibleWithPlatform(BuildTarget.Android, true);
                plugin.SetPlatformData(BuildTarget.Android, "CPU", "ARMv7");
                plugin.SaveAndReimport();
            }

            GameObject root = PrefabUtility.LoadPrefabContents(ServerMenuPrefab);
            try
            {
                InputField endpoint = root.transform.Find("endpoint").GetComponent<InputField>();
                if (endpoint.text != PvpServerPanel.DefaultEndpoint)
                {
                    endpoint.SetTextWithoutNotify(PvpServerPanel.DefaultEndpoint);
                    PrefabUtility.SaveAsPrefabAsset(root, ServerMenuPrefab, out bool saved);
                    if (!saved)
                    {
                        throw new InvalidOperationException("服务器地址预设保存失败。");
                    }
                }
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }

            // Fantasy 和 UTP 都用原生网络，不依赖 Unity 自动从业务脚本推断网络权限。
            PlayerSettings.Android.forceInternetPermission = true;
            EditorUserBuildSettings.buildAppBundle = false;
            AssetDatabase.SaveAssets();
            return "ARMv7 原生插件、网络权限和局域网默认地址已装配，输出 APK。";
        }
    }
}
