# RevGame技术积累与来源

按 [通用 CPU、GPU 与内存实测](common-performance.md#revgame-实测保留对应条件)、[Windows 证据](windows-performance.md#revgame-独立包实测)、[安卓证据](android-performance.md#revgame-已有的安卓证据) 和 [WebGL 证据](#webgl-定制优化实测) 选适用案例。每组数值附测试条件及原报告。

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

## WebGL 定制优化实测

2026-11-21 至 22 的案例以普通浏览器版 RevGame 为主。先读 [WebGL 定制性能优化](webgl-performance.md) 的对应路径，再对照场景、目标机、当前版本和真实业务数据。下表里的配置、阈值和结果属于各自的对照，供定位和复测时选条件。

| 问题 | 做法与当时证据 |
| --- | --- |
| 大厅点开无尽要等下载 | 可交互后按设置、对战、无尽顺序，由正式面板元数据串行准备，并另持有无尽入口使用的 `EndlessMenu` 图集。Edge 同一批 WebGL AB，1366 × 768、DPR 1、模拟 85 ms 与 2.5 MB/s，首页就绪约 7 秒后点真实按钮，每版三轮。可见区间的中位数从 1.68–2.22 到 0.53–0.99 秒，较晚的一端减少约 55.4%。点击后发出的无尽请求从 5 到 0，候选合计提前传约 1.77 MiB。 [大厅与画布实测](https://github.com/Yokino337088/RevGame/wiki/%E8%AE%A9%E7%BD%91%E9%A1%B5%E7%89%88%E7%82%B9%E5%BE%97%E5%BF%AB%E3%80%81%E6%89%93%E5%BE%97%E9%A1%BA) |
| 大厅资源的独立依赖原先串行 | 该版普通浏览器加载器每批最多 3 个独立依赖，全局 HTTP 实际并发上限 4，目标仍等依赖成功。匹配缓存版本，防部分失败、首个等待者取消、旧代迟到。五个无尽相关请求的一对样本，总区间 1.362→1.232 秒，实际最大重叠数 1→2。 [加载器实现提交](https://github.com/Yokino337088/Revolution/commit/959d04f38f9966fc3485988267ca68041f3c6daf) · [开发日志](https://github.com/Yokino337088/RevGame/wiki/%E5%BC%80%E5%8F%91%E6%97%A5%E5%BF%97-2026-11-22) |
| 高 DPR 画了过多像素 | 网页保持全屏视口，缓冲上限 1600 × 900 像素量。1366 × 768、DPR 2 时实物 2732 × 1536→1600 × 899。同 Wasm 的第 4 章 81 棋短窗为 49.9→59.1 FPS，P95/P99 为 43.9/95.4→16.8/17.0 ms，超过 50 ms 从 26 到 2。第 6 章单改尺寸的对照收益很小，另测缩小也仍有慢帧。浏览器正式战斗的显示额度为同屏 14 文字、24 弹道、12 近战短效，每帧最多新增 3 条弹道。 [画布与战斗预算](https://github.com/Yokino337088/RevGame/wiki/%E8%AE%A9%E7%BD%91%E9%A1%B5%E7%89%88%E7%82%B9%E5%BE%97%E5%BF%AB%E3%80%81%E6%89%93%E5%BE%97%E9%A1%BA) |
| 棋盘遮住的动态背景仍计算 | Built-in 前向正交相机用合并九宫格内芯网格，在 `BeforeForwardOpaque` 背景绘制前以 `ColorMask 0` 只写深度。背景 `ZTest LEqual`，生效图集的完全不透明区域才遮挡。第 1 章兼容 UI 仍走原路径。PC 真 AB 同帧第 4/6 章最终 RGB 差异 0，诊断各挡 92,144/123,358 个背景像素。Web 第 6 章四段约 59.9 FPS，单笔背景 GPU 中位数有波动，计算净收益还需纳入深度 Pass。 [棋盘遮挡实测](https://github.com/Yokino337088/RevGame/wiki/%E6%94%BB%E5%9D%9A-%E6%A3%8B%E7%9B%98%E9%81%AE%E6%8C%A1%E8%83%8C%E6%99%AFGPU%E4%BC%98%E5%8C%96%E5%AE%9E%E8%B7%B5) |
| 首枚炸弹等待驱动链接 | 同包约 40 秒各三轮，最慢帧间隔从 1,516.7 到 193.6 ms，平均 FPS 的组内中间值从 57.4 到 60.0。可见的首次链接挪进读条，仍有 53.90 FPS 的慢轮，另一次两分钟为 57.62 FPS。读条按实际材质分批准备弹道、近战、积分飞星、炸弹与帮手技能。 [首次链接复盘](https://github.com/Yokino337088/RevGame/wiki/%E6%94%BB%E5%9D%9A-WebGL%E7%AC%AC4%E7%AB%A081%E6%A3%8B%E9%95%BF%E5%B8%A7%E5%A4%8D%E7%9B%98) |
| 持续慢帧、字形和飘字等待 | 先缩绘制尺寸、停绘、停字做排除。缩小画布让 GPU 样本 P95 约 6.2→4.9 ms，仍有绘制前耗时。35 秒四组试验把飘字每帧从 8 条限到 3 条，最慢处理 6.6→5.6 ms。读条分批准备常用字形，单批最多 16 字符，组合方案三轮满盘约 60 FPS，超过 50 ms 为 0，最老启动等待却约 184→483 ms。字形重建数在部分组合轮次反增。 [81 棋控制变量实测](https://github.com/Yokino337088/RevGame/wiki/%E6%94%BB%E5%9D%9A-WebGL%E7%AC%AC4%E7%AB%A081%E6%A3%8B%E6%8E%A7%E5%88%B6%E5%8F%98%E9%87%8F%E5%8E%8B%E6%B5%8B) |
| 把数字等半秒的情况拉回来 | 常态 3 条或约 1.5 ms，积压 8 条或队头等到 80 ms 时升到最多 8 条、约 3.25 ms。提早停止失去竞争的摆位候选，只优先换掉批前旧字。同包两轮实际提交最长从 466.2–467.2 ms 降到 183.5–184.7 ms，仍接近 60 FPS。超过 25 ms 合计从 26 到 30 次。 [飘字等待实测](https://github.com/Yokino337088/RevGame/wiki/%E6%94%BB%E5%9D%9A-WebGL%E7%AC%AC4%E7%AB%A081%E6%A3%8B%E9%A3%98%E5%AD%97%E7%AD%89%E5%BE%85%E4%BC%98%E5%8C%96) |
| 追查超 25 ms 增多与同批复用 | 固定 3 条两轮仍有 123 次，当前额度两轮有 137 次，其中有一帧完全没新飘字，回调运行 5.3 ms 后等 103.7 ms。当前额度长帧中，68 次启动至少 4 条，其中 15 次仅在算术上减去额外用时才回到 25 ms 内。减少写字号与颜色未得到一致收益。最终复用同一同步批次的避让范围，同工作量的一对里队列用时 P95 3.0→2.8 ms。轻量复测复用三轮为 59.769～59.956 FPS，最老实际提交等待 216.6、182.6、168.1 ms。浏览器空档和慢轮均保留，整体收益按六轮完整对照理解。 [逐帧归因与复用实测](https://github.com/Yokino337088/RevGame/wiki/%E6%94%BB%E5%9D%9A-WebGL%E7%AC%AC4%E7%AB%A081%E6%A3%8B%E9%A3%98%E5%AD%97%E9%95%BF%E5%B8%A7%E5%BD%92%E5%9B%A0) |

RevGame 源码从 `Assets/WebGLTemplates/RevGameFullscreen/index.html` 的画布配置入口、`HomePanel.BrowserWarmup` 的大厅域预热、`SceneResourceWarmup` 的读条预热、`BoardView.BackgroundDepth` 的深度命令和 `BoardView.Feedback`、`BattleEffectView` 的反馈实现查起。项目开发测试包中的 `GamePlayerPerformanceSession` 注册 `game.performance.prepare/state/cleanup`，实际调用前核对隔离会话与入口。第 4 章满盘用真实 81 棋、1600 × 899 和 120 秒验证，攻击、炸弹、退场逻辑数量与 `PieceFrames` 引用均有正式回执。 [同批复用提交](https://github.com/Yokino337088/RevGame/commit/9949fd8054fe92fd35044b3d6d9169727438d62f) · [网页游戏卡顿怎么量](https://github.com/Yokino337088/RevGame/wiki/%E7%BD%91%E9%A1%B5%E6%B8%B8%E6%88%8F%E5%8D%A1%E9%A1%BF%E6%80%8E%E4%B9%88%E9%87%8F)

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
