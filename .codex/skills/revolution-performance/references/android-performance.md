# 安卓性能优化

安卓的设备性能会随持续负载、温度、电源和屏幕刷新状态变化。按型号和实际图形路径建立基线。**冷启动、首次交火、热态持续玩和反复退场是各自的观察窗口。** [安卓优化 Wiki](https://github.com/Yokino337088/RevGame/wiki/%E6%94%BB%E5%9D%9A-%E6%80%A7%E8%83%BD%E4%BC%98%E5%8C%96-%E5%AE%89%E5%8D%93%E4%BC%98%E5%8C%96) · [Android 热状态与性能](https://developer.android.com/games/optimize/adpf)

## 开测前先验实际 APK

- 核对安装包和同一版本的 Android AB、目标 ABI（处理器接口格式）、实际图形 API、原生插件、章背景、启动场景和签名版本。走真实玩法，核对能启动、画面完整、触屏可点、声音正常、换章与离场可完成。异常先按构建、资源、宿主或玩法逐层定位。 [开测前先验包](https://github.com/Yokino337088/RevGame/wiki/%E5%AE%89%E5%8D%93%E5%BC%80%E6%B5%8B%E5%89%8D%E5%85%88%E9%AA%8C%E5%8C%85)
- 目标切到 Android 的 Windows Editor、Device Simulator（编辑器设备预览）和真机各自出证据。复用现有构建与验包流程，在实际 APK 上验证设备的图形与触屏。区分诊断包和拟发布包，记录 Unity、脚本后端、Development、图形接口和采样能力。 [Unity Android 发布与验证](../../revolution-framework/references/android-runtime-and-layout.md)

## 怎样量从冷到热

1. 同一设备记录型号、系统、CPU/GPU、图形 API、分辨率与实际绘制尺寸、刷新率和帧率上限。固定画质、亮度、充电和前后台状态，记温度或系统热状态。当目标机支持 Thermal API 或性能分析时，将热状态变化和慢帧对齐，标明缺测项。 [Android Thermal API](https://developer.android.com/games/optimize/adpf/thermal)
2. 用同一关卡、种子、棋子和攻击负载，分开记冷启动就绪、首个技能与长时间战斗。比较相同热状态或明确的冷段和后段。保留实际时长、FPS、帧间隔 P95/P99、最大值、超线次数、输入与反馈等待。可用时保持 GPU 和 CPU 计数与实际帧时对齐。没有设备且只完成编辑器预览时，标明证据层级。
3. 先重复原样确认波动，再只切一项画质、有效像素、图形 API 或帧率目标。比较同机同条件的多轮结果，必要时等设备回到相近的起始热状态。打开性能分析器的归因组与轻量验收组分别记录。 [安卓帧率与发热](https://github.com/Yokino337088/RevGame/wiki/%E5%AE%89%E5%8D%93%E5%B8%A7%E7%8E%87%E4%B8%8E%E5%8F%91%E7%83%AD)

## 按测到的瓶颈取舍

| 现象 | 做法 |
| --- | --- |
| 温度上升后全段慢下来 | 优先选择热态还能稳定输出的目标帧率和画质。比较从冷到热的帧间隔与温度，核对升降档过程、反馈和画面。 |
| 平均 FPS 接近目标，画面却一顿一顿 | 查刷新率是否变化、出帧是否均匀，以及游戏目标档与实际屏幕周期是否匹配。在 Unity 2022.3 的 Android Player 用 `Application.targetFrameRate` 管理目标帧率，按可用条件单独对照优化帧节奏设置。 |
| 明确是像素或透明叠层的 GPU 成本 | 分别试实际像素预算、过绘与无用采样。固定视觉要求，保留棋子、血条、危险预警和触屏坐标。纹理压缩按设备和图形 API 支持选择，按真实 AB 验画质、内存和回退。 |
| CPU 规则、界面或装配越忙越慢 | 局部标记确认重复计算、脏更新或一次装配太多。复用通用缓存和分帧方法，保留规则顺序与反馈等待预算。 |

有证据后再决定 Vulkan 或 OpenGL ES、帧率或画质档位，按设备自己的差异验收。Android 应用中的帧率设定与桌面 VSync 是两条配置路径。 [Unity 目标帧率设置](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/Application-targetFrameRate.html) · [Unity 安卓优化帧节奏](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/PlayerSettings.Android-optimizedFramePacing.html) · [Android 帧节奏说明](https://developer.android.com/games/sdk/frame-pacing)

## 退场与系统内存

- 记大厅起点、相同战斗的峰值和每次退场后固定等待点。重复完整进出后比较稳态。用 Unity 计数查资源所有权，并用设备可用的进程计数补充，例如 `adb shell dumpsys meminfo <包名>`。PSS 是按比例分摊共享页的进程占用，Unity 托管/原生、图形内存和系统压缩各自分列。系统查询和截图尽量放到正式计时窗口外。 [Android meminfo](https://developer.android.com/tools/dumpsys#meminfo) · [Android 进程内存说明](https://developer.android.com/topic/performance/memory-management)
- 区分暖池留存、共享引用与逐轮未归还的对象。只让本场景持有当前用到的资源，退出归还棋子、事件、动画和本局的资源租约。在允许的空闲点整理。另验切后台再恢复的实际状态，按相同前后台状态比较占用。 [安卓退场后怎么查内存](https://github.com/Yokino337088/RevGame/wiki/%E5%AE%89%E5%8D%93%E9%80%80%E5%9C%BA%E5%90%8E%E5%86%85%E5%AD%98%E6%80%8E%E4%B9%88%E6%9F%A5) · [通用内存验证](common-performance.md#内存同时看分配峰值与退场趋势)

## RevGame 已有的安卓证据

| 记录 | 实测条件与当前判读 |
| --- | --- |
| 构建和验包 | 2026-11-08，Unity 2022.3.15f1c1。Mono ARMv7 开发包为 81.17 MiB，宽屏 IL2CPP ARM64 Release 1.0.1 为 70.93 MiB（74,379,415 字节），包内验得 167 个 Android 资源文件、0 个 PC AB。两版后端、ABI 与出包配置不同，表中是文件大小。 |
| 图集与画质 | 鸦鸦 10 张表情原图 1600 × 2400，导入上限提高后切片约从 683 × 1024 到 1365 × 2044。同次记录 Android ASTC 6×6 两套图集约 6.6 MiB，PC BC7 约 12.8 MiB，旧文件约 1.9 MiB。后续资源与已签 APK 分开，显存与帧率需目标机复测。 |
| 宿主与真机边界 | Device Simulator 覆盖布局，Android 目标 Editor 直读验过章节流程。Windows Editor 读 Android AB 的首章棋盘 Shader 曾不可用，保留原始结果。旧版有用户真机反馈，最终代码的装机路径、帧间隔、热态和长时间内存尚无可对照的目标机记录。 |

[安卓构建与真机反馈原报告](https://github.com/Yokino337088/RevGame/blob/262dcf11944c4519328f706db4a13e211081554a/RevGame_Client/%E6%96%87%E6%A1%A3/%E5%AE%89%E5%8D%93%E6%9E%84%E5%BB%BA%E8%B8%A9%E5%9D%91%E6%8A%A5%E5%91%8A/%E5%AE%89%E5%8D%93%E6%9E%84%E5%BB%BA%E8%B8%A9%E5%9D%91%E5%A4%8D%E7%9B%98_2026-11-08.html)
