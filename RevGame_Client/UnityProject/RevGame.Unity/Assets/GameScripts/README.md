> 2026-10-19 **数值全部改走 Excel 导表**：棋子血攻与星级倍率、16 种增益的数值与说明文案、8 种战斗事件、敌方成长/人数/AI 档位、章节与 Boss 阵容、敌方出场名单与补兵规则、奖励权重与强化量、战斗节奏/伤害公式/通关辅助等 68 个参数，原先写死在 `PieceCatalog` / `Consts` / `BattleService.*` 里，现在由 `RevGame_Client/配置表/` 下 5 个工作簿（共 13 张表）经框架导表工具生成 `Generated/Battle*.cs` 与 `GameRes/Data/*.txt`，启动时 `GameConfig.Initialize` 统一校验。详见下方「配置表」一节。验证边界：已在工程外证明新旧行为逐字节等价（约 430 万行事件流水）、改表生效与填错拦截（24 项反向测试）、`Assembly-CSharp` / `Assembly-CSharp-Editor` 真实编译 0 错误；**尚未在 Unity 内 Play 或运行 `GameRuleChecks` 等编辑器断言，也未跑 AB 构建与真机**，这些仍需在 Unity 中执行一遍。
>
> 2026-10-18 当前交付：敌方战斗低频补子、16种准备增益（新增万箭齐发）、被围三帧、背景全屏流动和UI魔光；AI注释与59脚本功能目录已完成。固定战斗规则中位数291.343→40.912ms，落子13.773→3.557ms；141项AB隐式重复降至0，PC/Android各19包构建通过，199个GameRes资源真实AB加载通过。详见[RevGame-战斗性能与资源规范-2026-10-18](../../../../项目进度分析/RevGame-战斗性能与资源规范-2026-10-18.md)。以下较早数字保留作历史记录，当前结果以上述报告为准。

# RevGame 游戏代码架构与运行说明

> 2026-10-18 当前动态美术更新：最多20条更小飘字，密集输入实测20条；攻击提速35%，单发伤害按比例摊薄以保留长局，敌棋五秒移动冷却独立计算。弹道按距离收缩/计时，增加自然结束与中断寿命回收。三类Shader位于GameShader，四个共享材质位于GameRes/Material，已绑定35个UI底图；移除UI大地图背景，六牌流光与DOTween点击抖动已接入。645条规则、737条布局、1280条战术、37条动态美术Play、49条密集反馈Play、136条回合Play及池化检查通过。详见[RevGame-魔幻动态美术与弹幕增强-2026-10-18](../../../../项目进度分析/RevGame-魔幻动态美术与弹幕增强-2026-10-18.md)，下方旧版本数字为历史记录。

> 2026-10-18 最新实现：敌方6～19名、基础属性逐轮成长、四档气眼AI、双牌原位升级、15种准备增益（每轮3～5种）、3秒后DOTween横穿播报、进度条缓动与Boss真实换帧。完整参数、代码职责与实际证据见[成长与准备增益验收](../../../../项目进度分析/RevGame-成长与准备增益验收-2026-10-18.md)。布局保持1920×1080手机横屏，手牌保持六张。

本项目首先验证 Revolution 框架能否支撑真实小游戏业务。当前已形成主界面、选关、六牌布阵、自动战斗、结算和章节奖励闭环。全部玩法数值已由 Excel 经框架导表工具接入运行时（见「配置表」一节）；当前参数以Excel和上方最新实现记录为准，调参后通过框架导表更新。完整时间与证据边界见工程外的《RevGame-开发总结与明日计划-2026-10-18》及前序实现报告。

## 战斗节奏与Boss18技能（2026-10-23）

当前所有战斗不限时；删除按时间清敌、超时保胜和最后一子免死。事件效果与频率翻倍，新增本轮攻击、防御、攻速、生命增益。每15秒随机敌区3×3逐格波动（双方4%原始伤害），每24秒自动“能量巨大”（全场敌军5%原始伤害）。新增玩家近战接敌/封气、远程贴敌后撤；Boss必须≤20%血且零气才擒王，击杀仍胜。

