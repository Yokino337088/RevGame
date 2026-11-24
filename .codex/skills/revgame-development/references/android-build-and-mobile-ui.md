# RevGame Android 出包、宽屏 UI 与真机回归

本页记录 RevGame 的具体约定。通用步骤见 [Android 构建与验包](../../revolution-unity-editor/references/android-build-and-verification.md) 和 [Android 运行时与宽屏验收](../../revolution-framework/references/android-runtime-and-layout.md)。2026-11-08 原始复盘见 [安卓构建踩坑报告](../../../../RevGame_Client/文档/安卓构建踩坑报告/安卓构建踩坑复盘_2026-11-08.html)。

## 构建与交付

- 在原始 RevGame Git 工作区编写和修改 Unity 源码。Windows 英文目录联接可用于启动编辑器和打包。这两个入口共用工程文件和 `Library`。2026-11-24 的前台出包同时通过英文入口启动 Unity 安装和工程，随后用 RevCLI `--mode live` 核对编辑态与 Android 目标。RevMCP 可通过同一工程联接绑定，绑定后先发现实际能力。
- 出包预检放在隔离 AB 和提交工单之前。2026-11-24 的再次构建中，SDK 33、Build Tools 34.0.0 和 Platform Tools 32.0.0 均已安装，Unity 仍在联网运行 `sdkmanager --list`。按 [SDK 清单和 Gradle 缓存预检](../../revolution-unity-editor/references/android-build-and-verification.md) 查实际工具和缓存。离线构建先验证本机 SDK 与 Gradle，再从英文安装及工程联接启动同一工程。最新一轮实际拿到 Unity `Succeeded`，0 错误、12 条警告。
- 变更 Android 品牌时，使用 `productName` 设置显示名，再在 `applicationIdentifier` 中维护 Android 专属值。先用 `settings.get` 读取现有映射，再用 `settings.set` 改动对应字段。只替换包名前缀时保留用户未要求改动的后缀及其他平台标识。唐门测试包实测 `围棋大作战`、`com.Tangmen.RevGame.Unity`。以 APK 的 `application-label` 和 `package` 为最终验收证据。
- 应用名称与 ID 是本次要保存的项目设置。先保存品牌设置，再备份本轮要临时改动的 ABI 和目标 SDK。退出临时构建后读回品牌，并验证恢复后的 `ProjectSettings.asset` 与品牌设置保存后、构建前的快照一致。PC、WebGL 目录及 `.meta` 原路恢复后刷新。
- ARM64 测试包使用 IL2CPP。在现场注册支持时，用 `settings.get` 和 `settings.set` 临时配置 Android 架构和目标 SDK。提交 `build.player` 前读回设置。本次 Unity 版本的 `AndroidTargetArchitectures` 为 `2` 时仅启用 ARM64，构建后端传 `backend=il2cpp`。Unity 2022.3.15f1c1 以 Mono 为当前后端重启时曾重置这项设置。需要真机自动化或帧时采样时，按任务设置 `automation` 和 `frameTiming`。结束后恢复项目设置，APK 输出到用户指定位置下的独立子目录。
- 2026-11-24 实测使用 Unity 2022.3.15f1c1、JDK 11、Gradle 7.2、AGP 7.1.2、NDK 23.1.7779620 和 SDK 33，最低 API 22。工程入口已是英文时，NDK 仍因 Unity 安装路径里的中文在链接 `libil2cpp.so` 时失败。从英文安装联接重开同一工程后通过链接。Gradle 随后因 `com.android.tools.external.com-intellij:kotlin-compiler:30.1.2` 下载超时失败。用校验过的同版本 JAR 和 POM 在本地解析此坐标，对临时生成的 Gradle 工程离线执行 `:launcher:assembleRelease`。最终结果以 Gradle 终态、APK 签名、ELF 架构和资源哈希为证据。 [RevGame Wiki 实测复盘](https://github.com/Yokino337088/RevGame/wiki/%E5%AE%89%E5%8D%9364%E4%BD%8D%E5%8C%85%E4%B8%BA%E4%BB%80%E4%B9%88%E5%8D%A1%E5%9C%A8%E6%9C%80%E5%90%8E%E4%B8%A4%E6%AD%A5)
- 构建前核对 Android 主包、BuildManifest 和 `ab.check-map`。资源需要更新时用项目 `game.background.build` 发布目标 AB，核对主包、映射与哈希。只隔离确认为其他平台的目录和同名 `.meta`，结束后恢复并刷新。验包核对 `assets/Android/`、`arm64-v8a`、`libRevGameCore.so`，并检查 APK 中其他平台 AB 是否为零。
- 历史组合分别归档。Mono 决斗测试曾用 ARMv7 与 API 32，另一轮用 SDK 33 避开旧 D8 读取 API 35 的错误。2026-11-08 的正式 ARM64 包沿用当时的英文构建副本，通过 `ExportAndroidReleaseBatch` 导出，实测 JDK 17、Gradle 8.9、AGP 8.7.3、API 35 和 NDK 23.1.7779620。那一轮归档了旧 `RevGameActivity.java` 和 `.meta`，宽屏 Release 用可调整大小的 `UnityPlayerActivity`。发行包核对已发布包名、版本号和证书。路径、签名口令、用户设置、缓存、外部 AB 和 APK 按仓库约定处理。
- 复用缓存前核对后端、ABI、工具链与构建输入。Mono 与 IL2CPP 的产物不能互换，涉及运行时代码修复需重编 Player。仅有本机验包证据时，标明真机范围。检查所有 ELF 和 APK ZIP 对齐，自研库的 16 KB 结果不能代表其它 .so。
- 联机开发包的内网默认地址随测试主机/网段变动。确认端口和设备可达性，把按钮触达、Android P/Invoke 与两设备帧哈希分别验收。
- 贴图走 GameSpriteAtlasBuild 与 GameSpriteAtlasService。决斗帧按名字和顺序切片及唯一 V2 图集，验 Android 实际帧切换和 HUD 命中。鸦鸦剧情/战斗表情分包，源图保留较高分辨率，最终图集压缩一次，PC BC7、Android ASTC 6×6。清晰度按实际像素和分包字节报告。文案与 Excel 更新走真实导表和字体清单校验；输入变化时两端按同一版重建。
- 构建或域重载后等待 editor.wait，核对目标后重新发现。若 build.target 在 HTTP 模式尝试写旧工单标记，沿用受控 HTTP 动作切平台并读回，清理临时脚本与恢复原目标。崩溃先存日志再重连。Windows Editor 的真实 AB 验收使用 PC 包，Android 真机使用 Android 包并单列结论。若保留 Android 构建目标后 Editor 粉屏，按 [Editor 资源排查](unity-and-assets.md) 检查实际图形 API 和主包来源。

## 布局、场景和本地配置

- 以 GameBattleLayout 和 GameMobileLayout 为布局入口。Android 以设备真实画幅和安全区绘制；1920×1080 是与 PC 对照的设计坐标。背景等比铺满，棋盘与侧栏使用同一投影。普通面板使用安全设计框；剧情、教学、Loading 保留全屏遮罩；剧情跳过、人物和长对白放安全区；决斗独立 HUD 做场景 Canvas 验收。固定 16:9 原生壳、直接拉伸、整屏缩小、叠加上一次偏移都容易出现留空或遮挡。
- 远征手牌、准备按钮、无尽牌组/强化、九路围棋 Agent/结算按钮分别检查不挡棋盘、没超安全区。剧情验全屏点下一句、安全区内跳过、最长句和立绘/对话分列。盘点 21 个 RevUI 面板加独立决斗 HUD，区分盘点、逐模式实测和真机覆盖。普通 Game 与宽屏刘海 Device Simulator 都要对照，实测按钮、屏幕边界和业务结果。
- 大厅鸦鸦通过 LobbySceneView 加载 YayaIdle24、重绑并采样 Animator 首帧，然后断言 Sprite 与实际动画换帧。六章/无尽的随机台词分开验配置、触发、图集、布局和排序；无尽移动端立绘目标占可见视口高度 50%，独立对白层要压过棋子。主流程报告中保留前置失败与后续通过证据。
- DeepSeek Android 保存由 PvpAgentSettingsStore 按实际运行平台使用 persistentDataPath 下的明文 DeepSeekAgentSettings.txt，Windows 保留 DPAPI。读写/清除使用同一条配置，测试用假 Key 隔离槽，不能打印凭据或复制进 AB。真机应测保存、重启读取和清除。Device Simulator 的移动设备标识只供布局，不能作为存储平台。
- 第一章 Boss 领奖到第二章应走 GameFlow.Story：等待背景完成并核对目标章号，再打开剧情。真实路径用隔离 Boss 最后一击、实际结算/领奖按钮、第二章画面和实际落子确认。单独跳进第二章或只验证大厅不能覆盖这个竞态。
