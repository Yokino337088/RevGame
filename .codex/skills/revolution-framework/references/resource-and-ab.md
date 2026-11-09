# 资源加载与 AB（RevResourceSystem 专题）

> 权威文档：`Revolution.Document/资源加载系统/资源加载系统使用说明.md`、`资源加载系统架构解析.md`。
> 本文件是"写代码时会用到的规则 + 最容易踩的坑"。

## 1. 一条链路（业务只需要记这个）

```text
RevResManager.Load / LoadAsync(根目录, 资源名, 类型, 分组)
   → 拼逻辑路径（两段式，缓存键用增量 FNV-1a，命中时零字符串分配）
   → 查缓存（命中：引用计数 +1、清 MarkedUnused、刷新 LRU）
   → 未命中：按注册顺序问策略链 → 第一个 Match 的策略负责 MapPath + 建 Loader
   → 加载 → 建句柄（IsLoaded / Content / ErrorReason）→ 入缓存
   → 业务 Release → 计数归零 → 进 _unused（延迟释放）→ FlushUnused()（切场景读条期）真卸
```

关键点：**"从哪加载"由策略链决定，业务代码一行不用改**（编辑器直读 / AB / Resources 三种来源同一套业务代码）。

## 2. 策略链与两种模式（最常踩的坑）

| 模式 | 注册了谁 | 表现 |
|---|---|---|
| **编辑器默认（开发模式）** | 只注册 `RevEditorResPolicy`，**直接 return** | 所有资源走 AssetDatabase 直读，**AB / Resources 完全不参与**；改文件立即生效 |
| **AB 模式（真机 / 编辑器手动开启）** | `RevABResPolicy`（除 `Res/` 前缀外全接管）→ `RevResourcesResPolicy`（链尾兜底） | AB 找不到时靠 `AllowFallback = true` 落到 Resources 兜底 |

- 编辑器想看 AB / 热更效果：`RevResBootstrap.UseABInEditor = true`（菜单 `Revolution.Tools/资源/AB 加载模式（编辑器）`，EditorPrefs 持久化）。
- 这个开关**切换后会立即重建策略**，不用重启编辑器。
- 兜底是"能用"也是"陷阱"：AB 里漏标的资源，在编辑器直读下**照样正常**，直到出真机才报 `FileNotExist` —— 所以打包前必须跑漏标检查（见 `editor-tools.md`）。

### 2.1 切换构建目标后 Editor Play 粉屏或加载失败

先分清活动 Build Target、实际运行宿主与 AB 构建目标。桌面 Editor 切到 Android 会带上 `UNITY_ANDROID`，但 Play 仍由桌面图形 API 渲染。Revolution 从源提交 `58e5af2` 起让 `RevABLoader.MainName` 优先匹配 `UNITY_EDITOR` 并返回 `PC`。实际 Player 再按目标选择 `Android`、`iOS`、`WebGL` 或 `PC`。不同宿主的桌面 AB 可能共用 `PC` 目录名，仍须核对实际构建目标与图形 API。

1. **记录现场。** 从当前 Unity 实例确认活动构建目标与实际图形 API，读取 `UseABInEditor`，检查项目安装的框架版本。必要时检查热更扩展或项目注册的路径重定向是否改变了实际加载来源。Device Simulator 的设备型号只用于预览设备布局。
2. **核对整条包链。** 真实 AB 模式下确认主包、依赖包和材质所在包的实际来源，并核对 `BuildManifest.json`、ResMap 与包依赖同属对应平台构建。目录名称和资源句柄加载成功只能证明一部分。对可疑材质核对 `Shader.isSupported`、错误 Shader、实际采样纹理、绑定和屏幕输出。
3. **对照资源模式。** 直读正常、AB 粉屏时优先查平台误选、混包、过期依赖和图形 API 变体。两种模式都异常时继续查源 Shader 编译、渲染管线和材质配置。切换模式后从干净的 Play 会话复测。
4. **按故障所在层修复。** 若安装的旧框架把 Android 构建目标误当成 Editor 宿主，更新框架并同步快照。若已选正确平台但内容不匹配，用正常构建链重建对应平台的 AB、依赖和映射。项目业务继续通过资源门面加载逻辑路径。
5. **保持原条件验收。** 保留出错时的活动 Build Target，从正式启动场景开启真实 AB 冷加载，核对实际包来源、材质采样和画面。若涉及 Android 发布，再在 Android Player 独立验收 Android 包。

