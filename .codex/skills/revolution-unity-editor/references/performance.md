# 少往返与性能诊断

## 连接

编辑器操作优先 RevCLI 常驻服务：`connect` 一次（内含 `daemon start`），之后 `rev.py ...` 只做一次本机转发。服务发现 CLI 代码或动作清单更新后会自动退出，该次调用转本地执行，下次 `connect` 再起新服务。同一工程、只读 `editor.status`、15 轮交替取样的中位数：无常驻 329ms，常驻 201ms，`curl` 直连 `/call` 130ms；`rev.cmd` 390ms 和 `py -3.13` 启动器 278ms 反而更慢，不要用。数字是单机一次测量，换环境需用 `tests/benchmark_daemon.py` 复测。

更新 Unity 桥后 `editor wait` 等编译完成、重新查契约。运行时 GM 用 `game` 命令直连；用 MCP 时，更新 Python 服务后重启该 MCP 会话。安装路径和解释器由用户/项目配置决定，不改其他工具的登录凭据。

服务支持 `--compact-tools` 时可只暴露 `rev_discover`、`rev_call`、`rev_run` 三个稳定工具。此模式仅缩小 tools/list，不删除真实能力；先按关键词发现，拿返回的 ID、keys/inputSchema，再用 rev_call。旧版本不支持时省略该选项。

```text
python Revolution.RevMCP/rev_mcp.py --project RevolutionFrameWork_Unity --compact-tools serve
```

以上是仓库根目录下的 AI/CI 参考，不要求用户手敲。MCP 客户端路径确需固定时用占位符并明确工作目录。

## 查询与批量

- `rev_discover` 空参数完整发现；新版支持 `target/query/offset/limit`。显式发现不缓存结果，返回 unavailable 必须保留，不把离线清单当真实注册。
- 普通 Editor 调用只查 Editor 目录，复用最多两秒的能力说明；session、domainId 域代际、Play、暂停、编译、导入变化失效，变更或失败后失效。Session 保留工单跨重载恢复，domainId 单独随域变化；旧桥缺此字段仍受两秒TTL约束。业务 get/state 每次真实执行，不缓存业务结果。
- CLI/Player 会话操作不需要先查询 Unity 编辑器；GM/game 仍按真实本局注册表校验。
- `batch.call` 最多 64 步，默认遇错停止。只用于允许的同步动作。每步仍过参数和确认校验；禁止嵌套、构建、编译重载、Play、测试和运行时跨帧动作。
- `rev_run` 保留跨帧等待、状态断言和 finally；不可为了省往返改成 batch 或并发变更。

## 证据与活动日志

普通查询保留 JSON 与活动日志，不为每次 status 重做图像扫描/HTML；测试计划和本次截图独立生成报告目录，不扫描历史报告图墙。

活动窗口读取账本而非向 MCP 自查。最近 500 项、最多 64 个日志流、每次 Poll 共享 256 KiB / 128 行预算，积压跨刷新消化；缓存行说明和筛选结果，只绘制视口内控件，不在 OnGUI 读盘。无变化不主动持续 Repaint。半行 UTF-8 等下次完整后解析，坏行不阻塞后续事件。

`editor.activity` 的 `pending=true` 表示预算积压，只读重查会继续原游标；此时不能用 total 宣称“全部日志”。单次最多8批并在批间检查20ms软预算（不含筛选/摘要，非硬期限）。`totalIsFinal=true` 仅对观察到的64流/新流尾部256KiB/最多500条视图表示无预算积压、无已记录读取错误，非全历史，也不保证下一页保持快照不变。半行/无效行不计入total；同时检查 error、invalidLines、scope。

## 固定输入复测

使用当前安装版本的 `Revolution.RevMCP/tests/benchmark_unity.py`，显式传工程、样本数和被忽略的输出目录。它走真实 stdio→Unity，只读 editor.status；不会开关编辑器或 Play。

```text
python Revolution.RevMCP/tests/benchmark_unity.py --project RevolutionFrameWork_Unity --samples 20 --output .codex/work/mcp-benchmark/stdio.json
```

分别记录 tools/list、第一条冷调用、mean、median、P95 和所有样本。修改前后同一个编辑器、相同输入、相同窗口状态，不同时跑另一任务。窗口卡顿另测开启/关闭；Python耗时不代表渲染帧时间，离线 reader/虚拟行测试不代表整机 UI 已验收。没有有效基线只报告本次读数与结构性成本，不能编加速倍数。
