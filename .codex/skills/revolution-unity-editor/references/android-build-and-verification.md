# Android 构建与验包

从 Unity 构建、Gradle 到最终 APK 按顺序确认。版本号以下面实测组合为参考，采用前核对项目 Unity、Gradle 导出文件、SDK/NDK 路径及发行目标。运行时与宽屏见 [Android 运行时与宽屏验收](../../revolution-framework/references/android-runtime-and-layout.md)。

## 1. 当前 Unity 编辑器和工具链

1. 用 `editor status` 确认实际连接的是用户当前打开的工程绝对路径，检查是否处于编辑态。用 RevCLI `--mode live` 串行执行切平台、等待导入、构建或核对目标 AB、打 Player。进度从当前 Unity 窗口和该实例的日志核对。交付构建禁止后台另开 Unity、batch 和临时项目副本。
2. 工程、SDK／NDK、JDK、临时目录或 Gradle 路径出错时，以当前实例的错误和实际路径为证据。确认本轮能在同一工程中继续的修复项，报告需要用户决定的环境变更。包完成后同时核对 BuildReport 和文件，日志中的凭据留本机。
3. 先确定 ABI、SDK、签名及测试能力。Unity 2022.3.15f1c1 的 RevGame 实测中，Mono 测试包为 ARMv7，ARM64 测试与发行都使用 IL2CPP。每次重开编辑器后读回 Android 架构和目标 SDK，再提交构建。对 C++ 插件核对 PluginImporter 的平台和 CPU、APK 内 ELF ABI、导出符号、同版本配置和真机 P/Invoke。
4. 旧 D8 遇到 API 35 的 android.jar 抛 NPE，desugarDebugFileDependencies 失败时先查 AGP 和 Gradle。历史上使用 API 32 完成过 Mono 测试出包，正式 Release 有 JDK 17、Gradle 8.9、AGP 8.7.3、API 35 的实测组合。按本工程实际配置核对 namespace、旧 manifest package 声明、Java UTF-8、仓库解析和 debug signingConfig，再选择版本。
5. 遇到 CXX1100 或随后出现 NDK is not installed，比较 ndkVersion、ndkPath 和目标目录的 source.properties。案例中 AGP 默认期望 27，实际 Unity IL2CPP NDK 为 23.1.7779620。显式对齐两个模块后通过。限制 Gradle 与 IL2CPP 工作者，预热依赖后再用 offline。只复用相同后端、ABI、工具链的可重建缓存。代理配置与凭据留本机。
6. build.target 跨重载用 SessionState 保存同一请求，只等待已发起的切换。旧快照若在 HTTP 模式报 jobs/<id>/target-started，优先同步新版桥。等待完成后用 build.settings 读回目标并检查编译状态，再重新发现 Play 能力并恢复原目标。编辑器崩溃时先保留日志，重连后再复测。

### Windows 目录联接和工具链路径

- 工程入口、Unity 安装、Android SDK／NDK、JDK 和输出路径分别核对。报 `Invalid project path` 先查 Unity 打开工程时的路径。`ld.lld` 报打不开 `crtbegin_so.o`、`baselib.a` 或找不到 `-llog`，同时日志路径出现乱码时，先核对文件存在性及实际传给链接器的路径。此时原生源文件可能已经编完。
- 在 Windows 上可用英文目录联接指向原工程及原 Unity 安装，确认联接与目标落到同一实际目录。源码在真实 Git 工作区维护。需要英文路径构建时，从英文的 `Unity.exe` 入口显式传 `-projectPath` 打开同一工程。现有窗口从原路径启动时，确认没有未保存场景后再切换。之后可在同一窗口开发、Play 和出包。
- `editor status` 核对 `editorPath`、`project`、编辑态和导入状态。链接出错时再看编辑器日志的 `WorkingDir` 和实际 NDK 进程路径。RevCLI 和 RevMCP 会解析工程联接后校验身份。RevCLI 的 `connect` 可能按解析后的真实路径自动打开 Unity，依赖英文入口的构建从显式启动开始，再用 `--mode live` 或 `rev_bind` 接入。

### 先检查 SDK 清单和 Gradle 缓存

- 提交 Android 构建前，用本轮 Unity 实际选中的 SDK 和 JDK 运行 `sdkmanager --list_installed`。核对目标平台、Build Tools 和 Platform Tools。用实际调用的命令行工具路径及 NDK 的 `source.properties` 另外核对两个组件的版本。读取本机安装清单这一步放在隔离资源与提交构建之前。
- `Detecting Android SDK` 持续等待，控制台出现 `Still waiting for package manifests to be fetched remotely` 或 `Failed to update Android SDK package list` 时，先核对当前构建实际调用了哪个 `sdkmanager`。Unity 2022.3 的这一轮在 Gradle 之前执行 `sdkmanager --list` 刷新远程清单。Gradle 离线设置只覆盖之后的 Gradle 阶段。先确认已安装的 SDK 是否齐全，再决定是否需要网络和具体组件。
- 已有完整本地工具与依赖且要求离线时，先在任务进程内预检所需 SDK 列表和 Gradle。Windows 的 Unity 2022.3.15f1c1 与 cmdline-tools 6.0 实测为任务 Editor 注入以下环境。确认本机 `127.0.0.1:1` 无监听，再使用本机拒绝连接让 Java 的远程 SDK 查询尽快返回。`UNITY_THISISABUILDMACHINE=1` 用于处理该版本的 SDK 查询告警。启动前，用同一离线环境执行 `sdkmanager --list`，确认返回并能读取已装组件。

