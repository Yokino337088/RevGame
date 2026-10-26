# Revolution.Automation 2.0

Python 3.10+ 标准库 CLI，通过 Unity 主线程桥调用编辑器、GM 和测试。新增场景/对象/组件/资产/Importer/Prefab/项目设置、Console/编译、Test Runner、Player 构建、运行时 UI/截图，以及流程引用、断言、有界轮询和 finally 清理。

先用 Unity 打开目标工程，等待编译；下面命令从仓库根目录执行：

```powershell
$project = '<本仓库绝对路径>/RevolutionFrameWork_Unity'
$env:PYTHONIOENCODING = 'utf-8'
python Revolution.Automation/rev.py --project $project doctor
python Revolution.Automation/rev.py --project $project --mode live commands --live
python Revolution.Automation/rev.py --project $project --mode live editor coverage
python Revolution.Automation/rev.py --project $project --mode live scene list
python Revolution.Automation/rev.py --project $project --mode live gameobject find --query Canvas
```

commands.json 是离线说明，commands --live 才是实际安装能力。2026-10-22 示例工程有 122 个 Editor 动作，另 3 个 Python 动作为 editor.launch/editor.wait/scenario.validate；20 个 Revolution.Tools 菜单都有映射。覆盖表用于发现漏接线，不意味着所有第三方控件或未来新增功能自动可用。

需要一句话 Agent 测试、GM 动态工具、独立开发版 Player、外部 CLI/Python，使用相邻的 [Revolution.RevMCP](../Revolution.RevMCP/README.md) 与 Skills/revolution-ai-automation。

完整的新手使用说明（操作示例、参数契约、pipeline、旧 scenario、报告与故障处理）：

- [HTML 文档](../Revolution.Document/自动化工具/Revolution自动化工具使用说明.html)
- [Markdown](../Revolution.Document/自动化工具/Revolution自动化工具使用说明.md)
- [RevMCP HTML](../Revolution.Document/自动化工具/RevMCP使用说明.html)

参数可用 call 动作ID --json 参数文件，避免 PowerShell 转义中文和复杂对象。全局选项 --project/--mode/--timeout/--confirm/--output 放在子命令前。Python 直接导入 revautomation.client.RevClient 可复用同一协议。

```powershell
python Revolution.Automation/rev.py --project $project --mode live --output result.json call component.get --json inspect.json
python Revolution.Automation/rev.py pipeline Revolution.Automation/examples/editor-inspect.pipeline.json --validate
python -m unittest discover -s Revolution.Automation/tests -v
```

Unity 接线位于 Assets/Revolution/Editor/RevAutomation，运行时共用模块位于 Assets/Revolution/Runtime/RevAutomation。仅复制 Python 不会自动安装 Unity 代码。已有工程使用 live，关闭工程后才可 batch 执行允许的资产动作；游戏 scenario 必须未暂停的图形 Play。

结果位于 Unity工程/Library/RevolutionAutomation/jobs/id，scenario 报告默认导出 Revolution.Automation/results/。CLI 退出码非零、response.success=false、业务断言或清理失败都属于失败；超时不代表动作未执行，不自动重放变更。

截至 2026-10-22：Python 19 项与 Unity EditMode 24 项通过；真实 MCP 到 Editor 的按钮、GM、断言、PNG、清理链路已运行。详见文档中的脱敏验证记录。
