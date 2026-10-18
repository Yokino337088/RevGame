> 2026-10-18 当前动态美术更新：最多20条更小飘字，密集输入实测20条；攻击提速35%，单发伤害按比例摊薄以保留长局，敌棋五秒移动冷却独立计算。弹道按距离收缩/计时，增加自然结束与中断寿命回收。三类Shader位于GameShader，四个共享材质位于GameRes/Material，已绑定35个UI底图；移除UI大地图背景，六牌流光与DOTween点击抖动已接入。645条规则、737条布局、1280条战术、37条动态美术Play、49条密集反馈Play、136条回合Play及池化检查通过。详见[RevGame-魔幻动态美术与弹幕增强-2026-10-18](RevGame-魔幻动态美术与弹幕增强-2026-10-18.md)，下方旧版本数字为历史记录。

> 2026-10-18 最新追加：敌方6～19名及逐轮基础属性成长、四档AI、双牌原位升级、15种准备增益（每轮3～5种）、3秒后DOTween横穿播报、时间/Boss血条缓动、Boss真实重剑序列帧已实现。620条规则、737条布局、1280条战术、136条回合Play、23条成长Play及对象池检查通过；两组种子各连续66轮通关。升级保留Id/位置/占格，新牌照常落下。 详见[成长与准备增益验收](RevGame-成长与准备增益验收-2026-10-18.md)。下方早期规则与数字仅作历史记录。

> 2026-10-18 当前更新：战斗与围棋 AI 已实施，普通敌方 6～10 名含三种远程，真实一眼/两眼布阵、深快照保阵移动、90 秒普通上限和阶段式保障。1150 条战术断言、570 条规则、737 条布局、136 条 Play 回归通过；两组固定种子各连续取胜 66 回合。详见[战斗与围棋 AI 改造验收](RevGame-战斗与围棋AI改造验收-2026-10-18.md)。保持 1920×1080 横屏、六牌和既有资源。下文历史的完成百分比、18秒/45秒和旧测试数字不代表当前版本；正式基础血攻/阵容仍待用户数值。

> 2026-10-17 历史回填：第1–7节保留接手时历史快照；手机横屏、六牌刷新、全盘重摆、DOTween池化弹幕/Excel和回合清理等前序细节见第11节。

# RevGame 项目现状与 Codex 交接文档

> **给读这份文档的 Codex**：这是一份"接手即可开工"的交接书。
> 读完后你应该能独立完成剩余功能：所有路径、类名、契约、约定、已知坑都写在下面，**不要重新设计架构**，按「第 8 节 任务看板」从上往下做即可。
>
> 更新时间：2026-10-17 · 当前编译状态：**0 error** · 文档作者上一轮完成到「UI 三面板 + 棋子动画预制体」

---

## 0. 一页速览

| 项 | 值 |
|---|---|
| 项目 | RevGame（围棋史莱姆）——**本质是 Revolution 框架的可行性验证项目**，玩法只是载体 |
| 玩法来源 | `RevGame_Client/策划案/玩法拓展策划案.html`（v18，权威，改玩法先改它） |
| 引擎 | Unity **2022.3.15f1c1**（2D 模板，uGUI + TextMeshPro 已装） |
| 框架 | Revolution 装在 `Assets/Revolution`（从 `package` 分支快照装入，**不要在这里改框架**） |
| AI 桥 | `com.coplaydev.unity-mcp`（已装，用于**所有资源/预制体操作**） |
| 本机工程根 | `d:\AAAAAAA王者荣耀项目组工作日志\RevGame\RevGame_Client\UnityProject\RevGame.Unity` |
| 代码规模 | 22 个 `.cs` / **2,811 行**，命名空间 `RevGame.*` |
| 美术 | `Assets/ArtRes` 共 **286 个文件**（全中文命名，7 个目录） |
| 预制体/配置 | `Assets/GameRes` 共 **11 个**（10 prefab + 1 配置表） |
| **整体完成度** | **约 45%**（规则内核 90% / 战斗服务 70% / UI 30% / 表现层 15% / 局外 0%） |

---

## 1. 目录与文件清单（真实清单，勿臆测）

### 1.1 代码 `Assets/GameScripts/`（22 个文件）

