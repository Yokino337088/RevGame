# RevGame 游戏代码架构与运行说明

2026-10-17 当前实现：Revolution 原型闭环、手机横屏安全区布局、六牌刷新与 DOTween、回合表现清理、升级/攻击/伤害/支援 Excel。框架可行性证据及验证范围见[实现与框架验证报告](RevGame-实现与框架验证报告-2026-10-17.md)。正式基础数值仍待用户提供。

## 先跑起来

打开 `Assets/Scenes/SampleScene.unity`，按 Play。依次点击“开始闯关”→第一章→手牌→任意空格→“准备完毕”。准备期可以点己方棋子再点空格换位，也可以刷新剩余手牌。手机方向固定横屏。章节解锁只在本次运行保留。

GameBootstrap 在 Awake 只初始化一次框架、注册服务、初始化 DOTween；Start 使用 RevTask 异步加载两张生成表，校验后才由 GameFlow 开放首页。不要再手动初始化事件、UI 或计时器。

## 文件分工

| 文件或目录 | 职责及理由 |
|---|---|
| Core/Consts、Events、PieceAttackInfo、BattleHitInfo | 基准周期与小步、事件唯一签名、攻击和扣血当刻的不可变快照。动画晚播放也不反推下一帧数据。 |
| Data/PieceCatalog、LevelCatalog | 基础图鉴和关卡仍为原型数值；不能把弹幕表接入等同于全部数值已导表。 |
| Generated/BattleDataStructures、BattleDataTables | 原 Revolution.ExcelTool 生成的数据结构和表类型，不手工维护。 |
| Pieces/Piece | 生命、位置、身体大小、眼和行动进度；不持有 GameObject。 |
| Board/BoardModel、GoRules | 完整占格、连通块、去重气数、围困和眼；先预检整块身体再提交。 |
| Battle/HandModel、MoveRules | 唯一牌编号与目标查询。刷新后旧编号不能操作新牌；我方远程可跨盘锁定。 |
| Services/BattleService | 唯一规则服务，控制阶段、落子、伤害、胜负与固定模拟。 |
| BattleService.Placement | 准备期棋盘统一点击：选牌、选己方棋子和选眼位石互斥；全盘落子与免费重摆。 |
| BattleService.HandRefresh | 只重抽手中剩余牌，保留星级和落子额度，提交后通知 UI；应用 Excel 远程频率。 |
| BattleService.Progression / Events / Comfort | 奖励、合成、职业加成、支援与轻松模式。partial 是同一个服务分文件，并非三套管理器。 |
| BattleService.*Checks / Diagnostics | 仅编辑器或开发环境的边界断言、受控造局，不进入正式游戏业务。 |
| Flow/GameFlow / BattleLogic | FSM 切换大厅、选关、战斗和退出；Scope 管回收；Logic 每 0.3125 秒驱动服务并转发 UI 动作。 |
| View/BoardView | 编号映射到池化格子/棋子/特效；攻击和受击快照驱动口号与弹道，批次检查避免退出后补播。 |
| View/GameTextConfig | 只查询已加载导表数据，检查必需编号、颜色、字号和动画参数；不直接读磁盘。 |
| View/BattleEffectView | RevPool 复用伤害、口号、弹道与帧图，DOTween 执行表现；文字边界避免手机顶排裁切。 |
| View/BattleEventNoticeView | RevPool 下的短命 UI 零件，显示升级或增益，不是独立业务面板。 |
| UI/BattleHudPanel | 实时落屏，接 PieceMerged/BattleEventTriggered，替换升级占位符，持有并归还活动提示。 |
| UI/PreparePanel / CardItem | 六个槽位（包含隐藏项）复用，每次准备强制复原旧翻牌；牌内肖像和文字响应槽位尺寸，翻牌期间锁定操作。 |
| UI/GameMobileLayout / GameBattleLayout | 归一化 Device.Screen 安全区，共用棋盘/侧栏/顶栏区域；相机和提示使用同一计算结果，缩放时不积累偏移。 |
| View/GamePresentation / BattlePresentation | 流程与阶段到面板的装配，相机适配和异步有效性检查。 |
| View/GameAudio | 业务事件转为 RevSound 逻辑资源路径。 |
| Assets/Editor/RevGame/GameArtMigration、GameFeelBuilder | 经 Unity MCP 保存新版资源引用、提示预制体、拼格和横屏布局；重跑不重复建按钮。 |
| Assets/Editor/RevGame/GameRuleChecks、GameFeelChecks、GameRoundPresentationChecks、GameLayoutChecks | 570条规则、197条布局、136条回合表现回归；另验证池化动画，均调用真实业务/视图。 |
| Assets/Editor/RevGame/GamePreviewGuard | 仅 Unity 2022.3 本项目 Play 态去掉重复 Game/Simulator 预览；不修改框架或打包到手机。 |

