# RevGame 实践 · 章节战斗、AI、无尽与表现

Unity 路径相对 RevGame Assets。这个项目的棋盘格数、六章节奏、随机权重和伤害关系属于游戏规则。可迁移的是输入、提交、时钟、快照和清理的边界。

## 章节战斗

| 工作节点 | 源码阅读顺序 | 迁移时要复核 |
| --- | --- | --- |
| 真实进入和退出 | `GameScripts/Flow/States/GameFlow.Scenes.cs` → `GameScripts/Flow/Logic/BattleLogic.cs` → 局内服务 | 进入就绪才推进玩法。结算、失败、返大厅、重复进入分开验证。 |
| 准备、手牌、落子和占格 | `GameScripts/Services/Battle/Simulation/BattleService.cs` → `GameScripts/Services/Battle/Preparation/BattleService.PieceManagement.cs` | 校验选择、合法体型、资源额度和位置后一次提交。拒绝前后数量、状态、选中与占格一致。 |
| 预判、公式和真实扣血 | `GameScripts/Services/Battle/Support/BattleService.Damage.cs` → 对应视图和 HUD | 查询使用同一规则参数；命中、盾、复活、积分以实际回执为准。视觉到达不能再次打伤害。 |
| 敌人、领主和帮手 | `GameScripts/Battle/AI/Enemy/EnemyAI.cs`、`GameScripts/Battle/AI/Boss/BossAI.cs`、`GameScripts/Battle/AI/Helper/HelperAI.cs` | 普通敌人、领主演出、独立支援角色有各自规则与生命周期。多格落点要核实际几何能否放置。 |
| Boss 进场和终局 | `GameScripts/Flow/States/Boss/GameFlow.BossIntro.cs`、`GameFlow.BossVictory.cs` | 顺序演出等待素材、持有自己的镜头和音效令牌，结束或打断恢复输入、镜头/UI与时间设置。 |
| 配置与平衡 | `GameScripts/Data/Config/GameConfig.cs`、`Editor/RevGame/Balance/GameBalanceCampaignChecks.cs` | 数值、文案、事件、Boss 和奖励索引先过真实导表与装配，从固定种子对照局次检查战斗结果和事件。 |

## 权威逻辑和表现

1. 跟踪实际角色身份、棋盘或实体版本，以及该阶段的步长。阵型/AI 规划先读局面快照，在副本上试算；通过相同提交规则检查候选，避免边试边改真实棋盘。
2. 找唯一击中/伤害/计分出口。视图只从提交后的事件、回执和快照读取结果；重复特效、动画跳帧、回放跳转或帧率变化都留业务状态可追踪。
3. 缓存的失效依据业务版本。布局、占格或生存状态变更才让对应缓存过期，退出释放单局持有。新项目选择自己的定步、时间刻度和预算，避免从一个性能样本直接推导帧率保证。

## 无尽模式

- 从 `GameScripts/Flow/States/GameFlow.Endless.cs` 和 `GameScripts/Flow/Logic/Endless/EndlessLogic.cs` 看真实开局、继续、钟与离场。 `GameScripts/Services/Endless/EndlessService.cs` 持有棋局，`EndlessService.Combat.cs`、`EndlessService.BossSkills.cs` 负责对应提交，配置校验、牌与结算沿所属服务。
- `GameScripts/Services/Endless/Ecs/EndlessHordeComponents.cs` 的实体槽及代次用于识别实际身份。 `GameScripts/View/Endless/EndlessHordePresentation.cs` 用逻辑提交样本向渲染交付插值；删除、重用、瞬移或阶段断点重新确定起点。渲染结果不会回写敌人逻辑位置。
- `GameScripts/View/Endless/EndlessQuadBatch.cs` 只提交有用顶点范围。上一版和新版分开算整帧、单帧位置/外观提交、每秒数据量、颜色 UV 是否复用。更多输出帧会改变总量，按对应口径解释。报告用固定种子、相同工作量和时段，回大厅后核对旧绘制和引用消失。
- 按实际模式上限、伤害回执、出牌/升星、Boss 技能、暂停、改阵、存档与结算逐项回归。 `GameScripts/Bootstrap/Diagnostics/Endless/EndlessAutomation.cs` 提供隔离检查。压力入口使用声明负载，不移植为生产能力。

进一步查看 [固定步与表现帧规则](../../revolution-performance/references/frame-presentation.md) 和 [性能采样口径](revgame-delivery.md#性能与目标设备)。