18个Boss技能按章节解锁，原0~8编号保持。Excel通过框架导表，当前共14张表；数值见BalanceRule、BattleEventConfig、BossSkillConfig。GM：`RevGame/战斗增益 Attack|Defense|Haste|Vitality 额外百分比`、`RevGame/区域波动`、`RevGame/能量巨大`、`RevGame/战斗节奏`。本轮加成累计上限由Combat.BuffCap配置，回合切换重置。

新增代码：`Services/Battle/Support/BattleService.CombatPacing.cs`（时钟/属性/伤害）、`Services/Battle/AI/BattleService.PlayerAI.cs`（快照试走）、`Battle/AI/BossPatternGeometry.cs`与`BossPatternStates.cs`（九新招）、`View/Board/BoardView.CombatPacing.cs`与`View/Scene/GamePresentation.Impact.cs`（回收安全的表现）、`Bootstrap/Diagnostics/GameCombatGmCommands.cs`（开发GM）。

本轮实测：.NET 218场景4445断言、Unity规则704条、真实PC AB Play 253条通过，新增Unity错误0，18招/20音源/波浪/炸弹/UI/震屏/退出清理已验。手机振动、Android真机及整章平衡未验；详细截图与首次失败记录见`RevGame_Client/文档/进度同步/战斗节奏与Boss18技能升级-2026-10-23.html`。下文较早“保底支援/不限时”等历史说明以本节为准。

## 章节背景与主题 Shader（2026-10-23）

六章均使用独立材质和专用 Shader，每章图片+材质+Shader同包；第一章还包含原有SpriteAtlas。第一章原图保留GUID迁入`GameRes/ChapterBackground`，保留ArcaneScene参数与手调布局，大厅不再预热第一章，GameScene无章节图片/材质硬引用。

| 章节 | 图片逻辑名 | 独立Shader / 主题 |
|---|---|---|
| 1 | `SceneBackdrop_0fc7e394` | `Chapter01_Tutorial` / 原教学效果 |
| 2 | `Chapter02_Village` | 明朗日照、薄荷淡紫魔光、萤火 |
| 3 | `Chapter03_Lake` | 双向水流折射、原画柔化镜像反射、焦散 |
| 4 | `Chapter04_Swamp` | 黏稠泥面、慢流褶皱、油膜和泥泡 |
| 5 | `Chapter05_Ice` | 固态冰面扫光、霜雾与三层飘雪 |
| 6 | `Chapter06_Temple` | 慢旋螺纹、呼吸光环和催眠符文 |

Shader文件位于`GameShader/Background/*_Shader.shader`，声明名为`RevGame/Background/Chapter0X_*`。材质位于`GameRes/Material/Background`：第一章ArcaneScene，其余`Chapter0X_*_Material`。效果参数直接在各章材质Inspector调节，场景只保存逻辑名、棋盘保护与布局。旧ChapterAtmosphere已移除，共用cginc只提供数学/采样，不按主题分支。

六个AB分别为`background_chapter01_tutorial`、`background_chapter02_village`、`background_chapter03_lake`、`background_chapter04_swamp`、`background_chapter05_ice`、`background_chapter06_temple`。PC/Android真实Manifest均为章节依赖0、其它包反向引用0，构建会强制检查。通过RevResManager按章异步租用，先接新图再释放旧图；Release归零进入未使用缓存，在空闲整理点真正卸载，不代表进程内存瞬间归零。

本轮Direct 231条、真实PC AB冷启动252条断言通过，含快速取消、六章切换、强度0/速度0静止、GPU动态、多比例覆盖、第一章packed图集和引用回收。真实AB整理点核对当前章仅1背景包，大厅0包。PC/Android各32包、310映射构建发布；最终C#编译和Console错误0。Android真机、整章通关与平衡尚未验证。

入口`game.background.import / audit / build / check / status`；`game.background.mode`只在编辑态保存下次Play偏好，规避当前框架setter的编辑态初始化问题。详情与原始PNG见`RevGame_Client/文档/进度同步/章节背景独立Shader与六章分包-2026-10-23.html`。

## 先跑起来

打开正式入口 `Assets/Scenes/StartScene.unity`，按 Play。依次进入冒险选关→第一章→手牌→任意合法空格→“开始战斗”。每回合发满六张；约3秒领取准备增益后可开战，无需等全部播报结束。准备期可在全盘空格落子；准备或战斗中均可点己方棋子再点空格免费换位，大棋必须整块放得下。战斗期不能落手牌或眼位石，手动换位保留伤势、护盾和攻击进度。刷新只重抽剩余 N 张，不补落子额度。本次运行中击败章节 Boss 后解锁下一章；退出程序后进度不保存。