## 攻击节奏为什么分两层

普通棋行动周期是 1.25 秒，不能为了让远程加快而把所有棋子都加速。服务累积每颗棋子的 ActionProgress：普通棋攒满一次出手，我方远程默认两倍速度，因此 0.625 秒可出手。

服务和 BattleLogic 都以 0.3125 秒小步推进。只改服务的循环、不改驱动间隔，会让两个小步在同一帧执行，看起来仍然两发挤在一起。敌人移动另外要求每四个普通周期一次，也就是五秒，且必须没有射程内目标。攻击增益不加快移动。

## Excel 配置怎么改

编辑[战斗弹幕配置.xlsx](../配置表/战斗弹幕配置.xlsx)。前三行是字段名、类型、中文注释，不要当成普通数据删除。BattleText 包含九种攻击、两级升级、伤害与支援文案，BattleTuning 控制我方远程频率。

Unity 菜单 `Revolution.Tools/配置表/导表工具`，选择此表并全量导出。只改现有文案和数值可用“快速导出（仅数据，不生成代码）”。生成 C# 到 `Assets/GameScripts/Generated`，数据到 `Assets/GameRes/Data`。共享输出设置在 ProjectSettings，个人源在 UserSettings，换机器重新选表。

运行时只走 `RevDataTableManager.LoadAsync<T>()`、`Get<T>()` 和 `Unload<T>()`。标题、颜色、字号、时长和冲击倍率都从生成数据取；升级用 `{piece}` / `{star}`，伤害用 `{damage}`。缺行会报中文原因，不偷偷采用旧文案。

## 生命周期最容易踩的点

- IBattleService 属于启动入口，保留本次运行的解锁。每局由 BeginCampaign 重置棋盘和强化，不能误以为关战斗域就会销毁全局服务。
- OnBindView 只首次接线，OnOpen 每次重建 owner 事件；Close 会摘监听。Button.onClick 在 OnRelease 手动移除。
- UI 提示使用 Canvas 下的独立容器，避免父物体停用时对子树换父节点。池对象恢复局部 Z=0，不能继承世界物体的深度。
- 每个效果只持有自己的 Tween，回池 `Kill(false)`，不能 Complete 后再触发旧回调，也不能全局 KillAll。
- CardItem 是常驻槽位，刷新只改内容。服务先提交新牌，视图暂存新牌，转到 90 度再渲染；普通 SelectionChanged 不能抢先换正面。
- 异步回调核对进入批次、面板关闭状态和当前阶段。过期加载立即回池，不能把上局提示补到大厅。
- 阶段交接也要显式清理，不能只依赖退出域。BoardView 独立效果批次 + 取消延迟伤害 + 回池；HUD 清提示/日志并拦晚到请求，池保留到退出战斗。每次仅展示一条最新增益/升级提示。
- 大棋落子和换位先检查所有占格；失败保留整块旧身体。自动合成不消耗守眼棋子，援军不填眼。

## 美术与资源约定

指定背景保存在 ArtRes 的棋盘目录。英文新版 UI 位于 `Assets/ArtRes/界面/新版`，结算和奖励统一使用 UI_Pvp_imgBK.png。棋子等原有美术继续使用；旧版 UI 资产保留但不引用。棋盘由 81 个独立格子组成。

