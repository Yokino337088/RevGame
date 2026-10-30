# RevGame：真实工程架构与编程参考

## 先查真实示例，再设计业务

使用 Revolution 编写玩法、UI、流程、资源或开发态测试接口时，**必须先在 [RevGame 工程](https://github.com/Yokino337088/RevGame) 中找到并阅读职责相近的实际实现**，再结合目标工程的版本、目录和约束设计。不能只根据框架 Demo 或 API 名称猜实现。

优先读可访问的本地 RevGame checkout；没有本地工程时读取 GitHub。只读命中的最小文件组，不通读工程。记录参考文件、提交版本、采用的契约以及适配点。无法访问时明确“工程实践参考未读取”，用已安装框架的公开 API 和目标工程同类代码继续可验证工作，不假称已参考。

RevGame 是实践样本，框架 API 仍以目标项目安装版本为准。它的玩法、数值、9×9棋盘、六章结构、.NET版本和分包数量不是其它游戏的统一要求。样本中的历史注释、测试夹具、项目专用反射接线与压测强化也不能直接当生产模板。

### 参考版本与入口

- [当前项目说明](https://github.com/Yokino337088/RevGame/blob/main/README.md)：先了解现状，再选择下面的源码。
- [协作约定](https://github.com/Yokino337088/RevGame/blob/main/AGENTS.md)：RevGame自己的目录、性能与自动化约束。
- 本参考核对的业务基线：`ed8e3627963d3eabc21b9ef21806e9445cb1f8fb`（2026-10-28）。固定提交便于复现；参考main的新改动时重新核对路径与契约。
- 下面目录均相对 `RevGame_Client/UnityProject/RevGame.Unity/Assets/`。GitHub代码入口：[固定提交 Assets](https://github.com/Yokino337088/RevGame/tree/ed8e3627963d3eabc21b9ef21806e9445cb1f8fb/RevGame_Client/UnityProject/RevGame.Unity/Assets)。

## 按职责阅读真实代码

| 当前任务 | 必读的最小示例组 | 应提取的契约 |
|---|---|---|
| 启动/服务注册 | `GameScripts/Bootstrap/Startup/GameBootstrap.cs` | 初始化一次、组合根集中注册、谁注册谁注销 |
| 大厅/战斗/场景切换 | `GameScripts/Flow/States/GameFlow.cs`、`GameFlow.Scenes.cs`、`GameScripts/Flow/Logic/BattleLogic.cs` | FSM决定业务阶段，RevScope管理域，RevScene异步交割；accepted不是完成 |
| 原子业务与失败边界 | `GameScripts/Services/Battle/Simulation/BattleService.cs`、`Preparation/BattleService.PieceManagement.cs` | 先完整预检再提交；失败不消费、不部分移除、不改当前流程 |
| 战斗伤害/预判 | `GameScripts/Services/Battle/Support/BattleService.Damage.cs` | 查询与提交共用公式，预判不扣盾/血/积分；实际伤害只结算一次 |
| 重量级AI | `GameScripts/Battle/AI/EnemyAI.cs`、`EnemyStates.cs`、`EnemyTactics.cs` | 宿主提供动作，FSM管理时序，纯算法选目标；深快照不污染实战棋子 |
| 独立支援演员 | `GameScripts/Battle/AI/HelperAI.cs`、`GameScripts/Services/Battle/Support/BattleService.Helper.cs` | 数据身份、胜负资格与表现身份分开；钱包/历史成绩分开；生命周期清理 |
| UI转发与观察 | `GameScripts/UI/Panels/Battle/BattleHudPanel.cs`、`BattleHudPanel.PieceManagement.cs` | 钩子分工、实际按钮到Logic/Service、定频观察、状态门禁与复用清理 |
| 剧情/真实场景教学 | `GameScripts/Flow/Logic/TutorialLogic.cs`、`GameScripts/UI/Panels/Story/StoryView.cs`、`GameScripts/Data/Story/StoryBook.cs` | UI只引导真实业务，打字/音效句柄归属；SO文案是项目需求选择 |
| 顺序演出 | `GameScripts/Flow/States/GameFlow.BossIntro.cs`、`GameFlow.BossVictory.cs` | RevSequence缓存蓝图，等待资源，finally还原时间/镜头/UI，不二次判胜负 |
| 共享图集/资源池 | `GameScripts/View/Resources/GameSpriteAtlasService.cs`、`GamePoolRequests.cs` | 一次租约、Release配对、晚绑定、在途请求、池持有与临时加载引用分开 |
| 动态Shader/序列帧 | `GameScripts/View/Combat/Helper/HelperView.cs`、`HelperAttackView.cs`、`GameShader/Helper/HelperGoldReflect_Shader.shader` | 共享材质、每实例参数块显式当前帧纹理/局部UV、对象池停止旧动画 |
| 配置/资源制作 | `GameScripts/Data/Config/Balance.cs`、`Editor/RevGame/ResourceBuild/GameResourceBuild.cs`、`GameSpriteAtlasBuild.cs` | Excel→工具生成→启动校验；项目入口串联框架工具，不手改生成代码 |

可直接打开的典型实现：

- [GameFlow场景交割](https://github.com/Yokino337088/RevGame/blob/ed8e3627963d3eabc21b9ef21806e9445cb1f8fb/RevGame_Client/UnityProject/RevGame.Unity/Assets/GameScripts/Flow/States/GameFlow.Scenes.cs)
- [唯一伤害出口](https://github.com/Yokino337088/RevGame/blob/ed8e3627963d3eabc21b9ef21806e9445cb1f8fb/RevGame_Client/UnityProject/RevGame.Unity/Assets/GameScripts/Services/Battle/Support/BattleService.Damage.cs)
- [项目资源构建入口](https://github.com/Yokino337088/RevGame/blob/ed8e3627963d3eabc21b9ef21806e9445cb1f8fb/RevGame_Client/UnityProject/RevGame.Unity/Assets/Editor/RevGame/ResourceBuild/GameResourceBuild.cs)

## 从实践提取工程约束

1. **阶段与生命周期有唯一控制者**：流程FSM负责何时交接，域/Logic负责推进与回收，Service负责数据事实。普通面板和视图不启动第二台全局流程机。
2. **意图、规则、事实、表现分开**：UI/GM转发同一玩家入口；服务提交事实；不可变事件快照传Id、位置、实际伤害；延迟弹道不能重新扣血或发积分。
3. **预检是无副作用查询**：多格出生、整队布阵、移除候选与配额先验证，再原子提交。失败前后检查库存、数量、占格版本与阶段，不能只检查返回false。
4. **owner按生命周期拆分**：常驻刷新与阶段短效用不同owner。阶段清理取消短效不应误停持续装配；退出才收走全部。所有Tween、在途加载、回调都要取消/代次检查。
5. **性能优化保留规则更新**：拓扑版本驱动气眼/计数缓存；致死、补子、移动后规则不能漏刷新。高频候选复用调用方缓冲，一次决策用独立深快照。
6. **资源就绪是业务交付条件**：Sprite引用存在不等于图集像素已绑定；加载遮挡必须等实际纹理、帧、材质与交互控件可用。重进战斗与冷AB都需验证。
7. **美术生命周期也是分包边界**：按章节/用途加载材质、Shader、音频与图集，先解决隐式重复/循环。非Sprite采样纹理、引擎字形和UI内置图不能误当Sprite打包。
8. **清晰度先看源图和平台结果**：放大不创造细节；核对源像素、NPOT、mipmap、过滤、平台尺寸与图集压缩。RevGame四帧小助手使用无损独立图集是局部权衡，不要求全项目无损。
9. **中文新手注释说明契约**：类/接口说明职责，字段写所有者和单位/范围，关键方法写失败路径；算法解释为什么预检/快照/去重，异步解释何时回收，避免只逐句翻译。

## 不把样本当未经审查的标准答案

RevGame现有 `BattleService` 的partial、项目侧资源适配器与受控Editor反射，是已有项目演进结果。新业务不应复制一个万能Service或新增第二套框架门面；按目标工程现有目录、程序集边界和服务寿命选最小职责单元。遇到样本缺陷先核对目标版本，在已授权范围修目标实现并留测试，不复制缺陷或把参考项目改动自动扩到框架内核。

编写前输出一个简短映射：**参考文件 → 复用契约 → 目标文件 → 需要验证的后置条件**。编写后走 [AI开发闭环](revgame-ai-workflow.md)，涉及Unity资产操作读 [编辑器实践](revgame-editor-practice.md)。
