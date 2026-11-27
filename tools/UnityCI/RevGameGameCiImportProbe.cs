using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace RevGame.Editor
{
    /// <summary>GameCI 在临时检出中运行的导入探针。通过后将证据留在本次 Library 内。</summary>
    public static class RevGameGameCiImportProbe
    {
        /// <summary>资格验证要求使用的国际版编辑器版本。</summary>
        private const string EditorVersion = "2022.3.15f1";

        /// <summary>RevGame 实际的启动场景，由本轮 AssetDatabase 加载。</summary>
        private const string StartScene = "Assets/Scenes/StartScene.unity";

        /// <summary>编译后核对业务类型和启动场景。遇到错误抛出异常，让 GameCI 返回失败。</summary>
        public static void Verify()
        {
            if (Application.unityVersion != EditorVersion || EditorUtility.scriptCompilationFailed || EditorApplication.isCompiling)
            {
                throw new InvalidOperationException($"编辑器版本或编译状态未通过导入检查。实际版本 {Application.unityVersion}");
            }

            // 验证脚本域中已有真实业务程序集，避免仅凭编辑器启动成功判定导入完成。
            bool gameCompiled = AppDomain.CurrentDomain.GetAssemblies()
                .Any(assembly => assembly.GetType("RevGame.Flow.GameFlow") != null);
            if (!gameCompiled || AssetDatabase.LoadAssetAtPath<SceneAsset>(StartScene) == null)
            {
                throw new InvalidOperationException("未能加载 RevGame 的业务类型或启动场景。");
            }

            // 只在全部断言通过后写回执。宿主与容器共享工程目录，可以独立核对证据。
            string library = Path.Combine(Path.GetDirectoryName(Application.dataPath), "Library");
            File.WriteAllLines(Path.Combine(library, "RevGameGameCiImport.txt"),
                new[] { Application.unityVersion, AssetDatabase.AssetPathToGUID(StartScene) });
        }
    }
}
