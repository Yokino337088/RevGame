# MCP 与 CLI 的 token 成本

**默认一套 CLI**：编辑器操作和运行时 GM（`game gm`）都能走 CLI；MCP 是可选项。本页回答的是“同一件事两边都能做时怎么省”，以及为什么这样分工划算。

数字是 2026-10-31 在 `RevolutionFrameWork_Unity` 本体工程实测（139 个 Editor 工具已注册，`tiktoken` `o200k_base` 计数，同一 `editor.status` 只读调用）。

## 实测成本

| 路径 | 每轮固定开销 | 单次调用结果 | 其他 |
|---|---:|---:|---|
| MCP 全量（139 工具） | **17,255** | 447 | schema 每轮都随请求重发 |
| MCP 紧凑（`--compact-tools`，3 入口） | **325** | 445 | 发现一次 340（带 `query`） |
| CLI（`rev.py call`） | 0 | **206** | 无常驻约 329ms；`daemon start` 后约 201ms |

补充两条容易踩的：

- `rev_discover` **不带 `query`** 一次 12,007；带 `query` 只要 340。相差 35 倍。
- 直接读 `commands.json` 全文 8,688 token / 136 个动作；不要为了查一个动作名读它。

## 为什么差这么多

1. **schema 是每轮的税**。MCP 工具定义随每次请求重发，和对话多长无关。全量 139 个工具 ≈ 每轮 1.7 万，聊 10 轮就是 17 万；紧凑模式压到 325，基本可以忽略。
2. **MCP 结果比 CLI 贵约 2 倍**。MCP 要同时带 `content` 和 `structuredContent`（447 vs 206），CLI 只打印一份。
3. **CLI 没有固定成本，但每次启动进程**（无常驻约 329ms，常驻服务下约 201ms），命令回显也算输入。编辑器操作默认走 CLI，见 `revolution-unity-editor`。

## 选择规则

| 情况 | 用 | 理由 |
|---|---|---|
| 编辑器操作：场景、脚本、Prefab、资产、导表、打 AB、打包 | CLI | 零固定开销，结果只有 MCP 的一半 |
| 编辑器里一次做很多步 | `scene.compose`、`batch.call`、`pipeline` | 把 N 次调用压成 1 次，这是省得最多的一招 |
| 运行时 GM、本局状态 | CLI `game gm` / `game call` | 同样读本局动态注册表，零固定开销 |
| 重量级测试：长流程、要带截图/GIF 的 HTML 报告 | MCP `rev_run` 一份计划 | 带断言、等待和 finally，一次提交，长测试总 token 反而更少 |
| 客户端只能全量暴露工具 | 全量 MCP，但会话尽量短 | 17,255/轮，长会话是主要浪费 |
| 不确定有哪些能力 | `commands --query <词>` | 本地过滤，不占 Unity；MCP 侧用 `rev_discover` + `query` |

粗算判据（T=会话轮数，N=调用次数）：

- 紧凑 MCP ≈ `325×T + 445×N + 340`
- CLI ≈ `206×N`
- 每次调用 CLI 都便宜 239，还没有每轮 325 的固定开销，所以编辑器操作没有理由走 MCP。MCP 的价值在 CLI 做不到的地方：运行时注册表和证据报告。

## 别用 token 换错误的结论

省 token 不能牺牲验证：业务断言、`finally` 清理、真实截图和报告该做还得做。省的是**发现和目录**，不是执行和证据。

CLI 常驻服务只影响耗时，不影响 token：`connect` 之后单次调用约 200ms，不需要为了速度改走 MCP。

## 复测方法

```powershell
py -3.13 Revolution.RevMCP/tests/measure_tokens.py --project <工程路径> --output result.json
```

脚本只调 `editor.status`，不会改工程；工具数变化（新增动作、插件）后重跑一次再下结论，不要沿用旧数字。