```text
Core/              纯 C# 基建（不依赖 UnityEngine 逻辑）
  Consts.cs          81 行  常量：棋盘 9、6 章、11 回合、18s 战斗、落子上限 4、锁死伤害×2…
  Enums.cs          104 行  PieceClass(4 职业) / PieceKind(9 外形) / StarTier / GameSide / GamePhase / GroupState / MovePattern
  BoardPos.cs       107 行  格子坐标 struct（可当 Dictionary key，内置 ToWorldPosition/FromWorldPosition）
  Events.cs          65 行  全局事件名常量（唯一真源）
  GameDomain.cs      20 行  业务域常量 GameDomain.Battle = "RevGame.Battle"
Data/              静态数据（数值为占位，等 Excel）
  PieceDef.cs        67 行  一种棋子的图鉴定义（Kind/Class/Star/MaxHp/Attack/Pattern/MoveRange/IsRanged/SpriteName）
  PieceCatalog.cs   110 行  9 外形 × 3 星 = 27 份定义（静态构造生成，数值占位）
  LevelCatalog.cs   101 行  6 章 × 11 回合（RoundDef：Chapter/Round/IsBoss/EnemyKinds）+ 敌方星级规则
Pieces/            运行期棋子模型
  Piece.cs          107 行  Id / Def / Side / Pos / Hp / Liberties / GoState / IsLocked / HpRatio / TakeDamage / Heal
Board/             棋盘数据 + 围棋规则
  Cell.cs            41 行  一格（Pos / Occupant / IsStarPoint / IsEyeMarked）
  BoardModel.cs     207 行  ★ RevGameCell[9,9] 二维数组 + Dictionary<BoardPos,Piece>；Place/Remove/Move/CanPlace/GetPiecesOf/IsSideWiped/Dump(字符画)
  GoRules.cs        195 行  ★ BuildGroups(洪水填充) / CountLiberties / HasTwoEyes / Evaluate(刷新气·叫吃·锁死)
Battle/            走法与攻击
  MoveRules.cs      201 行  Step / Slide / Knight(蹩马腿) / Cannon(隔子打)；GetMoveTargets / GetAttackTargets
Services/          服务层（取代原项目单例管理器）
  IBattleService.cs  79 行  战斗服务契约（Phase/Chapter/Round/Board/Elapsed/Duration/PlayerPieces/EnemyPieces/LastWinner/StartRound/TryPlayCard/StartBattle/Tick/CompleteSettle/LastError）
  BattleService.cs  458 行  回合生命周期、落子校验、固定 0.5s 步长推进、寻敌与攻击、伤害(锁死×2)、超时按血量判胜、Boss 回合、RebuildBoardFromRoster
Flow/              域业务（节奏编排）
  BattleLogic.cs    183 行  [RevLogic(GameDomain.Battle)] RevLogicBase：Every(0.5s) 驱动 Tick、监听 BattleEnded 自动进下一回合/重开本章、BeginBattle/PlayCard/ContinueToNextRound/Snapshot
Bootstrap/         启动入口
  GameBootstrap.cs  117 行  RevInitialize.Initialize() → RevServices.Register → RevScope.Enter → RevLogic.Start → 4 条 RevGM 指令 → OnDestroy 退出域
View/              表现层（棋子）
  PieceArtConfig.cs  87 行  ScriptableObject：Entry{kind,star,side,idleFrames,frameInterval,baseSprite,lockedFrames,lockedInterval}；Find(kind,star,side)
  PieceView.cs      188 行  一个预制体装所有棋子：Bind(pieceId,kind,star,side)/SetHp(ratio)/SetLocked(bool)/Clear()；RevTimer.Every(0.05s,…,owner:this) 播帧
UI/                界面
  BattleHudPanel.cs 131 行  阶段图标 + 回合文本 + 进度条(fillAmount) + 双方存活数 + 战斗日志（订阅 PhaseEnterBattle/Settle/BattleLog/PieceHpChanged/PieceDied，owner:this）
  PreparePanel.cs    84 行  剩余落子提示 + 准备按钮 → BattleLogic.BeginBattle()；失败原因原样显示
  SettlePanel.cs     78 行  胜/负 + 章节回合 + 继续按钮 → BattleLogic.ContinueToNextRound()；数据类名 **SettleData**（不能叫 Data）
```

### 1.2 美术 `Assets/ArtRes/`（286 个文件，**全中文命名**）

| 目录 | 数量 | 内容 |
|---|---|---|
| `棋盘/` | 12 | 单格底、星位、可落子高亮、不可落红框、中线、布阵半场、走法提示（兵/车/马/炮架）、占格虚影 2×2/3×3 |
| `棋子/` | 208 | `棋子_<种类><星>_立绘.png`（9×3）+ `棋子_Boss_大棋_2x2/3x3/底座光环` + `序列帧/{棋子_近战,棋子_弓箭,棋子_法师,棋子_史莱姆,棋子_特殊}` |
| `状态标记/` | 18 | 叫吃脉冲 ×3、围困锁链、断、活棋光环、眼、连（上下左右）、气数小圆点（橙白红蓝）、`围困/状态_被围标识_0001~0003` |
| `界面/` | 21 | 手牌栏底框、卡牌底（默认/选中）、回合进度条（底/填充）、准备按钮（默认/按下）、阶段指示（准备/战斗/结算）、决死加速、战斗日志弹条、三选一弹窗底、王冠、Boss 血条框、战斗技能栏、战斗选中框、战斗买兵底图、通用提示底、结算背景、选关背景 |
| `图标/` | 12 | 事件图标 ×8（回气/天降援兵/断链/炮火支援/疗愈雨/瞬移潮/补眼/连击）+ 奖励图标 ×4（全队强化/同名牌/新棋子/眼位石） |
| `特效/` | 14 | 三合一升星 ×8、提子破碎 ×6 |
| `字体/` | 1 | `明朝体.ttf`（I.Ming） |

### 1.3 预制体/配置 `Assets/GameRes/`（**全英文目录**）

