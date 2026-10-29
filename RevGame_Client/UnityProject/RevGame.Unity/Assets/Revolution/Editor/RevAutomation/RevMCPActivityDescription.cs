// RevMCPActivityDescription —— 用确定的命令契约解释“在做什么/有什么用”，不臆测 Agent 的心理。
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Revolution.Editor
{
    /// <summary>
    /// 日志的人话说明。优先使用内置操作解释，其次使用实时登记的 Description；
    /// Agent 的 intent 独立标成“AI 说明的目的”，不把自动推断的用途冒充 Agent 的真实意图。
    /// 未识别的扩展明确提示补说明，原命令和参数仍可查看，不编造业务效果。
    /// </summary>
    public static class RevMCPActivityDescription
    {
        private static readonly Dictionary<string, string[]> Known = new Dictionary<string, string[]>
        {
            ["editor.status"] = new[] { "检查编辑器是否就绪", "确认当前工程、编译和游戏运行状态，判断能否继续操作。" },
            ["editor.refresh"] = new[] { "重新导入修改后的资源", "让 Unity 发现磁盘上的代码与资源变化；后续仍需等待编译完成。" },
            ["editor.capabilities"] = new[] { "查看当前工程能提供哪些工具", "读取实际注册的编辑器动作与参数，避免调用未安装的接口。" },
            ["editor.compilation"] = new[] { "检查脚本编译结果", "发现代码错误，确认新代码是否已在编辑器中生效。" },
            ["editor.console"] = new[] { "检查 Unity 控制台消息", "查找本次操作产生的报错、警告或诊断信息。" },
            ["editor.play"] = new[] { "启动游戏测试", "进入 Play，让运行时 GM、业务查询和 UI 操作可用。" },
            ["editor.stop"] = new[] { "结束游戏测试", "退出 Play，回到可修改工程资源的编辑模式。" },
            ["editor.pause"] = new[] { "暂停游戏", "暂时停止游戏帧，便于观察当前状态。" },
            ["editor.resume"] = new[] { "继续运行游戏", "解除暂停，使等待中的运行时操作能够继续。" },
            ["editor.activity"] = new[] { "查看 AI 操作日志", "核对最近执行的命令、结果与耗时，不改变游戏业务。" },
            ["editor.activity-window"] = new[] { "打开 AI 活动窗口", "让用户直接观察调用进度，或切换日志页签和筛选。" },
            ["scene.list"] = new[] { "检查场景与未保存状态", "确认当前场景和修改是否保存，避免测试切场景丢失编辑内容。" },
            ["ui.dump"] = new[] { "查看画面中可以操作的控件", "查出本次真实按钮/输入框及遮挡状态，再决定如何交互。" },
            ["ui.click"] = new[] { "模拟玩家点击控件", "通过真实 EventSystem 指针事件触发 UI，检查玩家是否能完成操作。" },
            ["ui.input"] = new[] { "向输入框填写文字", "通过公开输入事件填写内容，可用于登录、搜索等界面测试。" },
            ["runtime.screenshot"] = new[] { "截取游戏画面作为证据", "保存当前画面供检查；截图不能替代业务数值断言。" },
            ["runtime.logs"] = new[] { "检查游戏运行报错", "读取运行时日志与 Error 数量，确认操作期间有没有异常。" },
            ["runtime.start"] = new[] { "开启本机游戏测试服务", "允许 Agent 通过开发态 HTTP 接口调用已注册的游戏能力。" },
            ["runtime.stop"] = new[] { "关闭本机游戏测试服务", "停止监听并收尾未完成工单，已发生的业务效果不回滚。" },
            ["runtime.capabilities"] = new[] { "查看游戏实际开放的测试接口", "发现本局注册的 GM、UI 和业务能力，避免猜测可用指令。" },
            ["ab.res-paths"] = new[] { "生成资源目录常量", "把资源目录写成 RevResPath C# 常量，减少业务代码手拼路径出错。" },
            ["ab.sound-paths"] = new[] { "生成音效目录常量", "更新音效和背景音乐的逻辑目录常量，供声音系统使用。" },
            ["ab.build"] = new[] { "构建资源包", "收集、校验并打包资源，生成映射与清单；失败需检查具体校验结果。" },
            ["ab.configure"] = new[] { "修改资源打包配置", "设置项目资源根目录或生成选项，后续生成/构建使用这些配置。" },
            ["ab.dependencies"] = new[] { "检查资源包依赖", "找出跨包依赖、未分包共享资源与循环引用，帮助减少重复打包。" },
            ["rev_discover"] = new[] { "确认 Agent 能调用哪些接口", "同时查看当前工程与游戏的真实工具目录，缺项会给出原因。" },
            ["rev_call"] = new[] { "请求执行一个自动化操作", "把具体命令和参数交给实际接口，结果由下方执行记录确认。" },
            ["rev_run"] = new[] { "执行一份测试计划", "按步骤调用、检查业务断言并执行 finally 清理，不能只看单条命令成功。" }
        };

        public static string Title(RevAutomationActivityEntry entry)
        {
            string action = EffectiveAction(entry, out string gmLine);
            if (entry.target == "plan") { return "核对测试步骤：" + DescribeAction(action, entry); }
            if (!string.IsNullOrEmpty(gmLine)) { return "执行 GM 指令：" + gmLine; }
            if (entry.target == "gm") { return "执行 GM 指令：" + entry.action; }
            if (Known.TryGetValue(action, out string[] text)) { return text[0]; }
            string description = Description(entry, action);
            return string.IsNullOrWhiteSpace(description) ? "调用已注册接口：" + action : description;
        }

        public static string Purpose(RevAutomationActivityEntry entry)
        {
            string action = EffectiveAction(entry, out _);
            if (entry.phase == "finally") { return "执行已配置的测试收尾步骤，恢复业务状态；具体清理结果仍以返回值与断言为准。"; }
            if (entry.target == "plan") { return "检查调用后的实际状态是否符合预期；失败时仍执行已配置的收尾清理。"; }
            if (Known.TryGetValue(action, out string[] text)) { return text[1]; }
            string description = Description(entry, action);
            return !string.IsNullOrWhiteSpace(description) ? "接口声明的用途：" + description
                : "此扩展没有可读说明。查看原命令和参数，或为注册接口补充 Description。";
        }

        private static string DescribeAction(string action, RevAutomationActivityEntry entry)
            => Known.TryGetValue(action, out string[] text) ? text[0] : Description(entry, action) ?? action;

        private static string Description(RevAutomationActivityEntry entry, string action)
        {
            if (!string.IsNullOrWhiteSpace(entry.description)) { return entry.description; }
            if (entry.target == "gm" || entry.action == "gm.exec")
            {
                try { if (RevGM.TryGet(action, out RevGMCommand command)) { return command.Description; } }
                catch (Exception) { }
            }
            return RevAutomationCatalog.DescriptionOf(action);
        }

        /// <summary>传输动作只负责转发，把真实能力名展示给用户；解析失败仍回退到原动作。</summary>
        private static string EffectiveAction(RevAutomationActivityEntry entry, out string gmLine)
        {
            gmLine = null;
            string action = entry.action;
            try
            {
                var arguments = RevAutomationJson.Object(RevAutomationJson.Parse(entry.argumentsJson ?? "{}"));
                if (action == "rev_call" && arguments.TryGetValue("action", out object nested)) { action = Convert.ToString(nested); }
                if (action == "runtime.call" && arguments.TryGetValue("runtimeAction", out object runtime)) { action = Convert.ToString(runtime); }
                if (action == "gm.exec" && arguments.TryGetValue("command", out object line))
                {
                    gmLine = Convert.ToString(line);
                    action = RevAutomationActions.GMCommandName(gmLine);
                }
            }
            catch (Exception) { }
            return action;
        }
    }
}
