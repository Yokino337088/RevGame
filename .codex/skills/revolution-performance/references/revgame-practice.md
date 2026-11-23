# RevGame技术积累与来源

按 [通用 CPU、GPU 与内存实测](common-performance.md#revgame-实测保留对应条件)、[Windows 证据](windows-performance.md#revgame-独立包实测)、[安卓证据](android-performance.md#revgame-已有的安卓证据) 和 [WebGL 证据](#webgl-定制优化实测) 选适用案例。每组数值附测试条件及原报告。

规则资料位于 `RevGame_Client/文档/战斗性能优化规则/`，2026-10-30 批次的实现锚点为 `7706f7bf9eaa19f9f2d5910d4dbdd8c70b7b8a5b`。2026-11-19 的分场景、敌潮和慢帧实践见 [战斗与场景实践](#2026-11-19-战斗与场景实践)。本地优先核对当前代码及数据，历史断言只说明对应版本的结果。 [10 月固定资料版本](https://github.com/Yokino337088/RevGame/tree/0e41fcefc866d28e53dde961f1c9a5a246fe080f/RevGame_Client/文档/战斗性能优化规则)

## 性能 Wiki 的学习路线

从 [攻坚 → 性能优化](https://github.com/Yokino337088/RevGame/wiki/%E6%94%BB%E5%9D%9A-%E6%80%A7%E8%83%BD%E4%BC%98%E5%8C%96) 选问题，读对应主题的机制与边界，再回案例查代码和完整轮次。这里记录实现取舍，原试验条件和失败轮在 Wiki。

| Wiki 路线 | 官方技能的可复用做法 |
| --- | --- |
| [压测导论](https://github.com/Yokino337088/RevGame/wiki/%E6%94%BB%E5%9D%9A-%E6%80%A7%E8%83%BD%E4%BC%98%E5%8C%96%E5%AF%BC%E8%AE%BA) · [战斗慢帧与反馈](https://github.com/Yokino337088/RevGame/wiki/%E6%88%98%E6%96%9709-%E6%80%8E%E6%A0%B7%E6%9F%A5%E6%85%A2%E5%B8%A7%E5%92%8C%E8%BF%9F%E5%88%B0%E5%8F%8D%E9%A6%88) | [测量](measurement.md) 用固定工作量、轻量窗口、同帧归因及交互延迟。 [验证](verification.md) 记录异常轮、缺测及实际退出。 |
| [通用 CPU](https://github.com/Yokino337088/RevGame/wiki/CPU-%E5%B0%91%E5%81%9A%E9%87%8D%E5%A4%8D%E8%AE%A1%E7%AE%97) · [棋子外观、技能格与 UI](https://github.com/Yokino337088/RevGame/wiki/%E6%88%98%E6%96%97%E8%A1%A8%E7%8E%B0%E4%B8%8E%E7%95%8C%E9%9D%A2) | [CPU 与缓存](cpu-cache.md) 保留实时输入和业务顺序。 [UI 与反馈](ui-feedback.md) 按实际显示变化提交，覆盖池代次、活动清单、有界请求槽及跨面板复位。 |
| [通用 GPU](https://github.com/Yokino337088/RevGame/wiki/GPU-%E5%B0%91%E7%94%BB%E7%9C%8B%E4%B8%8D%E5%88%B0%E7%9A%84%E4%B8%9C%E8%A5%BF) · [棋盘遮挡](https://github.com/Yokino337088/RevGame/wiki/%E6%94%BB%E5%9D%9A-%E6%A3%8B%E7%9B%98%E9%81%AE%E6%8C%A1%E8%83%8C%E6%99%AFGPU%E4%BC%98%E5%8C%96%E5%AE%9E%E8%B7%B5) | [GPU 与绘制](gpu-rendering.md) 量真实不透明覆盖、透明顺序、动态网格有效范围与提交。 |
| [通用内存](https://github.com/Yokino337088/RevGame/wiki/%E5%86%85%E5%AD%98-%E6%9F%A5%E6%B8%85%E8%B0%81%E5%80%9F%E8%B0%81%E8%BF%98) | [资源与生命周期](resources-lifecycle.md) 对照相同 AB 根和冷暖峰值，追资产与池的持有者，再看退场稳态。 |
| [设备](https://github.com/Yokino337088/RevGame/wiki/%E8%AE%BE%E5%A4%87) · [设备总结](https://github.com/Yokino337088/RevGame/wiki/%E8%AE%BE%E5%A4%87%E6%80%BB%E7%BB%93) | [成员顺序与并行交叉点](cpu-cache.md#成员顺序与并行交叉点)、[顶点插值](frame-presentation.md#将重复显示插值交给顶点着色器)、[动态网格](gpu-rendering.md#动态网格仅提交使用范围与变化流) 和 [按状态提交反馈](ui-feedback.md)。 |
| [Windows 优化](https://github.com/Yokino337088/RevGame/wiki/%E6%94%BB%E5%9D%9A-%E6%80%A7%E8%83%BD%E4%BC%98%E5%8C%96-Windows%E4%BC%98%E5%8C%96) | [Windows 证据](windows-performance.md) 核对实际适配器、帧节奏与显示提交，分开 Editor 与实包。 |
| [安卓优化](https://github.com/Yokino337088/RevGame/wiki/%E6%94%BB%E5%9D%9A-%E6%80%A7%E8%83%BD%E4%BC%98%E5%8C%96-%E5%AE%89%E5%8D%93%E4%BC%98%E5%8C%96) · [安卓 USB 接入](https://github.com/Yokino337088/RevGame/wiki/%E6%89%8B%E6%9C%BA%E6%8F%92%E7%BA%BF%E5%90%8E%E6%80%8E%E4%B9%88%E6%8E%A5%E8%BF%9B%E6%80%A7%E8%83%BD%E5%8E%8B%E6%B5%8B) | [安卓证据](android-performance.md) 用实际包、持续热态、帧率分频、恢复设置与退场内存口径。通过已安装的 `revolution-android-testing` 发现和连接目标设备。 |
| [Web 端优化](https://github.com/Yokino337088/RevGame/wiki/%E6%94%BB%E5%9D%9A-%E6%80%A7%E8%83%BD%E4%BC%98%E5%8C%96-web%E7%AB%AF%E4%BC%98%E5%8C%96) · [WebGL 页内测量](https://github.com/Yokino337088/RevGame/wiki/%E7%BD%91%E9%A1%B5%E5%8D%A1%E9%A1%BF%E6%80%8E%E4%B9%88%E8%87%AA%E5%8A%A8%E6%8B%86%E5%BC%80%E6%B5%8B) | [WebGL 证据](webgl-performance.md) 按首开、首次效果、持续交火和反馈分别量。保留画布档位失败回退与回调内外证据。 |

## 2026-11-23 设备与远征表现的正负样本

| 场景 | 可复用判断与证据边界 |
| --- | --- |
| 成员稳定与变化对并行的影响 | .NET Release、i7-13700、7 轮微基准，240 敌稳定时重排 5.145 µs、缓存串行 0.876 µs、缓存加 2 工作线程 2.237 µs。1,024 敌稳定时串行 7.943 → 双线程 4.975 µs，换员时线程方案较慢且多约 1.5–2.0 KiB/次。按实际规模和变化率选择了缓存串行。 [排序、分路和线程对照](https://github.com/Yokino337088/RevGame/wiki/%E8%AE%BE%E5%A4%87%E6%80%BB%E8%AE%BA) |
| 显卡插值与 Compute 试验 | i7-13700 / RTX 4060、Windows Editor、D3D11、真 PC AB，400 敌 ABBA 四轮，绘图位置等提交约 3.05 → 0.365 MB/s，P95 6.34 → 5.45 ms，终态与随机状态一致。另一个 48 棋沼泽 Compute 实验 GPU 中位 0.401 → 0.451 ms，保留原路径。两者工作量与技术不同，独立包和核显插值收益需另测。 [显卡插值四轮](https://github.com/Yokino337088/RevGame/wiki/%E8%AE%BE%E5%A4%8701-Windows%E6%80%8E%E6%A0%B7%E5%8C%BA%E5%88%86%E6%A0%B8%E6%98%BE%E5%92%8C%E7%8B%AC%E6%98%BE) · [Compute 负结果](https://github.com/Yokino337088/RevGame/wiki/%E8%AE%BE%E5%A4%87%E6%80%BB%E8%AE%BA) |
| 安卓与 Web 调档 | 90 Hz 模拟档位序列 45 → 90 → 30 FPS 并还原，只覆盖策略。Web 软件绘制构建覆盖像素调整与退出还原，历史硬件同 Wasm 对照中第 4 章 P95 改善、第 6 章 P99 反弹。实际温度、功耗和不同设备的长帧都需按设备实测。 [安卓热态策略](https://github.com/Yokino337088/RevGame/wiki/%E8%AE%BE%E5%A4%8702-%E5%AE%89%E5%8D%93%E6%80%8E%E6%A0%B7%E5%85%BC%E9%A1%BE%E5%B8%A7%E7%8E%87%E5%92%8C%E5%8F%91%E7%83%AD) · [网页像素对照](https://github.com/Yokino337088/RevGame/wiki/%E8%AE%BE%E5%A4%8703-%E7%BD%91%E9%A1%B5%E6%80%8E%E6%A0%B7%E7%BB%99%E7%94%BB%E5%B8%83%E5%88%86%E9%85%8D%E5%83%8F%E7%B4%A0) · [调档观察窗口](https://github.com/Yokino337088/RevGame/wiki/%E8%AE%BE%E5%A4%8704-%E8%AE%BE%E5%A4%87%E6%A1%A3%E4%BD%8D%E6%80%8E%E6%A0%B7%E8%B7%9F%E7%9D%80%E6%88%98%E5%86%B5%E5%8F%98%E5%8C%96) |
| 盾牌和剑气流 | 400 敌守眼阵三轮，新版 API 顶点提交量对照旧布局公式减少 99.70–99.71%，炎环补测减少 99.49%。分开保留公式估算、实际 API 字节、P95/P99 和画面回归。 [无尽效果真实提交](https://github.com/Yokino337088/RevGame/wiki/%E8%AE%BE%E5%A4%8705-%E7%9B%BE%E7%89%8C%E5%92%8C%E5%89%91%E6%B0%94%E6%80%8E%E6%A0%B7%E5%B0%91%E5%81%9A%E9%87%8D%E5%A4%8D%E5%B7%A5%E4%BD%9C) |
| 远征格子、弹道与其他表现 | 第 4 章 81 棋，四轮各有 771 次出手和 2,339 次命中。棋格与弹道改动组合 P95 75.7–77.1 → 38.0–57.9 ms。另一个用两轮 45 笔炸弹与 16 笔波动伤害核对三项表现层优化的 Editor 实验，整体 P95 改善，部分轮次最慢帧上升。格面空队列访问 162 → 0，播放中的材质参数提交独立计时。 [格子和弹道四轮](https://github.com/Yokino337088/RevGame/wiki/%E8%AE%BE%E5%A4%8706-%E8%BF%9C%E5%BE%81%E6%A3%8B%E6%A0%BC%E5%92%8C%E5%BC%B9%E9%81%93%E6%80%8E%E6%A0%B7%E6%8C%89%E5%8F%98%E5%8C%96%E5%88%B7%E6%96%B0) · [技能峰值组合验收](https://github.com/Yokino337088/RevGame/wiki/Windows%E7%BC%96%E8%BE%91%E5%99%A8%E9%87%8C%E6%80%8E%E4%B9%88%E6%9F%A5%E5%8D%A1%E9%A1%BF) · [空槽调度与活动成本](https://github.com/Yokino337088/RevGame/wiki/%E6%8A%80%E8%83%BD%E6%A0%BC%E6%B2%A1%E8%AF%B7%E6%B1%82%E5%B0%B1%E8%B7%B3%E8%BF%87) |

无尽另一个盾牌和护盾名单实验里，400 敌炎环两轮把旧路径的两遍筛选按调用次数折成 2,976/1,808 次访问，新路径实际重建访问 40/48 次。固定五演员的 ABBA 微基准支持复用降低该段成本。独立整帧对照中改后 P95 明显升高，局部访问和整局慢帧不能合并解释。 [盾牌和护盾名单与整帧异常](https://github.com/Yokino337088/RevGame/wiki/%E7%9B%BE%E7%89%8C%E5%92%8C%E6%8A%A4%E7%9B%BE%E6%8C%89%E5%90%8D%E5%8D%95%E7%BB%98%E5%88%B6)

## 远征 Boss 战按规则、提交和片元查热点

从 [远征 Boss 战目录](https://github.com/Yokino337088/RevGame/wiki/%E8%BF%9C%E5%BE%81Boss%E6%88%98) 进入三篇案例。只需观察某个阶段时，保持进入该阶段的真实前置条件，同一次 A/B 固定章回、目标、种子、技能落点、有效交火时间、画面与目标平台。预警出现、首次命中、持续更新、最后一跳、取消和退场都有不同的观察点。

| 修改 | 证据与范围 |
| --- | --- |
| 到期门槛、格面位图和持续效果值记录 | 第 6 章第 10 回合的 48 棋，固定种子 `1029`，七组 .NET 10 Release 的局部前后对照。10 万次无效果更新的中位用时 52.71 → 1.43 ms，一块领域和 15 条待结算灼烧为 450.23 → 85.12 ms。列表预热后，1 万次施法清理中位用时 82.43 → 37.32 ms，线程分配约 15.6 MB → 0。首轮异常保留。数字分别对应本机局部耗时和线程分配。 [持续技能控制和区域查询](https://github.com/Yokino337088/RevGame/wiki/Boss%E6%94%BE%E5%87%BA%E6%8C%81%E7%BB%AD%E6%8A%80%E8%83%BD%E5%90%8E%E6%A3%8B%E7%9B%98%E6%80%8E%E4%B9%88%E7%AE%97) |
| 同段格片共享动画与参数准备 | 具有相同纹理、图集 UV、形状、颜色和顺序约束的一组 N 片把进度轨从 N 条合为 1 条，同一进度准备一次参数块，变化时仍按原顺序提交 N 次。格面预警初始提交由 3N 次减为 N 次，实际效果格片由 2N 次减为 N 次。颜色、光束等独立项保留自己的轨道。该计数用于解释减少多少准备工作，画面与提交耗时分别验收。 [预警格片和共享进度](https://github.com/Yokino337088/RevGame/wiki/Boss%E7%9A%84%E5%8D%B1%E9%99%A9%E6%A0%BC%E6%80%8E%E6%A0%B7%E4%B8%80%E8%B5%B7%E4%BA%AE%E8%B5%B7%E6%9D%A5) |
| 透明片元圆外早退 | `BossSkill.shader` 的模式 16 到 23 在半径超过 1.0001 时原预乘输出为透明零，旧公式的 457,920 组采样均支持此分支。模式 24 在半径 1.01 有外圈亮点，保留原实现。数学和公式采样支持分支范围，实际像素、Shader 变体、目标 AB 和 GPU 帧时按采样结果另记。 [透明边角与保留尾光](https://github.com/Yokino337088/RevGame/wiki/Boss%E6%8A%80%E8%83%BD%E7%9A%84%E9%80%8F%E6%98%8E%E8%BE%B9%E8%A7%92%E8%83%BD%E5%B0%91%E7%AE%97%E5%A4%9A%E5%B0%91) |

Windows Editor 使用重新构建的 PC AB，六章 60 招全量第一轮为 59/60。唯一未命中的绝对零域按实际目标条件调整靶位后，单招 19 项断言通过，含命中、音效和退场，共覆盖 60 招。整轮和定向各自保留，且记录零新增 Unity、音频错误。该记录覆盖业务功能和实际 Windows 资产，设备档位、整场帧时、实际 GPU 帧时及像素比较另依目标平台的对照。 [Windows Boss 验收口径](https://github.com/Yokino337088/RevGame/wiki/%E8%BF%9C%E5%BE%81Boss%E6%88%98)

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

页内桥还以真正的 WebGL IL2CPP 包做过指标验收。Intel Iris Xe / Edge / ANGLE D3D11，1600 × 899，第 4 章 81 棋始终有活跃战斗，120.002 秒内新增 10,899 次攻击。P95/P99/最长为 17.00/18.10/53.50 ms，超过 25/50 ms 为 20/1。最长一次实际回调工作 2.6 ms、回调间 50.9 ms，另一次 30.7 ms 里工作占 30.3 ms。两种慢帧分别归因，计数和清理通过，单轮验收用于确认工具口径。 [页内桥真实包记录](https://github.com/Yokino337088/RevGame/wiki/%E7%BD%91%E9%A1%B5%E5%8D%A1%E9%A1%BF%E6%80%8E%E4%B9%88%E8%87%AA%E5%8A%A8%E6%8B%86%E5%BC%80%E6%B5%8B)

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
