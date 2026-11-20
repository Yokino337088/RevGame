# RevGame技术积累与来源

规则资料位于 `RevGame_Client/文档/战斗性能优化规则/`，2026-10-30 批次的实现锚点为 `7706f7bf9eaa19f9f2d5910d4dbdd8c70b7b8a5b`。2026-11-19 的分场景、敌潮和慢帧实践见下节。本地优先核对当前代码及数据，历史断言只说明对应版本的结果。 [10 月固定资料版本](https://github.com/Yokino337088/RevGame/tree/0e41fcefc866d28e53dde961f1c9a5a246fe080f/RevGame_Client/文档/战斗性能优化规则)

## 2026-11-19 战斗与场景实践

| 实践 | 可复用的决策与证据 |
| --- | --- |
| 无尽网格提交 `50c67cfd` | 用有界缓冲提交实际活动方块的连续顶点和索引，无对象时收起索引。RTX 4060 Editor 的 240 敌压力段整帧三角形均值下降 61.1%，整帧 P95 6.451 → 5.551 ms。该测量对应 10 秒压力窗口，关注实际绘制数量与工作量。 |
| 敌潮采样与插值 `30551529` | 读取局次、版本和槽代次采样，位置与颜色/UV 分流。Iris Xe Editor 的 240 敌三轮对照中，用实际 Mesh API 载荷算得 144.00 → 84.14 字节/绘制方块，12.880 → 8.869 KiB/帧。帧数增加后是 0.243 → 0.258 MiB/秒。单敌独立连拍的位移 P95 0.06300 → 0.01050 格。 |
| 六章与第五章 `50c67cfd` | 81 棋六章 P95 有升有降。第五章同进程配对测得相机布置每帧 CPU 17.55 → 13.33 µs。用同帧 CPU 规则、反馈、提交、GPU 与 Present 等待定位慢帧，保留各章负结果。 |
| 鸦鸦图集 `af7b4e8b` | 按 Gal、对战、运行时反应等使用范围拆分表情图集与预载范围。显式检查生产 Sprite 归属与 AB 依赖，用实际目标平台的包体、图集引用、画质和首屏验收。 |

敌潮报告的每版三轮使用种子 1101、0 号阵型、同 PC AB、1920×1080 Ultra，统一从逻辑 2.6 秒采到 12.5 秒。业务终点和三类战斗回执一致，12 项边界断言覆盖插值、掉血、跳位、槽复用、暂停和清空网格。[全场景实测](https://github.com/Yokino337088/RevGame/blob/50c67cfd/RevGame_Client/文档/AI自动化测试报告/全场景性能优化/全场景性能优化实测_2026-11-19.html) · [敌潮性能与连拍](https://github.com/Yokino337088/RevGame/blob/88018164/RevGame_Client/文档/AI自动化测试报告/无尽模式性能提升测试/无尽敌潮插值_性能提升报告_2026-11-19.html) · [敌潮回归](https://github.com/Yokino337088/RevGame/blob/88018164/RevGame_Client/文档/AI自动化测试报告/无尽模式性能提升测试/无尽敌潮插值_自动化测试报告_2026-11-19.html) · [表情资源提交](https://github.com/Yokino337088/RevGame/commit/af7b4e8b)

源文件按 `Assets/GameScripts` 定位。逻辑读 `Flow/Logic/BattleLogic.cs`、`Flow/Logic/Endless/EndlessLogic.cs` 与 `Services/Endless/Ecs/EndlessHordeComponents.cs`。显示读 `View/Endless/EndlessHordePresentation.cs`、`EndlessArena*.cs`、`EndlessQuadBatch.cs` 和 `View/Pvp/PvpDuelView.cs`。只挑和任务同职责的代码，使用目标项目真实的步长、场景和性能门槛。 [当前敌潮实现锚点](https://github.com/Yokino337088/RevGame/commit/305515292faf515728746e7dbc868a967abfc24a)

## 2026-10-30 覆盖来源

盘点51文件：22份文本（md/html/txt/json）、27媒体、2历史ZIP。md/html配对叙述，图/GIF辅助证明画面，ZIP仅历史采样索引，未解包Profiler录制。技能不复制原始大数据/图或凭据。

| 来源（相对该目录） | 提炼内容 | 边界 |
|---|---|---|
| 战斗性能优化规则.md/.html | 唯一V2/寿命图集、共享材质/UV、规则固定步/独立草稿、反馈预算/池、域/分场景加载与中文注释 | 原10-18基线与10-25补充非现机器统一性能 |
| 六章战斗性能压测报告_2026-10-25.md/.html | 统一伤害预测/唯一提交、GM集中注册、冷池临时租约、真正卸载、子Canvas负结果与计时污染 | Iris Xe Editor最终60.9～99.2FPS，P95 17.91～30.11ms；80～90未达 |
| 本机战斗CPU_GPU与内存瓶颈诊断_2026-10-29.html | GPU/UI/反馈分责，有效GC、状态波动、未完成90秒撤回 | 笔记本63.7～95.4FPS/P95 16.75～28.10ms，停背景仅诊断 |
| 战斗性能热点与缓存优化_2026-10-30.md/.html | Id/射界版本、深试盘/全字段、队列八槽、委托/字形/矩形/HUD、Shader数据场与唯一预分配测量 | RTX4060开发机，完整负/异常保留，不替Iris Xe |
| 战斗棋子数量与ECS架构审查_2026-10-30.md/.html | 数据/表现时钟分开，先热点再并行架构 | 无ECS隔离对照，不设置固定迁移数量 |
| 2026年10月30日大范围优化总结/2026年10月30日战斗性能大范围优化总结与新手代码导读.md/.html | 35文件导读、失效/寿命/270处定点解释与Python算法、收益百分比区分 | 注释有效词元不变，无执行提速 |
| 验收记录/渲染基线.json；纹理内存对比.txt；AB二进制检查.txt | 固定110 Sprite批次24→13、贴图13→5；同根AB纹理172→8，9,009,968→7,219,172B；无源Texture副本 | 固定画面/根集，不是整场/进程总 |
| 验收记录/图集Play验收.txt；资源Play验收.txt；最终AB构建.txt | 9图集191Sprite/3重入，231加载，PC/Android25包旧残留0 | 历史布局不当今天59包527映射 |
| 验收记录/规则与布局验收.txt；密集反馈验收.txt；回合清理验收.txt；Shader与动画验收.txt | 649规则/13,418随机/737布局；避让透明/材质UV/短长轨迹/回合清理 | 历史弹道峰值14不证明后来35实飞 |
| 验收记录/HTML验收.json；规范正文/文档预览1440/390.png | 窄屏/交互/减少动画证据 | 后来新导读仅结构链接脚本检查，不当浏览器视觉QA |
| 战斗性能压测报告/画面/；本机战斗性能诊断_2026-10-29_素材/；验收记录/性能热点与缓存_2026-10-30/ | 六章/JPEG/实际交战齐射与背景动态 | 图证明内容，不能证明伤害/耗时/无泄漏 |
| 战斗性能压测报告/证据/两个2026-10-25.zip | 旧采样/Profiler来源索引 | 不当新报告规范，当前禁止新ZIP/录制导出 |

## 从成功与失败得到的决策

1. 备用UI混常用Atlas曾9.84MB高于原9.01MB，按寿命拆到同根7.22MB才省19.9%。不是“打图集/用2048”就成功。
2. 10-18固定批次24→13/SetPass17→10；10-25密集新内容约64DC不是优化回退，比较必须同画质/数量。20/35/16及尾迹描边子层仍计成本。
3. 子Canvas A/B无可靠净收益已撤；UI脏写/整行Mask可测，但拆Canvas不是默认答案。
4. 临时加载引用finally还、池/场景释放后空闲FlushUnused，10-25大厅包24→14；公共字体/图集/菜单合理保留，不为0强卸。
5. 沼泽停绘GPU11.50→3.65ms是归因，不是省了68%；真方案保全屏，局部范围跳算/静态场查表有近似与新增纹理代价。
6. 射界缓存几何但HP实时评分；一度额外过滤隐藏/死棋造成一局差异，恢复后180回合逐字段一致，胜场一样不够。
7. 同48棋4800射界13.302→0.309ms（耗时−97.68%），100深快照含气眼4.643→3.575ms（−23.00%，另一轮25.93%）；是局部不是整場FPS。
8. 固定六章18段射界48,906/0、文字12,951/61（99.53%）但试盘0；动态十分钟103,948/202（99.806%）、96试盘/4,913副本才有试算证据。
9. 十分钟600.003秒120,105帧/58,070攻击/48～52棋，RTX4060平均200.174/P95 7.444/P99 20.082/max286.558ms，仍慢帧；十次重入错误0/逻辑0/棋子图集引用0不等长期零泄漏。
10. 旧文件误列新更长作业已撤回；唯一runId/实际时长/全部段/清理联验。稳定窗口CSV/截图改预分配值数组+窗口外汇总。
11. ScorePreview嵌套租约try外失败，改复用已有保护只还自己的。默认攻击套件前代码同样失败一条，不降预期或宣称全套通过。
12. 新手注释讲所有者/失效比翻译赋值更重要；35源码词法等价、31,947回归/Unity编译；项目字体清单收注释输入也同步，不能忽略构建门禁。

## 只读同类源码组

以下相对 `RevGame_Client/UnityProject/RevGame.Unity/Assets/`，[固定源码根](https://github.com/Yokino337088/RevGame/tree/0e41fcefc866d28e53dde961f1c9a5a246fe080f/RevGame_Client/UnityProject/RevGame.Unity/Assets)。读当前同类组/核安装签名，不通读内核。

| 主题 | 源码 | 契约 |
|---|---|---|
| 查询副本 | GameScripts/Board/Model/BoardModel.cs、BoardModel.Queries.cs；Pieces/Model/Piece.cs；Battle/Rules/MoveRules.cs | 索引/版本全入口、原算法、有序结果、全状态副本 |
| AI预测 | Battle/AI/EnemyTactics.cs、RangedFormationPlanner.cs、EnemyFormationPlanner.cs、BossTactics.cs；Services/Battle/AI/BattleService.PlayerAI.cs；Support/BattleService.Damage.cs | 顺序、草稿、预测无副作用/唯一提交、实时评分 |
| UI/反馈 | UI/Panels/Battle/BattleHudPanel*.cs；View/Board/BoardView*.cs；Combat/Effects/BattleEffectView.cs；Text/GameTextConfig.cs；Pieces/PieceView.cs | 有界公平、字体矩形失效、脏写/稳定委托/回收 |
| 资源 | View/Resources/GamePoolRequests.cs、GameSpriteAtlasService.cs；Scene/GamePresentation.cs；Editor/RevGame/ResourceBuild/GameSpriteAtlasBuild.cs、GameResourceBuild.cs | 临时池租约、冷交付、V2依赖、UV |
| GPU | GameShader/Background/Chapter04_Swamp_Shader.shader、Chapter05_Ice_Shader.shader；Editor/RevGame/ResourceBuild/GameBackgroundFieldBuild.cs | 局部数学/静态查表/编码量化/章寿命 |
| 验收 | Editor/RevGame/GameBattleMeasuredChecks.cs、GameReadabilityChecks.cs；ResourceBuild/GamePerformanceAutomation.cs | 窗口预分配、唯一结果、正式收尾、真实业务 |

工程外 `RevGame_Client/Tests/CombatAttackRegression/QueryCacheRegression.cs` 链真实表/规则；`tools/performance-fields.py`只是开发图生成。音频/章技能状态是泛化寿命原则，目录不含它们独立专项量化证据，实施仍核当前Boss/Helper资源源码。