```powershell
$buildEnv = @{
    GRADLE_USER_HOME = $taskGradleHome
    UNITY_THISISABUILDMACHINE = '1'
    _JAVA_OPTIONS = '-Dhttp.proxyHost=127.0.0.1 -Dhttp.proxyPort=1 -Dhttps.proxyHost=127.0.0.1 -Dhttps.proxyPort=1 -Dsun.net.client.defaultConnectTimeout=3000 -Dsun.net.client.defaultReadTimeout=3000'
}
```

- 用任务专用的 `GRADLE_USER_HOME` 和初始化脚本启用离线模式，复用已校验的 Gradle 缓存。已有生成工程时，用相同任务配置运行 `help`，确认离线初始化脚本实际生效。完整依赖由真实构建核对。遇到缺件按实际坐标核对。Windows 从两个英文入口启动同一工程的 Editor，环境通过启动进程的 `-Environment` 传入。只作用于该 Editor 和它的构建子进程，保持系统代理和 Unity 安装完整。构建进程的环境先过滤凭据，日志只提取必要的错误字段。
- 已有工单卡住时，先核对当前 Unity、任务和子进程身份。确认只是该工单启动的 SDK 清单查询后，结束已确认卡住的只读查询，处理提示并读取工单终态。等到当前构建终止再重试和恢复暂存资源。

### Gradle 下载超时与离线续打

- Unity 生成原生库后，Gradle 还要完成依赖解析、资源装配、Java 处理、检查和签名。`extractReleaseAnnotations` 等任务报 `Read timed out` 时，记录日志中的完整 Maven 坐标和版本，先查当前 Gradle 仓库及缓存里是否有该版本的实际 JAR。`.pom` 和 `.module` 只描述依赖，核对时也要确认二进制文件。
- 当本次 Unity 的原生构建已经完成，确认仍是该次生成的 Gradle 工程和同一 ABI、SDK、签名配置。Unity 2022.3 的此次实测目录为 `Library/Bee/Android/Prj/IL2CPP/Gradle`。用生成工程对应的 JDK 和 Gradle 执行 `--offline :launcher:assembleRelease`，从 `launcher/build/outputs/apk/release` 查实际产物。
- 缓存缺少指定 JAR 时，优先复用已有且校验过的同版本文件。需要临时本地 Maven 仓库时，核对 JAR 与 POM 的坐标和摘要，用单次 `--init-script` 将该坐标指到本地仓库。离线执行可保留注解提取和 Lint 校验。再次缺依赖时以报出的坐标为准，补齐后再重试。
- 保留 Unity 的失败 BuildReport，另外记录 Gradle 最终状态及交付的 APK。错误回执可能包含完整进程环境，输出前提取工单、失败任务、首因及必要路径，发布前检查凭据和本机配置。

## 2. AB、宽屏与签名

- 用当前编辑器检查 Android 的主清单、映射、依赖和发布文件哈希。需要更新时按项目规则依次执行 `ab.build-incremental`、`ab.check-map` 和 `ab.publish-incremental`，三步用同一目标。
- 打 Player 前核对 `Assets/StreamingAssets`。将确认属于 PC、WebGL、iOS 等其他平台的 AB 目录和同名 `.meta` 成对移到当前工程内的 Git 忽略目录，记录原路径与暂存路径。公共 StreamingAssets 和 Android AB 留在原位置。刷新当前 Editor 后再提交构建。收到构建结束回执或确认 Unity 已退出该次构建后，将隔离文件按原路径恢复并再次刷新。CLI 等待超时须先确认 Unity 实际状态。
- 解包核对 `assets/Android/` 的平台主包、构建清单与实际条目，检查包内其他平台的 AB 目录是否为零。按需要核对文件 SHA-256、依赖和原生 ABI。Git 检出的文本 `.manifest` 若有换行差异，记录 LF 归一化后的文本比较。发布校验、APK 文件比对和二进制 AB 哈希按实际字节验证。APK 大小按 AB、原生库、Player 数据拆分。
- 设计分辨率与 Android 窗口分开。宽屏回归核对合并清单的 launcher、横屏、resizeable 及固定宽高比的旧 Activity，再用 Game 和 Device Simulator 检查 UI。真机检查窗口尺寸、系统栏和可点性。三种证据分别出结论。
- Android 桌面显示名与应用 ID 分别配置和验包。用 `aapt dump badging` 核对 `application-label`、`package`、目标 SDK 和 `native-code`。更改应用 ID 会得到独立的 Android 应用，面向已发布版本的更新需核对原应用 ID 与签名证书。
- Release 确认 applicationId、versionCode/Name 和已有证书匹配升级线。签名前 zipalign，签后用 apksigner verify --verbose --print-certs 核对签名与证书，aapt dump badging 核对 SDK、ABI 与 Activity，列出 APK 内容并比较 AB SHA-256。证书私钥、口令和原始机器日志留本机，签完再复制到最终目录。见 [Android apksigner](https://developer.android.com/tools/apksigner)。
- ARM64 和 16 KB 页兼容分别验。检查 APK ZIP 对齐、每个 .so 的 ELF LOAD 对齐和 16 KB 环境启动。仅自研库为 16 KB、Unity/IL2CPP/Burst 仍为 4 KB 时，保留未满足或待验证状态。见 [Android 16 KB 页尺寸指南](https://developer.android.com/guide/practices/page-sizes)。

## 3. 最小证据

分别记录构建、包内容和签名、Windows Editor 直读、Windows Editor 目标 AB、独立 APK 真机或模拟器。无设备或目标 AB Shader 与宿主不兼容时，保留明确失败与补测矩阵。Player 代码改动要更新对应后端的 Player，纯资源改动可用独立 AB 流程。发布、热更新和部署按用户当次授权执行。
