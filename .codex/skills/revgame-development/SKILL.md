---
name: revgame-development
description: 用于 RevGame（围棋史莱姆）项目的功能开发、快速迭代、排错与审查。按当前代码和项目规范选择业务层与功能目录，联动本项目 Revolution 技能，优先用 RevCLI 完成 Unity 编辑器操作、UI/Prefab 制作、导表与资源构建，再验证业务与框架可行性。适用于六章远征、独立无尽模式、剧情、表现、资源和开发态测试接线；不把项目规则扩展为其它游戏的通用规范。
---

# RevGame 项目开发

把需求落到本项目现有架构、资源链和自动化入口，完成可运行、可审查的一次迭代。**第一交付物是 Revolution 在真实业务中的可行性结论**：说明本次用了哪些门面、是否支撑目标、发现什么边界，以及证据覆盖到哪里。

本技能是项目入口。用户只需调用 `$revgame-development` 并描述需求；AI 自行读取必要的联动技能、定位同类实现、完成开发与相应验证，不要求用户逐个指定技能或背命令。使用技能本身不增加推送、发布或其它外部操作的授权。

## 先确定当前事实

1. 从工作目录或本技能位置向上定位同时含 `AGENTS.md` 和 `RevGame_Client/UnityProject/RevGame.Unity/Assets` 的仓库根；目录迁移后重新定位，不把开发者机器盘符写入产物。
2. 读取当前 [AGENTS.md](../../../AGENTS.md)，检查 `git status --short` 与暂存区，记录本次目标。保留用户已有改动和暂存状态，不把其它工单混进本次修改。
3. 只用 `rg --files` / `rg -n` 找任务命中的文档与同类代码。目录与源码入口见 [项目地图](references/project-map.md)；不用全仓扫描或通读框架内核替代定位。
4. 实施前明确四件事：**哪种模式 → 谁拥有状态 → 改到哪个功能目录 → 怎样证明改对**。普通可推断的实现选择自行推进，需求冲突只澄清影响验收的部分。

当前布局：Unity 工程为 `RevGame_Client/UnityProject/RevGame.Unity`，版本以 `ProjectSettings/ProjectVersion.txt` 为准（本技能核对为 2022.3.15f1c1）；业务在 `Assets/GameScripts`，项目 Editor 在 `Assets/Editor/RevGame`，资源在 `Assets/GameRes`，Shader 在 `Assets/GameShader`。`Assets/Revolution` 是导出快照；需要改框架时回 Revolution 源仓库 `main` 修改、验证、重新导出。这里没有 `Assets/Revolution.Demo`。

**资料分工与冲突处理：**

- 当前用户要求与项目 `AGENTS.md` 决定约束；项目特例优先于联动技能的通用示例。
- 六章玩法意图查 [玩法拓展策划案](../../../RevGame_Client/策划案/玩法拓展策划案.html)；无尽查 [无尽模式策划案](../../../RevGame_Client/策划案/无尽模式策划案_棋阵幸存者_2026-11-01.html)。无尽专属规则只覆盖无尽。
- 当前源码、Excel 和实时注册表分别证明已实现行为、实际配置与可调用能力。设计文档的“建议值”“尚未实现”及旧报告路径不能覆盖新实现；实现与目标有差异时说明差异，按本次需求处理。
- 历史报告只证明对应版本、输入与平台的结果。不要复制旧帧率、图集数量、表数量或文件行号作为本次结论。

## 按任务联动技能

先读命中的 `SKILL.md`，再按它的分诊只读必要专题。以下链接均指向本项目安装版本，不自动换成用户全局目录的同名旧版本。

| 本次工作 | 联动入口 | 本技能补充的项目约束 |
|---|---|---|
| 玩法、UI、流程、资源接线、排错或代码审查 | [revolution-framework](../revolution-framework/SKILL.md) | 找本地同类代码；门面/API 以安装快照为准；用项目现有层次承载业务 |
| Unity 编辑器、Prefab/场景/材质/动画、导表、AB、出包或连接问题 | [revolution-unity-editor](../revolution-unity-editor/SKILL.md) | **优先 RevCLI**；显式目标工程；按 [编辑器与资源流程](references/unity-and-assets.md) 保护现场与资产 GUID |
| 实际游玩测试、GM/UI/跨帧回归、截图或 GIF 报告 | [revolution-ai-automation](../revolution-ai-automation/SKILL.md) | 游戏测试按本项目 AGENTS 优先常驻 RevMCP：真实 `rev_discover` → `rev_call` / `rev_run`；展示真实调用与断言 |
| CPU/GPU、缓存、内存、图集加载或目标设备性能验收 | [revolution-performance](../revolution-performance/SKILL.md) | 先读项目 [战斗性能规则](../../../RevGame_Client/文档/战斗性能优化规则/战斗性能优化规则.html)，固定输入先测再改 |
| 维护已有 GM scenario 循环、预热或性能采样 | [revolution-automation-tester](../revolution-automation-tester/SKILL.md) | 仅用于既有 scenario 契约；不把普通快速开发自动变成大规模回归 |

