# Revolution.RevMCP

把 Revolution 的 Editor、实际注册 GM、运行时业务、UGUI、状态/截图，以及显式 CLI/Python 接口提供给 Agent。Python 3.10+，标准库即可运行。Unity 内入口为 `Revolution.Tools/AI/RevMCP 控制台`，外部 Python 进程提供 MCP stdio；独立开发版 Player 复用运行时桥。

保留相邻的 `Revolution.RevCLI` 目录，Unity 工程安装当前框架。先打开目标工程并等待编译，再执行：

```powershell
$project = '<本仓库绝对路径>/RevolutionFrameWork_Unity'
python Revolution.RevMCP/rev_mcp.py --project $project discover
python Revolution.RevCLI/rev.py --project $project --mode live editor play --scene 'Assets/Revolution.Demo/RevAutomation.Demo/RevAutomationDemo.unity'
python Revolution.RevMCP/rev_mcp.py --project $project run Revolution.RevMCP/examples/ui-gm.plan.json
python Revolution.RevCLI/rev.py --project $project --mode live editor stop
```

Agent 启动命令：`python <rev_mcp.py绝对路径> --project <Unity工程绝对路径> serve`。配置示例见 examples/mcp-config.example.json，也可直接从 Unity 控制台复制。稳定工具为 rev_discover、rev_call、rev_run；真实注册表还会生成动态工具。

打开 RevMCP 控制台后，活动日志实时显示 Agent 请求、编辑器执行、游戏 GM、外部工具和计划断言。窗口读取临时事件后立即删除，关闭时释放视图并停止采集。`intent` 说明调用目的，通过工具的外层或计划字段传入。升级框架与工具后，重启 MCP 会话。

新手说明：[活动日志与生成路径](../Revolution.Document/自动化工具/RevMCP活动日志与生成路径使用说明.html)。

CLI 目录默认取同级 `Revolution.RevCLI`；若放在别处用 `--cli-root` 指定（改名前的 `--automation-root` 仍可用）。

## 产物路径规则

- **默认返回本次响应。** 在 MCP 用 `structuredContent` 查看结果，需更多细节时传 `full: true`。CLI 可用 `--full`。用 `run -` 将计划从标准输入传入，必要临时工单在领取结果后立即永久删除。旧 `--reports` 仅保留启动兼容。`Revolution.RevMCP/results/` 及其别名禁止写入。
- **用户明确要求时生成报告。** CLI `run` 和 MCP `rev_run` 都识别计划的 `report: true` 或 `reportDirectory`。后者优先用项目内绝对路径或相对仓库根的路径。只设 `report: true` 时从 Unity 工程向上查现有文档，优先已有测试报告子目录。找不到会在 `evidenceError` 说明，同时返回测试结果。
- **交付画面。** 单次显式截图直接返回 MCP 画面。要求导出时，`evidencePath` 指向一份 HTML，最多附三张缩放截图和一份 GIF。

## 临时文件用完即删

- 日常查询、UI、GM 和计划直接读取本次响应。AI 使用内联参数或标准输入传计划，需要临时输入文件时放系统临时目录，最后一次读取后在 `finally` 永久删除自己创建的文件。
- `<Unity 工程>/Library/RevolutionAutomation/jobs`、`activity` 和 `screenshots` 由工具管理。把 JSON、截图、GIF 和报告的正式导出放在用户明确指定的位置。项目已有正式文档目录时按项目规范选择。
- 临时截图和兼容工单交付后立即删除。取消、失败也执行收尾，原动作仍在跑时先等终态。窗口关闭时停止活动采集，活动文件供实时窗口读取，读完即删。
- 升级 Unity 桥、RevCLI 和 RevMCP 后重启常驻工具。检查遗留占用用 `storage.status`，用 `storage.prune` 默认预览，`dryRun=false` 应用工具识别的回收。

关于速度（2026-10-31），普通 Editor 调用按目标发现并短缓存能力说明（2 秒、会话与状态约束、变更和失败失效）。可选 `--compact-tools` 只暴露三个稳定工具，配合 `rev_discover` 的 target/query/offset/limit 按需发现。默认动态工具保留。真实同层 12 次 editor.status 平均耗时从 1465ms 降到 354ms。同步多动作继续优先 batch，跨帧业务用计划。 [提速教程](../Revolution.Document/自动化工具/RevMCP提速与活动窗口优化使用说明.html)

独立 Player 显式 `-revAutomation` 开启服务，MCP 使用 `--runtime-endpoint <runtime.json>`。正式包默认无运行时测试接口。game 业务能力通过 `RevRuntimeAutomation.Register` 登记，GM 从真实 RevGM.Commands 读取；外部脚本必须用 manifest 显式登记，不执行任意模型生成的 shell。

安装 `Skills/revolution-ai-automation` 后可以说：“点击 Demo 增加按钮，再 GM 增加 2，断言 3，截图并清理归零。”Skill 负责发现与组织计划；业务仍需真实可调用的契约。

`rev_run` 以第一个失败步骤结束本轮业务流程，并继续执行 `finally`。调用失败或 `until` 超时会保留首个错误，成功响应才进入 `expect`。Agent 可用实际响应、活动日志和当前源码记录缺陷，随后在授权内修复并复跑同一用例。 [功能完整性与缺陷闭环](https://github.com/Yokino337088/Revolution/blob/main/Skills/revolution-ai-automation/references/defect-closure.md)

完整的新手教程、接口注册、Player、CLI/Python、计划格式、限制和验证记录：

- [RevMCP 使用说明 HTML](../Revolution.Document/自动化工具/RevMCP使用说明.html)
- [Markdown](../Revolution.Document/自动化工具/RevMCP使用说明.md)
- [Editor Automation](../Revolution.RevCLI/README.md)
- [AI 自动化 Skill](../Skills/revolution-ai-automation/SKILL.md)

无 Unity 依赖的回归：

```powershell
python -m unittest discover -s Revolution.RevMCP/tests -v
```

实际 Unity/MCP 验证（先启动教学场景 Play）：

```powershell
python Revolution.RevMCP/tests/verify_unity.py --project $project --output .codex/work/revmcp/verification.json
```

截至 2026-10-22：Python 13 项通过；实际 MCP → Editor 与 Windows 开发版 Player 的 UI/GM/断言/截图/清理已通过。设备与其他平台没有以此冒充已验收。
