# RevGame · 分场景加载与AB界面修复

2026-10-18 · Unity 2022.3.15f1c1 · 1920×1080横屏

## 本次行为

- `StartScene`：正式启动、大厅、选关。Build Settings第0项。
- `GameScene`：战斗棋盘、棋子和背景。选择章节后，通过Revolution的`RevScene.LoadAsync`进入。
- 过场：全屏绿色迷幻背景，居中加粗文字，DOTween进度缓动；所有资源和画面准备完成后，进度到100%，再用0.35秒淡出。
- 准备阶段的三秒增益计时从过场结束后开始，加载期间不允许棋盘落子或重复切场景。
- 返回大厅真实卸载战斗场景，先回收视图再清池。加载失败显示原因及返回入口，不用假100%掩盖错误。

## 白图问题的定位范围

本次起始冷启动中，旧首页最终能显示正常图片，未复现截图中“长期停留白块”的全部条件。确认存在两个缺口：旧流程会先显示UI再等待异步图集；旧GameScene还保存了验收用池对象，可能在图集监听注册前请求资源。旧图集测试又先加载全部图集，不能证明真正首次开屏可靠。

本次修复将图集预载放到显示之前，给Image增加`AtlasImageBinding`晚绑定刷新，并在揭开过场前检查真实Image纹理与Canvas装配。清理了场景内保存的验收池实例。验收从实际首页开始，不先遍历预载所有图集；最终截图同时检查了可见效果。

## 加载顺序与代码

1. `GameBootstrap`只初始化一次，并保持跨场景常驻；新场景的重复启动器只销毁自己，不注销真正的服务。
2. `GameFlow.Scenes`打开`LoadingPanel`，用`RevScope.Detach`明确它属于全局过场，旧大厅关域不会收掉遮挡。
3. `GameSpriteAtlasService.PreloadAsync`等待当前屏幕所需图集；`SceneResourceWarmup`逐项异步预载预制体，保留并配对释放自己的资源租约。
4. `RevScene.LoadAsync`真正切换Unity场景。框架当前公开门面提供单场景异步切换，没有单独的延迟激活门面；项目通过持续遮挡保证玩家只在完整交付后看到战斗。
5. 新场景载入后，再通过`RevPool`分帧预创建24颗棋子、20个反馈对象和2条事件通知；避免预热实例被RevScene默认清池销掉。
6. FSM交割到战斗域，`BoardView.BuildCellsAsync`每批最多4格或3毫秒主动让帧；81格不再同帧创建，也不再每创建一格就重复刷新整盘。
7. `GamePresentation.WaitUntilReadyAsync`等待背景材质、全部格子、开局棋子、HUD、六手牌，再检查Image纹理、重建Canvas并让出渲染帧。
8. 过场到100%后淡出。`BattleLogic`在遮挡或失败期间冻结真实计时。成功后统一解除门禁。

主要源码位于`Assets/GameScripts/Flow/States`、`Flow/Loading`、`UI/Panels/Loading`、`UI/Effects`、`View/Scene`与`View/Board`。核心类、字段、方法、所有权、晚到回调和关键步骤已补中文注释。编辑器装配与回归在`Assets/Editor/RevGame/Scenes`。

## 资源与打包

新增预制体`Assets/GameRes/UI/Panel/Loading/LoadingPanel.prefab`，单独归入`ui_loading`，只依赖现有字体与程序背景材质，不为打开读条先拉入全部战斗图集。保留已有两个场景的GUID、原背景和横屏布局。

PC/Android的AB通过`GameResourceBuild`调用Revolution工具构建并复制到StreamingAssets；ResMap和RevResPath重新生成。外部AssetBundles目录仍不入库。框架内核没有修改。

本次每平台26包，已逐包核对StreamingAssets与外部构建结果的SHA-256一致。Unity原生字体句柄会让整目录复制遇到文件占用；项目发布工具增加了恢复入口`PublishBuilt`：优先调用框架复制，遇到IO占用时逐文件核对，仅跳过字节完全相同的文件，其余必须真实复制成功，随后清理已过时的生成包。无需关闭Unity。

## 验收

- 真正AB模式：冷启动首页/选关、三次进入与返回、图集像素及材质、唯一启动宿主、域/Logic回收、无活动弹道残留、两种加载失败返回。
- 编辑器直读模式：相同三次往返、六牌、完整遮挡、准备计时、图集显示验证通过。
- 81格实测跨约20～22帧装配，观察到单帧新增格上限4；单个Unity实例化仍不可拆分，不据此承诺所有手机零卡顿。
- 实际AB资源回归：232个资源加载、三种屏幕比例背景铺满、被围/Boss三帧与共享材质五次回池通过。37条Shader、六牌、卡牌动画与弹道回收检查通过。
- 原有136条回合回归通过：连续回合六牌、翻牌/升级/弹幕清理、迟到伤害拦截与日志清空。
- 本次没有Android真机帧时间、GPU与温度数据；Android AB构建成功不等于真机性能达标。

详细日志与截图见同目录`分场景加载验收`。逐帧断言数量随编辑器帧率变化，不把断言总数当作功能数量。

## 后续开发规则

新增战斗预制体须加入对应预载/预热清单；就绪条件必须覆盖新视图。不得恢复“选关立即同步打开战斗”或用固定等待秒数冒充加载完成。既有Play测试已改为等待真实流程完成，不再假设`EnterBattle`返回true后战斗已经可用。

编辑器测试入口：`GameSceneTransitionChecks.Begin()`；故障注入仅开发环境存在，命令`RevGame/过场故障 before-scene`或`after-scene`。玩家流程与GM共享同一条GameFlow入口。

框架验证结论：RevScene、RevTask、RevScope/FSM、RevUI、RevResManager和RevPool能够组合完成本次场景交付。屏幕就绪屏障、资源预热计划与图集晚绑定刷新属于项目适配，没有另造加载框架。
