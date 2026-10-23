# 自动化测试工作流

## A. 定位工程

从用户当前工程、明确路径或工作区递归发现包含 `Revolution.Automation/rev.py`、`Assets/Revolution/Editor/RevAutomation` 的 Unity 项目。多工程匹配时结合用户提到的功能/活动文件选择，不要随意选首个。CLI 默认工程可能指向 Revolution 示例工程，务必显式传 `--project <UnityProject>`。

检查：

```text
python <AutomationRoot>/rev.py --project <UnityProject> doctor
python <AutomationRoot>/rev.py --project <UnityProject> --mode live editor status
python <AutomationRoot>/rev.py --project <UnityProject> --mode live gm list
```

`doctor` 的 `bridgeInstalled`、PID/session、工程路径，`editor status` 的 `playing` / `compiling` 都需检查。`gm list` 在 Play 时是当前运行期注册表；编辑态结果不代表 Play 里已经注册。

## B. 设计测试

将自然语言需求拆成：

- **前置状态**：进入哪个 scene/scope/domain、测试账号/存档是否安全；
- **动作**：现有 GM、受控 Editor Check 或用户实际 UI 输入；
- **等待条件**：异步加载、过场、战斗结算用 `until`，不用固定长 `wait` 猜时间；
- **断言**：服务状态、返回文本、资源/池计数、业务结果；
- **清理**：每轮 `cleanup` 与整场 `teardown`，失败后也尽可能恢复。

先读 GM 参数签名和业务实现，核实 `expected` 精确字符串。不要把“命令存在”“场景 JSON 有效”“编译成功”当成业务功能通过。

## C. 场景契约

```json
{
  "name": "功能冒烟测试",
  "repeat": 1,
  "warmup": 0,
  "stopOnFailure": true,
  "failOnUnityError": true,
  "setup": [
    { "kind": "until", "command": "业务/就绪", "expected": "ready", "timeoutSeconds": 60, "intervalSeconds": 0.2 }
  ],
  "steps": [
    { "kind": "gm", "command": "业务/执行" },
    { "kind": "until", "command": "业务/状态", "expected": "完成", "timeoutSeconds": 60, "intervalSeconds": 0.2 },
    { "kind": "assert", "command": "业务/断言", "expected": "通过" }
  ],
  "cleanup": [{ "kind": "gm", "command": "业务/复位" }],
  "teardown": [{ "kind": "gm", "command": "业务/退出" }],
  "thresholds": { "maxFrameP95Ms": 0, "maxFrameMs": 0, "maxMonoUsedBytes": 0 }
}
```

有效字段仅限：顶层 `name/repeat/warmup/setup/steps/cleanup/teardown/stopOnFailure/failOnUnityError/thresholds`；step `kind/command/expected/frames/seconds/timeoutSeconds/intervalSeconds`。`kind` 仅 `gm/wait/until/assert`；`steps` 不能为空。`expected` 为 Message 精确字符串、大小写敏感。

性能门限 `0` 表示关闭。若用户不是要测性能，不要擅自加 P95 门槛；若用户要求性能测试，先说明目标设备、分辨率、采样时长等缺少的关键口径，再定义门限。

## D. 执行模式

`gm.exec` 与 `scenario.run` 要求**图形 Unity Editor 中未暂停的 Play**。不可在 batchmode 假装运行游戏。

- 编辑器已 Play：直接跑 scenario；
- 编辑器未 Play：从 Build Settings / 项目启动流程找真实启动场景；若目标流程是自动化入口，可用 `editor play --scene ...`，再等 GM 的 `ready` 条件；
- 用户编辑器已打开但不是 Play：不要无提示地覆盖其工作状态，明确当前状态后再启动；执行后尽量按 scenario teardown 恢复业务屏幕；
- Play 期间禁止导表、构建资源等 Editor 资产操作；
- 一次只发一个 Automation job，不并发请求；`Busy` 表示等待前一 job 完成。

建议执行：

```text
python <AutomationRoot>/rev.py --project <UnityProject> scenario validate <Scenario.json>
python <AutomationRoot>/rev.py --project <UnityProject> --mode live --timeout 600 scenario run <Scenario.json> --report-dir <ReportDir>
```

将原始 `response.json`、`scenario-result.json`、`iterations.jsonl`、`unity-errors.jsonl` 和自动生成的 HTML/CSV/JSON 报告关联同一 job。优先通过 Python 参数列表调用，避免 Windows PowerShell 中文 GM 命令乱码。

## E. 结果判定

严格检查：

1. 最终 CLI response `success=true`；
2. `data.success=true`、`completed == requested`、`failedIterations == 0`；
3. `errors` 为空、`unityErrors == 0`；
4. 具体业务断言全命中；有视觉需求时另有 MCP 截图/对象检查证据。

任何一项不满足均不能汇报通过。保留错误首因及 log 路径。若修复后重跑，最终总结要提及首次失败、修复内容和重测结果。

## F. MCP 辅助验证

优先使用可用 MCP 获取 Unity readiness、编辑器是否 Play、Game/Hierarchy 画面、Console 编译错误与运行错误；调用具体 MCP 工具前先查询其 schema。MCP 工具不可用时不要假设存在，明确记录“视觉/编辑器检查未执行”。MCP 不能替代 `scenario-result.json` 的自动断言，也不能自己生成虚假的 GM 通过结果。

## G. 常见故障分诊

- `bridgeInstalled=false`：检查 Unity 工程是否打开、Revolution Automation Editor Bridge 是否编译/启用。
- `compiling=true`：等待重载完成，重查 status 和运行期 GM list。
- `没有这条命令`：Play 中 GM 未注册或旧程序集仍运行；停止 Play、刷新/编译、重新进入后查 list。
- `Busy`：顺序等待，不要并发发命令。
- `Expired`：加大请求超时，或缩短排队。
- `InterruptedByReload`：动作不会自动重放；检查已执行副作用后再决定重跑。
- `IOException` 原子替换文件被占用：可能 Unity 与 CLI 同时写进度文件；从 job 响应确认后串行重跑一次。
- 业务 `until` 超时：读取真实返回与 `unity-errors.jsonl`，确认 setup 是否完成、`expected` 是否与返回完全一致。
- 过场 `WaitUntilReadyAsync` 超时：检查 BoardView/面板就绪门禁；不要在 loading 尚未结束时强行开始游戏。
