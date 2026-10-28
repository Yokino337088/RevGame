# Revolution 开发与自动化验证闭环

## 1. 先识别实际能力，不猜桥接

先区分真实执行入口和协议适配：

- `RevGM`：运行期同步命令注册/执行后端，由项目实现真实动作与查询。
- `Revolution.RevCLI`：Editor CLI/文件工单，覆盖场景/对象/组件/资产/设置/构建/测试及旧 GM scenario。
- `RevRuntimeAutomation`：开发态真实业务注册表，Editor 与独立开发版 Player 共用 UI、状态、日志和截图；Player 显式开启本机 HTTP。
- `Revolution.RevMCP`：外部 Python stdio MCP，统一 Editor/game/gm/cli 的发现、调用、断言、清理和 image 证据。Unity 内入口是 RevMCP 控制台。
- 第三方 Unity MCP：可选补充连接，调用前核对目标工程，不能从包已安装推断已连接。

工程内已安装 Automation 时优先复用，不再要求项目重复建一套 GM 文件桥。未安装时发现并明确缺项；框架、CLI、项目业务命令需版本兼容。通用工具不自动提供游戏业务。

## 2. 开发前先写验收契约

把需求转换为覆盖表：

| 需求 | 前置条件 | 真实动作入口 | 结果断言 | 失败/清理 |
|---|---|---|---|---|
| 例如落子 | 准备阶段、手牌存在、空格合法 | 与玩家相同的服务/Logic方法 | 占格、扣牌、额度变化 | 非法落点不扣牌；回合复位 |

实现玩法仍按现有架构：FSM负责状态业务，纯算法抽离，服务提供原子动作，UI转发，表现不二次结算。**GM不是第二套业务逻辑**。

新增/修改业务时同步维护受控开发态测试入口：

1. 准备：安全造局或选择测试数据，不触碰真实存档/支付。
2. 动作：调用玩家同一个业务入口；参数有范围校验。
3. 查询：快速、无副作用，返回稳定状态文本；不要主线程忙等异步。
4. 断言：核查具体伤害、目标、段数、数量或数据差；不能只检查命令成功或回Idle。
5. 复位：每轮回收对象、监听、计时器、租约，支持半途失败后清理。
6. 收尾：整场恢复业务安全状态。

注册限定 `UNITY_EDITOR || DEVELOPMENT_BUILD` 或项目明确授权的测试环境。注册/注销对称，真实API签名必须查源码。测试夹具的修改不能伪装成普通玩法效果。

## 3. 开发完成后实际跑，不只交命令

跨 Editor/游戏/UI/Player 的一句话任务优先加载 `revolution-ai-automation`，用 RevMCP rev_discover/rev_run 做真实验证。GM 多轮/性能 scenario 可继续用 `revolution-automation-tester`。未安装新版时先明确缺项，不把旧桥当成新能力。

1. 定位目标 Unity 工程、`Revolution.RevCLI/rev.py`、`commands.json` 和真实启动场景。始终显式传 `--project`，不能默认测框架 Demo。
2. 查询 `doctor`、`editor status`，记录原始Play/场景状态；编译中有界等待，未保存场景不得擅自覆盖，不另开占用工程。
3. Editor 游戏回归用未暂停图形 Play、`--mode live`；已有工程的资产操作也走live，不另启batch Unity。独立开发版 Player 用 runtime endpoint。资产变更退出Play后做，等待导表/编译完成再开Play。
4. 查看运行期 `gm list`，确认刚补的入口实际注册。源码里的注册语句或编辑期快照不代表运行期可用。
5. 复制/生成本次scenario到报告目录，先 `scenario validate`，再 `scenario run`；默认1轮、0预热、无性能阈值，按用户要求调整。
6. 回合间使用 `cleanup`，全场用 `teardown`。`setup`只执行一次，重复测试不能复用已被消耗的数据。
7. 同一实例串行执行；RevMCP 可调用运行时日志、UGUI 与截图。旧 scenario 执行期间不能并发停止Play或改场景。
8. 按原始状态和安全清理契约收尾，保留失败证据，禁止自动无限重试或降低断言门槛。

## 4. 场景格式和验证要求

三个格式按当前源码区分：RevMCP plan 为 name/steps/finally，步骤 target/action/arguments，可 expect/until/$ref；Automation pipeline 用 action/payload；旧 GM scenario 才是下列格式。不要混用字段。教程见 `Revolution.Document/自动化工具/RevMCP使用说明.md`。

旧 scenario 顶层为 `name/repeat/warmup/setup/steps/cleanup/teardown/stopOnFailure/failOnUnityError/thresholds`，步骤仅 `gm/wait/until/assert`。

- `until/assert.expected` 比较GM Message精确字符串，不支持包含/表达式。
- 等业务就绪用带正数超时的`until`，不靠固定等待猜资源装配时间。
- `failOnUnityError=true`；无明确性能需求则阈值为0（关闭），不能把关闭阈值说成性能达标。
- Python参数数组或UTF-8 JSON传中文，避开PowerShell编码和引号问题。
- 最终检查CLI response.success、场景data.success、completed=requested、failedIterations=0、unityErrors=0、errors为空、未取消。
- 查看job的response.json、scenario-result.json、iterations.jsonl及unity-errors.jsonl。progress不是完成信号。
- Busy/重载/超时先检查当前作业与已发生的副作用，再决定是否重跑；测试自己的故障与玩法故障分别归因。

## 5. 交付与证据分级

分别报告：编译、纯C#断言、Automation Play、真实AB加载、MCP视觉、真机性能。一个级别通过不能替代另一个。

至少报告覆盖用例、轮数、错误、报告/作业路径、首次失败根因与修复、清理结果、未验证项及改动范围。不凭历史断言数字宣布本次通过，不把Editor帧间隔等同真机CPU/GPU耗时。

测试缺入口时，在用户要求测试/开发功能且允许编辑的范围内补最小项目侧GM/Check；涉及生产数据、破坏性操作或未获授权的框架改动须确认。受环境阻塞时如实报告，而不是只生成JSON就宣称完成。

## 6. 一句话任务分工

- “写Boss激光并测试”：本Skill指导状态/算法/数值表/事件/资源实现，测试Skill负责Play编排与证据。
- “验证面板反复开关”：检测真实打开/刷新/关闭入口，断言订阅/计时器与池的回收，MCP补画面。
- “更新参数并回归”：Automation导表→等待编译→真实业务回归；按需验证AB而不只测编辑器直读。

安装方式：工具可由 `automation` 生成分支独立获取；技能由 `skill` 生成分支分发。修改源文件在main对应目录完成，不直接编辑生成分支。安装CLI不等于安装Unity桥接。


## 真实工程闭环补充

按 [真实工程闭环补充](revgame-ai-workflow.md) 读取RevGame同类实现，核对版本、所有者、失败路径与已执行证据；不得把历史样本结果当当前项目通过。
