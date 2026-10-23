// Revolution.Editor 非 autoReferenced；此文件留在显式引用它的项目工具程序集。
// Unity asmdef 不能引用预定义 Assembly-CSharp-Editor，因此只桥接五个固定项目方法，不开放任意类型/反射执行。
using System;
using System.Reflection;
using Revolution.Editor;
using UnityEditor;
using UnityEngine;

namespace RevGame.Editor
{
    public static class GameChapterBackgroundAutomation
    {
        [Serializable] private sealed class Options { public string target = "StandaloneWindows64"; }
        [Serializable] private sealed class ModeOptions { public bool enabled; }
        [Serializable] private sealed class BuildResult { public string text; }

        private static object Invoke(string method)
        {
            Type type = Type.GetType("RevGame.Editor.GameChapterBackgroundTools, Assembly-CSharp-Editor", true);
            MethodInfo entry = type.GetMethod(method, BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
            if (entry == null) { throw new MissingMethodException(type.FullName, method); }
            try { return entry.Invoke(null, null); }
            catch (TargetInvocationException error) { throw error.InnerException ?? error; }
        }

        [RevAutomationAction("game.background.import", Keys = new string[0], Description = "导入指定五张原图并配置按需分包与场景 Inspector")]
        public static object Import(RevAutomationRequest request) { return Invoke("Import"); }

        [RevAutomationAction("game.background.audit", Environment = "either", Keys = new string[0], Description = "核对背景原图字节、导入设置与 Shader")]
        public static object Audit(RevAutomationRequest request) { return Invoke("Audit"); }

        [RevAutomationAction("game.background.status", Environment = "either", Keys = new string[0], Description = "读取最近背景验收结果")]
        public static object Status(RevAutomationRequest request) { return Invoke("Status"); }

        [RevAutomationAction("game.background.mode", Keys = new[] { "enabled" }, Description = "仅在编辑态保存下次Play的资源模式，不提前初始化运行时单例")]
        public static object Mode(RevAutomationRequest request)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) { throw new InvalidOperationException("请先完整退出Play再切换模式"); }
            bool enabled = JsonUtility.FromJson<ModeOptions>(request.payloadJson).enabled;
            // 当前框架属性setter会在编辑态Init并创建DontDestroyOnLoad宿主，因此这里仅保存已核对的同一EditorPrefs键。
            // 不在项目内修改框架快照；策略仍由下一次Play的唯一RevInitialize入口正常装配。
            EditorPrefs.SetBool("Revolution.Res.UseABInEditor", enabled);
            return new BuildResult { text = "下次Play UseABInEditor=" + enabled };
        }

        [RevAutomationAction("game.background.build", Keys = new[] { "target" }, Description = "通过项目资源入口构建并发布 PC 或 Android AB")]
        public static object Build(RevAutomationRequest request)
        {
            Options options = JsonUtility.FromJson<Options>(request.payloadJson);
            BuildTarget target;
            if (options.target == "StandaloneWindows64" || options.target == "PC") { target = BuildTarget.StandaloneWindows64; }
            else if (options.target == "Android") { target = BuildTarget.Android; }
            else { throw new ArgumentException("只允许 PC/StandaloneWindows64 或 Android"); }
            return new BuildResult { text = GameResourceBuild.BuildAndCopy(target) };
        }

        [RevAutomationAction("game.background.check", Environment = "play", Keys = new string[0], Description = "真实流程入场、六章切图、GPU 动态与租约验收")]
        public static object Check(RevAutomationRequest request)
        {
            if ((bool)Invoke("IsChecking")) { throw new InvalidOperationException("已有背景验收在运行"); }
            Invoke("BeginCheck");
            return new CheckOperation();
        }

        private sealed class CheckOperation : RevAutomationOperation
        {
            public override bool Tick()
            {
                if ((bool)Invoke("IsChecking")) { return false; }
                Result = Invoke("Status");
                string text = (string)Result.GetType().GetField("text").GetValue(Result);
                Success = text.StartsWith("通过", StringComparison.Ordinal);
                Message = text;
                return true;
            }
        }
    }
}
