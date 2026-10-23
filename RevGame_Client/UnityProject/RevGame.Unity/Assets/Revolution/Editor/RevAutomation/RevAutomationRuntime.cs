// ============================================================
// RevAutomationRuntime —— Editor 工单到游戏注册表的轻量适配层。
// 【为什么】Editor 与 Player 必须测试同一份业务；这里不复制 UI/截图实现。
// 【生命周期】运行时跨帧操作由 Editor 每帧推进，取消与 Dispose 一起转发。
// ============================================================
// Editor 与开发版 Player 共用运行时注册表，避免 UI/状态执行逻辑出现两套行为。
using System;
using UnityEngine;
// 适配层只负责把 Editor 工单转换为运行时调用。UI、截图、GM 不在这里再实现一份，
// 否则 Editor 测试通过而 Player 行为不同，很难知道到底验证了哪套业务。
namespace Revolution.Editor
{
    public static class RevAutomationRuntime
    {
        [Serializable] private sealed class StartOptions { public int port = 18766; }
        [Serializable] private sealed class Endpoint { public string endpointFile; }
        private static object Wrap(object result) => result is RevRuntimeOperation operation ? new Adapter(operation) : result;
        private static object Invoke(RevAutomationRequest request) => Wrap(RevRuntimeAutomation.Invoke(request.action, request.payloadJson ?? "{}", request.confirm));
        private sealed class Adapter : RevAutomationOperation
        {
            private readonly RevRuntimeOperation _operation;
            public Adapter(RevRuntimeOperation operation) { _operation = operation; }
            public override bool Tick() { bool done = _operation.Tick(); Success = _operation.Success; Message = _operation.Message; Result = _operation.Result; return done; }
            public override void Cancel() { _operation.Cancel(); Success = _operation.Success; Message = _operation.Message; Result = _operation.Result; }
            public override void Dispose() { _operation.Dispose(); }
        }
        [RevAutomationAction("runtime.start", Environment = "play", Keys = new[] { "port" }, Description = "显式启动开发态本机 HTTP 服务；返回凭据文件路径")]
        public static object Start(RevAutomationRequest request) => new Endpoint { endpointFile = RevRuntimeAutomation.Start(JsonUtility.FromJson<StartOptions>(request.payloadJson ?? "{}").port) };
        [RevAutomationAction("runtime.stop", Environment = "play", Keys = new string[0], Description = "停止运行时 HTTP 服务并取消其未完成工单")]
        public static object Stop(RevAutomationRequest request) { RevRuntimeAutomation.Stop(); return new RevAutomationActions.RevAutomationText("运行时服务已停止"); }
        [RevAutomationAction("runtime.capabilities", Environment = "play", Keys = new string[0], Description = "游戏本局实际注册的 GM 参数契约与自定义能力")]
        public static object Capabilities(RevAutomationRequest request) => RevRuntimeAutomation.Capabilities();
        [RevAutomationAction("runtime.call", Environment = "play", Keys = new[] { "runtimeAction", "argumentsJson" }, Description = "调用显式注册的游戏能力；参数由其 JSON Schema 验证")]
        public static object Call(RevAutomationRequest request)
        {
            var p = RevAutomationEditorOptions.Read(request);
            return Wrap(RevRuntimeAutomation.Invoke(p.runtimeAction, p.argumentsJson ?? "{}", request.confirm));
        }
        [RevAutomationAction("runtime.state", Environment = "play", Keys = new string[0], Description = "当前场景、框架域/服务/UI 状态与面板列表")]
        public static object State(RevAutomationRequest request) => Invoke(request);
        [RevAutomationAction("ui.dump", Environment = "play", Keys = new[] { "query", "offset", "limit", "includeInactive" }, Description = "UGUI 可交互控件、文本、屏幕位置和遮挡情况")]
        public static object Dump(RevAutomationRequest request) => Invoke(request);
        [RevAutomationAction("ui.click", Environment = "play", Keys = new[] { "id", "objectPath", "scene" }, Description = "校验遮挡与 interactable 后经 EventSystem 注入真实指针事件")]
        public static object Click(RevAutomationRequest request) => Invoke(request);
        [RevAutomationAction("ui.input", Environment = "play", Keys = new[] { "id", "objectPath", "scene", "text", "submit" }, Description = "填写 UGUI/TMP 输入框，触发公开文本变更事件")]
        public static object Input(RevAutomationRequest request) => Invoke(request);
        [RevAutomationAction("ui.back", Environment = "play", Keys = new string[0], Description = "调用框架 RevUI.Back 返回上一层界面")]
        public static object Back(RevAutomationRequest request) => Invoke(request);
        [RevAutomationAction("ui.close", Environment = "play", Keys = new[] { "id", "objectPath", "scene" }, Description = "经 RevUI.Close 关闭指定框架面板")]
        public static object Close(RevAutomationRequest request) => Invoke(request);
        [RevAutomationAction("runtime.screenshot", Environment = "play", Keys = new string[0], Description = "在下一帧末捕获完整 GameView 到当前工单目录，等待 PNG 写完")]
        public static object Screenshot(RevAutomationRequest request) => Invoke(request);
    }
}
