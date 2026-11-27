# RevGame 专项约定

## 交付前的自动检查

- 完成功能后，最后一次影响 Unity 编译的改动必须在目标在线编辑器主动完整重编译并验收，再进入 Play 或交付。先确认编辑态。用户正在运行的对局先交接。用 RevCLI 刷新导入并等待稳定，依次运行 `editor recompile`、`editor wait`、`editor compilation`。诊断的 `requestId` 必须对应重编回执的 `id`，同时满足 `finished=true`、`compiling=false`、`failed=false` 且无 Error。验收后再改脚本须重跑。
- 直接执行 `editor console --severity error --limit 50` 查 Unity Console，用 `--query` 按原文或源文件筛选。若 `editor wait` 返回非零，读取随附的同一编辑器会话诊断，再到 `editor compilation` 核对本次结果。根据文件、行列与错误信息修复任务范围内的问题，再重编并复测。

- 改代码或 Unity 资产后，在仓库根运行 `python tools/RevPolicy/check.py`，修正本次新增的违规。准备提交时用 `--staged` 检查实际暂存内容。Git hook 和 CI 使用同一个检查器。
- 材质、Shader、资产与 `.meta`、GUID、框架快照更新和部分 C# 规则可以自动检查。 PR 还会以 main 为基线检查脚本位置、原生库路径、各平台 AB 清单与文件摘要、新的外部插件和包来源。GitHub 日志中的 REV120 到 REV124 分别指向脚本、Shader、原生库、StreamingAssets 和第三方依赖。面板职责、中文注释质量、资源寿命与实际画面仍按本文件及当前专题验收。安装和诊断见 [Codex 插件与规范检查](../../../README.md)。
- 交付代码前，按 [新手注释守则](code-and-comments.md) 核对实际改动中的声明和方法内关键实现。补齐遗漏、过期和不准确的注释。自动检查器通过后仍需完成这一步。

## 编辑器自动化调用方式（2026-10-23 实测）

- 用户要求优先采用更快的通道。已有常驻MCP连接或需要连续多次操作时，优先复用同一个RevMCP stdio会话；只有一两条临时编辑器命令时，优先 `Revolution.RevCLI/rev.py --project <工程> --mode live call <真实动作ID>`，省去常驻服务的启动/握手成本。
- 不再逐条启动 `rev_mcp.py ... call editor ...` 作为常规调用方式：本次状态查询/AB扫描中，其耗时高于直接CLI和常驻MCP。短命令优先合并在一次工具调用或现有pipeline中，避免重复启动、重复读大清单；Unity资产修改、构建、Play仍串行执行。
- 会话首次使用、编译/域重载后按技能重新发现真实能力；不能为了速度跳过参数校验、业务断言、错误检查、资源依赖门禁或收尾。已有编辑器只用live，不另启batch Unity。
- 测量范围是调用通道开销；AB构建本体仍调用同一套Unity/Revolution工具，不能宣称切换CLI/MCP会同比缩短打包时间。数据与边界见 `RevGame_Client/文档/进度同步/CLI与MCP速度对比-2026-10-23.md`。

## RevMCP连接与调用展示（2026-10-31用户要求）

- 后续游戏测试优先使用 `RevGame_Client/UnityProject/Revolution.RevMCP`，显式连接 `RevGame_Client/UnityProject/RevGame.Unity`，连续操作复用同一个常驻 stdio 会话。此要求优先于上述少量命令优先直接CLI的旧约定。
- 先连接并执行真实 `rev_discover`；首次连接及编译/域重载后重新发现，再按运行期注册表通过 `rev_call` 调用完整GM名称。带断言与清理的测试计划使用 `rev_run`。
- 在对话中明确展示实际 `rev_discover`、`rev_call`、`rev_run` 的调用顺序、关键参数、目的与结果。未执行的工具必须标明未执行，不能把直接CLI或Python Hub调用冒充MCP工具调用。
- 连接成功不等于GM已注册或测试通过；编辑模式缺少运行期GM时如实说明。工具调用成功仍需核对业务断言与清理结果。

