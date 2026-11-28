using System;
using System.IO;
using System.Linq;
using Revolution.Editor;
using UnityEditor;
using UnityEngine;

namespace RevGame.Editor
{
    /// <summary>GameCI 专用的正式出包入口。使用检出中已验证的目标平台 AB，调用原有 Player 构建。</summary>
    public static class RevGameGameCiRelease
    {
        /// <summary>测试版产品名称，用于桌面、本地数据目录和 WebGL 启动配置。</summary>
        private const string TestProductName = "围棋大作战test";

        /// <summary>测试版应用标识，用于 Unity Player 设置及 Android 安装身份。</summary>
        private const string TestApplicationId = "com.Tangmen.RevGameTest";

        /// <summary>核对国际版编辑器、脚本编译状态和本轮已激活的出包平台。</summary>
        private static BuildTarget RequireImportedTarget()
        {
            if (!Application.isBatchMode || Application.unityVersion != "2022.3.15f1" ||
                EditorUtility.scriptCompilationFailed)
            {
                throw new InvalidOperationException("需要国际版 Unity 2022.3.15f1 的 GameCI 批处理环境。");
            }

            BuildTarget target = EditorUserBuildSettings.activeBuildTarget;
            if (target != BuildTarget.StandaloneWindows64 && target != BuildTarget.Android && target != BuildTarget.WebGL)
            {
                throw new InvalidOperationException($"GameCI 没有选中 RevGame 的出包平台 {target}。");
            }
            return target;
        }

        /// <summary>使用正式包的同一导入环境生成回执，供宿主验收并保存导入缓存。</summary>
        public static void VerifyImport()
        {
            BuildTarget target = RequireImportedTarget();
            const string startScene = "Assets/Scenes/StartScene.unity";
            if (EditorApplication.isCompiling || AssetDatabase.LoadAssetAtPath<SceneAsset>(startScene) == null ||
                !AppDomain.CurrentDomain.GetAssemblies().Any(assembly => assembly.GetType("RevGame.Flow.GameFlow") != null))
            {
                throw new InvalidOperationException("未完成业务脚本编译与启动场景导入。");
            }
            string library = Path.Combine(Path.GetDirectoryName(Application.dataPath), "Library");
            File.WriteAllLines(Path.Combine(library, "RevGameGameCiImport.txt"),
                new[] { Application.unityVersion, target.ToString(), AssetDatabase.AssetPathToGUID(startScene) });
        }

        /// <summary>使用托管 runner 上已经导入的目标平台，真实构建失败时向 GameCI 抛出异常。</summary>
        public static void Build()
        {
            BuildTarget target = RequireImportedTarget();

            // 激活由容器完成，子构建进程无需继承 Unity 账号凭据。
            Environment.SetEnvironmentVariable("UNITY_EMAIL", null);
            Environment.SetEnvironmentVariable("UNITY_PASSWORD", null);
            Environment.SetEnvironmentVariable("UNITY_SERIAL", null);
            bool testPackage = Environment.GetCommandLineArgs().Contains("-revGameCiTestPackage");
            bool android = target == BuildTarget.Android;
            string selectedPlatform = target == BuildTarget.StandaloneWindows64 ? "PC" : target.ToString();
            if (new[] { "PC", "Android", "WebGL" }.Any(platform => platform != selectedPlatform &&
                Directory.Exists(Path.Combine(Application.streamingAssetsPath, platform))))
            {
                throw new InvalidOperationException("检测到未隔离的其它平台 StreamingAssets。");
            }
            string output = ReadArgument("-customBuildPath");
            if (!android)
            {
                BuildPlayer(target, output, testPackage);
                return;
            }

            // 沿用正式 ARM64 APK 的平台设置，GameCI 将签名参数传给当前进程。
            string project = Path.GetDirectoryName(Application.dataPath);
            string name = ReadArgument("-androidKeystoreName");
            if (name != "revgame-gameci-release.keystore")
            {
                throw new InvalidOperationException("GameCI Android 签名文件名不符。");
            }
            string key = Path.Combine(project, name);
            if (!File.Exists(key))
            {
                string encoded = Environment.GetEnvironmentVariable("ANDROID_KEYSTORE_BASE64");
                if (string.IsNullOrWhiteSpace(encoded))
                {
                    throw new InvalidOperationException("GameCI 没有传入本次 Android 发布密钥库。");
                }
                File.WriteAllBytes(key, Convert.FromBase64String(encoded));
            }
            Environment.SetEnvironmentVariable("ANDROID_KEYSTORE_BASE64", null);
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
            // 国际版 2022.3.15f1 的枚举没有 API 33 的成员名，Unity 设置保存的是 API 数值。
            PlayerSettings.Android.targetSdkVersion = (AndroidSdkVersions)33;
            PlayerSettings.Android.forceInternetPermission = true;
            PlayerSettings.defaultInterfaceOrientation = UIOrientation.LandscapeLeft;
            PlayerSettings.defaultScreenWidth = 1920;
            PlayerSettings.defaultScreenHeight = 1080;
            EditorUserBuildSettings.buildAppBundle = false;
            EditorUserBuildSettings.exportAsGoogleAndroidProject = false;
            try
            {
                PlayerSettings.Android.useCustomKeystore = true;
                PlayerSettings.Android.keystoreName = key;
                PlayerSettings.Android.keystorePass = ReadArgument("-androidKeystorePass");
                PlayerSettings.Android.keyaliasName = ReadArgument("-androidKeyaliasName");
                PlayerSettings.Android.keyaliasPass = ReadArgument("-androidKeyaliasPass");
                BuildPlayer(target, output, testPackage);
            }
            finally
            {
                // 密码只留在本轮 Unity 进程，Player 构建终止后清除设置中的引用。
                PlayerSettings.Android.keystorePass = string.Empty;
                PlayerSettings.Android.keyaliasPass = string.Empty;
                PlayerSettings.Android.keystoreName = string.Empty;
                PlayerSettings.Android.keyaliasName = string.Empty;
                PlayerSettings.Android.useCustomKeystore = false;
                AssetDatabase.SaveAssets();
            }
        }

