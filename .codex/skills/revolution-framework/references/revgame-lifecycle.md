# RevGame 实践 · 启动、流程与长期状态

路径相对目标 RevGame 的 Unity Assets。确认当前目录与包版本后追对应阶段，交付按 [框架自动交付契约](automatic-delivery.md)。

## 启动和配置

- 从 `GameScripts/Bootstrap/Startup/GameBootstrap.cs` 找实际组合根。先初始化框架，再装配跨场景的图集、设置、积分和服务。应用设置要在第一次使用前恢复，常驻服务的关闭归同一个宿主。局部阶段使用实际域和 Logic。
- 配置链从仓库 `RevGame_Client/配置表/` 到生成的类型与数据，再到 `GameScripts/Flow/States/GameFlow.Scenes.cs` 的装配入口及 `GameScripts/Data/Config/GameConfig.cs` 的跨表校验。先准备依赖、整体验证再交付快照。表缺失、资源键错、跨表索引非法需要能指出源表与行。需要同时运行 Editor 工具的玩法目标同时核对 `GameScripts/Data/Config/GameConfigFileLoader.cs` 的离线读表入口。
- 编辑器与运行时的数据来源可能不同。观察导出的数据版本、路径映射、真正使用的资源加载模式及是否来自既有缓存。新项目先保留自己的表结构和数值语义，再选择依赖计划、启动顺序、验证入口与归还方式。

## 页面与场景交接

从 `GameScripts/Flow/States/GameFlow.cs`、`GameFlow.Scenes.cs`、`GameFlow.Endless.cs`、`GameFlow.Pvp.cs`、`GameFlow.Gal.cs` 找实际入口及返回。场景、业务状态和 UI 操作性可能在不同帧交付。

| 阶段 | RevGame 的责任安排与可观察事实 |
| --- | --- |
| 玩家意图 | 正式按钮与开发动作走同一个业务入口。允许得到请求 ID，多次点击共享或拒绝已有在途请求。 |
| 画面过场 | `GameScripts/UI/Panels/Loading/LoadingPanel.cs` 先实际落屏，等待工程自己的加载阶段。过场由流程持有，约定跨域时主动脱离旧战斗域。 |
| 准备 | `GameScripts/Flow/Loading/SceneResourceWarmup.cs` 用正式资源路径与真实租约准备必需对象。数据、图集、对象池和可操控棋盘分开读证据。 |
| 激活与业务提交 | 确认目标实际场景，找到场景内的表现宿主。提交业务域后预建本域实例，首屏准备完毕才恢复真实操作。 |
| 结案 | 一个请求记录真实终态、原故障阶段、活动场景和业务状态。淡出可以跨帧，解除门禁和关闭加载面板在同一同步结案段执行，之后才唤醒等待者。成功回大厅后记录旧战斗域的实际资源状态。 |
| 可恢复失败 | 保留过场并提供真正的返回动作。回退有自己的请求身份，不改写旧失败结案。网络房间已经服务端确认离开时按实际权威状态恢复。 |

先迁移最相近的一次进出，再用 [场景真实 AB 验收](revgame-delivery.md#场景与资源证据) 检查候选失败、回退、重入、过场还在和输入真的恢复。新项目按自己的单场景/叠加场景策略决定预备与激活边界。

## UI 和显示设置

- 面板连接数据和输入，真实命令交领域服务。读 `GameScripts/UI/Panels/Pvp/PvpPanel.cs` 对照房间快照与按钮字段，读 `GameScripts/UI/Panels/Pvp/PvpPanel.Navigation.cs` 观察页面交割与遮挡。每轮面板关闭时收走监听、确认、动画和临时租约。
- 把绑到哪些控件、快照字段、操作权、异步是否在途作为四件事。批量列表使用稳定数据键和行绑定版本，旧行回包按旧代次作废。对用户真正可操作的边界，用 EventSystem 走同一按钮或棋盘输入。
- 对照 `GameScripts/UI/Layout/GameMobileLayout.cs`、`GameScripts/Services/Progression/GameDisplaySettings.cs`，分别观察安全区、棋盘相机投影、实际点击与窗口恢复。编辑器模拟器能验布局，真机档位用目标设备覆盖；比例、锚点和平台支持由项目声明。
- 九路围棋、远征、无尽和决斗把棋盘设计框与安全区边缘控件分开。面板在绑定时声明要定位的节点，`GameBattleLayout` 计算偏移，`GameMobileLayout` 按轴重算，教学讲解靠近棋盘和立绘。4:3 平板模拟同一课的状态栏距顶从 203.52 像素降到 11.52 像素，退出键距底从 226.56 像素降到 34.56 像素。项目验收覆盖八组几何组合、实际桌面、平板与宽屏模拟。 [RevGame 适配提交](https://github.com/Yokino337088/RevGame/commit/7b1426de3b18c2f1306eadc53d6f4377613ed70d) · [屏幕分辨率实测](https://github.com/Yokino337088/RevGame/wiki/%E5%B1%8F%E5%B9%95%E5%88%86%E8%BE%A8%E7%8E%87%E5%88%86%E8%AE%BA)

## 存档设置与复位

- 主档、备份与恢复前试盘读 `GameScripts/Services/Endless/EndlessService.Save.cs`、`EndlessRecords.cs` 及隔离校验 `Diagnostics/EndlessService.EcsChecks.cs`。业务快照包括真实逻辑状态。恢复先在独立状态校验，写入失败按现有规则保护可用主档和备份，结算资格由玩法决定。
- `GameScripts/Services/Progression/ScoreProgressStore.cs` 是不同的常驻积分职责；不要把一局结束状态当账户余额。 `GameAudioSettings.cs` 声音即时应用，持久化由独立检查点合并。实际音档、UI显示、写入时机和应用生命周期分开断言。
- 真实 Play 验收使用隔离进度、局次、存档槽及可回收会话。优先选现有隔离入口，失败也尝试复位到真实大厅。修改存档业务时先运行坏主档、可用备份、主备都坏和重入场景。
