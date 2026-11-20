# RevGame 实践 · 交付、回归和流程证据

Unity 路径相对 Assets。只选择当前任务需要的现有证明。正式通道、输出、编译门禁和玩家进度隔离以目标的 AGENTS.md 为准。

## 本轮先定位的入口

- 仓库 `.github/workflows/revgame-policy.yml` 和 `tools/RevPolicy/check.py` 是规范检查。修改代码或资产先查工作区，准备提交用 `--staged` 核对应索引。框架包、已有项目生成路径、资源 `.meta` 与业务源文件分别维护其来源。
- 仓库 `tools/sync-framework.ps1`、项目的 `Assets/Revolution/PACKAGE_VERSION.txt` 及 `.agents/plugins/marketplace.json` 各自记录框架快照与插件来源。框架从自己的源码发布，RevGame 对照准确发布 SHA 读取正式包，保留项目生成的 ResMap、资源路径和声音路径。业务改动及试测留在实际项目。工具、Skill 和运行库根据真实安装版本共同核查。
- 先发现公开能力及真实环境。编辑态完整刷新、请求重编、等待并核对本次编译 requestId 和 Error；每次域重载后重新发现。无需启 Play 的资产、映射、Prefab 接线做只读验证。当前应用用户有未保存资产或 Play 时先保全状态。

## 场景与资源证据

- `Editor/RevGame/Scenes/GameSceneTransitionChecks.cs` 通过真正进入章节、返回与重入对照资源。使用 `game.scene-handoff.resource-check` 及 `game.scene-handoff.resources` 查看请求 ID、首屏、阶段、临时引用归还、旧域资源和共享资产。测试失败阶段也读取同一个 ID 及独立回退结果。
- `GameScripts/Bootstrap/Diagnostics/GameDomainChecks.cs` 对照共享包、实际域退出、同名重入、在途回调和失败预热归还。冷启动时同时核对 `game.resources.validate` 的项目资源来源和真实 AB 模式；相同包的其他域仍持有引用时，引用计数正常可保留。
- 业务走 `GameScripts/Automation/Input/GamePointerInput.cs`、实际注册的游戏能力或现有编辑器夹具。模式切换后重新查询 UI ID；点击用真实事件。每轮记录准备、接受、后置、失败与清理，不能从单个 OK 当结果。

## 覆盖真实业务和故障

| 目标 | 找实际入口并观察 |
| --- | --- |
| 大厅、章节、教学、剧情和收藏 | `story.state` 与对应进度隔离入口，Gal 或收藏的实际可点节点，目标面板、画面和场景 ready。返大厅后销毁旧会话和引用，复查玩家原档。 |
| 六章战斗和敌潮 | 实际牌、棋子、伤害、计分、结束、重进。固定局次/种子对照正常拒绝和后置。新部署的独立 Player 使用目标设备和标明的验证模式。 |
| 无尽存档与长期状态 | 先经 `endless.enter` 走正式隔离模式加载无尽配置，返回大厅，再用 `endless.save-recovery-check` 验主档/备份/坏档及重入。 `game.scoped-request-check` 验两个同键观察者在退域后结案，晚回包只归还一次。 |
| PvP、LAN、服务器和决斗 | `pvp.state`、`pvp.room.snapshot` 观察当前代次、棋谱、指纹、席位及一致性。房间字段缺失和迟到数据使用实际受控入口。按真本地、两端 LAN、服务器与目标原生 ABI 分列结果。 |
| UI 分层、虚拟行、资源缓存、声音 | 通过真实按钮、列表、弹窗、场景和分辨率核对角色、绑定窗口、输入与释放。视觉证据核对关键帧，声音同时读业务与播放资源。 |

## 性能与目标设备

- 性能计划使用固定版本、场景、种子和负载，分别保留预热、有效采样、GC、渲染量与退场。确认设备、图形 API、资源模式、窗口焦点和画质；无样本或标记为不支持的指标保留缺失。
- 章节切换的分阶段数据走 `game.scene-handoff.performance`。游戏内与 Player 采样看 `GameScripts/Automation/Diagnostics/GamePerformanceMonitor.cs` 和已注册运行时采样。可用 RevMCP 的 `benchmark` 从每轮业务回执取指标并复位，比较时确认声明条件及实测身份。
- [240 敌对比样本](https://github.com/Yokino337088/RevGame/blob/880181648b15851077e6fd3132928ed413ec965d/RevGame_Client/文档/AI自动化测试报告/无尽模式性能提升测试/无尽敌潮插值_性能提升报告_2026-11-19.html) 使用逻辑样本和呈现帧的不同口径。分别报告帧时分布、单帧与每秒数据量、业务终点和必要实拍，早期性能数字不代替目标机证据。
- 控制台单测、同机两端、编辑器真 AB、模拟器和真机受限场景各自解释。真机和产物需要明确 ABI、运行后端、发布平台及数据包版本。

## 交付收尾

使用 [框架自动交付](automatic-delivery.md) 的 `contract --live` 与 `ship --verified` 核对实际注册、同次编译、同平台 AB 和可执行回归。先观察正常用户路径，再核实际后置、拒绝、资源清理和错误日志。只在要求时导出正式报告；临时工单由创建者在消费后清理。资产操作和测试成功均附真实结果，发布以已有工作流与用户授权为准。
