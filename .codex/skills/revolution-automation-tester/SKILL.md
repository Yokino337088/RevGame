---
name: revolution-automation-tester
description: 执行或维护 Revolution.RevCLI 的 GM scenario 循环回归、预热、性能采样及报告时使用；发现真实GM，验证轮次、业务断言和清理。跨Editor/业务/UI/截图/独立Player/外部工具的一句话自动化任务使用revolution-ai-automation与RevMCP，本Skill保留旧scenario格式与采样流程。
---

# Revolution Automation Tester

把“一句话测试需求”转成**真实业务入口 + 精确预期 + 可回收清理**的自动化测试，并实际执行、核对结果。本 Skill 专注 Revolution.RevCLI 的 GM scenario 循环/性能采样；跨 Editor/业务/UI/截图/Player/CLI 的一句话任务优先使用 `revolution-ai-automation` 与 RevMCP。已有 scenario 可继续复用，无需改格式。

## 真实工程示例

维护项目测试/GM接线时，先在 [RevGame GitHub工程](https://github.com/Yokino337088/RevGame) 阅读同类真实指针、GM、规则或Play检查，按 [RevGame测试规范](../revolution-ai-automation/references/revgame-testing.md) 提取契约与失败/清理边界。该参考未随单独安装分发时，从官方Skills源读取或直接查RevGame代码；样本参数与命令必须适配目标项目。用户/AGENTS证据策略优先，RevGame禁止报告ZIP和提交.codex。

## 一句话执行协议（优先于后文的通用说明）

收到“测试 X”后立即执行，不只输出计划、教程或待用户运行的命令。把缺少的非关键参数从工作区推断并说明，默认一次功能回归、零预热、不设性能达标阈值；用户明确次数与目标则采用用户值。

1. 先运行 `scripts/discover_tests.py --root <用户工程或工作区> --query <测试目标>`。用结构化结果定位真实工程、CLI、Build Settings 场景和既有 scenario；多工程时结合用户路径/当前文件选择，无法唯一选择才询问。
2. 读候选 scenario、业务源码、相关检查入口，生成“需求—前置—动作—断言—清理”覆盖表。候选只是线索，不照搬模板中的虚构命令，不因名称匹配就运行。
3. 通过 Automation `doctor/editor status` 与可用 MCP 观察编辑器。发现编译中则有界等待；桥接未连通则检查编译/连接，不重复开启被占用工程。缺编辑器路径时优先从已运行 Unity 实例发现；不能发现才报告阻塞。
4. 无需把普通测试中的进入/退出 Play 交回用户：先记录原始状态，说明将进入测试 Play，选择真实启动场景执行。遇到未保存场景、用户正在操作的 Play 或破坏性存档动作则不得擅自覆盖；解释冲突并暂停该动作。
5. 用运行期 `gm list` 验证命令注册。没有匹配入口时先通过 MCP 调用已存在的受控 Editor Check；仍无法覆盖时，**测试请求允许补最小的项目侧测试接线**（仅开发构建 GM/Check/查询/夹具），不允许重写玩法、改框架内核或随意修改生产数据。按现有 API 编写、复用真实服务动作；实现后编译、重新确认注册，并实际继续运行，不停在“入口已补好”。不允许编辑时，列出无法验证项。
6. 场景正确不等于覆盖完整：等待回到 idle 只能证明状态结束，不能证明造成伤害、段数正确、实际目标命中或资源回收。每项关键需求要有相应数据断言；缺失时补查询或标为未验证。重复测试必须每轮复位，首次 setup 不会逐轮执行。
7. 跑 Automation 场景，再用 MCP 获取 Console/画面证据（用户关心特效时）。同一编辑器的变更动作串行，不与测试并发 stop/play/改场景；只读观察在兼容时进行。视觉无工具则明确未测，不能编造截图。
8. 执行结束后收尾、核对原始 Play 状态和业务回收，报告结果与报告路径。发现失败先诊断根因；只修本次测试接线/编码错误后有界重跑，玩法缺陷另行报告或在用户要求修复时处理。

### 一句话示例与应有动作

- “测试 Boss AI”：发现 Boss scenario，验证夹具章节、近远目标、技能事件计数/伤害/回收；强制技能测覆盖，另测自主选招与章节解锁，不把第1章强制九招当第6章难度测试。
- “测一下这个面板”：从当前面板定位打开入口，执行开关/数据刷新/返回，再观察截图与订阅清理。
- “重复测落子20次”：每轮重置棋盘/手牌/阶段，使用同一真实落子入口，断言扣牌额度、占格与失败不扣牌。
- “看看激光动画”：用真实技能入口进入预警与命中，MCP取实际画面；不以编译成功代替视觉验收。

## 什么时候使用

用户提出“帮我测一下 X”“跑一下 Boss AI 自动化测试”“验证这个功能/修复”“重复回归 UI/业务流程”等请求时触发。用户只说“测试一下”时，先从当前文件/最近任务/项目可用 GM 推断对象；只有无法确定测试目标或缺少关键通过条件时才简短澄清。默认选最小、非破坏的冒烟测试；若测试会改存档、清数据、构建覆盖产物或触发付费/联网操作，先说明风险并要求确认。

## 执行流程

### 1. 找到真实工程与工具

1. 从当前工作区/用户路径定位 Unity 工程和 `Revolution.RevCLI/rev.py`（可能位于 `UnityProject/Revolution.RevCLI/`）。不要假设 cwd、工程名、场景名或 Python 启动器。
2. 检查 `Revolution.RevCLI/README.md` 和 `commands.json`；需要精确 schema 时读取 `Revolution.Document/自动化工具/Revolution自动化工具使用说明.md`。
3. 用实际 Python 解释器执行：`python <automation-root>/rev.py --project <unity-project> doctor`，并查询 `editor status`。先看 `bridgeInstalled`、`playing`、`compiling`、会话和工程路径。
4. 若 Unity 正在编译，等待 `compiling=false`；若 GM/scenario 测试不在 Play，选择项目真实启动场景后用 `editor play`。启动场景从 Build Settings/项目源码/README 中发现，不猜默认名字。
5. `gm list` 读取**运行期真实注册表**，不能拿源码里找到的 `RevGM.Register` 当已在当前 Play 注册。编辑态静态快照只作线索。

### 2. 把一句话变成测试计划

将请求拆成可观察的断言，优先覆盖：

- 正常路径：用户操作被接受，状态/数据产生预期变化；
- 边界/反例：无目标、锁定、非法输入、冷却中、资源缺失等相关边界；
- 生命周期：切回合、关闭面板、退出场景、对象池/订阅/计时器回收；
- 若用户关心画面：特效、预警、顺序、遮挡、返回后的清理。

每条断言必须对应**真实 GM 的稳定返回字符串**，或真实 Editor/Play Check 的成功结果；`expected` 是完整精确字符串比较，不是模糊包含或猜测。

### 3. 选最贴近真实业务的入口

优先顺序：

1. 项目已经有的 `Game*Checks` / 专用 `RevGM` 查询和动作；
2. 项目已有 `Revolution.RevCLI/examples/*.scenario.json` 模板，复制到本次输出目录后修改；
3. 用真实运行期 GM 组合 `setup → steps → cleanup → teardown`；
4. 需要视觉证据时，配合可用 Unity MCP 读取运行状态、Console、对象层级或截图。

如果缺测试接口：先检查现有查询/动作与 MCP 可调用的项目 Check；仍无法覆盖时按“一句话执行协议”补最小项目侧测试接线，说明修改范围，核实真实 API、清理与构建限制，重编译并验证运行期注册后继续执行。明确用户禁止编辑、涉及存档/生产数据或无法安全造局时报告阻塞。禁止臆造业务命令、直接改内存假造通过或放宽预期掩盖失败。

### 4. 生成并验证 scenario

- 优先临时写到 `Revolution.RevCLI/results/<test-name>/scenario.json` 或本次报告目录，不覆盖用户现有场景文件。
- 场景 schema 仅允许 `name/repeat/warmup/setup/steps/cleanup/teardown/stopOnFailure/failOnUnityError/thresholds`；step `kind` 仅 `gm/wait/until/assert`。
- `until` 必须给准确 GM、精确 `expected`、正数 `timeoutSeconds`；使用合理 `intervalSeconds`，不要用长时间盲等代替状态轮询。
- 设置 `failOnUnityError: true`、`stopOnFailure: true`；无性能诉求时将阈值设为 0（关闭采样门禁），不要为了“测性能”临时编阈值。
- 有副作用的每轮都定义 `cleanup`，整场定义 `teardown`；注意 GM 可能失败，清理也要尽量执行。
- 用 `scenario validate <file>` 先做离线格式检查，再运行。

### 5. 实际执行

1. 场景与 GM 测试要求未暂停的图形 Play：用 `--mode live`，不可退化到 batch。Batch 仅用于资产/导表，不能运行 GM/scenario。
2. 使用 Python subprocess 参数数组或 `python rev.py`，避免 PowerShell 中文 GM 参数乱码；不要并行派多个 Automation job（同一编辑器一次只接一个请求）。
3. `scenario run` 完成后检查结构化结果：`success`、`completed == requested`、`failedIterations`、`errors`、`unityErrors`、报告路径；不能只看“进程退出码 0”。
4. 若提示 `InterruptedByReload`、`Busy`、`Expired`、锁文件竞争或 `response.json` 写入 `IOException`，读取该 job 的 `response.json`、`scenario-result.json` 和 `unity-errors.jsonl` 后再决定是否安全重试。避免并发轮询导致文件占用。
5. 默认保留用户打开的编辑器与 Play 状态；若本次由测试启动 Play，成功/失败都通过 `teardown` 返回业务安全画面，并按需求停止 Play。不要擅自关闭用户原本打开的编辑器。

### 6. MCP 配合和视觉验证

- 新版 RevMCP 已能发现真实 GM 与业务注册表，并提供 UI/截图；通过它复用 Automation 工单，不另建重复执行链。第三方 Unity MCP 仍可补充目标工程的观察，但先确认实例身份。
- 调用 MCP 前先确认对应 MCP 工具 schema；若 MCP 已连接，用它核对当前 Play/场景、Console 编译错误、技能对象/效果是否在场，并在用户要求视觉测试时取实际截图。
- 没有 Unity MCP 或无法取截图时，明确标注“本次只做逻辑/Automation 验收，视觉未验证”。禁止把离线回归、scenario validate 或编译成功说成 Play 视觉通过。

### 7. 回报

简洁但可追溯地报告：

1. 测试了哪些功能/用例；
2. Automation 完整结果（通过轮数、错误数、必要性能指标）；
3. HTML/JSON 报告及 job id/路径；
4. MCP/视觉验收是否做过；
5. 每个失败的第一根因和日志位置；
6. 是否改了代码、测试文件、资源或场景，以及是否提交/推送。

使用“通过”只在 `success=true`、轮数齐全、`unityErrors=0` 且全部断言通过时；部分通过要逐项区分。即使修完失败再重跑，也要保留首轮失败与修复说明。

## 参考

详细场景 schema、执行模式限制、超时/清理契约与错误解释见 `references/workflow.md`。项目命令/接口速查见 `references/automation-cli.md`。

- `scripts/discover_tests.py`：只读发现工程、工具、Build Settings 场景与候选测试，结果交给 Agent 判断，不自动执行候选。
- `assets/scenario-template.json`：生成测试时参考结构；必须替换占位 GM，并核对覆盖和清理。
- `scripts/run_scenario.py`：在准备好的 Play 中校验并运行场景，保存原始输出并严格判断结果；不取代 Agent 的业务理解或 MCP 视觉验收。
