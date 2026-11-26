# RevGame IL2CPP 正式包

出包按用户当次要求从 GitHub Actions 手动运行。普通提交继续触发 RevGame 规范检查。成功后从对应运行的 **Artifacts** 领取完整的 Windows x64 游戏目录或 Android ARM64 APK。项目在出包前提交与所选代码版本配套的 AB（游戏资源包）。 [RevGame 规范检查](https://github.com/Yokino337088/RevGame/actions/workflows/revgame-policy.yml)

中国版 Unity 授权使用 **RevGame IL2CPP 正式出包**。点 **Run workflow**，选 `main` 和单一目标 `windows` 或 `android`。每个目标使用自己的托管 Windows runner、独立检出和按平台区分的并发组。 [中国版工作流](https://github.com/Yokino337088/RevGame/actions/workflows/revgame-il2cpp-release.yml)

## 构建授权与正式签名

仓库管理员在 **Settings → Secrets and variables → Actions** 配置下列 Secrets。Unity 三项使用明确授权给这版 Unity 中国编辑器、可从命令行激活的构建许可。构建结束会尝试归还该次许可。只有一个可用席位时，等当前 Unity 任务结束后再手动运行另一个平台。

| Secrets | 作用 |
| --- | --- |
| `REVGAME_UNITY_EMAIL`、`REVGAME_UNITY_PASSWORD`、`REVGAME_UNITY_SERIAL` | Unity 中国版 `2022.3.15f1c1` 无人值守构建许可 |
| `REVGAME_ANDROID_KEYSTORE_B64` | 已有发布 keystore 的 Base64 内容 |
| `REVGAME_ANDROID_STORE_PASS`、`REVGAME_ANDROID_KEY_ALIAS`、`REVGAME_ANDROID_KEY_PASS` | 该 keystore 的密码、发布密钥别名与密钥密码 |

Windows 只读 Unity 授权。正式签名放在 Android 专属步骤，签名文件在 runner 的临时目录还原并在用完后删除。保留原来的发布签名，验包步骤会输出公开证书的 SHA-256 摘要和 APK 包名版本。 [Unity 命令行授权](https://docs.unity3d.com/2022.3/Documentation/Manual/ManagingYourUnityLicense.html) · [GitHub Actions Secrets](https://docs.github.com/en/actions/how-tos/write-workflows/choose-what-workflows-do/use-secrets)

## 目标平台资源

1. 无凭据的预检在 Windows runner 上执行隔离回归，并在实际检出验证目标目录，只将其它平台的目录及对应 `.meta` 暂移到工程外。恢复时使用原文件，保留原 GUID。
2. 安装固定 c1 Editor 和该平台模块。Windows 使用 IL2CPP Build Support，Android 沿用 JDK 11.0.14.1、NDK 23.1.7779620、SDK 33、Build Tools 34.0.0 和 Unity 自带 Gradle 7.2。
3. 直接使用本次检出的 `StreamingAssets/PC` 或 `StreamingAssets/Android`。核对 `BuildManifest.json` 的平台、文件集合和摘要，再调用正式 Player（玩家运行的程序）构建。Windows 使用框架 `build.player` 的 IL2CPP 与正式设置。Android 使用 `GamePvpBuild.ExportAndroidReleaseBatch` 导出 ARM64 IL2CPP Gradle 工程，运行 `assembleRelease` 后对齐、签名。
4. 从 Windows `RevGame_Data/StreamingAssets` 或 APK `assets/` 读取实际 AB，与构建前记录的 SHA-256 核对。验包还检查原生决斗库、IL2CPP 产物及其它平台资源是否混入，通过后才上传 Actions Artifact。

## 失败后的检查

看 Actions 中最先失败的步骤。资源预检可以在配置许可前验证隔离与恢复。缺少授权或签名时，出包任务会先列出待配的 Secret 名称。下载、Unity BuildReport、Gradle 或签名失败各自保留错误边界。正式包经验证后的公开发布由现有发行流程处理。

## GameCI 个人版导入验证

在仓库的 Actions Secrets 中添加 `UNITY_LICENSE`、`UNITY_EMAIL` 和 `UNITY_PASSWORD`。`UNITY_LICENSE` 保存 Unity Hub 激活个人版后生成的 `.ulf` 文件全文，另外两项来自同一 Unity 账号。 [GameCI 个人版授权说明](https://game.ci/docs/github/activation/#personal-license)

在 Actions 中手动运行 **RevGame GameCI 个人版导入验证**，选择 `main`。该任务用 GitHub 标准 Ubuntu 托管 runner 和国际版 `2022.3.15f1` 导入工程。通过条件是 Unity 完成脚本编译并实际加载启动场景，随后生成本轮回执。通过后才进入 Windows IL2CPP 和 Android 正式签名适配。 [验证工作流](https://github.com/Yokino337088/RevGame/actions/workflows/revgame-gameci-personal-smoke.yml)

导入验证通过后，按需求单独打开 **RevGame GameCI Windows 手动出包** 或 **RevGame GameCI Android 手动出包**，从 `main` 运行。Windows 在标准托管 Windows runner 构建，ARM64 Android 在标准托管 Ubuntu runner 构建。Android 沿用上文的四项签名 Secrets，验包读取成品中的应用 ID `com.Tangmen.RevGame`、桌面名称「围棋大作战」和公开证书指纹。 [Windows 出包](https://github.com/Yokino337088/RevGame/actions/workflows/revgame-gameci-personal-windows.yml) · [Android 出包](https://github.com/Yokino337088/RevGame/actions/workflows/revgame-gameci-personal-android.yml)

两个入口分别校验本轮提交里的目标 AB，记录摘要后才启动 Unity。构建完成再比对检出和实际成品，成功后从对应该平台的 Artifacts 下载。`Library` 是 Unity 的导入缓存，首次检出出现缓存提示时会执行完整导入。 [Unity 导入缓存说明](https://docs.unity3d.com/2022.3/Documentation/Manual/AssetDatabaseCustomizingWorkflow.html)

GameCI 按构建目标、runner 系统及架构、编辑器镜像和项目配置缓存 `Library` 的资源导入产物及索引。第一次成功出包保存缓存。此后在同一兼容范围内，Unity 可复用已经导入的图集和纹理，只更新改动或缺失的部分。Player、Gradle 工作区与签名文件属于当次任务的临时数据。 [GitHub Actions 缓存说明](https://github.com/actions/cache#cache-action)