| 路径 | 说明 |
|---|---|
| `棋子美术配置.asset` | PieceArtConfig：**54 组**（9 外形×3 星×玩家/敌方）、**135 帧**待机动画 + 3 帧被围标识（由工具扫描生成，0 未匹配） |
| `Pieces/棋子.prefab` | **一个预制体装所有棋子**：根（SpriteRenderer 10 + PieceView）+ 子 `底座`(9) / `血条底图`(11) / `血条`(12) / `被围标识`(13) |
| `Pieces/PieceView.prefab` | 早期静态占位（可保留作对照，实际用 `棋子.prefab`） |
| `Pieces/BossPieceView.prefab` | Boss 大棋静态占位（未接逻辑） |
| `Board/BoardCell.prefab` | 单格底（SpriteRenderer 0） |
| `Board/BoardStarPoint.prefab` | 星位点（1） |
| `Board/BoardMoveHighlight.prefab` | 可落子高亮（5） |
| `Board/BoardDeployZone.prefab` | 布阵半场（2） |
| `UI/BattleHudPanel.prefab` | 根 + 6 子节点：`phaseIcon` / `roundText` / `progressBar` / `progressFill`(Filled) / `countText` / `logText` |
| `UI/PreparePanel.prefab` | 根 + `hintText` + `btnReady`(label) |
| `UI/SettlePanel.prefab` | 根 + `background` + `titleText` + `detailText` + `btnContinue`(label) |

---

## 2. 完成度评估（诚实版）

| 维度 | 完成度 | 说明 |
|---|---|---|
| 规则内核（棋盘/气/眼/锁死/走法） | **90%** | 缺：三合一升星、同职业加成、Boss 大棋占格、战斗事件 |
| 战斗服务（回合/推进/伤害/胜负） | **70%** | 缺：手牌系统、抽牌、跳过换强化、超时细则、Boss 擒王 |
| 流程编排（RevScope + RevLogic） | **80%** | 回合推进已通；缺：登录/大厅/结算→奖励的完整链路 |
| UI | **30%** | 3/7 个界面；**手牌卡牌列表未做**（最关键缺口）、三选一未做、关卡选择/主界面 0 |
| 表现层（棋盘渲染） | **15%** | 棋子动画已完成；**棋盘/格子/高亮/标记/特效一行没写**（BoardView 缺失） |
| 数据（Excel） | **20%** | 全是占位表；用户明确说"数值等他敲定" |
| 资源 | **85%** | 图基本够用；**音频完全没导入** |
| 工具与规范 | **100%** | 命名/注释/服务/域/事件/预制体工具链都已成型 |
| **整体** | **≈45%** | 逻辑骨架能跑，但"看得见的一盘棋"还差 BoardView + 手牌 |

---

## 3. 已实现的功能（可验收）

1. **棋盘数据模型**：9×9 二维数组 + 字典索引；落子/移动/移除/查询/全灭判定/字符画调试。
2. **围棋规则**：连通块洪水填充、气数统计（去重）、叫吃、锁死、两只眼判定，每次结算统一刷新。
3. **象棋式走法**：单步 / 滑步（车）/ 马步（含蹩马腿）/ 炮（隔且仅隔一子吃）。
4. **战斗推进**：固定 0.5s 步长（不受帧率影响）；先打能打到的，打不到逼近最近敌人；伤害 = 攻击 ×（锁死 ? 2 : 1）。
5. **回合与关卡**：6 章 × 11 回合（第 11 回合 Boss）、敌方阵容与星级按章生成、超时按剩余血量判胜。
6. **服务化**：`IBattleService` + `RevServices.Register/GetRequired`，无单例；`GameBootstrap` 负责注册与 `RevScope` 进出。
7. **域业务编排**：`BattleLogic`（`RevLogicBase`）驱动 tick、胜负后自动进下一回合/重开本章。
8. **棋子表现**：一个预制体 + 配置表装 54 种（种类×星级×阵营）待机动画，`RevTimer` 驱动，血条/锁死标识接口就绪。
9. **UI 三面板**：HUD（阶段/回合/进度/存活/日志）、准备（提示+开战）、结算（胜负+继续）。
10. **GM 指令 4 条**：`RevGame/快照`、`RevGame/开始战斗`、`RevGame/落一张盾战`、`RevGame/下一回合`。

---

## 4. 未实现的功能（按优先级，含验收标准）

### P0 — 不做就"看不见游戏"（必须最先做）

#### P0-1 `BoardView` 棋盘渲染（**最大缺口**）
- **落点**：`Assets/GameScripts/View/BoardView.cs`（新建）
- **职责**：按 `BoardModel` 铺 81 个格子（用 `GameRes/Board/BoardCell.prefab`），把 `Dictionary<int, PieceView>` 与棋子 Id 对上；订阅 `Events.PiecePlaced/PieceMoved/PieceDied/GoStateRefreshed/DamageResolved` 做增删与刷新；锁死时调 `PieceView.SetLocked`、掉血调 `SetHp`。
- **验收**：Play 后能看到 9×9 棋盘 + 双方棋子待机动画；GM `落一张盾战` 后新棋子出现在正确格子；`开始战斗` 后棋子会移动/掉血/死亡消失。
- **注意**：坐标换算只用 `BoardPos.ToWorldPosition(Consts.CellSize, Consts.BoardOriginOffset)`；**禁止**在逻辑层用世界坐标反算格子。

#### P0-2 手牌系统 + 准备面板改造
- **落点**：`GameScripts/Battle/HandModel.cs`（新建）、`UI/PreparePanel.cs`（改造）、`GameRes/UI/CardItem.prefab`（MCP 新建）
- **规则**（策划案 §05）：起手 3 张（`Consts.InitialHandSize`）、上限 5（`MaxHandSize`）、每回合可落 2~4 张（`MaxCardsPerRound`）、随机补给。
- **验收**：准备阶段底部显示 N 张卡（`UI_卡牌底_默认` / 选中用 `UI_卡牌底_选中`），点卡→点亮棋盘可落格（`BoardMoveHighlight`）→ 点格子落子→ 卡消耗、`HandChanged` 事件触发。
- **依赖**：P0-1（需要棋盘格子点击）。

