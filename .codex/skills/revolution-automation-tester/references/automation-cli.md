# Revolution.RevCLI CLI 速查

从 Unity 工程仓库根目录执行，或显式传 `--project`。`--mode/--timeout/--project` 等全局选项在子命令之前。

```text
python <AutomationRoot>/rev.py --project <UnityProject> doctor
python <AutomationRoot>/rev.py --project <UnityProject> --mode live editor status
python <AutomationRoot>/rev.py --project <UnityProject> --mode live gm list
python <AutomationRoot>/rev.py --project <UnityProject> scenario validate <scenario.json>
python <AutomationRoot>/rev.py --project <UnityProject> --mode live --timeout 600 scenario run <scenario.json> --report-dir <directory>
python <AutomationRoot>/rev.py --project <UnityProject> job <32-char-job-id>
python <AutomationRoot>/rev.py --project <UnityProject> report <scenario-result.json> --directory <report-directory>
```

## Automation 通信与产物

CLI 写请求到 Unity 工程 `Library/RevolutionAutomation/inbox/`，Editor Bridge 主线程执行后在 `jobs/<id>/response.json` 返回。完成信号看 response，不看 progress。job 通常含 `request.json`、`response.json`、`progress.json`、`scenario-result.json`、`iterations.jsonl`、`unity-errors.jsonl`；报告导出 HTML/CSV/JSON。

此文件通信桥是 Revolution.RevCLI 自己的 CLI/Editor Bridge，不等于 Unity MCP，也不表示框架内置 GM↔MCP 服务。

## `scenario-result.json` 常看字段

| 字段 | 含义 |
|---|---|
| `success` | 场景整体成功 |
| `requested / completed` | 要求轮次与已完成轮次 |
| `failedIterations` | 失败轮数 |
| `elapsedSeconds` | 总墙钟时长 |
| `frameMeanMs / frameP95Ms / frameMaxMs` | Play 内未缩放帧间隔统计，含编辑器与采集开销，不等于独立 CPU/GPU 时间 |
| `peakMonoUsedBytes / peakAllocatedMemoryBytes` | 采样峰值，可能漏掉两次采样间的短峰 |
| `unityErrors` | Unity Error/Exception/Assert 计数 |
| `errors[]` | 第一失败原因通常最有用 |

## Python 客户端

Python 3.10+、仅标准库，可使用 `revautomation.client.RevClient`：`client.call(action, payload, confirm=False)`。返回结构化字典；Unity 动作失败须检查 `success`，超时/客户端错误会抛异常。Windows 下避免把中文直接插入 PowerShell 命令字符串，使用参数列表或 UTF-8 JSON 文件。

## 边界

- GM/scene scenario 只在图形 Unity 未暂停 Play 中执行；batch 不能代跑游戏逻辑。
- 资产/导表动作要求非 Play。
- 默认 Unity 项目可能指向框架 Demo，测试其他工程时显式传 `--project`。
- scenario `assert/until.expected` 是 GM Message 精确匹配，不是子串或表达式。
- 未注册业务 GM 时 CLI 不会替项目创造业务测试能力。