        /// <summary>用 Revolution 的 IL2CPP 入口构建 Player，临时应用 Android 和测试版各自的产品身份。</summary>
        private static void BuildPlayer(BuildTarget target, string output, bool testPackage)
        {
            if (target == BuildTarget.StandaloneWindows64 && !testPackage)
            {
                RunPlayerBuild(target, output, testPackage);
                return;
            }

            BuildTargetGroup group = BuildPipeline.GetBuildTargetGroup(target);
            string previousProductName = PlayerSettings.productName;
            bool hasApplicationId = target != BuildTarget.WebGL;
            string previousApplicationId = hasApplicationId ? PlayerSettings.GetApplicationIdentifier(group) : null;
            string applicationId = testPackage ? TestApplicationId : "com.Tangmen.RevGame";
            try
            {
                // 网页通过启动配置的产品名称识别测试包，应用安装标识只用于原生 Player。
                PlayerSettings.productName = testPackage ? TestProductName : "围棋大作战";
                if (hasApplicationId)
                {
                    PlayerSettings.SetApplicationIdentifier(group, applicationId);
                    if (PlayerSettings.GetApplicationIdentifier(group) != applicationId)
                    {
                        throw new InvalidOperationException($"Unity 未接受本轮应用 ID {applicationId}。");
                    }
                }
                RunPlayerBuild(target, output, testPackage);
            }
            finally
            {
                PlayerSettings.productName = previousProductName;
                if (hasApplicationId)
                {
                    PlayerSettings.SetApplicationIdentifier(group, previousApplicationId);
                }
                AssetDatabase.SaveAssets();
            }
        }

        /// <summary>以同步 BuildReport 判断出包结果。测试版开启自动化桥和帧时采样，继续采用正式 IL2CPP 后端。</summary>
        private static void RunPlayerBuild(BuildTarget target, string output, bool testPackage)
        {
            if (RevAutomationActions.Execute(new RevAutomationRequest
            {
                id = Guid.NewGuid().ToString("N"),
                action = "build.player",
                payloadJson = JsonUtility.ToJson(new PlayerRequest
                {
                    target = target.ToString(),
                    path = output,
                    automation = testPackage,
                    frameTiming = testPackage
                })
            }) is not RevAutomationOperation operation)
            {
                throw new InvalidOperationException("Revolution 没有返回本轮 Player 构建结果。");
            }
            using (operation)
            {
                if (!operation.Tick() || !operation.Success)
                {
                    throw new InvalidOperationException("Revolution IL2CPP Player 构建失败，请查看 Unity BuildReport。");
                }
            }
        }

        /// <summary>读取 GameCI 明确传入的参数，报错仅包含参数名。</summary>
        private static string ReadArgument(string name)
        {
            string[] arguments = Environment.GetCommandLineArgs();
            int index = Array.IndexOf(arguments, name) + 1;
            if (index == 0 || index >= arguments.Length || string.IsNullOrWhiteSpace(arguments[index]))
            {
                throw new InvalidOperationException($"缺少 GameCI 参数 {name}。");
            }
            return arguments[index];
        }

        /// <summary>将平台、输出位置和按包类型启用的测试能力交给 Revolution 构建入口。</summary>
        [Serializable]
        private sealed class PlayerRequest
        {
            /// <summary>Editor 已经切换成功的构建目标。</summary>
            public string target;

            /// <summary>GameCI 挂载的本轮 Player 输出绝对路径。</summary>
            public string path;

            /// <summary>正式包固定使用 IL2CPP。</summary>
            public string backend = "il2cpp";

            /// <summary>加入 REV_AUTOMATION，使 Player 通过显式启动参数接受测试。</summary>
            public bool automation;

            /// <summary>启用 Unity 帧时统计，为性能自动化提供采样。</summary>
            public bool frameTiming;
        }
    }
}
