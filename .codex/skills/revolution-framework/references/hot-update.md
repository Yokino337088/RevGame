# 资源热更（扩展包 RevHotUpdate 专题）

> 权威文档：`Revolution.Document/热更新/RevHotUpdate使用说明.md`（怎么用）、`RevHotUpdate架构解析.md`（为什么这么设计、失败怎么收场）、
> `RevHotUpdate技术方案.md`（要不要做、平台核实、存储选型、大版本锚定、决策与分期）。
> 代码：`RevolutionFrameWork_Unity/Assets/Revolution.HotUpdate/`（程序集 `Revolution.HotUpdate` / `Revolution.HotUpdate.Editor`）· demo：`RevolutionFrameWork_Unity/Assets/Revolution.Demo/RevHotUpdate.Demo/`。
> 下文 `Assets/` 路径相对 Unity 工程 `RevolutionFrameWork_Unity/`；当前实现以源码核实，技术方案旧章节的分期目标不是已完成承诺。

## 1. 一行接法（业务侧唯一必须写的代码）

```csharp
// 放在"加载任何业务资源"之前（启动流程里一次即可）
RevHotUpdateResult result = await RevHotUpdate.InitializeAsync(
    new RevHotConfig
    {
        RemoteRoot = "https://cdn.example.com/gameA",   // 必填；http:// 需要 AllowHttp = true
        Env = "", Channel = "",                          // 留空 → URL 里不出现这两段
        VerifyMode = RevHotVerifyMode.Hash,              // 尺寸 + SHA-256
        Concurrency = 3,                                 // 手机上别超过 4
        LogTag = "HotUpdate",
        OnForceUpdateRequired = info => ShowUpdateDialog(info.Message),
    },
    p => progressBar.value = (float)(p.Percent / 100.0));

if (result.Success == false)
{
    RevLog.Warn(result.Error.Message, "HotUpdate");
    return;
}
// 成功后加载资源的 API 不变：RevResManager.Load / LoadAsync
```

- 分步：`CheckAsync`（只拉清单算差量，给"要不要更新/下多少"的 UI）→ `UpdateAsync(check)`（**必须把 check 原样传入**）。
- 只等不发起：`WaitReadyAsync()`（从没跑过会返回 `NotRun`，提示"你还没接热更"）。
- 观察：`RevHotUpdate.State` / `LastError` / `LocalResVersion` / `Progress` 事件 / `Dump()`。

## 2. 框架侧四个钩子（默认 null，不装热更包时保持默认行为）

框架源码前缀：`RevolutionFrameWork_Unity/Assets/Revolution/Runtime/RevResourceSystem/`；前三项声明在 `Implementation/Loaders/RevABLoader.cs`，最后一项在 `Support/RevResBootstrap.cs`。

| 钩子 | 实际签名与默认语义 | 热更桥绑定方法 |
|---|---|---|
| `RevABLoader.BundlePathResolver` | `Func<string, string>`：包名 → 完整文件路径/URL；null/空返回值回退 `StreamingAssets/<平台>/<包名>` | `RevHotStore.ResolveBundlePath` |
| `RevABLoader.BundleCacheKeyResolver` | `Func<string, RevBundleCacheKey>`：包名 → Unity Hash128/CRC 缓存键；无有效键走默认请求 | `RevHotStore.ResolveBundleCacheKey` |
| `RevABLoader.DependenciesOverride` | `Func<string, string[]>`：包名 → 依赖；null 回退内置主包 Manifest，空数组表示明确无依赖 | `RevHotStore.ResolveDependencies` |
| `RevResBootstrap.ResMapOverride` | `Func<Dictionary<string, string>>`：热更映射覆盖合并内置表；null 只用内置表，合并不能删除内置旧键 | `RevHotStore.LoadHotResMap` |

- 安装发生在 `RevResBootstrap.Init` 前；`InitializeAsync` 与 `CheckAsync→UpdateAsync` 正式路径均安装桥。实现见 `Assets/Revolution.HotUpdate/Runtime/Integration/RevHotResBridge.cs`，绑定用静态方法组。
- 断开清理要与安装区分：当前 `RevHotResBridge.Reset()` 仅复位 `_installed`，未将四个静态委托置 null；`RevHotUpdateUnityHooks` 复位内存、不删磁盘。不要把“重置标志”宣称成完整卸载钩子。
- 想手动接管资源系统初始化的工程才需要自己调 `RevHotUpdate.Install()`；后续若补断开入口，需复核四钩子同时解除与重装顺序。

## 3. 覆盖式语义（**最关键的一条设计**）

```text
已验证清单中的业务包 → 只从当前完整快照/版本化 URL 读；缺失不得混读任意同名旧包
无有效清单的旧版兼容路径 → 版本目录/历史目录/首包落地 → 交回框架默认
平台主包 Manifest → 始终来自随该 App 发布的内置基线，不由热更清单覆盖
```

