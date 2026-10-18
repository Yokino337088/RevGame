# RevGame 游戏代码架构与运行说明

> 2026-10-18 战斗更新已落地：敌方 6～10 名普通棋含重弩/弓箭/火法，真实一眼与两眼布阵、深快照保眼移动、阶段式通关保障。最新验收见[战斗与围棋 AI 报告](../../../../项目进度分析/RevGame-战斗与围棋AI改造验收-2026-10-18.md)。布局继续使用 1920×1080 横屏。

本项目首先验证 Revolution 框架能否支撑真实小游戏业务。当前已形成主界面、选关、六牌布阵、自动战斗、结算和章节奖励闭环。弹幕 Excel 已通过原框架导出并接入运行时，2 表共 22 行；正式基础血攻和敌方阵容仍等待用户提供数值。完整时间与证据边界见工程外的《RevGame-开发总结与明日计划-2026-10-18》及前序实现报告。

## 先跑起来

打开 `Assets/Scenes/SampleScene.unity`，按 Play。依次点击“开始闯关”→“第 1 章”→手牌→任意合法空格→“准备完毕”。每回合发满六张，准备期可在全盘空格落子；点击己方棋子再点空格可免费重摆，大棋必须整块放得下。刷新只重抽剩余 N 张，不补落子额度。本次运行中击败章节 Boss 后解锁下一章；退出程序后进度不保存。

启动只由 GameBootstrap 调用一次 RevInitialize.Initialize，并集中注册 IBattleService 与 GameFlow。不要再手动初始化事件、UI、计时器等模块，否则会清空已注册监听。

## 文件怎么找

