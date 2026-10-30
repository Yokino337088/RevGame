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
        [RevAutomationAction("game.performance.panel-layout", Environment = "edit", Keys = new string[0], Description = "调整性能面板高度以完整显示底部环境/负载信息")]
        public static object Layout(RevAutomationRequest request)
        {
            var type = Type.GetType("RevGame.Editor.GamePerformancePanelBuild, Assembly-CSharp-Editor", true);
            return new Result { text = (string)type.GetMethod("AdjustLayout").Invoke(null, null) };
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
