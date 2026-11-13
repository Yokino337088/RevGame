---
name: revolution-framework
description: 使用或维护 Revolution Unity 框架时选择公开门面、FSM、RevScope、UI、资源和开发态自动化契约。用于功能实现、框架改动、排错和代码审查。
---

# Revolution 框架开发

实现或审查项目功能时，先看项目 `AGENTS.md`、已安装的框架版本和同类代码。使用实际存在的公开 API。项目可用 [RevGame 实现索引](references/revgame-engineering.md) 找到同职责例子；项目版本决定签名与生命周期契约。编写业务时按任务读 [官方编程架构规范](https://github.com/Yokino337088/Revolution/blob/main/Revolution.Document/编程范式/Revolution官方编程架构规范.md) 的对应部分。

## 先选相关专题

| 任务 | 资料 |
|---|---|
| 框架选型、API 存不存在 | [决策与不存在清单](references/decision-guide.md)。签名不明确就定位公开定义和同类调用 |
| 启动、场景与生命周期 | [启动与平台](references/startup-and-platform.md)、[核心门面](references/modules-core.md) 的 RevScene 和 RevScope |
| UI 面板与动画 | [UI 与分层](references/ui-and-layering.md) |
| 资源、分包与 Editor Play 粉屏 | [资源和 AB](references/resource-and-ab.md)，平台误选看 §2.1 |
| 策划数值与 Excel | [实现配方](references/playbooks.md) 的 R22，含生成和装配校验 |
| 编辑器与项目制作配方 | [Revolution 编辑器技能](../revolution-unity-editor/SKILL.md) 和 [Editor 职责](references/revgame-editor-practice.md) |
| GM、RevMCP 与业务断言 | [自动化接入](references/automation-integration.md) 和 [实际测试技能](../revolution-ai-automation/SKILL.md) |
| 真机、Player 与性能 | [Player 测试](references/player-testing.md)、[移动端](references/android-runtime-and-layout.md) 或 [性能技能](../revolution-performance/SKILL.md) |
| 热更 | [热更](references/hot-update.md) |
| 排错、审查与交付 | [症状索引](references/troubleshooting.md)、[审查指南](REFERENCE_GUIDELINES.md)、[验证与交付](references/verify-and-test.md) |

## 编写时守住的契约

- 一次初始化用 `RevInitialize.Initialize()`。业务以公开门面为入口，FSM 管阶段，`RevScope` 管域和回收，域内无界面业务用 `RevLogicBase`。服务、事件、定时器、异步和对象池明确 owner 与清理。失败返回项目可诊断的原因。
- 资源通过 `RevResPath.*` 或两段式逻辑路径加载，引用成对释放。首次真机加载用异步。普通业务异步用 `RevTask`；过场、冷启动和资源验证采用目标项目的实际加载模式。
- `RevUIPanel` 的 `OnBindView` 装配，`OnRefreshView` 落屏，点击回调转发业务。关闭自身用 `CloseSelf()`，业务侧用 `SetData` 送数据。原地改数据后需要 `RefreshView()`。
- 策划数值按项目现有配置链管理。Excel 业务沿用导表、强类型读取和装配校验。代码使用独立大括号，写面向新手的中文契约说明和关键步骤注释。
- 完整约定见 [技术规则](references/technical-rules.md) 和 [代码与注释](references/code-style.md)。涉及框架源码时，先查安装版与源仓库的差异、CHANGELOG 和 [历史故障](references/pitfalls.md)。从框架源仓库维护，再同步生成分支和项目快照。

## 验收

- 完成功能后，凡改动会影响 Unity 脚本编译，最后一次写入后必须在目标编辑器主动完整重编译并验证通过，再进入 Play 或交付。已有 Play 先结束本次测试并退出；用户正在进行的对局需先交接。按 [编辑器技能](../revolution-unity-editor/SKILL.md) 核对本次编译工单。

- 项目有 `.codex/rev-policy.json` 时，通过项目入口检查本次 diff，按文件行号和规则 ID 修复。长任务恢复时重读项目 `AGENTS.md` 与当前专题。插件与规范检查见 [项目规范检查](references/project-policy.md)。
- 玩法、跨帧场景、资源和设备各按实际目标验收。关键业务在开发态留受控 GM 或 `RevRuntimeAutomation` 入口，复用玩家路径，核对成功、拒绝和退出。真实自动化先发现，按断言和清理判结果。见 [RevGame 交付实践](references/revgame-ai-workflow.md)。
- 修改框架公开行为时同步模块使用说明和架构文档的 Markdown/HTML。按现有源仓库工作流导出 `package`、`demo`、`hotupdate`、`skill` 或自动化工具分支。只在用户要求时提交或发布。

## 基础模块总览（新增开发态 RevAutomation 见自动化接入参考）

> "我想做 X → 用哪个门面"。签名与最小示例在对应参考文件里；没有合适门面时先按生存期选面板、域 Logic 或服务，不为单个业务新造框架系统。

| 我想… | 门面 | 一句话 | 速查位置 |
|---|---|---|---|
| 启动游戏、初始化框架 | `RevInitialize` | 启动入口调一次，装配资源系统策略 | `startup-and-platform.md` §1 |
| 切后台 / 回前台处理 | `RevAppLifecycle` | `Paused` / `Resumed` / `Quitting` 事件 + 后台时长 | `startup-and-platform.md` §2 |
| 确认代码裁剪没误伤 | `RevStripGuard` | 真机启动自检关键类型，缺了报人话 | `startup-and-platform.md` §3 |
| 打日志 | `RevLog` | 五级 + tag + 重复抑制 + 环形缓冲 | `modules-core.md` §1 |
| 多久之后 / 每隔多久 | `RevTimer` | 四时间域，owner/scope 防泄漏 | `modules-core.md` §2 |
| 纯 C# 类要每帧回调 / 协程 | `RevMono` | 不摆物体拿 Update/LateUpdate/FixedUpdate | `modules-core.md` §3 |
| 频繁创建销毁的对象 | `RevPool` / `RevRefPool` | GameObject 池（可 `[RevPoolPrefab]` 按类型取）/ 纯 C# 池 | `modules-core.md` §4 |
| 一对多解耦通知 | `RevEvent` | 字符串事件名，派发中可安全增删 | `modules-core.md` §5 |
| 跨界面共享的业务能力（注册 / 取用 / 用完注销） | `RevServices`（默认）/ `RevServiceLocator`（作用域·进阶） | 一行 `Register`、一行 `GetRequired`、一行 `Unregister`；每局一份才用 Builder | `modules-core.md` §6 |
| await 一帧 / 等待 / 取消 | `RevTask` | 轻量单线程异步（框架唯一异步基建） | `modules-core.md` §7 |
| 切场景 | `RevScene` | 进度永远 0~1，切前按约定清理 | `modules-core.md` §8 |
| 键鼠 / 触屏 / 手柄 / 手势 | `RevInput` | 动作名绑定 + 屏蔽栈 | `modules-core.md` §9 |
| 播音效 / BGM | `RevSound` | 一行播放，2D/3D/BGM，音量总线 | `modules-content.md` §1 |
| 状态切换（流程 / AI） | `RevLightFSM` / `RevLightStackFSM` / `RevHeavyFSM` | 类型名统一 `FSM`；宿主驱动 | `modules-content.md` §2 |
| 游戏域与无界面业务单元 | `RevScope` / `RevLogic` / `RevLogicBase` | 自定义字符串域、统一回收；FSM 管状态，域管生命周期 | `modules-core.md` §12、`playbooks.md` R19 |
| 加载资源 | `RevResManager` | 逻辑路径 + 引用计数 + 分组卸载 | `modules-content.md` §3、`resource-and-ab.md` |
| 单例 | `RevSingleton*` | 三个基类（优先服务定位器） | `modules-content.md` §4 |
| 做界面 | `RevUI` / `RevUIPanel` | 声明式面板、数据单向流、动画库 | `modules-content.md` §5、`ui-and-layering.md` |
| 一串动作（播放→等待→并行） | `RevSequence` | 不可变蓝图 + 取消收尾契约 | `modules-content.md` §6 |
| 关键业务测试接口 | `RevGM.Register` / `RevRuntimeAutomation.Register` | 项目真实业务入口，经 RevMCP 开放给 Agent；Editor 与开发版 Player 共用注册表 | `automation-integration.md`、`playbooks.md` R21 |
| 读配置表 | `RevDataTableManager` | 表也是资源，走资源系统 | `modules-content.md` §8 |
| 玩法数值/文案/概率放哪（血攻、倍率、冷却、奖励、关卡） | Excel 导表 | 策划数值进表、代码只读；启动时统一校验 | `playbooks.md` R22 |
| 资源热更（扩展包） | `RevHotUpdate` | 清单 + 差量下载 + 版本目录 | `modules-content.md` §9、`hot-update.md` |
