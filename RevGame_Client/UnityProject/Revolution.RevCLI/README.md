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

## Unity Console 与编译

```powershell
python Revolution.RevCLI/rev.py --project $project --mode live editor console --severity error --limit 50
python Revolution.RevCLI/rev.py --project $project --mode live editor console --severity warning --query Config --offset 0 --limit 20
python Revolution.RevCLI/rev.py --project $project --mode live editor compilation
```

`editor console` 读取 Console 保留的消息。按级别、消息或源文件筛选后分页，返回 Unity 原文、文件、行列和级别。Unity 面板的搜索、级别过滤和折叠不会限制这项查询。每页都保留完整 JSON，`data.total` 是匹配总数，`data.truncated` 表示还有下一页。

`editor wait` 等待导入与域重载稳定。Unity 报告编译失败时命令以非零退出，本次编辑器进程内已完成的诊断随错误 JSON 一并返回，包含 `data.messages`。用 `editor compilation` 核对 `requestId`、`finished`、`compiling`、`failed` 以及每条诊断。修复后重新执行同一项目的刷新、重编和等待，再核对新的工单。

多步同步操作走 `batch.call`，一次往返顺序执行最多 64 步。支持 `payload` 对象，并兼容已有的 `payloadJson`。用 `id` 命名步骤，通过 `{"$ref":"前序ID#/data/字段"}` 传入结果值，数组、对象和布尔值保留原类型。`expect` 支持 JSON Pointer 的 `equals` 与 `exists`。返回每步 `elapsedMs` 和 `dataJson`。

```powershell
python Revolution.RevCLI/rev.py --project $project --mode live batch call --json Revolution.RevCLI/examples/ui-compose.batch.json --validate-only
python Revolution.RevCLI/rev.py --project $project --mode live --full batch call --json Revolution.RevCLI/examples/ui-compose.batch.json
python Revolution.RevCLI/rev.py --project $project --mode live ui layout --object-path RevBatchCanvas/Icon
```

`--validate-only` 检查结构、前序引用、动作参数名和同帧限制，不创建对象。真正执行时再解析引用值、验证目标与确认权限。默认遇错即停，`stopOnError: false` 可继续独立步骤。RevCLI 有步骤失败时返回非零；已经完成的步骤由调用方按需清理。跨帧等待、Play、编译及构建仍走 `pipeline` 或 RevMCP。

`ui.layout` 读取 RectTransform 到根的父级链、CanvasGroup、最近/根 Canvas 的排序、实际采样纹理及可投影时的四角与屏幕范围。遇到 Prefab Mode 中的对象，使用当前选择的实例 `--id`。该动作只读取状态，不刷新布局。屏幕范围用于定位，真实命中仍查 `ui.dump` / `ui.click`。

## 后台工单与 UI 命中

长任务可提交到在线 Editor，拿到工单 ID 后再等待或查询。提交时的 `--timeout` 是任务期限，查询时的 `--timeout` 是本次等待期限。

```powershell
python Revolution.RevCLI/rev.py --project $project --mode live --timeout 300 test run --test-mode EditMode --detach
python Revolution.RevCLI/rev.py --project $project --mode live job <工单ID>
python Revolution.RevCLI/rev.py --project $project --mode live --timeout 60 job <工单ID> --wait
python Revolution.RevCLI/rev.py --project $project --mode live job <工单ID> --cancel
```

提交回执只确认 Editor 接受请求。等待超时或请求取消后可以继续查同一个 ID。首次读取终态会释放内存结果并永久删除 Library 内的临时身份文件。需要保留完整结果时，在领取终态的同一次命令使用 `--output <文件>`。普通同步调用优先走内存。

Play 中可查询屏幕点或指定控件中心的命中顺序。返回每个 Raycaster、命中对象、点击接收对象和可交互状态。坐标以左下为原点。此查询不会发送输入事件。场景查找可以组合组件、Tag 和 Layer。

```powershell
python Revolution.RevCLI/rev.py --project $project --mode live ui raycast --screen-x 780 --screen-y 480
python Revolution.RevCLI/rev.py --project $project --mode live ui raycast --id instance:12345
python Revolution.RevCLI/rev.py --project $project --mode live gameobject find --type Canvas --tag Untagged --layer UI
```

在 `pipeline` 和 `rev_run` 内嵌 `batch.call` 时，批处理步骤 `payload` 中的 `$ref` 由 Unity 解析，作用域为当前批次。外层引用用于批次配置、整个步骤表或旧式 `payloadJson` 字段。

设计参考了 Unity 官方 CLI 的后台工单生命周期、Coplay 的组合式场景查找，以及本地 Unity True CLI 的 EventSystem 命中诊断。 [Unity CLI 工单](https://github.com/Unity-Technologies/skills/blob/main/skills/unity-cli/references/integration-advanced.md) · [Coplay 工具参考](https://github.com/CoplayDev/unity-mcp/blob/beta/website/docs/reference/tools/core/index.md)

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

执行结果默认通过终端返回，`--full` 可展开完整 JSON。旧文件桥的临时工单在领取终态后立即永久删除。只有明确传 `scenario run <文件> --report-dir <项目文档目录>` 时才导出报告，导出完成后回收逐轮中间文件。超时后通过原 ID 和实际状态核对，工具不会自动重放变更。

截至 2026-10-22：Python 19 项与 Unity EditMode 24 项通过；真实 MCP 到 Editor 的按钮、GM、断言、PNG、清理链路已运行。详见文档中的脱敏验证记录。
