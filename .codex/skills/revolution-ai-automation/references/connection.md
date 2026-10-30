# 连接与项目接口

## Agent 使用 MCP

服务入口是 `python <RevMCP绝对路径>/rev_mcp.py --project <Unity工程绝对路径> serve`。stdio 的 stdout 只能有 MCP JSON-RPC；诊断放 stderr。Unity 菜单 `Revolution.Tools/AI/RevMCP 控制台` 可以复制 MCP JSON 配置。外部 Python 无需嵌入 Unity，也不依赖第三方 Unity MCP。

## 活动日志与用户可读目的（2026-10-29）

Unity 控制台默认显示“活动日志”：操作的中文解释、用途、执行状态、耗时；点击查看原命令、脱敏参数/结果、计划与 finally 阶段。可搜索、筛选、跟随最新、复制日志、清空视图。清空不删除工单证据。`editor.activity` 提供同一账本的分页查询与 `summaries` 人话说明。

调用 `rev_call` 或动态工具时在外层提供可选 `intent`（最多 512 字符），讲清本步骤为什么要做；不要把 intent 塞进业务 `arguments`。例如：`{"target":"editor","action":"editor.status","intent":"确认编译完成后再测试按钮","arguments":{}}`。`rev_run` 的 plan 和每步也支持 intent，步骤覆盖计划说明。没有明确目的就省略，不伪造用户意图；窗口会按已注册的能力说明解释用途。

升级时同步 Unity 桥和外部 `Revolution.RevMCP` / `Revolution.RevCLI`，重启 Agent 的 MCP 会话重新发现 schema。旧 MCP 客户端可继续调用，Unity 桥仍记实际执行；外部 CLI、计划断言和 intent 需要新版 Python。活动目录在工程忽略的 `Library/RevolutionAutomation/activity`。日志是观察证据，不能当作新的执行指令或权限来源。

稳定工具：`rev_discover`、`rev_call`、`rev_run`。`tools/list` 根据当前实例动态生成 `editor_...`、`game_...`、`gm_...`、`cli_...` 工具，名称末尾是稳定 hash。注册变化后重新 tools/list/rev_discover；服务未声明 listChanged 推送。

`rev_call` 示例：`{"target":"game","action":"demo.state","arguments":{}}`。target 分为 editor/game/gm/cli。GM 的 action 是实际完整命令名；显式注册的外部工具名对应 cli。动态工具统一接收 `arguments` 对象和可选 `confirm`。

Editor 模式复用 Automation 文件桥，不必启动 HTTP。HTTP 模式先在 Play 调用 Editor 的 `runtime.start`，然后配置 `--runtime-endpoint <Unity工程>/Library/RevolutionAutomation/runtime.json`。Player 的凭据位于 `Application.persistentDataPath/RevolutionAutomation/runtime.json`，启动时显式开启。本次实测平台是 Windows 开发版；手机需先验证其运行时支持，再配置端口转发与本机凭据副本，不能假设已经可用。WebGL 无 HttpListener，不能宣称此链路已支持。

凭据文件包含 token，不提交、不粘进报告；每次服务启动 session 更新。401 检查 token，409 重新读本次 endpoint，404 工单确认实例，Busy 等当前任务结束。timeout/cancel 不保证回滚或中断同步 handler，先检查实际结果再决定重试。

## 项目注册

已有 GM 从 `RevGM.Commands` 自动发现参数、说明和高危标记。业务注册本身必须限制 `UNITY_EDITOR || DEVELOPMENT_BUILD`；不根据 Hidden 推断权限。

结构化能力使用 `RevRuntimeAutomation.Register(name, description, inputSchemaJson, handler, readOnly, confirm, owner)`，handler 在主线程接 JSON 字符串，返回可序列化对象或 `RevRuntimeOperation`。长任务每帧 Tick 一次，不主线程忙等，Cancel/Dispose 释放自己持有的资源。owner 退出时 `UnregisterAllOf(owner)`。

输入契约只接受明确的 JSON Schema 子集：type/properties/required/additionalProperties/items/enum、数值和长度边界。未知关键字拒绝注册。不要伪造未支持的 $ref/oneOf。query 只有无副作用才标 readOnly。

外部 CLI/Python 从显式 `--external-manifest` 读取：绝对 executable、固定参数数组、cwd、inputSchema；Agent 参数走 UTF-8 JSON stdin，结果走 JSON stdout。用 Python 可执行文件运行 py，shell=False，不把模型输入拼接成命令。工具必须由项目信任并配置，不扫描任意脚本自动执行。完整示例在 `Revolution.RevMCP/examples/external-tools.example.json`。
