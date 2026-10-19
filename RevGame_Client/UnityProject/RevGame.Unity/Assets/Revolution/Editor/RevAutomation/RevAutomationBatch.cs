// RevAutomationBatch.cs —— Unity -executeMethod 入口；由桥接写响应并统一设置退出码。
using System;
using System.IO;
using UnityEngine;

namespace Revolution.Editor
{
    // ============================================================================
    //  RevAutomationBatch —— 批处理模式入口（给 CI 用）
    //
    //  正常模式：Unity 开着，外部写文件 → 编辑器轮询执行。
    //  批处理模式：不用开 Unity，CI 直接调
    //      Unity.exe -batchmode -projectPath <工程> -executeMethod Revolution.Editor.RevAutomationBatch.Execute
    //                -revAutomationRequest <请求JSON> -logFile <unity.log>
    //  它读完请求就交给 Bridge（逻辑完全复用），执行完自动以 0/1 退出码退出进程。
    //
    //  ⚠️ 批处理里**没有**交互能力：play/stop、gm.*、scenario.run、菜单都要用在线编辑器。
    // ============================================================================

    public static class RevAutomationBatch
    {
        public static void Execute()
        {
            string[] args = Environment.GetCommandLineArgs();
            int index = Array.IndexOf(args, "-revAutomationRequest");
            if (index < 0 || index + 1 >= args.Length) { throw new ArgumentException("缺少 -revAutomationRequest <JSON路径>"); }
            var request = JsonUtility.FromJson<RevAutomationRequest>(File.ReadAllText(args[index + 1]));
            RevAutomationBridge.Begin(request, true);
        }
    }
}
