# Android 构建与验包

从 Unity 构建、Gradle 到最终 APK 按顺序确认。版本号以下面实测组合为参考，采用前核对项目 Unity、Gradle 导出文件、SDK/NDK 路径及发行目标。运行时与宽屏见 [Android 运行时与宽屏验收](../../revolution-framework/references/android-runtime-and-layout.md)。

## 1. 构建副本和工具链

1. 如果出现 Project path … contains non-ASCII characters，在纯 ASCII 绝对路径建独立副本，同步 Assets、Packages、ProjectSettings 和对应的已构建 AB。原工程和最终产物可以在原路径，Unity、Gradle、Bee、IL2CPP 中间产物留在副本。确认 SDK/NDK、JDK、临时目录以及 local.properties 实际使用的路径。只在验证过的副本里归档旧输出、已移除的文件和残留 PC AB。增量复制会留下旧文件。
2. 正式编辑器走 live。需要 batch 时只对独立构建副本执行，编译、切平台、AB 发布、Play 串行。分开保留 Unity 与 Gradle 日志，核对退出码、BuildReport 与目标文件。构建日志避开凭据。
3. 分开 Mono 开发包和 IL2CPP Release，固定 ABI、SDK 与签名。RevGame 使用 Unity 2022.3.15f1c1 时，Mono 测试交付为 ARMv7，正式包为 IL2CPP ARM64。对 C++ 插件核对 PluginImporter 的平台/CPU、APK 内 ELF ABI、导出符号、同版本配置和真机 P/Invoke。
4. 旧 D8 遇到 API 35 的 android.jar 抛 NPE，desugarDebugFileDependencies 失败时先查导出的 AGP 和 Gradle。该轮临时 Mono Debug 工程使用 API 32 完成测试出包。正式 Release 单独导出，采用 JDK 17、Gradle 8.9、AGP 8.7.3、API 35 的实测组合。迁移旧工程时逐模块确认 namespace、旧 manifest package 声明、Java UTF-8、仓库解析和 debug signingConfig，兼容变更落在导出副本。正式发布要按自身目标选版本。
5. 遇到 CXX1100 或随后出现 NDK is not installed，比较 ndkVersion、ndkPath 和目标目录的 source.properties。案例中 AGP 默认期望 27，实际 Unity IL2CPP NDK 为 23.1.7779620。显式对齐两个模块后通过。限制 Gradle 与 IL2CPP 工作者，预热依赖后再用 offline。只复用相同后端、ABI、工具链的可重建缓存。代理配置与凭据留本机。
6. 如果 build.target 在 HTTP 模式仍报 jobs/<id>/target-started，检查动作内部是否还用旧标记。可用项目受控动作发起异步切换，等 editor.wait 后核对 build.settings 的目标和编译状态，再重新发现 Play 能力并恢复原目标。编辑器崩溃时先保留日志，重连后再复测。

## 2. AB、宽屏与签名

- 先从源项目构建目标 AB，再检查平台主清单、映射、依赖和发布文件哈希。导出副本和 APK 只含目标平台 AB，检查缺失与多余文件。检查纹理压缩、实际图集成员、切片、帧名与透明通道。APK 大小按 AB、原生库、Player 数据拆分。
- 设计分辨率与 Android 窗口分开。宽屏回归核对合并清单的 launcher、横屏、resizeable 及固定宽高比的旧 Activity，再用 Game 和 Device Simulator 检查 UI。真机检查窗口尺寸、系统栏和可点性。三种证据分别出结论。
- Release 确认 applicationId、versionCode/Name 和已有证书匹配升级线。签名前 zipalign，签后用 apksigner verify --verbose --print-certs 核对签名与证书，aapt dump badging 核对 SDK、ABI 与 Activity，列出 APK 内容并比较 AB SHA-256。证书私钥、口令和原始机器日志留本机，签完再复制到最终目录。见 [Android apksigner](https://developer.android.com/tools/apksigner)。
- ARM64 和 16 KB 页兼容分别验。检查 APK ZIP 对齐、每个 .so 的 ELF LOAD 对齐和 16 KB 环境启动。仅自研库为 16 KB、Unity/IL2CPP/Burst 仍为 4 KB 时，保留未满足或待验证状态。见 [Android 16 KB 页尺寸指南](https://developer.android.com/guide/practices/page-sizes)。

## 3. 最小证据

分别记录构建、包内容和签名、Windows Editor 直读、Windows Editor 目标 AB、独立 APK 真机或模拟器。无设备或目标 AB Shader 与宿主不兼容时，保留明确失败与补测矩阵。Player 代码改动要更新对应后端的 Player，纯资源改动可用独立 AB 流程。发布、热更新和部署按用户当次授权执行。
