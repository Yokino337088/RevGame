# 项目地图：状态所有者与功能目录

用于选实现位置、追踪调用和核对同类写法。下面路径按 2026-11-02 本地代码核对；每次开发仍用 `rg --files` 确认，特别是新增 partial 与目录整理。表格中的 `GameScripts/`、`GameRes/` 等相对 Unity `Assets/`。

## 最小阅读路线

先确定远征还是无尽，再读对应契约与本次命中的实现。不要每个任务都读完整启动链。

| 要修改的内容 | 当前最小源码入口 | 提取的契约与选址 |
|---|---|---|
| 全局服务、启动和最终注销 | [GameBootstrap.cs](../../../../RevGame_Client/UnityProject/RevGame.Unity/Assets/GameScripts/Bootstrap/Startup/GameBootstrap.cs) | Bootstrap 只装配；注册/取用/注销与所有者寿命对应；图集监听先于任何 Sprite 消费者安装 |
| 屏幕、跨场景、加载屏障 | [GameFlow.cs](../../../../RevGame_Client/UnityProject/RevGame.Unity/Assets/GameScripts/Flow/States/GameFlow.cs)、[GameFlow.Scenes.cs](../../../../RevGame_Client/UnityProject/RevGame.Unity/Assets/GameScripts/Flow/States/GameFlow.Scenes.cs) | FSM 状态管理本域；accepted 与完成分开；错误和取消不能进入半就绪屏幕 |
| 六章玩家操作和节拍 | [BattleLogic.cs](../../../../RevGame_Client/UnityProject/RevGame.Unity/Assets/GameScripts/Flow/Logic/BattleLogic.cs)、[IBattleService.cs](../../../../RevGame_Client/UnityProject/RevGame.Unity/Assets/GameScripts/Services/Battle/Contracts/IBattleService.cs) | 交互先过过场/阶段/教学门禁，再由服务改数据 |
| 布阵、手牌、换位、移除 | [BattleService.PieceManagement.cs](../../../../RevGame_Client/UnityProject/RevGame.Unity/Assets/GameScripts/Services/Battle/Preparation/BattleService.PieceManagement.cs)、[BattleService.Placement.cs](../../../../RevGame_Client/UnityProject/RevGame.Unity/Assets/GameScripts/Services/Battle/Preparation/BattleService.Placement.cs) | 多格/配额/库存完整预检，成功才原子提交；放 `Services/Battle/Preparation` |
| 六章伤害与 AI 预判 | [BattleService.Damage.cs](../../../../RevGame_Client/UnityProject/RevGame.Unity/Assets/GameScripts/Services/Battle/Support/BattleService.Damage.cs) | 无副作用计算与唯一提交共用公式；预判不扣盾、不抽随机、不发分 |
| 占格、身份索引、气眼 | [BoardModel.cs](../../../../RevGame_Client/UnityProject/RevGame.Unity/Assets/GameScripts/Board/Model/BoardModel.cs)、[GoRules.cs](../../../../RevGame_Client/UnityProject/RevGame.Unity/Assets/GameScripts/Board/Rules/GoRules.cs) | 格距与占格是模型事实；版本、缓存失效和私有工作区配套修改 |
| Boss/敌人/玩家/帮手 AI | `GameScripts/Battle/AI/{Boss,Enemy,Player,Helper}`、`Services/Battle/AI/{Boss,Enemy,Player,Formation}`、`Services/Battle/Support/Helper` | 演员 FSM 管时序、纯规划选目标、Service 提交动作；跨角色整队放 Formation，共用算法放 Shared |
| 六章 HUD/准备/结算 | [BattleHudPanel.cs](../../../../RevGame_Client/UnityProject/RevGame.Unity/Assets/GameScripts/UI/Panels/Battle/Hud/BattleHudPanel.cs)、`UI/Panels/Battle/{Preparation,Results,Boss}` | 面板钩子分工、实际按钮入口与字段绑定；不把不同功能堆在 Battle 根 |
| 棋盘、棋子、飘字、弹道 | `GameScripts/View/Board/{Cells,Boss,Feedback,Helper,Magic}`、`View/Pieces`、`View/Combat/Effects` | 事实先提交，表现可延迟/限量；回收使旧回调失效；按演员或效果职责细分 |
| 配置、成长、奖励 | [GameConfig.cs](../../../../RevGame_Client/UnityProject/RevGame.Unity/Assets/GameScripts/Data/Config/GameConfig.cs)、[Balance.cs](../../../../RevGame_Client/UnityProject/RevGame.Unity/Assets/GameScripts/Data/Config/Balance.cs)、`Services/Battle/{Progression,Scoring}` | Excel 是调参源；校验和只读配置快照；积分钱包、历史累计与测试隔离分清 |
| 剧情和真实棋盘教学 | [TutorialLogic.cs](../../../../RevGame_Client/UnityProject/RevGame.Unity/Assets/GameScripts/Flow/Logic/TutorialLogic.cs)、`Data/Story`、`UI/Panels/Story` | 引导真实业务，句柄归属、跳过与清理明确；现有 StoryBook 内容资产沿用原机制 |
| 图集绑定、异步池租约 | [GameSpriteAtlasService.cs](../../../../RevGame_Client/UnityProject/RevGame.Unity/Assets/GameScripts/View/Resources/GameSpriteAtlasService.cs)、[GamePoolRequests.cs](../../../../RevGame_Client/UnityProject/RevGame.Unity/Assets/GameScripts/View/Resources/GamePoolRequests.cs) | 晚绑定、在途合并、单次引用成对释放；这些是项目适配，不能再复制池/加载内核 |
| 无尽入口、域和节拍 | [GameFlow.Endless.cs](../../../../RevGame_Client/UnityProject/RevGame.Unity/Assets/GameScripts/Flow/States/GameFlow.Endless.cs)、[EndlessLogic.cs](../../../../RevGame_Client/UnityProject/RevGame.Unity/Assets/GameScripts/Flow/Logic/Endless/EndlessLogic.cs) | 大厅进入专属场景；域驱动固定步；过场门禁；暂停/切后台/保存沿现有入口 |
| 无尽规则、成长、战斗与保存 | [EndlessService.cs](../../../../RevGame_Client/UnityProject/RevGame.Unity/Assets/GameScripts/Services/Endless/EndlessService.cs)、[EndlessState.cs](../../../../RevGame_Client/UnityProject/RevGame.Unity/Assets/GameScripts/Services/Endless/EndlessState.cs)、[EndlessConfig.cs](../../../../RevGame_Client/UnityProject/RevGame.Unity/Assets/GameScripts/Data/Endless/EndlessConfig.cs) | 独立状态、随机状态、计时、配置与存档；按 Combat/Growth/Director/Save 等已有 partial 职责放置，不挂到六章服务 |
| 无尽 HUD、棋盘与 BGM | [EndlessHudPanel.cs](../../../../RevGame_Client/UnityProject/RevGame.Unity/Assets/GameScripts/UI/Panels/Endless/EndlessHudPanel.cs)、[EndlessArena.cs](../../../../RevGame_Client/UnityProject/RevGame.Unity/Assets/GameScripts/View/Endless/EndlessArena.cs)、[EndlessMusicPlaylist.cs](../../../../RevGame_Client/UnityProject/RevGame.Unity/Assets/GameScripts/View/Endless/EndlessMusicPlaylist.cs) | HUD 沿当前玩家 Service 入口；盘内复用 PieceView、外围网格表现；伤害回执消费不重算；长曲独立资源寿命 |
| 受控 GM 与自动化 | [GameGmCommands.cs](../../../../RevGame_Client/UnityProject/RevGame.Unity/Assets/GameScripts/Bootstrap/Diagnostics/GameGmCommands.cs)、[EndlessAutomation.cs](../../../../RevGame_Client/UnityProject/RevGame.Unity/Assets/GameScripts/Bootstrap/Diagnostics/Endless/EndlessAutomation.cs) | 注册仅开发环境，owner 对称注销；完整 GM 名来自实时注册；无尽测试沿 isolated 局与专用保存 |
| 制作资产和构建门禁 | [GameResourceBuild.cs](../../../../RevGame_Client/UnityProject/RevGame.Unity/Assets/Editor/RevGame/ResourceBuild/GameResourceBuild.cs)、[GameSpriteAtlasBuild.cs](../../../../RevGame_Client/UnityProject/RevGame.Unity/Assets/Editor/RevGame/ResourceBuild/GameSpriteAtlasBuild.cs)、[GameEndlessBuild.cs](../../../../RevGame_Client/UnityProject/RevGame.Unity/Assets/Editor/RevGame/Endless/GameEndlessBuild.cs) | 项目固定配方串联框架工具；Editor 资产处理不进入运行期规则 |