| 目录 / 文件 | 负责什么 | 为什么这样分 |
|---|---|---|
| Core | 坐标、常量、枚举、事件、域名 | 名称与规则比例集中，避免各处写不同数值。 |
| Data | 图鉴、关卡、奖励、事件定义 | 配置与运行状态分开。基础血攻和阵容仍为占位，星级倍率采用 v18。 |
| Pieces/Piece | 生命、身体大小、位置、气、眼 | 不持有 GameObject，视图回池不影响业务状态。 |
| Board/BoardModel | 唯一棋盘数据 | 大棋所有身体格引用同一个 Piece；名单遍历不重复计算身体。 |
| Board/GoRules | 连通块、气、眼、锁死 | 规则只依赖棋盘，不依赖画面。 |
| Battle | HandModel、MoveRules、EnemyFormationPlanner、EnemyTactics | 手牌编号防重复消费；真实射界、完整眼阵和深快照保阵试走，不直接搬动实战棋子。 |
| Services/BattleService | 回合、落子、攻击、胜负、敌方出生预检 | 所有动作先校验后提交；失败不留下半颗大棋或名单里的幽灵棋子。 |
| Services/BattleService.Progression | 奖励、章节推进、职业加成、合成 | partial 是同一个服务按职责分文件，不是额外管理器。 |
| Services/BattleService.Events | 八种事件、眼位石、伤害入口 | GM 与自动事件共用真实业务入口。 |
| Services/*.Diagnostics / *.Checks | 开发固定场景与编辑器断言 | 正式包排除造局与测试代码。 |
| Flow/GameFlow | RevLightFSM 调度 Home/LevelSelect/Battle/Exit | 状态控制当前页面，RevScope 负责当前域的回收。 |
| Flow/BattleLogic | 战斗域内以 0.3125 秒固定小步推进和动作转发，普通行动基准为 1.25 秒 | 小步用于分时远程出手，不表示全部棋子每小步攻击；节奏不藏在面板里。 |
| View/BoardView | 格子、棋子、特效池与模型编号映射 | 异步回调核对进入批次；实例独立容器避免 OnDisable 期间换父节点。 |
| View/*Presentation | 流程与阶段到面板的接线、相机适配 | 服务不引用 UI；晚完成的加载需核对当前阶段并刷新数据。 |
| View/PieceView、BattleEffectView | 帧动画、血条、王冠、破碎、升星、飘字 | owner 计时器在停用时取消，避免回池对象继续更新。 |
| View/GameAudio | RevSound 映射业务事件 | 运行时声音走逻辑路径，不直接加载磁盘。 |
| UI | 主界面、选关、准备、HUD、结算、奖励、卡牌 | OnBindView 装配、OnRefreshView 落屏、按钮只转发动作。 |
| Bootstrap | 唯一启动入口与开发 GM 注册 | 谁注册服务谁注销；正式环境不注册开发命令。 |
| [../Editor/RevGame](../Editor/RevGame) | 资产装配工具与规则/布局/回合/池化验收入口 | 编辑器代码集中在 Assets/Editor/RevGame，不放入 GameScripts；通过 Unity MCP 调用，运行时不依赖 AssetDatabase。 |

2026-10-18 按项目目录要求，将原 `Assets/GameScripts/Editor` 的 9 个 C# 文件整体通过 Unity MCP 迁至 `Assets/Editor/RevGame`，保留原目录和文件的 `.meta` GUID、`RevGame.Editor` 命名空间和公开调用入口。目录不决定 C# 命名空间，所以原有 `RevGame.Editor.GameLayoutChecks.Run()` 等调用不变。迁移前后都位于 Unity 的特殊 Editor 目录，且这里没有另设 asmdef，因此仍归属 `Assembly-CSharp-Editor`，编辑器/运行时代码边界不变。不要为了移动文件而把 UnityEditor 依赖放进运行时源码。

## 生命周期最容易踩的点

- IBattleService 由启动入口拥有，贯穿本次运行，保留章节与卡池解锁。BeginCampaign 重置局内强化与棋盘；它不是每次退出战斗域都会销毁的临时服务。
- GameFlow 为当前页面开一个大厅域或战斗域；BattleLogic 随战斗域退出。池中对象、声音和场景表现还有各自的显式清理，不能假设一个域会自动归还所有东西。
- OnBindView 只在首次绑定时执行，OnOpen 每次打开执行。框架 Close 会清除 owner 事件，因此重复打开的面板要在 OnOpen 重新监听。
- Button.onClick 不由 RevEvent 管理；首次绑定挂上，在 OnRelease 拆掉。面板关自己用 CloseSelf。
- 预制体异步加载可能跨越回合或场景切换；回调检查有效性后再更新画面，不能把旧结算数据盖到新战斗上。
- 全体敌人占格先预检再提交。如果残局挤不出完整大棋空间，保留原状态并显示原因，不能少生成一个 Boss 却继续游戏。

## 资源约定

美术在 `Assets/ArtRes/棋盘、棋子、状态标记、界面、图标、特效、字体、音频`，保留中文命名。运行时资源在 `Assets/GameRes/Board、Pieces、UI、Effects、Audio、Data`。弹幕工作簿位于仓库的 `RevGame_Client/配置表/战斗弹幕配置.xlsx`，导表生成 C# 位于 `GameScripts/Generated`；不要手改生成物来替代原框架导表。

面板预制体名与类名相同，例如 `GameRes/UI/PreparePanel.prefab`；RevBind 字段 `_hintText` 对应节点 `hintText`。资源根配置在 `Assets/Editor/ABBuildConfig.asset`，逻辑路径只写 `UI/`、`Pieces/` 等，不拼 StreamingAssets 或 Application.dataPath。

## 代码规范

使用 Revolution 公开门面，不顺手修改当前工程的框架内核；异步只用 RevTask。方法签名的括号与参数保持同一行；方法体及 if/for/foreach/while 均带大括号。文件头说明分工理由，关键事务、时序、回收用中文解释，不只复述代码。

## 验证入口

非 Play：规则入口为 `return RevGame.Editor.GameRuleChecks.Run();`；布局入口为 `return RevGame.Editor.GameLayoutChecks.Run();`。源码都在 [../Editor/RevGame](../Editor/RevGame)。前序规则历史为 570 条、前序 Play 回合表现历史为 136 条；今日编辑器目录迁移后，规则 570 条、布局 737 条及 Play 回合表现 136 条均已重新执行并通过。136 条覆盖连续 1/0/4 张剩牌换轮、暂停翻牌/升级/弹幕、延迟伤害与日志清空，不能与历史执行混为一次。

本轮 `GameLayoutChecks.Run()` 经 Unity MCP 返回 737 条通过，覆盖多尺寸/安全区的纯几何计算；字体调整后再次通过。纯计算不切换实际编辑器预览，当前唯一实际预览固定 Game“RevGame 1920x1080”。该结果不等于真机、全部视觉项或连续 66 回合已验收。

Play 回合表现入口为 `RevGame.Editor.GameRoundPresentationChecks.Begin()`，池化入口为 `RevGame.Editor.GameFeelChecks.BeginPoolCheck()`；池化检查需在同一流程至少等待四秒，不要中途切域或停止 Play。66 个回合的历史检查是分别新建服务生成配置，另有 1 血单兵/高血敌人的辅助兜底造局，均不等于连续带残局游玩 66 回合。

本轮池化检查也已通过：伤害/弹道/攻击文字/升级/增益启动，回池 Kill、复用，四秒后无旧回调且 Tween 总数恢复。字体资产审计 49 个 Text 全部为 NotoSansCJKsc-Bold/Bold/MiddleCenter/Outline，失败 0；8 个界面/提示、70 个 Sprite 引用，旧 UI 为 0。唯一 1920×1080 Game 预览实际检查首页、选关、六牌准备、结算、奖励与战斗 HUD，字清晰且无遮挡/截断；这些是本机编辑器结论，不是实体手机验收。

Play：GM 依次执行 `RevGame/打开选关`、`RevGame/进入战斗 1`、`RevGame/落首张牌`、`RevGame/开始战斗`、`RevGame/快照`。固定夹具包括 merge、large-merge、boss、events、reward、round10。非法命令返回 Success=false；重复消费牌、重复领奖不会生效。

当前已验证编辑器直读和真实交互；未执行 AB 构建、真机或连续 66 回合平衡验收。完整玩法案的扩展范围和极端 Boss 对峙问题见交付报告。

本轮新增非 Play 入口 `return RevGame.Editor.GameTacticsChecks.Run();`，1150 条通过：覆盖 66 个敌阵、真实眼数、射界、快照隔离、拥挤降级和两组各连续 66 回合取胜；570 条规则、737 条布局及136 条 Play 回归也再次通过。连续路径固定按首个合法格落牌、章节奖励选第一项，是通关回归样本，不等于全策略平衡；当前阶段式支援已解决默认辅助下的长局/Boss对峙收尾，关闭辅助时仍允许原规则僵持。当前参数与证据以最新战斗报告为准。