面板在 `Assets/GameRes/UI`，池化提示与特效在 `Effects`，导表数据在 `Data`。业务使用逻辑资源路径和分组，不拼 StreamingAssets 或 Application.dataPath。DOTween 官方免费版在 `Assets/Plugins/Demigiant/DOTween`，LICENSE 和来源信息随资产保存。

## 验证与后续

非 Play 经 MCP 执行 `GameRuleChecks.Run()`（570条）及 `GameLayoutChecks.Run()`（197条）。Play 从主界面执行 `GameRoundPresentationChecks.Begin()`，读取 LastResult（136条），会自动清场回主界面；不要同时手动操作。池化检查 `GameFeelChecks.BeginPoolCheck()` 需保持同一流程至少四秒。

真实服务与UI回归已确认：连续回合六张，六槽编号正确；暂停旧翻牌、弹幕、升级/支援后换回合均回池，延迟两秒无重现，日志清空。新版美术引用审计为8个预制体、70个Sprite、旧UI引用0。双预览问题通过仅保留当前预览标签规避，Unity保持打开。

GM 依次 `RevGame/打开选关`、`RevGame/进入战斗 1`，之后可刷新手牌、点棋盘、载入固定夹具或开始战斗。只看工具“调用成功”不够，还要检查业务 Success / Message。

尚未验证 AB、真机、小游戏、长期压力和连续 66 回合平衡。正式血攻、敌方阵容、持久存档及其余玩法扩展仍在后续范围。用户要求的轻松获胜辅助不能作为自然平衡的证据。

## 2026-10-18 补记：手牌布局与全屏状态的职责

`GameBattleLayout.HandCard` 统一计算六个卡槽的位置和尺寸，六牌保持两列三行。水平列间距为 `min(左侧栏宽度 × 16%, 安全区高度 × 6%)`，纵向间距仍为安全区高度的 1.2%。卡片宽高同时受两列总宽与三行总高限制：先为列间、行间留出空隙，再确定可容纳的卡片尺寸，避免只拉大坐标后把牌挤出左侧栏。

手机全屏由启动入口负责，而不是让背景或 UI 布局代码补偿系统导航栏。`GameBootstrap.Awake` 在 Android/Editor 下使用 `UnityEngine.Device.Screen.fullScreen = true` 明确全屏状态。Unity MCP 已量测到非全屏的 Note20Ultra 模拟器存在 `SimulatorInsets=(0,0,0,168)`，物理 3088×1440 对应的游戏渲染尺寸变为 2920×1440；根因是上轮验收 `SetResolution(..., false)` 留下的非全屏状态，指定背景与覆盖算法本身无误。已通过 `SetResolution(0, 0, FullScreenMode.FullScreenWindow)` 恢复默认全屏分辨率。

交互布局仍以安全区为依据，背景继续覆盖完整渲染区域。验收代码临时改分辨率时必须保持全屏，并用 `finally` 恢复默认分辨率和全屏状态；测试退出后也必须还原，不能给后续人工游玩留下导航栏空区。

本轮 Unity 编译未发现 `error CS`；经 MCP 执行 `GameLayoutChecks.Run()` 返回 **737 条通过**，覆盖 5 种尺寸与各 3 种安全区，检查六牌边界、列间距、1.08 倍放大所需横向空间及重复计算稳定性。Play 在 3088×1440、1920×1080 的实际截图中，背景均铺到右边缘；MCP 量测 `SimulatorInsets=(0,0,0,0)`，模式为 `FullScreenWindow`。验收全程使用全屏切换尺寸，之后恢复设备默认 3088×1440。GM 刷新手牌成功，六槽实际布局及翻牌动画回位已由 MCP 量测通过：六槽均活动，所有牌均已结束翻转、缩放恢复为 1、旋转恢复为初始状态，场景为 1 个相机与 1 个 Canvas。前文 570 条规则、136 条回合表现保留为上一轮历史，本轮未重跑；尚未验证真机。
