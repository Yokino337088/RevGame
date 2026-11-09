# RevGame Android 出包、宽屏 UI 与真机回归

本页记录 RevGame 的具体约定。通用步骤见 [Android 构建与验包](../../revolution-unity-editor/references/android-build-and-verification.md) 和 [Android 运行时与宽屏验收](../../revolution-framework/references/android-runtime-and-layout.md)。2026-11-08 原始复盘见 [安卓构建踩坑报告](../../../../RevGame_Client/文档/安卓构建踩坑报告/安卓构建踩坑复盘_2026-11-08.html)。

## 构建与交付

- 源工程保留原路径。Unity 2022.3.15f1c1 遇非 ASCII 路径门禁，在纯 ASCII 副本构建，外部 APK 最后复制到用户指定的 RevGame/Android。测试与 Release 分开。Mono 决斗测试是 ARMv7、API 32 临时组合；正式版是 IL2CPP、ARM64、API 35，实测 JDK 17 / Gradle 8.9 / AGP 8.7.3 / NDK 23.1.7779620。D8 读 API 35 失败与 CXX1100 的 NDK 不匹配是两种错误。
- 用项目 game.background.build 先发布目标 AB，真实 BuildManifest、主包和哈希对齐。构建副本与 APK 屏蔽 PC 目录及残留 PC.meta，归档已删除的 RevGameActivity.java 及其 meta。宽屏 Release 使用可调整大小的 UnityPlayerActivity。ExportAndroidReleaseBatch 在副本检查目标和 arm64-v8a 的 libRevGameCore.so，导出后再走 Gradle、zipalign、签名。固定已发布 package ID、递增版本号并验证证书与上一版一致。路径、签名口令、用户设置、缓存、外部 AB 和 APK 不入库。
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
