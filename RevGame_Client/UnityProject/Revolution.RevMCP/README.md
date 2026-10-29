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

Unity 控制台的“活动日志”实时显示 Agent 请求、编辑器执行、运行时 GM/业务、外部 CLI 和计划断言；中文解释操作及用途，点击查看参数、结果与工单。可选 `intent` 写在 rev_call/动态工具外层，或计划/步骤上，只说明目的、不进入业务参数。活动账本保存在 `Library/RevolutionAutomation/activity`，窗口最多显示最近 500 条。更新后重启外部 MCP 会话，并同步 Unity 桥。

新手说明：[活动日志与生成路径](../Revolution.Document/自动化工具/RevMCP活动日志与生成路径使用说明.html)。

CLI 目录默认取同级 `Revolution.RevCLI`；若放在别处用 `--cli-root` 指定（改名前的 `--automation-root` 仍可用）。

关于速度：真实编辑器上单次往返约 110ms，实测改造轮询间隔（100ms→20ms）前后没有可测差异——空闲编辑器 `EditorApplication.update` 本身约 110ms 才触发一次，瓶颈在编辑器 tick 而不是轮询。要减少耗时，应当减少往返次数（一次调用做更多事），而不是继续压轮询间隔。

独立 Player 显式 `-revAutomation` 开启服务，MCP 使用 `--runtime-endpoint <runtime.json>`。正式包默认无运行时测试接口。game 业务能力通过 `RevRuntimeAutomation.Register` 登记，GM 从真实 RevGM.Commands 读取；外部脚本必须用 manifest 显式登记，不执行任意模型生成的 shell。

安装 `Skills/revolution-ai-automation` 后可以说：“点击 Demo 增加按钮，再 GM 增加 2，断言 3，截图并清理归零。”Skill 负责发现与组织计划；业务仍需真实可调用的契约。

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
python Revolution.RevMCP/tests/verify_unity.py --project $project --output Revolution.RevMCP/results/verification.json
```

截至 2026-10-22：Python 13 项通过；实际 MCP → Editor 与 Windows 开发版 Player 的 UI/GM/断言/截图/清理已通过。设备与其他平台没有以此冒充已验收。
