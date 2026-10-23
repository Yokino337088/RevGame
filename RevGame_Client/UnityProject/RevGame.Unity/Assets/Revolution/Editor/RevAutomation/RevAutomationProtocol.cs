// RevAutomationProtocol.cs —— 本机 Python 与 Unity 的 JSON 契约。
// 放 Editor 程序集，不给正式包加入远程 GM、线程或网络服务；请求只能在 Unity 主线程执行。
using System;

namespace Revolution.Editor
{
    [Serializable]
    // ============================================================================
    //  RevAutomationProtocol —— 自动化工具的"协议定义"（纯数据，无逻辑）
    //
    //  【这套工具在干什么】
    //  外部（Python CLI / CI / Codex）想操作 Unity，但 Unity 是图形程序，不能直接调函数。
    //  于是约定：外部写一个 JSON 请求文件 → Unity 编辑器轮询到这个文件 → 执行 → 写回结果 JSON。
    //  本文件就是这两个 JSON 的 C# 映射。
    //
    //  【新手上路先记住 3 个词】
    //    · Request  = 外部给 Unity 的"工单"（做什么动作、参数是什么、什么时候过期）
    //    · Response = Unity 给外部的"回执"（成功没？错误码是什么？数据在哪）
    //    · Job 目录 = 一张工单一个文件夹（Library/RevolutionAutomation/jobs/<32位id>/）
    //
    //  ⚠️ 注意：这里的字段名必须和 Python 侧 / examples/*.json 保持一致，
    //     改名会让外部调用直接失效（JsonUtility 按字段名匹配）。
    // ============================================================================

    public sealed class RevAutomationRequest
    {
        public int protocol = 1;
        public string id;
        public string action;
        public string payloadJson = "{}";
        public string sessionId;
        public string deadlineUtc;
        public double timeoutSeconds = 120;
        public bool confirm;
    }

    [Serializable]
    /// <summary>
    /// Unity 返回给外部的回执。
    /// 关键字段：<c>success</c> 是否成功；<c>error</c> 是**短错误码**（Busy/Expired/ScenarioFailed…，
    /// 便于脚本判断）；<c>message</c> 是给人看的说明；<c>dataJson</c> 才是真正的业务数据（JSON 字符串）。
    /// 为什么数据要用字符串再包一层：不同动作返回的结构完全不同，统一用 JSON 字符串最省事，
    /// 外部想用时再解析一次即可。
    /// </summary>

    public sealed class RevAutomationResponse
    {
        public int protocol = 1;
        public string id;
        public bool success;
        public string error;
        public string message;
        public string dataJson;
    }

    [Serializable]
    /// <summary>
    /// 编辑器"心跳"（endpoint.json）。外部靠它判断：当前有没有 Unity 在跑、是不是同一个会话、
    /// 在不在 Play、有没有正在处理的请求。
    /// ⚠️ 它只是**旁路信息**，永远不要拿它当"命令执行完了"的信号 —— 完成信号只认 response.json。
    /// </summary>

    internal sealed class RevAutomationEndpoint
    {
        public int protocol = 1;
        public int pid;
        public string sessionId;
        public string project;
        public string unityVersion;
        public double heartbeatUnix;
        public bool playing;
        public bool compiling;
        public bool paused;
        public bool updating;
        public bool compilationFailed;
        public string scene;
        public bool batchMode;
        public string activeRequest;
    }

    // 只有这些明确命名的字段才会进入适配器。具体动作只读取自己需要的字段。
    [Serializable]
    /// <summary>
    /// 所有动作**共用**的参数包（扁平结构）。每个动作只读自己需要的字段，
    /// 用不到的保持默认值即可 —— 所以你会看到 AB 的字段和 Excel 的字段混在一起，这是刻意的简化。
    /// 参数由外部 JSON 反序列化而来，字段名必须与 CLI 的 --xxx 参数对应。
    /// </summary>

    public sealed class RevAutomationOptions
    {
        public string target;
        public string scene;
        public string path;
        public string command;
        public string configJson;
        public string userConfigJson;
        public string baseline;
        public string outputDir;
        public string appVersion;
        public string resVersion;
        public string channel = "";
        public string env = "";
        public string bundle;
        public string newBundle;
        public string[] assets;
        public string[] sources;
        public bool recursive = true;
        public bool dataOnly;
        public bool force;
        public bool enabled;
        public string prefab;
        public string className;
        public string nameSpace = "";
        public string outputFolder = "Assets";
        public string layer = "Normal";
        public bool useAttributes = true;
        public RevAutomationControl[] controls;
    }

    [Serializable]
    /// <summary>
    /// 一个 UI 控件的描述（ui.scan 的输出 / ui.generate 的输入）。
    /// 例如：path="Panel/BtnStart"、component="Button"、bind="_btnStart"、events=["click"]。
    /// </summary>

    public sealed class RevAutomationControl
    {
        public string path;
        public string component;
        public bool bind;
        public string[] events;
    }

    /// <summary>扩展包可注册自己的 CLI 动作；只扫描编辑器方法，不为业务 GM 引入运行期反射。</summary>
    [AttributeUsage(AttributeTargets.Method)]
    /// <summary>
    /// 注册"扩展动作"的标记。用法：
    /// <code>
    /// [RevAutomationAction("hot.manifest")]
    /// public static object BuildManifest(RevAutomationRequest request) { ... }
    /// </code>
    /// 约束（写错会直接抛异常）：
    /// · 必须是 <c>static</c> 方法；
    /// · 返回值不能是 void；
    /// · 有且仅有 1 个 <c>RevAutomationRequest</c> 参数；
    /// · 同一个动作名全工程只能有 1 个。
    /// 这样设计的好处：新模块（例如热更）不用改 RevAutomationActions 那个大 switch，
    /// 只要在自己文件里标个特性，工具启动时会自动扫描到。
    /// </summary>

    public sealed class RevAutomationActionAttribute : Attribute
    {
        public readonly string Name;
        public string Description { get; set; } = "项目扩展动作";
        public string Environment { get; set; } = "edit";
        public string[] Keys { get; set; }
        public bool Confirm { get; set; }
        // 仅用于能从已持久化状态继续等待的动作，不能用于重放工作。
        public bool ResumeAfterReload { get; set; }
        public RevAutomationActionAttribute(string name) { Name = name; }
    }

    /// <summary>跨帧操作由桥接持续推进，完成前仍占用唯一工单。</summary>
    public abstract class RevAutomationOperation : IDisposable
    {
        public bool Success { get; protected set; } = true;
        public string Message { get; protected set; } = "完成";
        public object Result { get; protected set; }
        public abstract bool Tick();
        public virtual void Cancel() { Success = false; Message = "操作取消或超时"; }
        public virtual void Dispose() { }
    }
}