RevGame 的实测环境是 Windows Editor、Android 构建目标与 Direct3D 11。旧桥选了 Android 包，序章 Shader 名称可读而背景粉屏。切回正确的 PC 包来源后，纹理绑定和画面恢复。项目名、素材名与预览动作只用于这个样例。 [框架修复](https://github.com/Yokino337088/Revolution/commit/58e5af27e4821c9ba14421564bc1cd81716d5b22) · [RevGame 验收证据](https://github.com/Yokino337088/RevGame/commit/0e04caf13aee2b3f0765a4c3740fd93fc611bd6f)

### 2.2 旧版 Revolution 的判断与最小升级

**先查 Unity 实际编译的副本。** 在目标工程查 `rg -n -A 25 'static string MainName' Assets Packages -g RevABLoader.cs`。Git 或 Registry UPM 依赖先在 Package Manager 核对安装来源与解析版本，再打开实际包源码查看。若 `UNITY_ANDROID` 在 `UNITY_EDITOR` 下仍能命中并返回 `Android`，该版本有误选风险。若 Editor 已优先返回 `PC`，或移动平台分支均排除 `UNITY_EDITOR`，再用 `ResolveBundlePath` 核对热更和自定义重定向后的最终路径。

| 安装方式 | 最小更新 |
|---|---|
| `Assets/Revolution` 源码快照 | 修复只涉及 `Assets/Revolution/Runtime/RevResourceSystem/Implementation/Loaders/RevABLoader.cs`。先对比上游补丁。文件兼容时可同步修复后的 C#，有定制或其它差异时只把 `UNITY_EDITOR` 优先返回 `"PC"` 的分支移植到 `MainName`，保留原 Player 分支和 `.meta`。 |
| Git UPM 或 Registry UPM | 在 Package Manager 或 `Packages/manifest.json` 更新到含修复且兼容的包版本。Git UPM 固定到含修复的包导出提交，注意框架源码 `main` 提交和 `package` 导出提交不同。等 Unity 重新解析，检查实际源码与生成的 `Packages/packages-lock.json`。需维持旧版时，在该依赖的维护分支或受版本管理的嵌入包回移单文件补丁，禁止修改 `Library/PackageCache` 作为交付。 |
| 本地或嵌入 UPM、Git 子模块 | 在实际包源或子模块分支回移同一处补丁，或将引用升级到含修复的提交。检查同名嵌入包是否优先于 manifest 中的依赖。 |

[上游单文件补丁](https://github.com/Yokino337088/Revolution/commit/58e5af27e4821c9ba14421564bc1cd81716d5b22) · [包含修复的 package 导出示例](https://github.com/Yokino337088/Revolution/commit/a6f8e1e6f4263e5c37cf49a0a11907e352fdbfa8) · [Unity UPM 更新说明](https://docs.unity3d.com/cn/2022.3/Manual/upm-ui-update.html)

**按结果补齐产物。** 同一项目已有与当前宿主、Unity 和资源版本兼容的一整套 PC AB 时直接复用。缺包、包版本不匹配或缺少适用图形 API 时，通过项目既有构建链重建 PC AB，配套同步主包、依赖、清单与映射。PC 包使用目标项目自己的产物。

**最小验证。** 等待脚本编译和依赖解析完成，保留 Android Build Target，开启真实 AB 并从正式场景冷启动。核对主包和相关依赖的最终路径，桌面应读取同套 PC 构建的包。打开使用自定义 Shader 的实际界面，读回 Shader 支持、纹理绑定并截图，再退出重进一次。涉及 Android 发布时另验 Android Player 的主包、依赖和相同场景。直读只用于区分资源模式。

## 3. ResMap.txt（逻辑名 → 包名 + 资源名）

- 位置：`Assets/Revolution/Resources/ResourceSystem/ResMap.txt`（**随包体进 Resources**，运行时 `Resources.Load<TextAsset>("ResourceSystem/ResMap")` 读）。
- 格式三列：`逻辑名|包名|资源名`；`#` 开头跳过；**段数 ≠ 3 的整行跳过**。
  - 逻辑名 = 资源相对**资源根目录**（默认 `Assets/GameRes`，见 `ABBuildConfig.resRoot`）的路径去扩展名，如 `Hero/1001`
  - 包名 = 该资源的 AB 包名（=`assetBundleName`）
  - 资源名 = 包内资源名（文件名去扩展名）
- **它是"引导链的起点"**：表放在 `Resources` 里、路径写死（运行时 `"ResourceSystem/ResMap"` ↔ 编辑器 `ABBuildSetting.MapAssetPath` 是一对，改一个必须改另一个）——因为读表**不能依赖资源系统自身**（否则死循环）。
- **表缺失不崩**：返回空表 → 查不到映射 → 全部走 Resources 兜底。这也是"编辑器里一切正常、真机大面积失败"的经典成因。

## 4. 引用计数、分组与释放（写业务必须懂）

- 命中缓存 `RefCount++`；`Release` 归零后进 `_unused` **延迟释放**，由 `FlushUnused()` 统一卸（`Resources.UnloadUnusedAssets` 很贵，所以不能在 Release 里立刻做）。
- **失败句柄也入缓存**：避免每帧反复重试坏资源。
- **分组 `RevResGroup`（`Unknown/Battle/UI/Sound/Config/Scene`）只影响卸载，不影响加载**：
  - 归属**首次确定、永不覆盖**（`Unknown` 可被首次补齐，是唯一例外）；
  - 仅当**当前活动域启用 `TakeOverOwner` 且配置了非 `Unknown` 的 `Reclaim`**时，加载传 `Unknown` 才由 `EffectiveGroup` 自动补为 `RevScope.DefaultGroup`，对象池加载 prefab 也走这条漏斗。`RevScopeOptions.TakeOverOwner` 默认 `true`、可关闭；`Reclaim` 默认 `Unknown`，此时关域会跳过对象池 / 资源的按组回收。没有活动域、关闭兜底或未配置回收组时，传入 `Unknown` 不会因此获得明确分组；
  - 跨域共享需审查真实持有者与释放时机，明确 `Resident` / 分组 / `owner` 归属，不把所有资源强制绑到 `this`；`RevResInstanceFlag.Resident` 连 `force` 都不参与卸载；
  - `UnloadGroup(group, force: true)` 会把"引用计数 > 0"的一起清账 → **会误伤别的域的共享资源**，慎用。
- 切场景约定：`RevScene.AutoClearPool` 已自动清对象池；UI / 音效 / 资源分组要自己在 `RevScene.OnLoadStart` 里收（`RevUI.CloseAll` / `RevSound.StopAll` / `RevResManager.UnloadGroup`）。

## 5. 句柄（RevResHandle）

- 只暴露 `Content` / `Data` 两个属性（避免业务乱强转），外加：`IsLoaded`、`State`、`ContentType`、`ErrorReason`、`Group`、`RefCount`、`BundleName`。
- 失败原因仍以 `ErrorReason` 为准：`FileNotExist` / `BundleLoadFail` / `AssetLoadFail` / `TypeMismatch` 等。`RevABLoader` 在 Android 真机（`UNITY_ANDROID && !UNITY_EDITOR`）会用 tag **`Res`** 记录异步加载的主包 / 依赖包 / 目标包失败及解析后的来源路径；资源加载成功后还检查材质 Shader 缺失、错误 Shader / `isSupported`。这些诊断不改材质，不覆盖全部变体问题，不能替代失败句柄检查。
- 同步 `Load` 在"未缓存 + 真机 AB"下拿不到内容（返回未完成句柄）→ 真机首次一律 `LoadAsync`。
- **`Load<T>(...)` 失败时返回 `null`，返回值不带原因**（底层可能已有日志）。需要原因就用 **`LoadHandle<T>(...)`**（同样泛型、不写 `typeof`，但返回完整句柄）：

```csharp
RevResHandle h = RevResManager.LoadHandle<Sprite>(RevResPath.Data, "Hero_1001", RevResGroup.UI);
if (!h.IsLoaded) { RevLog.Error($"加载失败：{h.ErrorReason}", "Res"); }
```

  业务里**判失败看 `h.ErrorReason`，不要靠 `content == null` 猜**。路径拼错 / 根目录没配置 / 没打标记，都会表现为 null。
- 失败句柄在**调用方释放且引用数归零后**，下次 `Load` 允许重新尝试（热更补上文件后能恢复）；引用还没归零时一直拿到上次的错误。

## 6. AB 打包（编辑器工具侧）

- 菜单：`Revolution.Tools/资源/RevAB 打包工具`（6 页签：打包 / 分包浏览 / 依赖 / 体积 / 检查 / 快照）、`Revolution.Tools/资源/RevAB 分包浏览`。
- 产物：`AssetBundles/<平台名>/`（平台名规则见 `ABBuildSetting.GetPlatformName`：Standalone* → `PC`，其余 `target.ToString()`）；每个包一个 `<包名>.manifest`（含 CRC / Hash / 依赖）；另有 `BuildManifest.json`（框架自用：version / platform / buildTime / bundles）。
- 主包（AssetBundleManifest）名 = **产物目录名** = 平台名（运行时 `RevABLoader.MainName` 同源）。
- 分包方式（`ABBuildConfig.markMode`）：
  - `ScanExisting`（默认）：读资源 `.meta` 里已有的 `assetBundleName`，工具**绝不改标记**；
  - `AutoByFolder`：按 resRoot 下**顶层目录名**当包名重打（无子目录 → `default`）。
- 一键打包链：`Collect` → `Validate` → `ValidateAndroidShaders(collect, validate, target)`（`CanBuild=false` 就别打）→ `ABBuilderCore.Build(target)` → 依赖分析 → `WriteResMap` + `WriteBuildManifest` + 布局快照 → `ABResPathGenerator.Generate()`（生成 `RevResPath` 常量）→ `CopyToStreamingAssets` → `AssetDatabase.Refresh()`。
- 校验会拦：命名非法、逻辑路径冲突、同包重名、空包、**漏标**（resRoot 下有资源没有 AB 标记），以及 Android 显式材质 Shader 缺失 / 无可打包资产且未由 Player 包含（详见 `editor-tools.md` §2）。门禁不改 Graphics Settings，不强制把所有 Shader 加入 Always Included，也不替代变体裁剪与实际设备兼容验证。
- **「检查」页签的 ResMap 一致性检查**（2026-10-08 新增）：选平台一键核对磁盘上的 `ResMap.txt` 与 `AssetBundles/<平台>/` 产物——格式错误、重复逻辑名 / 包内资源名、映射指向不存在的包、与当前资源标记不一致，逐条列出（只读不改）。堵的是"编辑器直读看不出、真机才 `FileNotExist`"的表包不一致。**出包前跑一遍**。
- **打包选项**：`deterministic`（默认开，同内容同产物）、`stripUnityVersion`（默认关；开了就要记住"换 Unity 版本先强制重打"）；窗口里有对应勾选框。
- **AB 运行期健壮性**（`RevABLoader`）：失败重试 `MaxAttempts` / `RetryDelayMs`、超时兜底 `AttemptTimeoutSeconds`（WebGL 上 `UnityWebRequest.timeout` 不生效，框架改按帧轮询 + `Abort`）、引擎缓存标识钩子 `BundleCacheKeyResolver`。
- 「仅生成映射」的唯一实现是 `ABMapGenerator`（打包窗口与导表工具共用）。

## 7. 写业务的四条硬规则

1. **只写逻辑路径**：用 `RevResPath.*` 生成常量（`Assets/Revolution/Generation/RevResPath.cs`）或两段式参数；**禁止** `StreamingAssets` / `dataPath` / `Resources.Load` 自己拼路径。
2. **成对 Release**：`Load`/`LoadAsync` 每次都要有对应释放点（面板 `OnClose` / 状态退出 / 分组卸载）。
3. **真机首次一律异步**：`LoadAsync` + 回调（或 `await` 包装）；同步 `Load` 只用于"已缓存 / 编辑器直读"。
4. **新增资源要打 AB 标记并重生成 ResMap**：资源放进 resRoot（默认 `Assets/GameRes`）→ 设 `assetBundleName`（或用分包浏览页签）→ 跑打包 / "仅生成映射" → 提交 `ResMap.txt` 与 `RevResPath.cs`。

## 8. 排查顺序

1. 加载完成后检查 `handle.ErrorReason` 是否为 `None`；Android 还查 tag `Res` 的主包 / 依赖包 / 目标包来源与 Shader 诊断（不能省略失败句柄判断）；
2. `ResMap.txt` 里有没有这条逻辑名（没有 → 没打标记 / 没重生成表）；
3. 编辑器直读 vs AB 模式是否一致（`RevResBootstrap.UseABInEditor`）；
4. `RevResManager.CachedCount` / `GetRefCount(...)` 看引用计数是否归零（不归零就永远不卸）；
5. 打包工具的「检查」页签看漏标 / 依赖 / 体积。


## RevGame资源所有权与分包实践

按 [RevGame资源所有权与分包实践](revgame-engineering.md) 读取RevGame同类实现，核对版本、所有者、失败路径与已执行证据；不得把历史样本结果当当前项目通过。