#### P0-3 场景装配
- **落点**：`Assets/Scenes/SampleScene.unity`（用 MCP `manage_gameobject` / `manage_scene`）
- **内容**：`GameBootstrap`（挂 `GameBootstrap`）、`BoardView` 根节点、`Main Camera`（正交，size≈5.5，看 9×9 棋盘）、Canvas 由框架自动创建。
- **验收**：打开场景直接 Play，Console 无 error，HUD 出现。

#### P0-4 `ResRoot` 持久化
- **问题**：`RevEditorResPolicy.ResRoot` 现在被临时设为 `Assets/GameRes/`，**重启编辑器会被 `ABBuildConfig` 覆盖**，然后面板加载失败。
- **落点**：`Assets/Editor/ABBuildConfig.asset`（框架的打包配置）→ 把 `resRoot` 设为 `Assets/GameRes/`；或让 `GameBootstrap` 在编辑器下启动时兜底赋值（`#if UNITY_EDITOR`）。
- **验收**：重启 Unity 后 `RevUI.Open<BattleHudPanel>()` 仍能加载。

### P1 — 玩法闭环

#### P1-1 三选一奖励面板（策划案 §04）
- **落点**：`UI/RewardPanel.cs` + `GameRes/UI/RewardPanel.prefab`（用 `UI_三选一_弹窗底` + 4 个奖励图标）
- **规则**：每章通关后弹 3 选 1（全队强化 / 同名牌 / 新棋子 / 眼位石）；也可"跳过关卡换一档强化"。
- **验收**：`RevUI.Open<RewardPanel, RewardPanel.RewardData>(data)` 能弹出、点击生效进下一章。

#### P1-2 Boss 大棋 + 擒王
- **落点**：`BattleService.SpawnEnemies`（Boss 生成）、`BoardModel`（支持 2×2/3×3 占格）、`View/BoardView`（Boss 用 `棋子_Boss_大棋_*` + 底座光环 + 王冠）
- **规则**：Boss 回合战斗不限时；打死或围死 Boss 直接通关本章；Boss 体积随章数长大（2×2 → 3×3）。
- **验收**：第 11 回合出现 Boss、血条用 `UI_Boss血条框`、击杀后 `GameFinished` 或进入下一章。

#### P1-3 三合一升星 + 同职业加成
- **落点**：`BattleService.TryPlayCard` 后检查同名同星 3 个 → 合成升星（发 `Events.PieceMerged`）；`PieceCatalog` 已按星级给数值。
- **验收**：连落 3 张同名 1★ 卡 → 棋盘上变成 1 个 2★（用 2★ 立绘），血量/攻击按表。

#### P1-4 战斗事件（随机天降帮手）
- **落点**：`BattleService.ResolveOneStep` 里按概率触发；事件定义放 `Data/BattleEventCatalog.cs`（新建）；图标用 `ArtRes/图标/图标_事件_*.png`。
- **规则**：8 种事件（回气/天降援兵/断链/炮火支援/疗愈雨/瞬移潮/补眼/连击），通过 `Events.BattleEventTriggered` 广播，HUD 弹提示条（`UI_战斗日志_弹条`）。
- **验收**：战斗中有概率触发，日志出现"炮火支援"等字样且效果生效。

### P2 — 局外与打磨

- **P2-1 局外一条线**：主界面（`UI_Begin_*`）/ 关卡选择（`UI_关卡选关_*`）/ 编队；验收：从主界面 → 选关 → 进入战斗 → 结算 → 回主界面。
- **P2-2 Excel 数据表接入**：替换 `PieceCatalog` / `LevelCatalog`，走 `RevDataTableManager`；用户尚未给数值，**等指令**。
- **P2-3 音效**：`ArtRes/音频` 尚未建；源在 `美术/原版美术资源/05_音频`（8 个文件）；用 `RevSound.Play`。
- **P2-4 表现打磨**：提子破碎（`特效/特效_提子破碎_*`）、升星（`特效_三合一升星_*`）、伤害飘字（`状态/状态_受伤数字_*`）、相机适配、`RevSequence` 编排回合表现。
- **P2-5 存档/成就/图鉴**：`goals` 未建，优先级最低。

---

## 5. 架构与约定（**改动前必读，违反即返工**）

### 5.1 分层（严格单向依赖）

```
Core  ←  Data  ←  Pieces  ←  Board  ←  Battle  ←  Services  ←  Flow  ←  Bootstrap
                                                                    ↑
                                                        View / UI（只读服务 + 事件）
```
- 逻辑层（Core→Flow）**不许**出现 `UnityEngine` 的 Transform/GameObject；
- `Board/` 的数据模型是唯一真源，**禁止**在别处再存一份棋盘副本；
- 表现层（View/UI）只订阅 `Events.*` 或读服务，**不改游戏数据**。

### 5.2 命名与代码风格（硬约束）

1. 命名空间按目录：`RevGame.Core` / `RevGame.Data` / `RevGame.Pieces` / `RevGame.Board` / `RevGame.Battle` / `RevGame.Services` / `RevGame.Flow` / `RevGame.Bootstrap` / `RevGame.View` / `RevGame.UI`；
2. **类名不加 `Rev`/`RevGame` 前缀**（命名空间已表达归属）；
3. **方法体必带大括号，`{` 换行**（哪怕一行）；`if / for / foreach / while` 均带大括号，按本地技能统一；
4. 注释**中文、面向新手**，解释"为什么"而不是复述代码；
5. 不使用 `System.Threading.Tasks`；异步只用 `RevTask`；
6. 不新增 `System`/`BusinessLogic` 之类分层；不为单个玩法新造管理器（用服务）；
7. 数值常量进 `Consts`，事件名进 `Events`，域名进 `GameDomain`。