## 新文件放哪里

| 层/目录 | 保存什么 | 选择规则 |
|---|---|---|
| `GameScripts/Core/{Board,Configuration,Events,Flow}` | 共用坐标、枚举、事件载荷、域常量 | 这是项目业务 Core，可以改；与 `Assets/Revolution/**/Core` 的快照边界区分 |
| `Board/{Model,Rules}`、`Pieces`、`Battle/{Rules,Hand}` | 数据结构与纯规则 | 不需 Unity 的算法继续纯 C#，不依赖 UI/View；先找实际已有子目录 |
| `Battle/AI/{Boss,Enemy,Helper,Player,Formation,Shared}` | 演员、FSM、规划算法 | 角色代码进对应角色；跨角色复用才进 Shared/Formation |
| `Services/Battle/{Contracts,Simulation,Preparation,Progression,Scoring,Support,AI,Diagnostics}` | 六章业务接口、提交与诊断 | `AI` 按角色；`Support/Helper` 放帮手业务；`Diagnostics` 按 Boss/Helper/Performance/Balance/Scoring/Preparation/Rules/Combat/General 等用途 |
| `Services/Endless` 与 `Diagnostics` | 无尽状态与既有业务 partial、规则检查 | 延续已有职责文件；多个独立功能形成目录时按功能细分，不因新功能搬走整个模块 |
| `Flow/{States,Logic,Loading}` | 阶段、域、节拍和资源准备 | Boss 演出放 `States/Boss`，无尽 Logic 放 `Logic/Endless`；服务不依赖表现 |
| `UI/Panels/{Lobby,Battle,Endless,Story,Loading,Diagnostics}`、`UI/{Items,Layout,Effects}` | 面板、可复用控件与布局 | 战斗细分 Hud/Boss/Preparation/Results；面板和 Item 不混；设置页按已有归属定位，保持现有命名空间 |
| `View/{Scene,Board,Pieces,Combat,Resources,Endless}` | 场景装配、渲染、效果、资源适配 | 棋盘细分 Cells/Boss/Feedback/Helper/Magic；Boss 特效/棋子外观进已有 Boss 子目录 |
| `Data/{Config,Endless,Pieces,Progression,Buffs,Story}` | 数据读取/校验和手工内容定义 | 强类型表在 Generated；不要在业务代码补任意默认值掩盖缺配置 |
| `GameScripts/Generated`、`GameRes/Data` | 工具生成 C# 与导表数据 | 从源表/模板重新生成；不擅自挪目录、不手改生成结果充当永久修复 |
| `Assets/Editor/RevGame/{Scenes,ResourceBuild,Endless,Story,Settings,...}` | 编辑器制作/构建/验证代码 | 优先已有功能目录；不放 `GameScripts/Editor`；尊重现有 asmdef 引用边界 |
| `GameRes/UI/{Panel,Item}`、`GameRes/{Board,Pieces,Effects,Endless,Art,Atlases,Audio,Material,Config}` | 正式 Prefab 与资源 | 分用途与寿命；美术原始库在 `RevGame_Client/美术`，入场资源通过项目配置接入 |
| `Assets/GameShader/{对应功能}` | 项目 Shader | 对应材质归现有 GameRes 功能目录；无尽沿 `GameRes/Endless` 配方，不全仓重排材质 |
| `RevGame_Client/文档/{架构,进度同步,战斗性能优化规则,...}` | 维护文档与阅读版报告 | 原始证据放 `.codex/work`，不把机器日志塞入阅读文档 |

