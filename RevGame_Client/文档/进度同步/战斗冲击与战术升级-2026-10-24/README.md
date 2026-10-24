# 战斗冲击与战术升级 · 2026-10-24

[打开图文报告与实拍动图](report.html)。本轮接着已推送的 `75f2733` 视觉升级继续修改，原来的沼泽、神殿和35条弹道保留。

## 玩家看到的变化

- 近战：下载Kenney Particle Pack 1.1的真实透明素材，重剑/剑士青色斩弧、盾战金色盾击环、刺客紫色斩弧；共享Shader随命中进度扫光和扩散，DOTween驱动。近战独立上限16，远程上限35，文字上限20。
- 远程：箭矢加宽加长拖尾；火球保留熔岩核心，加双层旋转法环、符文及烈焰尾迹。水法使用蓝色水纹，圣职使用金色法环。原画和图集绑定均来自真实AB。
- 敌人：普通攻击速度默认×1.45；无眼近战优先追玩家远程，每次直线最多3格且逐格检测占用。增援评分优先减少远程棋群的气，封最后一口气有更高优先级；眼位、阵型和落子预算仍受规则约束。
- 我方远程：至少2枚、互不相连时自动整队，6秒复查；已有四邻相连火力阵保持原位。使用独立深快照规划安全位置，不拆眼、不把原有正气友军变零气、不靠近贴身敌军；零气锁死棋、守眼棋及手动移动冷却中的棋子不搬。空间不足只执行安全的部分。表现为0.65秒弧线跃迁和风暴光效，模型通过BoardModel.Move先提交，动画不参与占格。
- 区域波动：参考用户视频的十字警示、紫色区域和消散节奏，增加深紫流云、周界亮线、升起光点、命中闪光与逐格退场。只动画覆盖层，不移动交互格或改伤害时刻。

## 调参位置

文件：`RevGame_Client/配置表/战斗数值参数.xlsx`，工作表 `BalanceRule`。

| Excel行 | 键 | 当前值 | 范围/作用 |
|---|---|---|---|
| 9 | Battle.EnemyMoveEveryTicks | 1 | 敌方移动冷却变为1个基础Tick，当前1.25秒 |
| 84 | EnemyAI.AttackRate | 1.45 | 0.1～5，敌方及Boss普通攻击速度倍率；技能频率不变 |
| 85 | EnemyAI.ChaseSteps | 3 | 1～4，近战单次直线追击最大格数 |
| 86 | PlayerAI.FormationEnabled | 1 | 0关闭/1开启远程整队 |
| 87 | PlayerAI.FormationMinCount | 2 | 2～20，启动整队的远程数量门槛 |
| 88 | PlayerAI.FormationSeconds | 6 | 1～60秒，整队复查间隔 |

改Excel并保存后，用项目已有Excel导表工具导出此工作簿（数据模式即可）；重新进入Play使配置重新加载。使用AB模式或发布时还要重新构建并复制目标平台AB。本轮已完成真实导表和PC/Android打包，未新增运行中热重载。

## 本次实际验证

| 环节 | 结果 | 证据 |
|---|---|---|
| 新战术与攻速 | 33条断言通过，含真实敌方FSM攻击、血量变化、两次追击、增援封气、障碍/守眼/大棋/手动优先与整队稳定性 | [rules.json](rules.json) |
| 原有规则/战术回放 | 704条业务+877条战术断言通过；固定策略分别赢16/21轮后真实失败，并验证本章重开 | [regression.json](regression.json) |
| PC真实AB冷启动 | 3341条采样断言，首页/六手牌/棋盘可画及3次进出 | [Cold-AB.txt](Cold-AB.txt) |
| 密集反馈 | 2897条采样断言，实际峰值35条弹道、20条文字；回收与换阶段清理通过 | [Stress-Result.txt](Stress-Result.txt) |
| 新画面及生命周期 | 37条Play断言，真实Tick整队、区域GPU紫色覆盖>25%、自然结束及中途离场清理，Console Error为0 | [visual.json](visual.json) |
| PC/Android资源 | 各29个包；6个实际图集、186张图片唯一覆盖；无重复/循环依赖门禁错误 | [pc.json](pc.json) / [android.json](android.json) |

展示夹具先走真实GameFlow和真实AB加载，再固定模型输入。整队由真实Tick触发，区域由真实TriggerRegionPulse触发；近战/远程展示镜头注入已结算的BattleHitInfo，只用于验证渲染、预算和回收，AI伤害由独立服务断言验证。GIF裁切自1920×1080真实截图，采样请求65/70/100/120ms，实际帧间隔受编辑器调度影响；没有补画特效。

## 本轮发现并处理的问题

1. 区域效果原来把Shader内建_Time与跨场景Time.time直接相减，动画句柄存在而画面消失。改成每次租约独立的_Age，近战用_Progress；最终同时验动画和GPU像素。
2. 第一次像素验收的截图API调用时点不在帧末，返回空纹理；改读已落盘帧。阈值依据实际sRGB截图校准，仍要求>25%区域呈深紫，未取消画面断言。失败截图被后续同名采样覆盖，未另造失败截图。
3. 旧战术回归仍断言“87秒单兵必胜/固定摆法66轮必胜”，与现有无限时真实胜负契约及本次加强敌人的要求冲突。更新为有界等待真实结算，胜利继续原进度验证，失败必须真实全灭、留本章首轮重开、清残局和发六牌；未给生产逻辑加入免死或保胜。保留[旧断言失败记录](failed-regression.json)。

## 框架可行性结论与边界

本轮验证了RevHeavyFSM驱动敌人、BattleService提交BoardModel、RevEvent发送事实、RevPool复用反馈对象、RevResManager与唯一Sprite Atlas绑定、DOTween独立表现并随回池/换阶段/离场清理的完整业务链。无需修改Revolution内核；仅更新正常生成的RevResPath和ResMap。

两次追击的证明来自固定无遮挡输入，有障碍、守眼或近身敌人时不能保证两步接敌；整队是有界贪心安全整理，不是最优阵型求解。旧回放没有证明新难度下固定自动策略可通关六章。编辑器两次追击查询9ms、66敌阵生成约5182ms均为单次CPU测量，不是整帧或手机性能；未宣称零GC、GPU合批收益或手机帧率。Android验证到资源构建，没有安装包或真机运行结论。

## 素材与复现入口

Kenney Particle Pack 1.1，CC0：[来源页面](https://kenney.nl/assets/particle-pack)。原包、License.txt、SHA256及使用映射保存在 `RevGame_Client/美术来源/近战特效-2026-10-24/`。剑弧为slash_03、盾环为circle_03，运行时着色不改原形；只把所用两张图纳入CombatFrames图集。用户提供的视频只作参考，未复制入仓库。

复现：连接当前Unity live编辑器后先发现能力；编辑态执行 `game.impact.rules` / `game.impact.regression`；Play下通过 `game.impact.check` 运行真实资源展示与清理。入口位于 `Assets/Editor/RevGame/GameCombatImpactChecks.cs`，服务夹具限定UNITY_EDITOR。完整编排证据保留于本报告JSON。

报告说明：Initial.png采用formation-00整队起始帧，单独initial截图与同帧后续截图请求冲突，未落盘；不把它标为整队前状态。附件和本地引用已检查。浏览器自动预览受file协议策略限制，未完成HTML整页渲染检查；Unity画面已单独查看。最终编译和资源门禁见[diagnostics.json](diagnostics.json)。