### 5.3 服务 / 域 / 事件

```csharp
// 注册（只在 GameBootstrap）
RevServices.Register<IBattleService, BattleService>(owner: this);
// 取用（任何地方，缺了当场炸）
IBattleService battle = RevServices.GetRequired<IBattleService>();
// 域
RevScope.Enter(GameDomain.Battle);          // 进域
RevLogic.Start<BattleLogic>();              // 起域业务
// 事件（同一事件名只能一种签名！）
RevEvent.AddEventListener<int>(Events.PiecePlaced, OnPlaced, 0, this);  // owner: this → 自动解绑
RevEvent.DispatchEvent<int>(Events.PiecePlaced, piece.Id);
```
- `RevLogicBase` 子类用 `[RevLogic(GameDomain.Battle)]`，生命周期 `OnStart/OnTick/OnStop`，内置 `After/Every/Listen`（自动带 `owner: this`）；
- **计时器必须带 owner 或域**，否则会泄漏。

### 5.4 UI 面板机制（关键，最容易踩）

```csharp
[RevUIPanel(RevResPath.UI, RevUILayer.Normal)]   // RevResPath.UI == "UI/"
public sealed class XxxPanel : RevUIPanel { [RevBind] private Text _title; ... }
```
- **预制体路径必须 = `Assets/GameRes/UI/<类名>.prefab`**（逻辑路径 `UI/<类名>` + `ResRoot = Assets/GameRes/`）；
- `[RevBind]` 按**节点名**匹配：字段 `_title` ↔ 节点 `title`（前导下划线自动忽略）；重名/深层用 `[RevBind("Top/Title")]`；
- `OnBindView()` 只装配（拿控件、挂事件）；`OnRefreshView()` 只落屏（不改数据）；
- 一次性数据走 `RevUIPanel<TData>` + `RevUI.Open<T, TData>(data)`；泛型面板的数据类**不要叫 `Data`**（会遮蔽基类 `Data` 属性）；
- UnityEvent（Button.onClick）框架不管 → 必须在 `OnRelease()` 里 `RemoveListener`。

### 5.5 资源与预制体命名

| 位置 | 语言 | 规则 |
|---|---|---|
| `Assets/ArtRes/**` | **中文** | 目录：棋盘/棋子/状态标记/界面/图标/特效/字体；文件：`棋盘_*` / `棋子_<种类><星>_立绘` / `UI_*` / `图标_事件_*` |
| `Assets/GameRes/**` | **英文** | 目录：Board / Pieces / UI / Effects；面板预制体 = 类名 |
| 序列帧 | 中文 | `棋子_<种类><星>_待机_n`（玩家）/ `棋子_敌_<种类><星>_待机_n`（敌方）/ `棋子_重剑待机<星>_序列_n`（第三种格式） |

---

## 6. 数据结构速查（写代码前对一遍）

### 枚举（`Core/Enums.cs`）
```csharp
PieceClass { Guard, Mage, Ranger, Assassin }
PieceKind  { Swordman, Shield, HeavySword, Archer, Crossbow, MageWater, MageFire, Priest, Assassin }
StarTier   { One=1, Two=2, Three=3 }
GameSide   { Player, Enemy }
GamePhase  { Prepare, Battle, Settle, Reward }
GroupState { Normal, Atari, Locked }
MovePattern{ Step, Slide, Knight, Cannon }
```

### 常量（`Core/Consts.cs`，节选）
```csharp
BoardSize=9  CellSize=1f  BoardOriginOffset=-4.5f
ChapterCount=6  NormalRoundsPerChapter=10  BossRoundIndex=11  RoundsPerChapter=11
BattleDurationSeconds=18f  BossBattleMaxSeconds=60f  BattleTickSeconds=0.5f
MaxCardsPerRound=4  InitialHandSize=3  MaxHandSize=5
LockedDamageMultiplier=2f
SortingOrderBoard=0 Piece=10 Status=20 Effect=30
```

### 事件（`Core/Events.cs`，节选）
```csharp
PhaseEnterPrepare(int)  PhaseEnterBattle(int)  PhaseEnterSettle(GameSide)  GameFinished(bool)
PiecePlaced(int)  PieceMoved(int)  PieceDied(int)  PieceHpChanged(int)  PieceMerged(int)
GoStateRefreshed()  DamageResolved(int)  BattleEnded(GameSide)  BattleEventTriggered(string)
HandChanged()  BattleLog(string)
```

### 战斗服务契约（`Services/IBattleService.cs`）
```csharp
GamePhase Phase; int Chapter; int Round; bool IsBossRound; BoardModel Board;
float BattleElapsed; float BattleDuration; IReadOnlyList<Piece> PlayerPieces/EnemyPieces;
GameSide? LastWinner; string LastError;
bool StartRound(int chapter, int round);
bool TryPlayCard(PieceKind kind, StarTier star, BoardPos pos);
bool StartBattle(); void Tick(float dt); bool CompleteSettle();
```

