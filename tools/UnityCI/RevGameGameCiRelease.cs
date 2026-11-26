using System;
using System.IO;
using Revolution.Editor;
using UnityEditor;
using UnityEngine;

namespace RevGame.Editor
{
    /// <summary>GameCI 专用的正式出包入口。在同一已经激活的 Unity 进程内重建 AB 并调用原有 Player 构建。</summary>
    public static class RevGameGameCiRelease
    {
        /// <summary>使用托管 runner 上已经导入的目标平台，真实构建失败时向 GameCI 抛出异常。</summary>
        public static void Build()
        {
            if (!Application.isBatchMode || Application.unityVersion != "2022.3.15f1" ||
                EditorUtility.scriptCompilationFailed)
            {
                throw new InvalidOperationException("需要国际版 Unity 2022.3.15f1 的 GameCI 批处理环境。");
            }

            BuildTarget target = EditorUserBuildSettings.activeBuildTarget;
            if (target != BuildTarget.StandaloneWindows64 && target != BuildTarget.Android)
            {
                throw new InvalidOperationException($"GameCI 没有选中 RevGame 的出包平台 {target}。");
            }

            // 激活由容器完成，子构建进程无需继承 Unity 账号凭据。
            Environment.SetEnvironmentVariable("UNITY_EMAIL", null);
            Environment.SetEnvironmentVariable("UNITY_PASSWORD", null);
            Environment.SetEnvironmentVariable("UNITY_SERIAL", null);
            bool android = target == BuildTarget.Android;
            string other = android ? "PC" : "Android";
            if (Directory.Exists(Path.Combine(Application.streamingAssetsPath, other)))
            {
                throw new InvalidOperationException("检测到未隔离的其它平台 StreamingAssets。");
            }
            Debug.Log(GameResourceBuild.BuildAndCopy(target));

            string output = ReadArgument("-customBuildPath");
            if (!android)
            {
                BuildPlayer(target, output);
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
            string previousProductName = PlayerSettings.productName;
            string previousApplicationId = PlayerSettings.GetApplicationIdentifier(BuildTargetGroup.Android);
            try
            {
                // 安装到手机时使用明确的桌面名称和应用 ID，设置只作用于本次 Android 构建。
                PlayerSettings.productName = "围棋大作战";
                PlayerSettings.SetApplicationIdentifier(BuildTargetGroup.Android, "com.Tangmen.RevGame");
                PlayerSettings.Android.useCustomKeystore = true;
                PlayerSettings.Android.keystoreName = key;
                PlayerSettings.Android.keystorePass = ReadArgument("-androidKeystorePass");
                PlayerSettings.Android.keyaliasName = ReadArgument("-androidKeyaliasName");
                PlayerSettings.Android.keyaliasPass = ReadArgument("-androidKeyaliasPass");
                BuildPlayer(target, output);
            }
            finally
            {
                // 密码只留在本轮 Unity 进程，Player 构建终止后清除设置中的引用。
                PlayerSettings.Android.keystorePass = string.Empty;
                PlayerSettings.Android.keyaliasPass = string.Empty;
                PlayerSettings.Android.keystoreName = string.Empty;
                PlayerSettings.Android.keyaliasName = string.Empty;
                PlayerSettings.Android.useCustomKeystore = false;
                // 成品已记录手机端身份，随后归还临时检出中的项目设置。
                PlayerSettings.productName = previousProductName;
                PlayerSettings.SetApplicationIdentifier(BuildTargetGroup.Android, previousApplicationId);
                AssetDatabase.SaveAssets();
            }
        }

        /// <summary>使用 Revolution 正式 Player 的 IL2CPP 入口，并按同步结果判断成功。</summary>
        private static void BuildPlayer(BuildTarget target, string output)
        {
            if (RevAutomationActions.Execute(new RevAutomationRequest
            {
                id = Guid.NewGuid().ToString("N"),
                action = "build.player",
                payloadJson = JsonUtility.ToJson(new PlayerRequest { target = target.ToString(), path = output })
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

        /// <summary>只读目标和输出位置，其余字段使用 Revolution 的正式包默认值。</summary>
        [Serializable]
        private sealed class PlayerRequest
        {
            /// <summary>Editor 已经切换成功的构建目标。</summary>
            public string target;

            /// <summary>GameCI 挂载的本轮 Player 输出绝对路径。</summary>
            public string path;

            /// <summary>正式包固定使用 IL2CPP。</summary>
            public string backend = "il2cpp";
        }
    }
}
