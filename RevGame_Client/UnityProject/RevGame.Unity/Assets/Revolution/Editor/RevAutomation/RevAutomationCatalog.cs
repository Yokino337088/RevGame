// 编辑器实际安装的能力目录。可选包只在真正注册后出现。
using System;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace Revolution.Editor
{
    [Serializable]
    public sealed class RevAutomationCapability
    {
        public string id;
        public string description;
        public string environment;
        public string[] keys;
        public bool confirm;
        public bool resumesAfterReload;
    }

    public static class RevAutomationCatalog
    {
        // 离线 commands.json 用于 CLI 帮助，不能证明目标工程已安装扩展包。
        // 这里在 Unity 内通过 TypeCache 读取真实特性注册，再把实际能力回给 Agent。
        // 新增动作时：声明特性 → 校验 payload → 复用窗口背后的服务 → 返回结构化结果。
        // 与 CLI commands.json 的既有核心动作保持一致；新增动作使用特性注册。
        private static readonly RevAutomationCapability[] Builtins =
        {
            new RevAutomationCapability { id = "editor.status", description = "工程、Play、编译和 GM 状态", environment = "either", keys = new string[] {  } },
            new RevAutomationCapability { id = "editor.play", description = "进入 Play，可指定场景；等待两帧", environment = "live", keys = new string[] { "scene" } },
            new RevAutomationCapability { id = "editor.stop", description = "停止 Play", environment = "live", keys = new string[] {  } },
            new RevAutomationCapability { id = "editor.refresh", description = "刷新 AssetDatabase", environment = "edit", keys = new string[] {  } },
            new RevAutomationCapability { id = "editor.menus", description = "发现实际安装的 Revolution 菜单", environment = "either", keys = new string[] {  } },
            new RevAutomationCapability { id = "editor.menu", description = "打开六类工具窗口；执行操作用专用动作", environment = "live", keys = new string[] { "path" } },
            new RevAutomationCapability { id = "ab.config", description = "读取 AB 配置", environment = "edit", keys = new string[] {  } },
            new RevAutomationCapability { id = "ab.configure", description = "局部修改 AB 配置字段", environment = "edit", keys = new string[] { "configJson" } },
            new RevAutomationCapability { id = "ab.scan", description = "读取分包布局", environment = "edit", keys = new string[] {  } },
            new RevAutomationCapability { id = "ab.validate", description = "资源与平台 Shader 校验", environment = "edit", keys = new string[] { "target" } },
            new RevAutomationCapability { id = "batch.call", description = "一次请求顺序执行多个动作（最多 64 步），省掉每步一次的往返等待", environment = "either", keys = new string[] { "steps", "stopOnError" } },
            new RevAutomationCapability { id = "ab.build", description = "收集、校验、构建、映射与首包拷贝", environment = "edit", keys = new string[] { "target" } },
            new RevAutomationCapability { id = "ab.map", description = "仅生成映射和路径常量", environment = "edit", keys = new string[] {  } },
            new RevAutomationCapability { id = "ab.res-paths", description = "生成资源路径常量", environment = "edit", keys = new string[] {  } },
            new RevAutomationCapability { id = "ab.sound-paths", description = "生成音效路径常量", environment = "edit", keys = new string[] {  } },
            new RevAutomationCapability { id = "ab.check-map", description = "检查运行期映射一致性", environment = "edit", keys = new string[] { "target" } },
            new RevAutomationCapability { id = "ab.dependencies", description = "包依赖、共享资源、循环依赖", environment = "edit", keys = new string[] {  } },
            new RevAutomationCapability { id = "ab.sizes", description = "按包估算源文件及其依赖源文件体积", environment = "edit", keys = new string[] {  } },
            new RevAutomationCapability { id = "ab.artifacts", description = "列出实际打包文件与字节数", environment = "edit", keys = new string[] { "target" } },
            new RevAutomationCapability { id = "ab.snapshot", description = "记录当前布局为基准", environment = "edit", keys = new string[] {  } },
            new RevAutomationCapability { id = "ab.diff", description = "比较基准与当前布局", environment = "edit", keys = new string[] { "baseline" } },
            new RevAutomationCapability { id = "ab.mark", description = "创建/移动资源到分包；空 bundle 清除显式标记", environment = "edit", keys = new string[] { "assets", "bundle" } },
            new RevAutomationCapability { id = "ab.mark-source", description = "查询资源的显式/继承标记来源", environment = "edit", keys = new string[] { "path" } },
            new RevAutomationCapability { id = "ab.rename", description = "按包名前缀改名或移动层级", environment = "edit", keys = new string[] { "bundle", "newBundle" } },
            new RevAutomationCapability { id = "ab.delete", description = "按包名前缀移除标记，资源仍保留", environment = "edit", keys = new string[] { "bundle" } },
            new RevAutomationCapability { id = "ab.clean-names", description = "清理闲置包名", environment = "edit", keys = new string[] {  } },
            new RevAutomationCapability { id = "ab.sync", description = "强制同步标记和缓存", environment = "edit", keys = new string[] {  } },
            new RevAutomationCapability { id = "ab.overlay", description = "切换 Project 窗口包标记显示", environment = "edit", keys = new string[] { "enabled" } },
            new RevAutomationCapability { id = "ab.copy", description = "按已启用配置拷贝首包", environment = "edit", keys = new string[] { "target" } },
            new RevAutomationCapability { id = "ab.mode", description = "切换编辑器 AB 加载模式", environment = "edit", keys = new string[] { "enabled" } },
            new RevAutomationCapability { id = "excel.settings", description = "读取团队及个人导表设置", environment = "edit", keys = new string[] {  } },
            new RevAutomationCapability { id = "excel.configure", description = "修改团队及个人导表设置", environment = "edit", keys = new string[] { "configJson", "userConfigJson" } },
            new RevAutomationCapability { id = "excel.read", description = "读取并校验 Excel", environment = "edit", keys = new string[] { "sources", "recursive" } },
            new RevAutomationCapability { id = "excel.plan", description = "预览导出变更", environment = "edit", keys = new string[] { "sources", "recursive" } },
            new RevAutomationCapability { id = "excel.export", description = "全量或仅数据导出，移除旧表需要 confirm", environment = "edit", keys = new string[] { "sources", "recursive", "dataOnly", "force" } },
            new RevAutomationCapability { id = "excel.mark", description = "标记配置表数据目录", environment = "edit", keys = new string[] { "bundle" } },
            new RevAutomationCapability { id = "excel.orphans", description = "列出旧数据文件", environment = "edit", keys = new string[] { "sources", "recursive" } },
            new RevAutomationCapability { id = "excel.clean", description = "移除已确认的旧数据文件", environment = "edit", keys = new string[] { "sources", "recursive" } },
            new RevAutomationCapability { id = "ui.scan", description = "扫描预制体控件及支持的事件", environment = "edit", keys = new string[] { "prefab" } },
            new RevAutomationCapability { id = "ui.preview", description = "预览面板代码", environment = "edit", keys = new string[] { "prefab", "className", "nameSpace", "outputFolder", "layer", "useAttributes", "controls" } },
            new RevAutomationCapability { id = "ui.generate", description = "生成面板代码；禁止覆盖业务文件", environment = "edit", keys = new string[] { "prefab", "className", "nameSpace", "outputFolder", "layer", "useAttributes", "controls" } },
            new RevAutomationCapability { id = "platform.resources", description = "首包 Resources 体积审计", environment = "edit", keys = new string[] {  } },
            new RevAutomationCapability { id = "platform.linkxml", description = "生成业务裁剪保护；覆盖手写文件需要 confirm", environment = "edit", keys = new string[] {  } },
            new RevAutomationCapability { id = "demo.ui-prefabs", description = "生成全部 UI 演示预制体", environment = "edit", keys = new string[] {  } },
            new RevAutomationCapability { id = "gm.list", description = "列出实际 GM 及参数、高危标记、注册错误", environment = "either", keys = new string[] {  } },
            new RevAutomationCapability { id = "gm.exec", description = "执行一条 GM；高危指令需要 confirm", environment = "live", keys = new string[] { "command" } },
            new RevAutomationCapability { id = "scenario.run", description = "通用业务循环、条件、断言、清理与采样", environment = "live", keys = new string[] { "name", "repeat", "warmup", "setup", "steps", "cleanup", "teardown", "stopOnFailure", "failOnUnityError", "thresholds" } },
        };

        [Serializable] private sealed class CatalogResult
        {
            public int protocol = 1;
            public string version = "2.0.0";
            public RevAutomationCapability[] actions;
            public string[] errors;
        }

        private static MethodInfo[] Methods => TypeCache.GetMethodsWithAttribute<RevAutomationActionAttribute>().ToArray();

        [RevAutomationAction("editor.capabilities", Environment = "either", Keys = new string[0], Description = "当前工程真正安装的动作、参数和运行条件")]
        public static object Discover(RevAutomationRequest request)
        {
            var groups = Methods.GroupBy(m => m.GetCustomAttribute<RevAutomationActionAttribute>().Name).ToArray();
            return new CatalogResult
            {
                actions = Builtins.Where(b => b.id != "demo.ui-prefabs" || TypeCache.GetMethodsWithAttribute<MenuItem>()
                    .Any(m => m.GetCustomAttributes<MenuItem>().Any(a => a.menuItem == "Revolution.Tools/Demo/生成 UI 演示预制体（全部）")))
                    .Concat(groups.Where(g => g.Count() == 1 && Valid(g.First())).Select(g => Describe(g.First())))
                    .OrderBy(c => c.id, StringComparer.Ordinal).ToArray(),
                errors = groups.Where(g => g.Count() != 1 || !Valid(g.First()) || Builtins.Any(b => b.id == g.Key))
                    .Select(g => "重复或签名无效的自动化动作：" + g.Key).ToArray()
            };
        }

        private static bool Valid(MethodInfo method) => method.IsStatic && method.ReturnType != typeof(void)
            && method.GetParameters().Length == 1 && method.GetParameters()[0].ParameterType == typeof(RevAutomationRequest);

        private static RevAutomationCapability Describe(MethodInfo method)
        {
            var attr = method.GetCustomAttribute<RevAutomationActionAttribute>();
            return new RevAutomationCapability { id = attr.Name, description = attr.Description, environment = attr.Environment,
                keys = attr.Keys, confirm = attr.Confirm, resumesAfterReload = attr.ResumeAfterReload };
        }

        /// <summary>
        /// 查询动作声明的运行环境（edit / either / live / play）。未登记返回 null。
        /// 批量执行要靠它判断某一步能不能在同一帧里安全跑完。
        /// </summary>
        public static string EnvironmentOf(string action)
        {
            var builtin = Builtins.FirstOrDefault(b => b.id == action);
            if (builtin != null) { return builtin.environment; }
            try
            {
                var method = Extension(action);
                return method == null ? null : method.GetCustomAttribute<RevAutomationActionAttribute>().Environment;
            }
            catch (InvalidOperationException)
            {
                // 重复或签名无效的扩展：交给 Execute 报原来的错，这里不掩盖。
                return null;
            }
        }

        public static MethodInfo Extension(string action)
        {
            var methods = Methods.Where(m => m.GetCustomAttribute<RevAutomationActionAttribute>().Name == action).ToArray();
            if (methods.Length == 0) { return null; }
            if (methods.Length != 1 || !Valid(methods[0]) || Builtins.Any(b => b.id == action))
            { throw new InvalidOperationException("重复或签名无效的自动化动作：" + action); }
            return methods[0];
        }

        public static void ValidatePayload(RevAutomationRequest request)
        {
            var method = Extension(request.action);
            string[] keys = method != null ? method.GetCustomAttribute<RevAutomationActionAttribute>().Keys : Builtins.FirstOrDefault(b => b.id == request.action)?.keys;
            RevAutomationPayload.Validate(request, keys);
        }

        public static object Invoke(MethodInfo method, RevAutomationRequest request)
        {
            var attr = method.GetCustomAttribute<RevAutomationActionAttribute>();
            if (attr.Environment == "edit") { RevAutomationActions.RequireEditMode(); }
            if ((attr.Environment == "live" || attr.Environment == "play") && Application.isBatchMode)
            { throw new InvalidOperationException("此动作需要在线图形编辑器：" + request.action); }
            if (attr.Environment == "play" && (!EditorApplication.isPlaying || EditorApplication.isPaused))
            { throw new InvalidOperationException("此动作需要未暂停的 Play：" + request.action); }
            if (attr.Confirm && !request.confirm) { throw new InvalidOperationException("此动作需要 --confirm：" + request.action); }
            try { return method.Invoke(null, new object[] { request }); }
            catch (TargetInvocationException e) { throw e.InnerException ?? e; }
        }

        public static RevAutomationOperation Resume(RevAutomationRequest request)
        {
            // 只有明确声明 ResumeAfterReload 的“等待型动作”允许恢复。
            // 普通导出/打包/业务变更不能在重载后再次 Invoke，否则可能重复产生副作用。
            var method = Extension(request.action);
            if (method == null || !method.GetCustomAttribute<RevAutomationActionAttribute>().ResumeAfterReload) { return null; }
            return Invoke(method, request) as RevAutomationOperation;
        }
    }
}