### 棋盘模型（`Board/BoardModel.cs`）
```csharp
Cell GetCell(BoardPos) / Piece GetPiece(BoardPos) / bool CanPlace(BoardPos)
bool Place(Piece, BoardPos) / bool Remove(Piece) / bool Move(Piece, BoardPos)
List<Piece> GetAllPieces() / GetPiecesOf(GameSide) / bool IsSideWiped(GameSide)
string Dump()   // 字符画，调试神器
```

### 棋子表现（`View/PieceView.cs`）
```csharp
void Bind(int pieceId, PieceKind kind, StarTier star, GameSide side);
void SetHp(float ratio);   void SetLocked(bool locked);   void Clear();
```

---

## 7. 已知坑（避免重复踩，逐条）

1. **嵌套数据类不能叫 `Data`**：`SettlePanel` 曾用 `Data` 遮蔽基类属性，报 `CS0119/CS0120`。→ 用 `SettleData`。
2. **面板预制体必须与类同名同路径**：`GameRes/UI/<类名>.prefab`；否则 `RevUI.Open<T>()` 加载失败（日志会报找不到资源）。
3. **`ResRoot` 会被覆盖**：静态字段重启即失效 → 需写进 `ABBuildConfig`（见 P0-4）。
4. **`[RevBind]` 靠节点名**：改节点名 = 断绑定；重名用显式路径。
5. **UnityEvent 要手动解绑**（`OnRelease`），否则回池叠加回调。
6. **美术改名必须连 `.meta` 一起改**（GUID 不变，预制体引用才不断）。
7. **MCP `manage_asset import` 不能从工程外拷文件**（会拼成 `Assets/D:/...`）→ 文件落地用文件系统，之后用 MCP 做导入设置/预制体/校验。
8. **MCP `execute_code` 挡 `AssetDatabase.DeleteAsset`** → 删除走 `manage_asset action=delete`。
9. **编译错误看 MCP `read_console`**，不要只看 `Editor.log`（追加式，含旧错误；重命名后必出 `CS2001` 旧记录，`csproj` 会自动重建）。
10. **`git commit -m "中文"` 会乱码** → 用 UTF-8 文件 + `git commit -F`（本项目暂未入 git）。
11. **序列帧三种命名**（见 5.5），生成配置表时用别名 + 双正则（现有 `PieceArtConfig` 生成逻辑已验证 54/54 命中）。
12. **`RevTimer.Every` 参数顺序**：`(interval, callback, times = -1, domain = Scaled, owner = null)`；`times: -1` 无限。

---

## 8. 任务看板（**按顺序做，Codex 直接照此执行**）

> 每完成一项：① `refresh_unity{compile:"request"}` ② `read_console(types:["error"])` 必须 0 条 ③ 按该项"验收"手工/脚本验证 ④ 在本文档把 `[ ]` 改成 `[x]`。

### 阶段 A：让它"看得见"（P0）
- [x] **A1** 写 `View/BoardView.cs`：铺 81 格、棋子 Id↔PieceView、事件驱动增删/血量/锁死
- [x] **A2** 场景装配 `SampleScene`：GameBootstrap + BoardView 根 + 正交相机（MCP `manage_gameobject`）
- [x] **A3** 手牌模型 `Battle/HandModel.cs` + `GameRes/UI/CardItem.prefab` + `PreparePanel` 改造
- [x] **A4** 棋盘点击落子（`BoardView` 接 `IPointerClickHandler` 或射线）
- [x] **A5** `ResRoot` 写入 `ABBuildConfig`，重启编辑器复验面板加载

**阶段 A 验收**：Play 后能看到完整棋盘与棋子动画；能用手牌落子；点「准备完毕」后棋子自动开打、掉血、死亡消失；结算面板弹出、点继续进下一回合。

### 阶段 B：玩法闭环（P1）
- [x] **B1** `UI/RewardPanel.cs` + `GameRes/UI/RewardPanel.prefab`（三选一）
- [x] **B2** Boss 大棋（2×2/3×3 占格 + 王冠 + Boss 血条 + 擒王判定）
- [x] **B3** 三合一升星 + 同职业加成
- [x] **B4** 战斗事件（8 种，`Data/BattleEventCatalog.cs` + 触发 + HUD 提示）

### 阶段 C：局外与打磨（P2）
- [x] **C1** 主界面 → 关卡选择 → 战斗 → 结算 → 回主界面（局外一条线）
- [x] **C2** 音效（导入 `05_音频` → `ArtRes/音频`，`RevSound.Play`）
- [x] **C3** 表现：提子/升星特效、伤害/攻击弹幕、升级/支援 UI；最新为 DOTween + RevPool，含手牌翻转
- [x] **C4a** 弹幕文案/动画参数/远程频率 Excel：原框架导出代码与数据并异步加载，2 表 22 行
- [ ] **C4b** 正式基础血攻和敌方阵容 Excel（等用户提供正式数值）

---

## 9. Codex 执行手册（工具与命令）

### 9.1 资源/预制体一律走 MCP（硬要求）

| 目的 | MCP 调用 |
|---|---|
| 建目录 | `manage_asset {action:"create_folder", path:"Assets/GameRes/UI"}` |
| 查资源/确认导入 | `manage_asset {action:"get_info", path:"Assets/ArtRes/界面/UI_手牌栏_底框.png"}` |
| 删资源/空目录 | `manage_asset {action:"delete", path:"..."}` |
| 批量操作/生成预制体 | `execute_code {action:"execute", code:"...C#..."}`（编辑器内执行，CodeDom = C# 6：**不能用 `$""`、`out var`、局部函数、`?.`**） |
| 预制体结构校验 | `manage_prefabs {action:"get_hierarchy", prefab_path:"..."}` |
| 触发编译 | `refresh_unity {compile:"request"}` |
| 读编译错误 | `read_console {action:"get", types:["error"], count:"10", format:"plain"}` |

