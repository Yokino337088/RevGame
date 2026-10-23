// 面向维护者的“漏接线报警器”：新增 Revolution 菜单后，这里会把未映射项列出来。
// 打开窗口仅是观察入口；真正的无人值守操作应使用它对应的 CLI 动作组。
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Revolution.Editor
{
    public static class RevAutomationCoverage
    {
        [RevAutomationAction("editor.coverage", Environment = "either", Keys = new string[0], Description = "核对实际 Revolution.Tools 菜单与 CLI 动作的接线，列出未覆盖入口")]
        public static object Inspect(RevAutomationRequest request)
        {
            var catalog = RevAutomationJson.Object(RevAutomationJson.Parse(RevAutomationJson.Write(RevAutomationCatalog.Discover(request))));
            var actions = ((List<object>)catalog["actions"]).Select(a => (string)RevAutomationJson.Object(a)["id"]).ToArray();
            var rows = new List<object>();
            foreach (var attr in TypeCache.GetMethodsWithAttribute<MenuItem>().SelectMany(m => m.GetCustomAttributes(typeof(MenuItem), false).Cast<MenuItem>())
                .Where(a => !a.validate && a.menuItem.StartsWith("Revolution.Tools/", StringComparison.Ordinal)).OrderBy(a => a.menuItem))
            {
                // 精确匹配菜单，不能把未来同目录的新按钮自动误判为已接线。
                string menu = attr.menuItem;
                string[] prefixes = menu switch
                {
                    "Revolution.Tools/AI/RevMCP 控制台" => new[] { "runtime.start", "runtime.stop", "runtime.capabilities" },
                    "Revolution.Tools/Demo/生成 UI 演示预制体（全部）" => new[] { "demo.ui-prefabs" },
                    "Revolution.Tools/GM 指令面板 %#g" => new[] { "gm." },
                    "Revolution.Tools/UI/面板代码生成器" => new[] { "ui.scan", "ui.preview", "ui.generate" },
                    "Revolution.Tools/平台/包体大小预测" => new[] { "platform.size" },
                    "Revolution.Tools/平台/生成 IL2CPP 裁剪保护 (link.xml)" => new[] { "platform.linkxml" },
                    "Revolution.Tools/平台/首包体积审计 (Resources)" => new[] { "platform.resources" },
                    "Revolution.Tools/热更新/Demo/① 一键：打包 + 清单 + 装配本地 CDN" => new[] { "hot.demo-prepare" },
                    "Revolution.Tools/热更新/Demo/② 打开本地 CDN 目录" => new[] { "hot.demo-open" },
                    "Revolution.Tools/热更新/Demo/③ 清空客户端本地热更目录（回到出包基线）" => new[] { "hot.demo-clear" },
                    "Revolution.Tools/热更新/Demo/④ 打印本次要填的 URL（排错用）" => new[] { "hot.demo-urls" },
                    "Revolution.Tools/热更新/热更清单窗口" => new[] { "hot.manifest", "hot.check" },
                    "Revolution.Tools/资源/AB 加载模式（编辑器）" => new[] { "ab.mode" },
                    "Revolution.Tools/资源/RevAB 分包浏览" => new[] { "ab." },
                    "Revolution.Tools/资源/RevAB 打包工具" => new[] { "ab." },
                    "Revolution.Tools/资源/生成资源路径常量 (RevResPath)" => new[] { "ab.res-paths" },
                    "Revolution.Tools/资源/生成音效目录常量 (RevSoundPath)" => new[] { "ab.sound-paths" },
                    "Revolution.Tools/配置表/导表工具" => new[] { "excel." },
                    "Revolution.Tools/配置表/快速导出（仅数据，不生成代码）" => new[] { "excel.export" },
                    "Revolution.Tools/配置表/快速导出（用上次的 Excel 源）" => new[] { "excel.export" },
                    _ => Array.Empty<string>()
                };
                var mapped = actions.Where(a => prefixes.Any(p => a.StartsWith(p, StringComparison.Ordinal))).ToArray();
                rows.Add(new Dictionary<string, object> { ["menu"] = menu, ["covered"] = mapped.Length > 0, ["actions"] = mapped });
            }
            return new Dictionary<string, object> { ["complete"] = rows.All(r => (bool)((Dictionary<string, object>)r)["covered"]),
                ["menus"] = rows, ["note"] = "窗口内功能使用对应动作；新菜单必须补专用动作并完善此映射。此表不声称第三方插件全部可控。" };
        }
    }
}