跨任务只增加必要技能。Prefab 编辑使用 CLI 与 Play 验收使用 MCP 可以出现在同一迭代；直接 CLI 调用不能写成已执行 MCP。仅改文本、技能或纯代码时不为了“走流程”启动 Unity。API 不确定先查框架 `references/decision-guide.md` §2，再查公开门面与本地同类调用，不凭印象补 API。

## 快速迭代闭环

1. **定位最小链路。** 找“输入入口 → Logic/Service → 模型/配置 → 事件或回执 → View/UI”，只读相关文件。简短说明参考文件、复用契约、目标文件和验收后置条件。
2. **选择所有者与目录。** 在 [项目地图](references/project-map.md) 找同职责目录；新文件放相应功能子目录。已有 partial 按职责扩展，不为小功能重排全项目或增加万能 Manager/System。
3. **实现一个完整切片。** 同步必要的契约、规则、配置、显示、拒绝原因与退出清理。需要 Prefab、节点绑定或导表才能运行的功能，把这些配套一起完成。自动化缺入口时补受控查询/动作/清理，复用玩家真实入口。
4. **执行相关验证。** 按 [验收与交付](references/validation-and-delivery.md) 选择最小有效检查。跨帧等真实完成；资源变更需真实 AB 冷启动与重入；性能修改保留基线并同输入复测。只有新改动、失败或未解风险才扩大测试。
5. **闭环交付。** 恢复本次测试现场，说明改动、证据、未验证项与框架边界。只有用户要求时提交/推送；“只推某目录”必须核对实际提交和待推送提交集合，不使用 `git add .`。

## 每次实现都守住的边界

- **唯一启动与流程：** `GameBootstrap` 初始化和注册一次；`GameFlow` + FSM + `RevScope` 管业务阶段，`RevScene` 管物理场景。正式入口 `StartScene`，远征 `GameScene`，无尽 `EndlessScene`；新增模式沿用唯一流程入口，不另建全局流程机。
- **规则与表现分工：** 六章操作经过 `BattleLogic` 的阶段/教学门禁再到 `IBattleService`；无尽沿用 `GameFlow`、`EndlessLogic` 和 `EndlessService` 的现有分工。伤害、胜负、库存与奖励只在服务/模型提交；视图消费事实，不在弹道落地或 Tween 完成时二次结算。
- **门面与异步：** 使用 `RevUI/RevTimer/RevEvent/RevResManager/RevPool/RevScope/RevTask` 等公开入口，禁止访问框架 `Core/Implementation`。异步业务方法用 `RevTask`；Unity 必须为 `void` 的生命周期回调可作薄适配并捕获异常，不扩散 `async void`。不引 `System.Threading.Tasks`、UniTask 或另一套异步基建。
- **项目已有动画：** 本项目已接入 DOTween，现有纯表现代码可按同类文件复用并维护停止/回池契约；面板过渡优先 `RevUIAnim`，流程演出用 `RevSequence`。不把框架“零第三方”旧例套成删除现有 DOTween 的任务，也不新增其它库或用动画承载业务时钟。
- **代码规范：** 中文新手注释解释用途、单位/范围、所有者、生命周期、失败路径与关键步骤的原因。所有方法体有大括号，方法签名的 `(` 与参数不换行；所有 `if/else if/else` 的条件、左右大括号和代码体分别展开，类/方法/属性的大括号同样独占一行。
- **UI 契约：** `OnBindView` 装配，`OnRefreshView` 落屏，交互只转发真实业务；关闭自身用 `CloseSelf()`。节点路径、绑定字段、按钮路由和 Prefab 必须一致；新增或重制 UI 使用已有 Sprite、正确裁切与九宫格，适用底板为 `Image.Type.Sliced`。
- **资源与生命周期：** 运行期只用生成逻辑路径或两段式加载，不拼 `StreamingAssets/Application.dataPath`。`GameSpriteAtlasService` 与 `GamePoolRequests` 组合框架门面，引用与池租约成对；回池/退出先令旧回调过期，再取消动画/计时器、归池与清队列。棋子使用共享材质，不每帧访问 `renderer.material`。
- **规则一致性：** 占格只由 `BoardModel.Place/Move/Remove/Reset` 改；气眼按占格、加气、围困免疫等真实输入失效。AI 复用调用方缓冲与独立深快照，不移动实战棋子试算；死亡、补子、换位、复活后的更新不能漏。
- **配置与生成目录：** 策划可调数值进入现有 Excel → RevCLI 导表 → 强类型校验链；不改生成文件作为源头，不顺手移动 `Generated`。无尽使用独立表与配置入口，改变共享规则须验远征回归。
- **报告与 Git：** `.codex/skills` 入库；原始证据/依赖/本机配置留 `.codex/work` 或相应忽略目录。报告保留 HTML 与必要截图/GIF，不生成 ZIP，不导出 Profiler 录制或内存快照，不自动 `SaveProfile`。Unity 缓存、生成 csproj/sln、外部 AB 和安装包不提交；用户指定的 `Assets/StreamingAssets` 可入库。