路径与类型不是一回事。移动 partial 时保留类名/命名空间与原 `.meta` GUID；新增 Unity 资产和文件夹交由正常导入生成对应 `.meta`。技能文件不属于 Unity 资产，不加 `.meta`。目录调整只做本次所需范围，历史报告保留历史路径。

## 架构判断中容易踩的坑

- `RevServices.Register<TService,TImpl>` 的第一参数是取用键，第二参数是实现；接口或类做键都可。替换/隔离确有收益才抽接口，不造空接口或第二套容器。注册可懒创建，必须早装配的图集监听/音量服务在组合根立即取一次。
- 服务“实例寿命”与“本局数据寿命”不同。`IBattleService` 和 `EndlessService` 在 Bootstrap 注册，域 Logic 负责停本局；不能仅凭旧注释断言域退出就自动注销所有服务。
- `RevScope.SetTickEnabled` 不停止 Timer/Event。六章和无尽的实际节拍均需检查过场/暂停门禁；不要把暂停域当成暂停所有时钟，也不新增全局 `Time.timeScale` 补丁误伤其它模式。
- 六章规则尽量保持纯 C#；当前 `Services/Endless` 部分使用 Unity 的数学类型。不要声称整个 Services 层都无 Unity 依赖，更不要为模仿历史图解扩大为全层迁移。
- `BoardModel` 的身体格、身份索引、占格/目标版本与快照一起维护。预测不能污染实盘，视图世界坐标不能反过来作为模型真源。
- 远征与无尽可以共用棋子原画、走法、几何和表现，仍各自管理胜负、成长、资源和存档。无尽计时/三合一/棋心/外围杂兵不作为新的远征规则默认传播。

## 文档入口与版本边界

- [项目架构与框架讲解](../../../../RevGame_Client/文档/架构/RevGame项目架构与Revolution框架讲解.html)：理解分层与装配；文件统计与早期能力清单是历史版本。
- [代码阅读指南](../../../../RevGame_Client/文档/架构/RevGame代码阅读指南-新手版.html)：按业务链路定位；用真实路径复查 2026-10-30 整理后的文件。
- [功能目录细分记录](../../../../RevGame_Client/文档/进度同步/业务脚本功能目录细分_2026-10-30.md)：角色/功能目录的依据和旧新路径映射。
- 六章查 `玩法拓展策划案.html`，无尽查 `无尽模式策划案_棋阵幸存者_2026-11-01.html`；设计建议与已提交/未提交实现逐项区分。
- 当前配置源在 `RevGame_Client/配置表`，包含棋子数值、关卡与奖励、增益与事件、战斗弹幕、战斗数值参数、无尽模式等工作簿。按字段查实际源表，不把这份枚举当封闭清单。

本地图核对时的仓库 HEAD 为 `39b02433fd36052e755835ed7f1373bcb6ad888a`，工作区另有无尽迭代改动。用表内稳定职责定位；本地文件可读不代表已提交，更不代表已运行验收。