**预制体生成模板**（照抄改字段）：
```csharp
var go = new GameObject("XxxPanel", typeof(RectTransform));
var img = go.AddComponent<UnityEngine.UI.Image>(); img.sprite = AssetDatabase.LoadAssetAtPath<Sprite>("Assets/ArtRes/界面/xxx.png");
var panel = go.AddComponent<RevGame.UI.XxxPanel>();
var so = new UnityEditor.SerializedObject(panel);
so.FindProperty("someSprite").objectReferenceValue = ...;   // [SerializeField] 字段
so.ApplyModifiedPropertiesWithoutUndo();
UnityEditor.PrefabUtility.SaveAsPrefabAsset(go, "Assets/GameRes/UI/XxxPanel.prefab");
UnityEngine.Object.DestroyImmediate(go);
```
> `[RevBind]` 字段**不用**手动赋值 —— 只要节点名对得上。

### 9.2 验收命令（GM）

Unity 菜单打开 GM 面板（`Revolution.Tools/GM`），依次执行：
```
RevGame/落一张盾战   → 己方半场落 1★ 盾战
RevGame/开始战斗     → 进入 18 秒自动战斗
RevGame/快照         → 打印双方血量/气/锁死状态 + 字符画棋盘
RevGame/下一回合     → 结算后进下一回合
```

### 9.3 编译基线

- 当前 **22 个 .cs / 2,811 行，0 error**；
- 每次改动后必须回到 0 error 才能继续；报错优先看 `read_console`。

---

## 10. 参考与边界

- **玩法权威**：`RevGame_Client/策划案/玩法拓展策划案.html`（v18）。策划案里"明确砍掉"的东西（商店/装备/遗物/天赋树/天梯/打劫/行动力条）**不要实现**；
- **框架文档**：`RevolutionFramework/Revolution.Document/`（编程范式 / 作用域系统 / 服务定位器 / UI / 资源 …）；
- **本仓库架构说明**：`Assets/GameScripts/README.md`（分层理由与原项目对比）；
- **框架本体**：`Assets/Revolution/`（**只读快照**，改框架请回 Revolution 仓库改 `main` 再重新导出）；
- **不要**引入第三方 UI 框架、DI 容器、异步库；**不要**为了一个面板新造"管理器"。

---

> **给 Codex 的最后一句**：先读第 5 节（约定）和第 7 节（坑），再从第 8 节 **A1** 开始。
> 每步都保持"编译 0 error + 验收通过"再往下走，不要一次改五个文件再一起编译。

## 11. 2026-10-17 用户追加需求的最终回填

本节覆盖前面的历史快照。当前手机方向固定横屏，Unity 编辑器保持打开。当前代码、资源和设置均保留在工作区，未提交或推送。

- 背景使用用户指定“棋盘_开始场景_背景.png”；UI 改用英文新版，结算/奖励为 UI_Pvp_imgBK.png。8个界面/提示预制体、70个Sprite引用，旧版UI引用0。
- 棋盘恢复 81 格独立拼接，格边长 1.15；可在全盘空格落子，准备期点己方棋子再点空格免费重摆，大棋完整占格预检。
- 我方战斗原地守阵，已有一眼/两眼不被自动移动拆开，守眼材料不自动合成、援军不填眼。敌方仅射程不足时每五秒最多靠近一次。
- 普通周期 1.25 秒，小步 0.3125 秒；我方远程默认两倍频率、约 0.625 秒出手，真正分时发射。普通回合 50 秒；Boss 不限时。
- 起手及额度统一六张，普通/Boss/重开/奖励后都满六张。六个槽包含隐藏项逐个重画，每次准备清旧翻牌；刷新只重抽剩余牌，保留星级、不补额度，DOTween侧面换图。
- 准备/开战/结算边界：BoardView推进独立效果批次、取消延迟伤害并回池；HUD清日志和旧提示，拦过期加载，不等待动画自然完成。暂停动画也必须回收，不误取消棋盘格加载。
- UI与相机共用GameBattleLayout安全区计算，牌内文字/肖像随槽位缩放，升级/增益放右侧独立空位。GamePreviewGuard仅在本项目2022.3运行时保留一个Game或Simulator标签，解决不同尺寸双预览叠画，Unity编辑器保持打开。
- 攻击口号、实际伤害、升级 UI 大字和支援提示都有 DOTween 动画。短命效果使用 RevPool，回池 Kill(false)，不全局 KillAll；顶排文字限制在屏幕内。
- 官方 DOTween 免费版 1.3.045、DLL/XML/九个 Modules/LICENSE/SOURCE 已导入。用户授权此第三方表现依赖，框架业务继续走 Rev 门面。
- 新工作簿位于 `RevGame_Client/配置表/战斗弹幕配置.xlsx`。原 Revolution.ExcelTool 导出 2 表 / 22 行 / 4 文件：C# 在 GameScripts/Generated，数据在 GameRes/Data。复导日志四文件未变；Play 实读 21 条弹幕与 1 条调优。不要手改生成物。
- 启动使用 RevTask 与 RevDataTableManager.LoadAsync 加载并校验配置，随后开放首页。Excel 可改标题、说明、颜色、字号、时长、冲击倍率和远程频率，升级/伤害支持占位符。
- 轻松模式明示最后一子守护和45秒真实决胜支援，事件首发3.75秒、间隔5秒、上限12次。保证获胜属于用户指定辅助，不代表数值自然平衡。
- 最新编译通过，570条规则、197条布局、136条Play回合表现回归通过。真实开战结算继续覆盖上轮剩1/0/4张，下轮均满六张；暂停翻牌/升级/弹幕并安排延迟伤害，切回合及两秒后均无残留，日志清空。伤害/弹道/攻击/升级/增益中途回池再复用、四秒无旧回调通过。

