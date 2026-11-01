# Revolution.RevCLI 2.0

Python 3.10+ 标准库 CLI，通过 Unity 主线程桥调用编辑器、GM 和测试。新增场景/对象/组件/资产/Importer/Prefab/项目设置、Console/编译、Test Runner、Player 构建、运行时 UI/截图，以及流程引用、断言、有界轮询和 finally 清理。

2026-10-31 增量：`transform.get/set`、`gameobject.primitive`、`shader.list`、`material.inspect/create/clone/set`，完整参数预检、Undo/Prefab与GUID约束；MCP按需发现与紧凑目录，活动窗口预算/缓存/虚拟化。新教程：[通用Unity扩展](../Revolution.Document/自动化工具/RevCLI与RevMCP通用Unity扩展使用说明.html)、[提速与窗口](../Revolution.Document/自动化工具/RevMCP提速与活动窗口优化使用说明.html)；Skill：`Skills/revolution-unity-editor/`。

2026-11-01 运行时：`runtime start` 后用 `game gm` / `game call` / `game gms` 直连游戏调 GM、点 UI，编辑器 Play 与独立 Player（`--session`）通用。怎么在 CLI 和 MCP 之间选，见 [RevCLI 与 RevMCP 怎么选](../Revolution.Document/自动化工具/RevCLI与RevMCP怎么选.md)。

2026-11-01 全链路：`connect` 重启恢复、`scene.compose` 一份 JSON 搭场景、`script.create/attach` 写脚本挂载、`scene.capture` 看画面、动画、烘焙、包管理、Excel 预览、AB 问题清单、`ship` 一键打整包。详见 [全链路开发与重启连接](../Revolution.Document/自动化工具/RevCLI全链路开发与重启连接使用说明.html)；Skill 配方在 `Skills/revolution-unity-editor/references/`。

2026-11-01 增量：编辑器操作（场景/UI 面板/Prefab/资产/构建/打包/AB/Excel/热更）默认走 RevCLI；运行时 GM 也可用 `game gm` 直接调，RevMCP 为可选项。新增 `ui.create`、`rect.get/set`、`prefab.apply/unpack`；`daemon start` 后调用中位数约 201ms（无常驻 329ms）。用解释器路径直调 `rev.py`，不要用 `rev.cmd` 或 `py` 启动器。详见 [CLI 优先工作流](../Revolution.Document/自动化工具/RevCLI优先工作流使用说明.html)。

> **关于名字**：本目录原名 `Revolution.Automation`，因为"automation"容易和游戏内的自动化玩法混在一起，现改名为 `Revolution.RevCLI`。
> 目录、文档、工作流和参数（`--cli-root`）都已同步；Python 包名仍是 `revautomation`，那是内部 import 名，不影响对外使用。
> 发布分支沿用 `automation` 名称，避免已经克隆过的人失效。

开工和电脑重启后，让 AI 跑 `connect`（可重复执行）：它会体检环境、打开 Unity、等编译、起常驻服务并核对能力，不用先手工开 Unity。下面命令从仓库根目录执行：

```powershell
$project = '<本仓库绝对路径>/RevolutionFrameWork_Unity'
$env:PYTHONIOENCODING = 'utf-8'
python Revolution.RevCLI/rev.py --project $project connect
python Revolution.RevCLI/rev.py --project $project doctor
python Revolution.RevCLI/rev.py --project $project --mode live commands --live
python Revolution.RevCLI/rev.py --project $project --mode live editor coverage
python Revolution.RevCLI/rev.py --project $project --mode live scene list
python Revolution.RevCLI/rev.py --project $project --mode live gameobject find --query Canvas
```

多步操作走 `batch.call`，一次往返执行多个动作（真实编辑器实测 10 步约 111ms，分 10 次调用约 994ms）：

```python
from revautomation.client import RevClient
client = RevClient(project, mode="live")
client.batch([{"action": "editor.status"}, {"action": "ab.config"}])
```

默认遇错即停（后续步骤常依赖前一步状态），需要全部执行时传 `stop_on_error=False`；单步可自带 `payloadJson` 与 `confirm`，批量不会绕过高危确认。

批量是**同一帧内同步跑完**，因此只接受能同步完成、不依赖帧推进的动作。`editor.play/stop/refresh/menu` 与各类构建会被明确拒绝，`gm.*`、`ui.*`、`runtime.*`、`scenario.*`、`test.*` 这类需要 Play 或运行时状态的动作同样拒绝。**游戏运行时测试（战斗、UI、业务流程）仍用 pipeline 或计划 JSON**——那里才有等待、断言和 finally 清理。

commands.json 是离线说明，commands --live 才是实际安装能力。2026-10-22 示例工程有 122 个 Editor 动作，另 3 个 Python 动作为 editor.launch/editor.wait/scenario.validate；20 个 Revolution.Tools 菜单都有映射。覆盖表用于发现漏接线，不意味着所有第三方控件或未来新增功能自动可用。

需要一句话 Agent 测试、GM 动态工具、独立开发版 Player、外部 CLI/Python，使用相邻的 [Revolution.RevMCP](../Revolution.RevMCP/README.md) 与 Skills/revolution-ai-automation。

完整的新手使用说明（操作示例、参数契约、pipeline、旧 scenario、报告与故障处理）：

- [HTML 文档](../Revolution.Document/自动化工具/Revolution自动化工具使用说明.html)
- [Markdown](../Revolution.Document/自动化工具/Revolution自动化工具使用说明.md)
- [RevMCP HTML](../Revolution.Document/自动化工具/RevMCP使用说明.html)

参数可用 call 动作ID --json 参数文件，避免 PowerShell 转义中文和复杂对象。全局选项 --project/--mode/--timeout/--confirm/--output 放在子命令前。Python 直接导入 revautomation.client.RevClient 可复用同一协议。

```powershell
python Revolution.RevCLI/rev.py --project $project --mode live --output result.json call component.get --json inspect.json
python Revolution.RevCLI/rev.py pipeline Revolution.RevCLI/examples/editor-inspect.pipeline.json --validate
python -m unittest discover -s Revolution.RevCLI/tests -v
```

Unity 接线位于 Assets/Revolution/Editor/RevAutomation，运行时共用模块位于 Assets/Revolution/Runtime/RevAutomation。仅复制 Python 不会自动安装 Unity 代码。已有工程使用 live，关闭工程后才可 batch 执行允许的资产动作；游戏 scenario 必须未暂停的图形 Play。

结果位于 Unity工程/Library/RevolutionAutomation/jobs/id，scenario 报告默认导出 Revolution.RevCLI/results/。CLI 退出码非零、response.success=false、业务断言或清理失败都属于失败；超时不代表动作未执行，不自动重放变更。

截至 2026-10-22：Python 19 项与 Unity EditMode 24 项通过；真实 MCP 到 Editor 的按钮、GM、断言、PNG、清理链路已运行。详见文档中的脱敏验证记录。