- **不要**实现成"热更包只从持久化读" —— 新装机 / 清数据 / 换机时持久化目录是空的，会直接崩（首屏若在热更包里就是黑屏）。
- 失败按阶段处理：候选未激活时不切 `current`；已切新而重装失败时尝试恢复受保护旧版；旧版也不可用则明确报错并停止进入游戏。不能承诺任意旧版兼容、不能把“删目录”当生产恢复接口。
- 覆盖式路径回退只描述来源优先级，不是兼容性证明；新装/清数据后仍要验证首包完整、映射与代码大版本一致。

## 4. 远端目录约定（唯一真相在包内 `RevHotUrlBuilder`）

```text
清单 ：{RemoteRoot}/{环境?}/{平台}/{大版本}/{渠道?}/RevHotManifest.txt          ← 不带资源版本（它是"发现新版本"的入口）
资源 ：{RemoteRoot}/{环境?}/{平台}/{大版本}/{渠道?}/{资源版本}/bundles/<包名>   ← 带版本段（内容不可变，可长缓存）
映射表：{RemoteRoot}/{环境?}/{平台}/{大版本}/{渠道?}/{资源版本}/ResMap.txt       ← 与包同批更新
```

- **上传纪律**：先传所有内容，**最后传清单**（清单是"新版本已就绪"的原子开关）→ 玩家要么拿旧版本、要么拿完整新版本，不会拿"半新半旧"。
- **两条缓存纪律**：内容路径 `immutable` 长缓存（URL 带版本段）；清单路径 **no-cache**（否则玩家永远拿到旧清单）。
- **平台名**：编辑器里恒为 `PC`（`RevHotPlatform` 带 `!UNITY_EDITOR`）；打包侧 `ABBuildSetting.GetPlatformName`（Standalone* → `PC`）——**两边必须对齐**，否则编辑器调试会 404。

## 5. 版本模型：大版本锚定（`@appVersion` = `Application.version`）

- 清单里 `@appVersion` 与客户端不一致 → `AppVersionMismatch`（**设计如此**）：跨大版本必须重新出包，资源层因此**不需要做向后兼容**。
- "大版本内热更资源、跨大版本重出包"是这个框架推荐的发版纪律（王者那套做法）；`ForceUpdateRequired` / `OnForceUpdateRequired` 只负责**识别 + 回调**，跳商店 / 整包下载由业务决定。
- 版本目录：`{LocalRoot}/{平台}/{大版本}/{资源版本}/`；保留目录提供恢复来源，但不是任意版本兼容保证。当前切换为 `完整候选校验 → MarkVersionComplete → BeginActivation(previousVersion) → 旧依赖图 ShutdownAll → 写 current/切生效版本与缓存键 → Init → CommitActivation → DeleteOldVersions`；旧目录必须保留到重装成功后。

### 5.1 当前门禁与续传事实（2026-10-16；含提交 `6ac97b2`，以源码复核）

以 `RevolutionFrameWork_Unity/Assets/Revolution.HotUpdate/` 为下列路径前缀：

| 项目 | 已落地依据与边界 |
|---|---|
| 清单门禁 | `Runtime/Core/RevHotManifest.cs`：`string ValidateForActivation(string platform, string channel, string environment = null)`；平台/渠道/可选环境、SHA-256、包名/版本安全、依赖闭包/环、WebGL Hash128/CRC。不替代门面单独做的大版本校验 |
| 候选快照 P0 | `Runtime/Pipeline/RevHotStore.cs`：`ValidateSnapshotAsync(manifest, version, token)` 强制目标包和 ResMap 真 SHA，补齐未变包；另加载内置主包确认 `AssetBundleManifest`。`ValidateWebGLCandidateAsync` 逐包用 Unity Hash128/CRC 下载/验证并检查内存 ResMap，不做 AB 文件 SHA 校验 |
| 激活/失败恢复 P0 | `Runtime/Facade/RevHotUpdate.cs`：`RunUpdateAsync` 在业务句柄被持有/正在加载时拒绝激活，先确保旧快照可恢复；`RestoreIfNeeded` 恢复版本/清单/映射并重装。Store 的 `.pending` + `RecoverInterruptedActivation()` 处理启动恢复；回退失败明确 `ApplyFailed`，不报 Ready |
| 跨启动续传 P1 | `Runtime/Download/RevHotDownloader.cs`：`PreparePart(partPath, item, resVersion, url)` 用 `.part.meta` 保存 `RevHotPart1` + 资源版本/包名/大小/SHA-256/完整 URL；身份完全一致才复用长度。切源清零；200/416 从零 GET；完整分片先校验，坏 hash 清分片与元数据；取消保留，过期清理见 Store `ClearStaleParts()` |
| 发布自检 P1 | `Editor/RevHotManifestBuilder.cs`：`string SelfCheck(RevHotManifest manifest, string outputDir)` 重读发布清单，对全部包与 ResMap 比尺寸/真 SHA-256，检查文件名大小写/引用/首包基线一致；平台主包仅验可读 Unity AB 文件头，不能说自检已在编辑器跨平台真加载所有包 |

