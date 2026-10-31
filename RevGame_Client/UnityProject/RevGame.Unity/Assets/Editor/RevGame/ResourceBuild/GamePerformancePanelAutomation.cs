// 复用项目既有程序集桥接：仅允许调用固定Prefab配方，不接受任意类型或方法名。
using System;
using Revolution.Editor;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace RevGame.Editor
{
    public static class GamePerformancePanelAutomation
    {
        [Serializable] private sealed class ToolchainOptions
        {
            public string path;
        }

        [RevAutomationAction("game.performance.toolchain", Environment = "edit", Keys = new[] { "path" }, Description = "当前Unity进程指定VS2022 Common7/Tools，绕过旧Bee的中文路径解码；空path恢复")]
        public static object Toolchain(RevAutomationRequest request)
        {
            const string saved = "RevGame.Build.VS170Original";
            const string active = "RevGame.Build.VS170Override";
            var options = JsonUtility.FromJson<ToolchainOptions>(request.payloadJson);
            if (string.IsNullOrEmpty(options.path))
            {
                if (SessionState.GetBool(active, false))
                {
                    string original = SessionState.GetString(saved, "");
                    Environment.SetEnvironmentVariable("VS170COMNTOOLS", string.IsNullOrEmpty(original) ? null : original);
                    SessionState.EraseBool(active);
                    SessionState.EraseString(saved);
                }
                return new Result { text = "已恢复当前Unity进程原工具链环境变量" };
            }
            string path = System.IO.Path.GetFullPath(options.path).TrimEnd('\\', '/');
            var tools = new System.IO.DirectoryInfo(path);
            if (!tools.Exists || tools.Name != "Tools" || tools.Parent?.Name != "Common7" || !System.IO.Directory.Exists(System.IO.Path.Combine(tools.Parent.Parent.FullName, "VC", "Tools", "MSVC")))
            {
                throw new ArgumentException("path必须是已安装VS2022的Common7/Tools目录");
            }
            if (!SessionState.GetBool(active, false))
            {
                SessionState.SetString(saved, Environment.GetEnvironmentVariable("VS170COMNTOOLS") ?? "");
                SessionState.SetBool(active, true);
            }
            // 只修改此Unity进程及后续构建子进程的环境，不修改系统全局变量或vswhere/Unity二进制。
            Environment.SetEnvironmentVariable("VS170COMNTOOLS", path);
            return new Result { text = "当前进程VS170COMNTOOLS=" + path };
        }

        [RevAutomationAction("game.performance.panel-layout", Environment = "edit", Keys = new string[0], Description = "调整性能面板高度以完整显示底部环境/负载信息")]
        public static object Layout(RevAutomationRequest request)
        {
            var type = Type.GetType("RevGame.Editor.GamePerformancePanelBuild, Assembly-CSharp-Editor", true);
            return new Result { text = (string)type.GetMethod("AdjustLayout").Invoke(null, null) };
        }

        [RevAutomationAction("game.performance.pc-window", Environment = "edit", Keys = new string[0], Description = "固定Windows默认1920×1080窗口，禁止调整大小及全屏快捷切换")]
        public static object PcWindow(RevAutomationRequest request)
        {
            PlayerSettings.defaultScreenWidth = 1920;
            PlayerSettings.defaultScreenHeight = 1080;
            PlayerSettings.fullScreenMode = FullScreenMode.Windowed;
            PlayerSettings.resizableWindow = false;
            PlayerSettings.allowFullscreenSwitch = false;
            AssetDatabase.SaveAssets();
            return new Result { text = "1920×1080，窗口模式，不可调整大小，不允许全屏切换" };
        }

        [Serializable] private sealed class PlayerPreflight
        {
            public string backend, fontShader, fontShaderPath, fontShaderFlags;
            public bool frameTiming;
            public int removedEditorOnlyShader;
        }

        [RevAutomationAction("game.performance.player-preflight", Environment = "edit", Keys = new string[0], Description = "移除误列入AlwaysIncluded的Editor DontSave字体Shader；记录当前构建设置")]
        public static object Preflight(RevAutomationRequest request)
        {
            var result = new PlayerPreflight
            {
                backend = PlayerSettings.GetScriptingBackend(BuildTargetGroup.Standalone).ToString(),
                frameTiming = PlayerSettings.enableFrameTimingStats
            };
            var settings = new SerializedObject(GraphicsSettings.GetGraphicsSettings());
            SerializedProperty included = settings.FindProperty("m_AlwaysIncludedShaders");
            for (int i = included.arraySize - 1; i >= 0; i--)
            {
                Shader shader = included.GetArrayElementAtIndex(i).objectReferenceValue as Shader;
                if (shader != null && shader.name == "GUI/Text Shader")
                {
                    result.fontShader = shader.name;
                    result.fontShaderPath = AssetDatabase.GetAssetPath(shader);
                    result.fontShaderFlags = shader.hideFlags.ToString();
                    // 只移除不能进入Player的编辑器内部对象引用，不删除字体、字体材质或任何项目Shader。
                    if ((shader.hideFlags & HideFlags.DontSaveInBuild) != 0 && result.fontShaderPath == "Library/unity default resources")
                    {
                        included.GetArrayElementAtIndex(i).objectReferenceValue = null;
                        included.DeleteArrayElementAtIndex(i);
                        result.removedEditorOnlyShader++;
                    }
                }
            }
            settings.ApplyModifiedPropertiesWithoutUndo();
            AssetDatabase.SaveAssets();
            return result;
        }

        [Serializable] private sealed class Result
        {
            public string text;
        }

        [RevAutomationAction("game.performance.configure", Environment = "edit", Keys = new string[0], Description = "制作真机性能面板Prefab并串联项目资源配置")]
        public static object Configure(RevAutomationRequest request)
        {
            var type = Type.GetType("RevGame.Editor.GamePerformancePanelBuild, Assembly-CSharp-Editor", true);
            try
            {
                return new Result { text = (string)type.GetMethod("Configure").Invoke(null, null) };
            }
            catch (System.Reflection.TargetInvocationException error)
            {
                throw error.InnerException ?? error;
            }
        }
    }
}
