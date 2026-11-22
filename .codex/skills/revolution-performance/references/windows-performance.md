# Windows 性能优化

Windows 上先确认目标是玩家运行的游戏包还是 Unity Editor 的 Game 窗口。Editor 便于重现、按帧分析和验证代码，独立 Player 用于验发布条件下的画质、资源和帧节奏。如果目标就是编辑器内开发体验，结论也按那个 Game 窗口测。 [Windows 优化 Wiki](https://github.com/Yokino337088/RevGame/wiki/%E6%94%BB%E5%9D%9A-%E6%80%A7%E8%83%BD%E4%BC%98%E5%8C%96-Windows%E4%BC%98%E5%8C%96)

## 固定显示与运行条件

- 记录 CPU/显卡、实际显卡适配器和驱动、图形 API、Unity 与资源版本、Mono 或 IL2CPP、Development 与 Frame Timing 设置。笔记本要记录供电、性能模式、温度或频率可用性、后台负载。核显可能与 CPU 共用系统内存，系统报告的共享上限和独显专用内存各自注明。
- 记录实际绘制宽高、窗口或全屏、屏幕刷新率、焦点、VSync 和 `Application.targetFrameRate`。在 Unity 2022.3 的 Windows Player 中，VSync 开启后按同步设置运行。默认同步用于玩家体验，隔离的 `vSyncCount=0`、`targetFrameRate=-1` 适合量吞吐，分开比较并恢复配置。 [Unity 目标帧率设置](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/Application-targetFrameRate.html)
- 开始前核对真实 PC AB、正式进关、采样可交互、棋数、攻击量和反馈预算。固定窗口大小、加载状态、种子和持续时间，用同包单开关多轮对照。Game 窗口模式保留编辑器本身的成本。正式 Player 的构建、连接、采样与退出沿 [独立 Player 测试](player-testing.md)。

## 用同一帧找到慢的阶段

| 慢在哪里 | 核对与处理 |
| --- | --- |
| CPU 规则、界面或画面准备 | 分别看规则标记、UI 重建、反馈和提交。先去掉重复工作。渲染线程等待主线程命令时查供给侧，例如 `Gfx.WaitForGfxCommandsFromMainThread`。 |
| 实际 GPU 绘制 | 棋局不变，单独切一项真实分辨率、覆盖或绘制方案，记录画质和有效 GPU 用时。用通用遮挡、局部采样或合批方案复测。 |
| 送屏或同步附近等待 | 看 Present（把画面交给系统显示）、前后台、窗口与刷新策略，用对应帧的 CPU/GPU 计时和系统证据定位。同步开关属于独立对照，核对屏幕实际变化。 |
| 慢帧与局部计数对不上 | 保留 EditorLoop、进程和系统状态。复测相同条件，按时间线查调度、温度、驱动、GC 或窗口呈现。缺证据时把原因留为待定位。 |

Windows 的 CPU 主线程、渲染线程、驱动和 GPU 可能重叠工作。用文档定义解释各计数，按新时间戳对齐有效 GPU 记录。平均 FPS、帧间隔 P95/P99、最大值与超线帧分别报。Editor 诊断和 Player 采样各有结论，性能面板、截图和深度分析放到正式窗口之外。 [Unity FrameTimingManager](https://docs.unity3d.com/2022.3/Documentation/Manual/frame-timing-manager.html) · [Microsoft Windows 显示提交](https://learn.microsoft.com/en-us/windows/win32/direct3ddxgi/dxgi-flip-model) · [编辑器案例](https://github.com/Yokino337088/RevGame/wiki/Windows%E7%BC%96%E8%BE%91%E5%99%A8%E9%87%8C%E6%80%8E%E4%B9%88%E6%9F%A5%E5%8D%A1%E9%A1%BF)

## RevGame 独立包实测

2026-10-31 的 IL2CPP 非开发包使用 Unity 2022.3.15f1c1，开启自动化和帧计时。设备为 i7-12700H / Intel Iris Xe，D3D11、1920 × 1080 窗口、VSync=1、`targetFrameRate=-1`。六章每段先热 5 秒，采样至少 30 秒，已隐藏性能面板。有效的 81 棋组为玩家 36、敌方 45 颗满盘重弩，夹具启用围困免疫和补血。 [IL2CPP 六章原报告](https://github.com/Yokino337088/RevGame/blob/262dcf11944c4519328f706db4a13e211081554a/RevGame_Client/%E6%96%87%E6%A1%A3/%E6%88%98%E6%96%97%E6%80%A7%E8%83%BD%E4%BC%98%E5%8C%96%E8%A7%84%E5%88%99/%E7%9C%9F%E6%9C%BA%E5%AE%9E%E6%B5%8B/IL2CPP%E5%85%AD%E7%AB%A0%E6%88%98%E6%96%97%E6%80%A7%E8%83%BD%E5%8E%8B%E6%B5%8B%E6%8A%A5%E5%91%8A_2026-10-31.html)

| 章 | 平均 FPS | P50 / P95 / P99 帧间隔 ms | 最慢 ms | GPU P95 ms |
| --- | ---: | --- | ---: | ---: |
| 1 | 59.87 | 16.67 / 18.11 / 22.34 | 28.07 | 14.14 |
| 2 | 59.93 | 16.67 / 18.09 / 20.04 | 28.58 | 13.99 |
| 3 | 59.90 | 16.66 / 18.09 / 20.56 | 27.74 | 14.48 |
| 4 | 59.87 | 16.67 / 18.23 / 21.99 | 29.78 | 14.01 |
| 5 | 59.93 | 16.67 / 18.11 / 20.38 | 28.82 | 14.03 |
| 6 | 59.86 | 16.67 / 18.15 / 21.92 | 29.93 | 13.88 |

81 棋组没有超过 33.33 ms 的间隔，帧间隔 P95 仍为 18.09～18.23 ms。另一组标为 48 棋，六段结束都变为 50 棋，固定负载判定失败。第一、五章 173.77/168.99 ms 的峰值作为实际动态负载记录保留。两组共 12 次都回到首页并通过 Logic 和棋子资源引用清理，构建中的资源来自当次已打好的包。正式优化前后、热态与更多 AI 路径各需独立证据。 [正式包怎么看](https://github.com/Yokino337088/RevGame/wiki/Windows%E6%AD%A3%E5%BC%8F%E6%B8%B8%E6%88%8F%E5%8C%85%E6%80%8E%E4%B9%88%E6%B5%8B)

另有同期 Mono 非开发包在同型号硬件、D3D11、1920 × 1080、PC 真 AB 和 VSync=1 下，跑第 4 章 48 棋，热身 2 秒，实采 10.000506 秒、598 帧。平均 59.797 FPS，P50/P95/P99 为 16.647/17.957/24.151 ms，最长 31.020 ms，超过 16.67 ms 为 47.32%。595 个有效 GPU 样本的 P95 为 13.819 ms。详细 GC 分配和部分合批计数缺测。它验证了运行时面板和退出链路。后端净差要另做匹配输入的对照。 [Mono 独立包与面板原报告](https://github.com/Yokino337088/RevGame/blob/262dcf11944c4519328f706db4a13e211081554a/RevGame_Client/%E6%96%87%E6%A1%A3/%E6%88%98%E6%96%97%E6%80%A7%E8%83%BD%E4%BC%98%E5%8C%96%E8%A7%84%E5%88%99/2026%E5%B9%B410%E6%9C%8831%E6%97%A5%E7%8B%AC%E7%AB%8BPlayer%E8%87%AA%E5%8A%A8%E5%8C%96/%E7%8B%AC%E7%AB%8BPlayer%E6%80%A7%E8%83%BD%E9%9D%A2%E6%9D%BF%E4%B8%8E%E8%87%AA%E5%8A%A8%E5%8C%96%E9%AA%8C%E6%94%B6_2026-10-31.html)

## RevGame Editor 的诊断与优化

- 2026-10-29 核显设备的真实 PC AB、D3D11、1920 × 1080 Game 窗口，六章 48 棋的平均 FPS 为 63.71～95.42，P95 为 16.75～28.10 ms。以 80 FPS 的 12.5 ms 预算核对，未达到当时目标。第 4 章各 30 秒完整与临时停背景对照，都打出 1,998 次攻击，GPU 中位 11.50→3.65 ms。该差值用于确认背景有成本，正式方案要恢复完整背景再验。HUD 停绘减少批次，帧率未同步改善。 [CPU、GPU 与内存原诊断](https://github.com/Yokino337088/RevGame/blob/262dcf11944c4519328f706db4a13e211081554a/RevGame_Client/%E6%96%87%E6%A1%A3/%E6%88%98%E6%96%97%E6%80%A7%E8%83%BD%E4%BC%98%E5%8C%96%E8%A7%84%E5%88%99/%E6%9C%AC%E6%9C%BA%E6%88%98%E6%96%97CPU_GPU%E4%B8%8E%E5%86%85%E5%AD%98%E7%93%B6%E9%A2%88%E8%AF%8A%E6%96%AD_2026-10-29.html)
- 2026-10-30 i7-13700 / RTX 4060 Editor、1920 × 1080 真实 PC AB、完整动态背景、81 重弩、临时无同步不限帧的六章样本为 295.35～374.46 FPS，P95 3.666～5.587 ms。原报告有 P99 与慢帧，核显与默认同步条件分别测。核显后续满盘与技能峰值也有实测，区域波动触发调用变轻，整帧峰值与首次炸弹未得到稳定改善。 [独显满盘](https://github.com/Yokino337088/RevGame/blob/262dcf11944c4519328f706db4a13e211081554a/RevGame_Client/%E6%96%87%E6%A1%A3/%E6%88%98%E6%96%97%E6%80%A7%E8%83%BD%E4%BC%98%E5%8C%96%E8%A7%84%E5%88%99/%E5%85%AD%E7%AB%A081%E6%A3%8B%E6%BB%A1%E7%9B%98%E5%BC%B9%E5%B9%95%E7%81%AB%E6%8B%BC%E6%80%A7%E8%83%BD%E5%8E%8B%E6%B5%8B_2026-10-30.html) · [核显与技能峰值](https://github.com/Yokino337088/RevGame/blob/262dcf11944c4519328f706db4a13e211081554a/RevGame_Client/%E6%96%87%E6%A1%A3/%E6%88%98%E6%96%97%E6%80%A7%E8%83%BD%E4%BC%98%E5%8C%96%E8%A7%84%E5%88%99/2026%E5%B9%B410%E6%9C%8830%E6%97%A5%E5%A4%A7%E8%8C%83%E5%9B%B4%E4%BC%98%E5%8C%96%E6%80%BB%E7%BB%93/81%E6%A3%8B%E4%BD%8E%E7%AB%AF%E6%9C%BA%E4%B8%8E%E6%8A%80%E8%83%BD%E5%B3%B0%E5%80%BC%E5%88%86%E5%B8%A7%E4%BC%98%E5%8C%96%E5%AE%9E%E6%B5%8B_2026-10-31.html) · [Windows Editor 续测](https://github.com/Yokino337088/RevGame/blob/262dcf11944c4519328f706db4a13e211081554a/RevGame_Client/%E6%96%87%E6%A1%A3/%E6%88%98%E6%96%97%E6%80%A7%E8%83%BD%E4%BC%98%E5%8C%96%E8%A7%84%E5%88%99/2026%E5%B9%B410%E6%9C%8831%E6%97%A5%E7%BB%AD%E6%B5%8B/%E6%88%98%E6%96%97%E6%80%A7%E8%83%BD%E4%BC%98%E5%8C%96%E7%BB%AD%E6%B5%8B%E6%8A%A5%E5%91%8A_2026-10-31.html)
- 2026-11-19 的敌潮实践用固定 240 敌对比网格提交和插值。同负载既看位移是否平滑，也分别量每方块、每帧和每秒提交字节。第 5 章观测把规则、反馈、CPU 提交、GPU 和 Present 等待按帧对齐。全场景、敌潮各保留版本和失效条件。 [无尽与场景实测索引](revgame-practice.md#2026-11-19-战斗与场景实践)

去掉停绘、短窗诊断和临时同步配置后，用正式画面与真实加载复验。按 [通用 CPU、GPU 与内存](common-performance.md) 一起检查规则、可见效果、反馈延迟和多轮退场。
