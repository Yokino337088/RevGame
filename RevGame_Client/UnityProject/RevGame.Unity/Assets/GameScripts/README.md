# RevGame 游戏代码架构与运行说明

> 2026-10-18 当前动态美术更新：最多20条更小飘字，密集输入实测20条；攻击提速35%，单发伤害按比例摊薄以保留长局，敌棋五秒移动冷却独立计算。弹道按距离收缩/计时，增加自然结束与中断寿命回收。三类Shader位于GameShader，四个共享材质位于GameRes/Material，已绑定35个UI底图；移除UI大地图背景，六牌流光与DOTween点击抖动已接入。645条规则、737条布局、1280条战术、37条动态美术Play、49条密集反馈Play、136条回合Play及池化检查通过。详见[RevGame-魔幻动态美术与弹幕增强-2026-10-18](../../../../项目进度分析/RevGame-魔幻动态美术与弹幕增强-2026-10-18.md)，下方旧版本数字为历史记录。

> 2026-10-18 最新实现：敌方6～19名、基础属性逐轮成长、四档气眼AI、双牌原位升级、15种准备增益（每轮3～5种）、3秒后DOTween横穿播报、进度条缓动与Boss真实换帧。完整参数、代码职责与实际证据见[成长与准备增益验收](../../../../项目进度分析/RevGame-成长与准备增益验收-2026-10-18.md)。布局保持1920×1080手机横屏，手牌保持六张。

本项目首先验证 Revolution 框架能否支撑真实小游戏业务。当前已形成主界面、选关、六牌布阵、自动战斗、结算和章节奖励闭环。弹幕 Excel 已通过原框架导出并接入运行时，2 表共 22 行；正式基础血攻和敌方阵容仍等待用户提供数值。完整时间与证据边界见工程外的《RevGame-开发总结与明日计划-2026-10-18》及前序实现报告。

## 先跑起来

打开本地 `Assets/Scenes/GameScene.unity`，按 Play（远端历史场景名为SampleScene，本地改名另行保留）。依次进入冒险选关→第一章→手牌→任意合法空格→“开始战斗”。每回合发满六张；约3秒领取准备增益后可开战，无需等全部播报结束。准备期可在全盘空格落子；准备或战斗中均可点己方棋子再点空格免费换位，大棋必须整块放得下。战斗期不能落手牌或眼位石，手动换位保留伤势、护盾和攻击进度。刷新只重抽剩余 N 张，不补落子额度。本次运行中击败章节 Boss 后解锁下一章；退出程序后进度不保存。

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
| Services/BattleService.Progression | 奖励、章节推进、职业加成、双牌原位升级 | partial 是同一个服务按职责分文件，不是额外管理器。 |
| Data/PreparationBuffCatalog、Services/BattleService.Preparation | 十五种准备增益、延时发放、属性继承与本轮清理 | 属性在模型先统一生效；动画仅告知，不决定奖励是否到账。 |
| Services/BattleService.Reinforcement | 强援选择、远程安全后排和近战可攻击敌眼评分 | 逐格深快照试放后才消费牌，保护己方眼和气。 |
| Services/BattleService.Events | 八种事件、眼位石、伤害入口 | GM 与自动事件共用真实业务入口。 |
| Services/*.Diagnostics / *.Checks | 开发固定场景与编辑器断言 | 正式包排除造局与测试代码。 |
| Flow/GameFlow | RevLightFSM 调度 Home/LevelSelect/Battle/Exit | 状态控制当前页面，RevScope 负责当前域的回收。 |
| Flow/BattleLogic | 战斗域内以 0.3125 秒固定小步推进和动作转发，普通行动基准为 1.25 秒 | 小步用于分时远程出手，不表示全部棋子每小步攻击；节奏不藏在面板里。 |
| View/BoardView.Feedback | 飘字/弹道预算、聚合、避让和遮挡淡化 | 异步加载预约也计数，清阶段同时清队列和代次。 |
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

## 验证入口与本轮证据

非Play依次执行 `return RevGame.Editor.GameRuleChecks.Run();`、`return RevGame.Editor.GameLayoutChecks.Run();`、`return RevGame.Editor.GameTacticsChecks.Run();`。前轮实际分别为643、737、1280条通过（本轮仅改表现，未重跑这三组）；战术回归包含两组种子各连续66回合取胜。

Play在主界面分别执行 `RevGame.Editor.GameRoundPresentationChecks.Begin()`（136条）、`RevGame.Editor.GameGrowthPresentationChecks.Begin()`（23条）、`RevGame.Editor.GameReadabilityChecks.Begin()`（本轮已扩展为密集齐射检查）和 `RevGame.Editor.GameFeelChecks.BeginPoolCheck()`（通过）。先确认Play启动入口已完成，再运行检查；一组结束再运行下一组，池化检查等待至少四秒，不能中途换域。成长检查验证真实三秒计时、准备横幅1.67秒新时序、Boss可见Sprite换帧、血条/时间条Tween以及换轮清理。

可通过开发GM进入关卡或固定夹具；非法命令仍返回Success=false，按钮和GM走同一业务入口。原攻击/伤害/升级/事件表共22条，本轮已通过Revolution导表工具修正两条旧升级说明；新增准备增益和基础成长仍是可调业务参数，尚未接正式数值Excel。

前轮已实际操作Unity进行六牌准备、双牌原位升级；本轮通过Unity MCP运行真实Play检查，新增战斗换位入口、密集反馈与事件居中验收。保持1920×1080横屏；未执行AB构建、手机真机性能或全策略平衡验收。后期样本仍较依赖保底支援，具体限制和下一步见最新验收报告。