导表菜单：`Revolution.Tools/配置表/导表工具`；只改数值/文案用“快速导出（仅数据，不生成代码）”。源地址在个人 UserSettings，换机器重新选择工作簿；共享输出设置在 ProjectSettings/RevExcelToolSettings.asset。

非 Play 验收入口 `RevGame.Editor.GameRuleChecks.Run()`。Play 池化验收 `GameFeelChecks.BeginPoolCheck()` 后至少保持当前流程四秒再看 LastResult。新增 GM：`RevGame/刷新手牌`、`RevGame/点棋盘 X Y`。资源重装配经 MCP 执行，最后调用 `GameFeelBuilder.Apply()`。

正式基础血攻/阵容表、AB、真机、小游戏、长期压力、连续66回合平衡和持久存档仍未验收。完整结论见[最新实现报告](RevGame-实现与框架验证报告-2026-10-17.md)，文件分工见[代码架构说明](RevGame-代码架构说明.md)。

## 12. 2026-10-18 六牌列距与手机右侧留空修复补记

本节补充第 11 节，保留此前六张手牌、横屏与回合清理行为。本轮六牌仍为左侧两列三行，新增 `GameBattleLayout.HandCard` 统一计算卡槽。水平列间距取 `min(左侧栏宽度 × 16%, 安全区高度 × 6%)`，纵向间距仍为安全区高度的 1.2%；卡片尺寸同时受两列总宽、三行总高限制，避免拉宽列距后越过侧栏边界。

右侧留空不是背景图片缺边。Unity MCP 实际量测：Note20Ultra 物理尺寸 3088×1440，非全屏的 `SimulatorInsets=(0,0,0,168)`，游戏渲染尺寸变为 2920×1440。上轮验收中的 `SetResolution(..., false)` 遗留了系统导航栏区域，指定背景图片和现有等比覆盖算法本身正常。

`GameBootstrap.Awake` 已在 Android/Editor 下通过 `UnityEngine.Device.Screen.fullScreen = true` 明确手机全屏。当前已用 `SetResolution(0, 0, FullScreenMode.FullScreenWindow)` 恢复模拟器默认分辨率与全屏。后续验收不得再用非全屏临时分辨率；确需改变尺寸时保持全屏，并在 `finally` 中恢复默认分辨率与全屏状态，包含检查失败或提前退出的路径。不要通过删掉安全区来掩盖导航栏问题：交互仍避开设备安全区，背景仍覆盖完整渲染区域。

本轮 Unity 编译未发现 `error CS`。经 MCP 执行 `GameLayoutChecks.Run()` 返回 **737 条通过**：5 种尺寸与各 3 种安全区，覆盖六牌边界、列间距、1.08 倍放大所需横向空间与重复计算稳定性。Play 实际截图确认 3088×1440、1920×1080 下背景均铺到右边缘；MCP 量测 `SimulatorInsets=(0,0,0,0)`，模式为 `FullScreenWindow`。使用全屏模式切换渲染尺寸后，已恢复设备默认 3088×1440。GM 刷新手牌成功，六槽实际布局及翻牌动画回位已由 MCP 量测通过：六槽均活动，所有牌均已结束翻转、缩放恢复为 1、旋转恢复为初始状态，场景为 1 个相机与 1 个 Canvas。第 11 节的 570 条规则与 136 条回合表现为上一轮历史，本轮未重跑；尚未通过 Android/iOS 真机验收。

## 13. 2026-10-18 战斗可读性与援军修复交接

最新交付见[RevGame-战斗可读性与援军修复-2026-10-18](RevGame-战斗可读性与援军修复-2026-10-18.md)。`HandlePrepareClick`已更名为`HandleBoardClick`，`BattleLogic.PlaySelectedCard`改为`ClickBoard`；BoardView、GM和检查均已同步。准备/战斗均允许玩家主动换位，自动AI的保眼和五秒移动间隔继续保留。

`BoardView.Feedback.cs`集中管理最多6条飘字、6条弹道、同目标0.32秒合并、12目标待播和1.2秒过期；避让计算包含放大与上浮，重叠下层35%透明。切阶段清实例、队列和异步预约，旧回调不准减少新一轮计数。

`BattleService.Reinforcement.cs`负责强远程优先、至少二星、远程安全后排及近战可攻击敌眼。深快照预检不拆己方眼、不堵死同伴。敌方生命基础系数当前为10.8（旧9.6是历史值），用于配合强援保持首关回归15秒以上。

Play新增`GameReadabilityChecks.Begin()`，46条通过；规则增至643条，其余最新证据以本轮报告为准。仅编辑器入口放`Assets/Editor/RevGame`。保持1920×1080横屏、六张手牌，未做真机性能及正式基础数值导表。