## 功能完整性与缺陷修复闭环

- 功能开发验收、找 Bug 与修后回归按 [缺陷闭环](../../revolution-ai-automation/references/defect-closure.md) 执行。由 Codex 等 Coding Agent 根据需求和当前源码列用例，覆盖相关主流程、拒绝、异步和退出重入。用实际业务状态、画面与清理判定各用例，标明受阻或未执行项。
- 发现偏差时从本次响应、活动日志和实际画面记录首轮实测，定位具体源码或资产的 `文件:行号`。写清触发条件、根因与修改建议。任务包含修复时落地证据支持的最小改动，完成目标编辑器重编译和项目检查后，按原输入和断言复测并覆盖受影响的回归。已确认根因可复用报告和当前源码位置，按证据补足复现。
- 完整性审查和缺陷闭环在对话关联用例、预期与实测、代码位置、修复前后和清理结果。只有明确要求报告时才生成文件。业务修复在 RevGame，框架修复回 Revolution 源仓库，再同步所需导出。

## 自动化工单与报告

- 工具只在 Unity 工程的 `Library/RevolutionAutomation/jobs`、`activity`、`screenshots` 和 `detached-jobs` 暂存当前操作。收到结果后由消费者清理文件。实时活动窗口只在开启期间采集，读取即删。异常退出遗留可用 `storage.status` 检查，再用 `storage.prune` 预览与回收。

- 优先使用内联计划、内存回执和标准输入。确需临时工单时，保存在系统临时目录或 `.codex/work/`，最后一次读取后立即永久删除本次生成的文件。`RevGame_Client/UnityProject/Revolution.RevMCP/results/` 禁止写入运行产物。
- 自动化默认在对话汇报实测、断言、失败和清理结果。只有用户明确要求报告时才生成文件。指定路径用 `reportDirectory`，只要求报告时用 `report: true` 发现已有的 `RevGame_Client/文档/AI自动化测试报告/`。导出后核对 `evidencePath`。
- 文档目录只保留 HTML 与必要截图/GIF。禁止生成 ZIP，确需另存的采样数据放 `.codex/work/`。
- 2026-10-28用户更新：`.codex/skills/` 与项目协作说明需要入库；`.codex/work/` 原始测试证据、依赖缓存以及本机配置/凭据继续忽略，不提交。
- 2026-10-29用户更新：后续性能测试不导出 Profiler 录制数据（如 `.raw` / `.data` / 内存快照），不自动调用 `SaveProfile`；报告仅保留必要的性能摘要、结论与截图/GIF。

## Unity Play 与测试包交付

- 默认直接在 Unity Editor Play 验收。仅在用户明确要求交付测试包，或目标验收项只能在独立 Player 中验证时，才进入测试包流程。
- 新测试包统一构建到仓库根目录 `打包测试(不入库)/<任务名>/`，按现有 `.gitignore` 排除。`.codex/work/` 用于必要的临时输入和机器证据，禁止用作测试包输出目录。
- 需要更新现有测试包时，先核对用户要验收的包及路径。适合增量交付时更新对应的 Player 程序集 DLL 和 AB。
- 先确认实际测试包、Unity版本及脚本后端。Mono 使用针对目标 Player 编译且配置兼容的业务 DLL（及必要依赖），不可直接拿含 UNITY_EDITOR 代码的编辑器程序集覆盖。替换前确认游戏已退出，备份本次覆盖文件；AB 同步必要依赖、映射和清单，验证编译结果、文件哈希及实际启动/相关业务。保留玩家本地配置、凭据和存档。
- IL2CPP 不能仅靠替换普通托管业务 DLL 更新已转为原生的代码；原生插件、后端、播放器设置或内置场景等变化也可能超出 DLL/AB 增量范围。确实无法增量交付时先说明具体原因，再确定处理方式，不默认整包重打。
