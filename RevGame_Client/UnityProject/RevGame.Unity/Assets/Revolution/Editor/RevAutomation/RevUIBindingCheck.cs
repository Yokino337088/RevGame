// RevUIBindingCheck.cs — 导入后在开窗之前检查实际预制体接线，沿用 RevUI 的运行时规则。
using System;
using UnityEditor;
using UnityEngine;

namespace Revolution.Editor
{
    /// <summary>Editor 的只读 UI 接线前置检查。</summary>
    public static class RevUIBindingCheck
    {
        /// <summary>待检查预制体的绝对逻辑路径。</summary>
        [Serializable]
        public sealed class Input
        {
            /// <summary>Assets 下预制体路径。</summary>
            public string prefab;
        }

        /// <summary>引用预制体 GUID 以及运行时字段接线错误。</summary>
        [Serializable]
        public sealed class Result
        {
            /// <summary>用于定位实际检查的资产。</summary>
            public string guid;
            /// <summary>为空代表所有必需的字段均可绑定。</summary>
            public string[] errors;
        }

        /// <summary>复用 RevUI 绑定计划查找节点，不实例化预制体。</summary>
        [RevAutomationAction("ui.binding-check", Description = "检查预制体上 RevBind 字段是否匹配真实节点与组件",
            ReadOnly = true, InputType = typeof(Input), OutputType = typeof(Result))]
        public static Result Check(RevAutomationRequest request)
        {
            Input input = JsonUtility.FromJson<Input>(request.payloadJson);
            RevAutomationActions.RequireAssetPath(input.prefab);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(input.prefab);
            if (prefab == null || !input.prefab.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("指定的预制体不存在：" + input.prefab);
            }
            return new Result { guid = AssetDatabase.AssetPathToGUID(input.prefab), errors = RevUI.CheckPrefabBindings(prefab) };
        }
    }
}