**不要补写不存在的能力**：当前 Config 无 `ProbeBundles`；没有独立候选上下文/在发布 `current` 前通过资源系统加载代表资源的探针链路。文件平台“SHA + 内置主包可加载”不等于所有候选 AB 及业务资源可用，`Init()` 只装策略；需另外做 Unity/真机验收。技术方案中的 `CoreBundles`、按标签下载、清单签名/公钥信任、代码热更等不属于已落地 API；hash/CRC 不是清单来源可信的签名。

## 6. 本地假 CDN demo（不改真云就能跑通全链路）

```text
① 菜单：Revolution.Tools / 热更新 / Demo / ① 一键：打包 + 清单 + 装配本地 CDN
② 双击 Assets/Revolution.Demo/RevHotUpdate.Demo/起本地CDN.cmd（默认 http://127.0.0.1:8000）
③ 打开 RevHotUpdateDemo.unity 点 Play → 面板：① 检查更新 → ② 执行更新 → ③ 加载演示资源
```

- 演示资源 `Assets/GameRes/RevHotDemo/revhot_demo.txt` 的"构建序号"每次跑菜单 ① 都会 +1 → 于是每轮都能看到真实的**差量更新**（第二轮只下变化的那一个包）。
- 假 CDN 脚本支持 **Range**（断点续传）与两类缓存头，行为与真实对象存储一致；用它验证"清单 no-cache / 内容 immutable"这两条纪律。
- demo 面板还演示了两个最容易踩的坑：`RevResBootstrap.UseABInEditor`（编辑器默认直读，看不到热更）、"先 Release 再 Load"（缓存 + 引用计数）。

## 7. 平台差异（技术方案第十七、十八章）

| | 文件型平台（Android / iOS / PC） | URL 型平台（WebGL / 小游戏） |
|---|---|---|
| 下载 | `UnityWebRequest` + `RangeFileHandler : DownloadHandlerScript` 校验响应后写分片 | 候选阶段逐包 URL 请求，AB 由 Unity 引擎下载/验证到缓存，不做本地版本文件落地 |
| 校验 | 完整快照强制尺寸 + SHA-256（分帧流式），另验内置主包 Manifest | AB 使用 Unity `Hash128` + CRC；ResMap 文本另做 SHA，主包来自随应用发布的基线 |
| 续传 | `Range` + `.part` + `.part.meta` 身份 | 无本包管理的文件分片续传 |
| 版本目录 | 本地 `…/{平台}/{大版本}/{资源版本}/` | **URL 里的版本段就是目录** |
| 特殊情况 | Android 首包在 APK 内（`jar:`），需要"首包落地"到 `persistentDataPath` | 没有文件系统；并发降到 2 |

存储选型（技术方案第十八章）：**对象存储 + CDN，首选腾讯云 COS + CDN**（与微信同生态），次选阿里云 OSS + CDN；
客户端只认"一个 HTTPS 域名 + 路径模板"，**不引任何云 SDK**（换家只改配置）；小游戏要在公众平台配"服务器域名"（备案 + HTTPS）。

## 7.1 小游戏 / WebGL 的配置硬约束（`RevHotConfig.Validate()` 在配置期就会拦）

- `Concurrency` **默认按平台**（小游戏 2 / 其余 3）；小游戏上并发 > 4 直接报错。
- 小游戏 / WebGL 上**禁止明文 http**（**不受 `AllowHttp` 影响**）：必须 https。
- 热更包从清单里的 `unityHash` / `unityCrc` 注入引擎缓存标识（`RevABLoader.BundleCacheKeyResolver` 钩子 + `RevBundleCacheKey`），否则小游戏每次进游戏都重下全部 AB；`SetActiveBundleKeys` 与"切版本"同步（**换版本必换 hash 表**）。
- 平台名：小游戏构建目标（`XxxMiniGame`）统一归 `WebGL`（`RevHotPlatform.Name` / `RevABLoader.MainName` / `ABBuildSetting.GetPlatformName` 三处对齐）；渠道段（微信 / 抖音）不硬编码，交由 `BundlePathResolver` 钩子按需处理。
- 发布侧工具：`Revolution.Could/`（腾讯云 COS 热更发布 WPF 可视化工具）与 `UploadToCos` / `UploadToOss` 命令行脚本（`Assets/Revolution.Demo/RevHotUpdate.Demo/`，给 CI 用）——都按"内容先传、清单最后传"发布，同版本路径内容不同**拒绝覆盖**。
- 完整平台约束清单见 `startup-and-platform.md` §4。

## 8. 装法与分支

```text
git clone -b package   https://github.com/Yokino337088/Revolution.git Assets/Revolution
git clone -b hotupdate https://github.com/Yokino337088/Revolution.git Assets/Revolution.HotUpdate
```

- `hotupdate` 是**生成分支**（CI 从 `main` 的 `Assets/Revolution.HotUpdate/` 整份重建），不要往上提交。
- **配对前提**：框架必须包含四钩子及对应签名（见 §2）；不能将当前热更包与只有路径/映射两钩子的旧框架配对。
- 跨大版本**不要复用远端包目录**（哪怕 hash相同）：会给"资源与代码接口不匹配"埋雷。