启动只由 GameBootstrap 调用一次 RevInitialize.Initialize，并集中注册 IBattleService 与 GameFlow。不要再手动初始化事件、UI、计时器等模块，否则会清空已注册监听。

## 文件怎么找

| 目录 / 文件 | 负责什么 | 为什么这样分 |
|---|---|---|
| Core | 坐标、结构常量、枚举、事件、域名 | `Consts` 只留棋盘边长、章节数、手牌槽位数、层级、表现时长这些与预制体/场景绑定的结构常量；平衡数值不在这里。 |
| Data | 图鉴、关卡、奖励、事件定义（数据来自 Excel） | `Data/Config` 负责装配与校验：`GameConfig`（11 张表一次性校验）、`Balance`（68 个强类型参数）、`ConfigText`（说明文案占位符）、`GameConfigFileLoader`（仅编辑器断言/工程外测试用）。`PieceCatalog` / `LevelCatalog` 等保持原 API，内部读配置快照。 |
| Pieces/Model/Piece | 生命、身体大小、位置、气、眼 | 不持有 GameObject，视图回池不影响业务状态。 |
| Board/Model/BoardModel | 唯一棋盘数据 | 大棋所有身体格引用同一个 Piece；名单遍历不重复计算身体。 |
| Board/Rules/GoRules | 连通块、气、眼、锁死 | 规则只依赖棋盘，不依赖画面。 |
| Battle | HandModel、MoveRules、EnemyFormationPlanner、EnemyTactics | 手牌编号防重复消费；真实射界、完整眼阵和深快照保阵试走，不直接搬动实战棋子。 |
| Services/Battle/Simulation/BattleService | 回合、落子、攻击、胜负、敌方出生预检 | 所有动作先校验后提交；失败不留下半颗大棋或名单里的幽灵棋子。 |
| Services/Battle/Progression/BattleService.Progression | 奖励、章节推进、职业加成、双牌原位升级 | partial 是同一个服务按职责分文件，不是额外管理器。 |
| Data/Buffs/PreparationBuffCatalog、Services/Battle/Preparation/BattleService.Preparation | 十六种准备增益、延时发放、属性继承与本轮清理 | 属性在模型先统一生效；动画仅告知，不决定奖励是否到账。 |
| Services/Battle/Support/BattleService.Reinforcement | 强援选择、远程安全后排和近战可攻击敌眼评分 | 逐格深快照试放后才消费牌，保护己方眼和气。 |
| Services/Battle/Support/BattleService.Events | 八种事件、眼位石、伤害入口 | GM 与自动事件共用真实业务入口。 |
| Services/*.Diagnostics / *.Checks | 开发固定场景与编辑器断言 | 正式包排除造局与测试代码。 |
| Flow/States/GameFlow | RevLightFSM 调度 Home/LevelSelect/Battle/Exit | 状态控制当前页面，RevScope 负责当前域的回收。 |
| Flow/Logic/BattleLogic | 战斗域内以 0.3125 秒固定小步推进和动作转发，普通行动基准为 1.25 秒 | 小步用于分时远程出手，不表示全部棋子每小步攻击；节奏不藏在面板里。 |
| View/Board/BoardView.Feedback | 飘字/弹道预算、聚合、避让和遮挡淡化 | 异步加载预约也计数，清阶段同时清队列和代次。 |
| View/Board/BoardView | 格子、棋子、特效池与模型编号映射 | 异步回调核对进入批次；实例独立容器避免 OnDisable 期间换父节点。 |
| View/*Presentation | 流程与阶段到面板的接线、相机适配 | 服务不引用 UI；晚完成的加载需核对当前阶段并刷新数据。 |
| View/Pieces/PieceView、BattleEffectView | 帧动画、血条、王冠、破碎、升星、飘字 | owner 计时器在停用时取消，避免回池对象继续更新。 |
| View/Audio/GameAudio | RevSound 映射业务事件 | 运行时声音走逻辑路径，不直接加载磁盘。 |
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

## 配置表（数值的唯一来源）

**规则**：策划会调的数——血攻速、星级倍率、增益/事件的数值概率冷却、敌方成长、关卡与 Boss 阵容、奖励权重、战斗节奏、说明文案里的数字——只写在 Excel，不写在 C#。判断法：策划想调它，要不要程序改代码重编译？要，就该进表。

| 工作簿（`RevGame_Client/配置表/`） | 表 | 管什么 |
|---|---|---|
| 棋子数值配置.xlsx | `PieceConfig` `StarConfig` | 9 种棋子 1★ 血攻/走法/职业/开局是否解锁；1~3★ 倍率与我方占格 |
| 增益与事件配置.xlsx | `PrepBuffConfig` `BattleEventConfig` `EventRotationConfig` | 16 种准备增益（标题、说明模板、数值）；8 种战斗事件效果量与稀有分组；通关辅助下事件轮换顺序 |
| 关卡与奖励配置.xlsx | `ChapterConfig` `EnemyRosterConfig` `EnemyReinforceConfig` `RewardConfig` `RewardBoostConfig` | 每章敌方星级与 Boss 阵容；敌方出场名单（核心+填充）；战斗中补兵规则；奖励权重/解锁条件；全队强化量 |
| 战斗数值参数.xlsx | `BalanceRule` | 68 个整局通用参数（节奏、伤害公式、Boss 加速、敌方成长、补兵节奏、事件、通关辅助、准备期、奖励抽取），每行带含义说明 |
| 战斗弹幕配置.xlsx | `BattleText` `BattleTuning` | 弹幕文案/颜色/字号/时长；我方远程攻频（原有，未改动） |

**怎么改数值**：改 Excel → Unity 菜单 `Revolution.Tools/配置表/导表工具`，**添加整个 `配置表` 文件夹**（不要只加单个文件，否则新工作簿会漏导）→ 只改了数值点“仅数据”（不触发脚本编译），增删字段/表点“导出”。生成物 `GameScripts/Generated/Battle*.cs` 与 `GameRes/Data/*.txt` 要一起提交，不要手改。**新增表**还要确认 `ResMap.txt` 有 `Data/<表名>|data_tables|<表名>`（Unity 导表工具会自动生成，无头导出需手动补）和 `.meta`，否则编辑器正常、真机报 `PathNotMapped`。

**进 AB 包**：13 张表的数据文件都在 `GameRes/Data`，AB 标记打在 **`Data` 文件夹**上（`GameResourceBuild.Groups` 里的 `{ "Data", "data_tables" }`），子文件自动继承，所以每个 `*.txt.meta` 的 `assetBundleName` 都是空的——**新增表不用改打包脚本、不用给 meta 填包名**。三者必须对上：生成表 `base("Xxx")` 推出的逻辑路径 `Data/Xxx`、`ResMap.txt` 的 `Data/Xxx|data_tables|Xxx`、打包脚本里 `Data` 目录的包名；对不上就是真机 `PathNotMapped`。

**运行时**：`GameFlow.LoadGameConfigAsync` 用框架资源系统 `RevDataTableManager.LoadAsync`（分组 `Config`）逐张加载 11 张表 —— 编辑器直读、AB、热更、缓存与引用计数走同一条链路，退出时 `GameBootstrap` 按表 `Unload` 还掉资源引用。加载完成后 `GameConfig.Initialize` 一次性校验并装配；任何问题（枚举名拼错、缺行/多余行、数值越界或 NaN、整数填小数、占位符写错、章节数与选关按钮不符、补兵规则覆盖不全…）会攒成一份中文清单，走过场失败出口展示，**不会带着 0 值进入战斗**。业务读取：`Balance.*`（参数）、`PieceCatalog`、`LevelCatalog`、`PreparationBuffCatalog`、`BattleEventCatalog`、`RewardCatalog`。`BattleService` 构造函数和字段初始化里不得读配置（它在配置加载前就被注册了）。编辑器非 Play 断言开头调用 `GameConfigTestSupport.Ensure()`。

**只有测试才直读 txt，且进不了真机包**：`GameConfigFileLoader` 整个类包在 `#if UNITY_EDITOR || REVGAME_OFFLINE_TESTS` 里，只服务于编辑器非 Play 断言（没执行过 `RevInitialize`，拿不到资源策略链）和工程外纯 C# 测试。出包后 txt 在 AB 包里、磁盘上没有散文件，直读必然失败，所以它不允许进真机包；已用“去掉 `UNITY_EDITOR` 宏重新编译运行时程序集”验证过运行时不依赖它。

**说明文案与数值同源**：`PrepBuffConfig.Detail` 写成 `本轮全体攻击加成+{Amount%}`，数字取同一行的 `Amount`；`{名字%}` 按百分数显示，`{名字}` 显示数字。改数值后玩家看到的文字自动同步，战报里的 `（锁死×N）`、僵持支援的 `每N秒`、HUD 的决死伤害百分比也都读同一批参数。

**刻意留在代码里的（并说明原因）**：棋盘 9×9 与格子大小（与 81 格预制体、数组尺寸绑定）、章节数 6（选关界面 6 个按钮）、手牌上限 6（6 个常驻槽位）、层级排序、棋子移动/弹道动画时长（纯表现）、`Piece` 初始气数 4（棋盘重算前的占位）、飘字节流与池容量（表现节流）、`EnemyTactics` / `EnemyFormationPlanner` 里的目标评分权重和固定眼阵模板坐标（算法内部的相对优先级，不是可调“数值”）、`MoveRules` 的马步偏移（走法几何）、`HandRefresh` 里远程攻频的合法区间 1~4（校验边界）。若要把 AI 评分权重也开放给策划，需要先决定哪些是策划意义上的“难度旋钮”，再单独设计，不建议整体搬进表。

## 资源约定

美术在 `Assets/ArtRes/棋盘、棋子、状态标记、界面、图标、特效、字体、音频`，保留中文命名。运行时资源在 `Assets/GameRes/Board、Pieces、UI、Effects、Audio、Data`。配置工作簿位于仓库的 `RevGame_Client/配置表/`（见上一节），导表生成 C# 位于 `GameScripts/Generated`；不要手改生成物来替代原框架导表。

面板预制体名与类名相同，例如 `GameRes/UI/Panels/Battle/PreparePanel.prefab`；RevBind 字段 `_hintText` 对应节点 `hintText`。资源根配置在 `Assets/Editor/ABBuildConfig.asset`，逻辑路径只写 `UI/`、`Pieces/` 等，不拼 StreamingAssets 或 Application.dataPath。

## 代码规范

使用 Revolution 公开门面，不顺手修改当前工程的框架内核；异步只用 RevTask。方法签名的括号与参数保持同一行；方法体及 if/for/foreach/while 均带大括号。文件头说明分工理由，关键事务、时序、回收用中文解释，不只复述代码。

## 验证入口与本轮证据

非Play依次执行 `return RevGame.Editor.GameRuleChecks.Run();`、`return RevGame.Editor.GameLayoutChecks.Run();`、`return RevGame.Editor.GameTacticsChecks.Run();`。前轮实际分别为643、737、1280条通过（本轮仅改表现，未重跑这三组）；战术回归包含两组种子各连续66回合取胜。

Play在主界面分别执行 `RevGame.Editor.GameRoundPresentationChecks.Begin()`（136条）、`RevGame.Editor.GameGrowthPresentationChecks.Begin()`（23条）、`RevGame.Editor.GameReadabilityChecks.Begin()`（本轮已扩展为密集齐射检查）和 `RevGame.Editor.GameFeelChecks.BeginPoolCheck()`（通过）。先确认Play启动入口已完成，再运行检查；一组结束再运行下一组，池化检查等待至少四秒，不能中途换域。成长检查验证真实三秒计时、准备横幅1.67秒新时序、Boss可见Sprite换帧、血条/时间条Tween以及换轮清理。

可通过开发GM进入关卡或固定夹具；非法命令仍返回Success=false，按钮和GM走同一业务入口。原攻击/伤害/升级/事件表共22条，已通过Revolution导表工具修正两条旧升级说明；准备增益、基础成长等业务参数现已全部进入 Excel（见「配置表」一节）。

前轮已实际操作Unity进行六牌准备、双牌原位升级；本轮通过Unity MCP运行真实Play检查，新增战斗换位入口、密集反馈与事件居中验收。保持1920×1080横屏；未执行AB构建、手机真机性能或全策略平衡验收。旧版样本曾依赖保底支援；2026-10-23已移除保胜清敌，当前结果见本轮战斗升级报告。
