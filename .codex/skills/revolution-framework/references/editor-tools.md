# 编辑器工具与菜单（一次找全）

> 全部菜单都挂在顶级菜单 `Revolution.Tools` 下。改代码前先确认"这件事有没有现成工具做"。

2026-10-22新增 `Revolution.Tools/AI/RevMCP 控制台`：显示工程/Play/GM、管理本机运行时服务、复制Agent配置。CLI用`editor.capabilities`与`editor.coverage`核对实际能力；当前20个菜单已映射。Editor场景/Inspector/资产/Prefab/测试/构建和游戏接口见`automation-integration.md`与RevMCP使用说明。

## 1. 菜单一览

| 菜单 | 用途 |
|---|---|
| `Revolution.Tools/资源/RevAB 打包工具` | **主工具，6 页签**：打包 / 分包浏览 / 依赖 / 体积 / 检查 / 快照。一键打包、"仅生成映射"都在这里 |
| `Revolution.Tools/资源/RevAB 分包浏览` | 直达「分包」页签：看包树、给资源打/改 AB 标记、Project 窗口行尾显示"属于哪个包" |
| `Revolution.Tools/资源/生成资源路径常量 (RevResPath)` | 重新生成 `Assets/Revolution/Generation/RevResPath.cs`（目录 → 常量） |
| `Revolution.Tools/资源/生成音效目录常量 (RevSoundPath)` | 重新生成 `RevSoundSystem/Generated/RevSoundPath.cs` |
| `Revolution.Tools/资源/AB 加载模式（编辑器）` | 勾选开关：编辑器里是否强制走 AB 模式（= `RevResBootstrap.UseABInEditor`，EditorPrefs 持久化） |
| `Revolution.Tools/GM 指令面板`（Ctrl+Shift+G） | 执行 GM 命令：联想、参数提示、**高危二次确认**、历史、返回值显示 |
| `Revolution.Tools/UI/面板代码生成器` | 选预制体 → 勾选控件与事件 → 生成唯一 `.cs`（只读预制体、**绝不覆盖业务代码**）；记住上次的输出目录 / 命名空间 / 层级 / 监听方式；事件按节点**名字**分发 |
| `Revolution.Tools/平台/生成 IL2CPP 裁剪保护 (link.xml)` | 扫 `RevUIPanel` / `RevUIPart` / `RevSingleton` 派生类 + 源码里用到的原生模块 → 生成业务侧 `Assets/link.xml`（已有手写文件弹窗确认，**不静默覆盖**）。主推关裁剪，万一要开时用 |
| `Revolution.Tools/平台/首包体积审计 (Resources)` | 列所有 `Resources` 目录的文件数与体积（按体积排序，含最占地方的 10 个文件），小游戏首包预算对照 |
| `Revolution.Tools/平台/包体大小预测` | 独立窗口的 Android APK / Windows 发布目录双页签；录入同配置的实测包体基准，扫描当前内置 `StreamingAssets` / `Resources` 原始文件差值，给出粗估范围。无基准不输出总包体；不会打包、切平台或修改资源。AAB/安装器/脚本与场景变化不在预测范围，最终以实际构建为准 |
| `Revolution.Tools/配置表/导表工具` | Excel → 数据（可只出数据 / 全量出代码+数据）；拖入即读、Excel 开着也能读、改表自动重读、只写有变化的文件 |
| `Revolution.Tools/配置表/快速导出（仅数据，不生成代码）` | 只改数值时用：**不触发脚本编译** |
| `Revolution.Tools/配置表/快速导出（用上次的 Excel 源）` | 用上次源直接重导 |

> 玩法数值**必须**经这个导表工具进游戏，而不是写在 C# 里；“什么该进表、怎么分表、怎么校验与验证”见 `playbooks.md` R22。Excel 来源请添加**整个配置表文件夹**而不是单个 xlsx，新增工作簿才不会被漏导。Unity 已占用工程时可用工程外的无头导出（链接 `RevExcelTool/Core/*.cs` + `RevDataTextFormat.cs`，做法见 R22 第 5 节）。
| `Revolution.Tools/热更新/热更清单窗口` | 生成 `RevHotManifest.txt` / `RevHotBuiltin.txt` + 自检（`Revolution.HotUpdate` 扩展包） |
| `Revolution.Tools/热更新/Demo/① 一键：打包 + 清单 + 装配本地 CDN` | 热更 demo：构建序号 +1 → 收集校验 → 打 AB → 写映射表/产物清单 → 拷首包 → 生成清单 → 装配 `{工程根}/LocalCDN`（内容先、清单最后） |
| `Revolution.Tools/热更新/Demo/②③④` | 打开本地 CDN 目录 / 清空客户端本地热更目录（验证回退出包基线）/ 打印该填的 URL |

**不在 Unity 菜单里的配套工具**（仓库根目录，独立 WPF 工程）：

| 工具 | 位置 | 用途 |
|---|---|---|
| 导表工具（WPF 版） | `Revolution.ExcelTool/` | 不依赖 Unity 的 Excel → C# 类 + 数据文件；与编辑器版**生成物逐字节一致**；有"仅生成数据 / 全量生成"两种模式 |
| 腾讯云 COS 热更发布工具 | `Revolution.Could/` | 选产物 → SHA-256 预检 → 按"内容先传、清单最后传"上传；同版本路径内容不同拒绝覆盖 |
| 导表 CI 入口 | `-executeMethod Revolution.Editor.ExcelTool.RevExcelCI.Export` | 无人值守导表 |

> 导表输出设置存 `ProjectSettings/`（团队共享），Excel 来源存 `UserSettings/`（个人）。配置表装载类已改名 **`DataLoad → RevDataLoad`**（导表模板已同步，重新导出一次即无缝衔接）；打包工具已更名 **`LiteAB → RevAB`**（日志 tag 改为 `RevAB`；静音过 `LiteAB` 的请改成 `RevLog.MuteTag("RevAB")`）。

## 2. 打包工具关键事实

- **产物目录**：`AssetBundles/<平台名>/`（`ABBuildSetting.GetOutputDir(target)`）；平台名规则：Standalone* → `PC`，其余 `target.ToString()`（Android / iOS / WebGL）。
- **产物内容**：各 `<包名>`（无扩展名的 AB）+ 每包一份 `<包名>.manifest`（CRC / Hash / 依赖）+ 主包（名 = 平台名）+ `BuildManifest.json`（version / platform / buildTime / bundles）。
- **配置资产**：`Assets/Editor/ABBuildConfig.asset`（`ABBuildConfig.Instance`）：
  - `resRoot`（资源根目录，默认 `Assets/GameRes`）——逻辑名就是"相对它的路径去扩展名"；
  - `markMode`（`ScanExisting` 读已有标记 / `AutoByFolder` 按顶层目录名分包）；
  - `copyToStreamingAssets` + `copyTarget`（打包后自动拷进 `Assets/StreamingAssets/<平台>`，首包基线就靠它）；
  - `version`（写进 `BuildManifest.json`）、`excludeFolders`、`excludeExtensions`、`checkUnmarkedAssets`、`compressMode`、`forceRebuild`、`cleanOutputBeforeBuild`。
- **一键打包链顺序**：`ABCollector.Collect()` → `ABValidator.Validate(collect)` → `ABValidator.ValidateAndroidShaders(collect, validate, target)`（`CanBuild == false` 就别打）→ `ABBuilderCore.Build(target)` → 依赖分析 → `ABManifestWriter.WriteResMap(collect)` + `WriteBuildManifest(build)` + 布局快照 → `ABResPathGenerator.Generate()` → `ABBuilderCore.CopyToStreamingAssets(outputDir)` → `AssetDatabase.Refresh()`。
- **Android Shader 前置门禁**（2026-10-16）：窗口 `ABBuildWindow` 与无头入口 `ABCIBuild` 均已调用 `ValidateAndroidShaders`。扫描资源及递归依赖中的 Material / Prefab / FBX 显式材质；Shader 引用缺失 / 错误 Shader，或无可打包资产路径且不在 `Graphics → Always Included Shaders` 时，报错阻断。`Assets/` / `Packages/` 下有资产路径的 Shader 可随材质作 AB 依赖，不要求所有 Shader 加 Always Included；默认 UGUI 未指定材质不误报。检查**只读、不改 Graphics Settings**，不证明变体未裁剪或 Android 设备支持，真机仍需验证。
- **无头构建入口已存在**：`-executeMethod Revolution.Editor.ABCIBuild.Build`（失败会以非 0 退出码结束进程），导表入口为 `Revolution.Editor.ExcelTool.RevExcelCI.Export`。入口可供 CI 调用，不代表仓库已部署编译 / 测试 / 真机质量 CI。

## 3. 生成物（**不要手改**）

2026-10-29：RevAB → 打包 → 代码生成新增 `RevResPath 生成路径`。填 `Assets` 下的目录或 `Assets/.../RevResPath.cs`，空值保持默认 `Assets/Revolution/Generation/RevResPath.cs`；“选择…”选择目录，“默认”清空配置。设置保存在 `ABBuildConfig.resPathCodePath`，菜单、打包、仅生成映射、自动重生成与 CLI 均读它。“打开 RevResPath.cs”也使用实际位置。生成时将带生成标记的旧文件移动并保留 GUID，不保留两份同名类；已有手写文件或新旧位置同时有文件时拒绝覆盖。自定义目录不能含 Editor，消费常量的 asmdef 要引用新位置所属程序集。RevSoundPath 保持原生成位置与接线。

AI 可通过 `ab.configure` 的 `configJson` 设置 `resPathCodePath`，然后 `ab.res-paths` 生成；留空恢复默认。输出路径是 C# 文件位置，资源根目录 resRoot 是扫描资源位置，两个概念不能混用。

| 文件 | 谁生成 | 作用 |
|---|---|---|
| `Assets/Revolution/Resources/ResourceSystem/ResMap.txt` | 打包工具「仅生成映射」/ 一键打包 | 逻辑名 → 包名 + 资源名（运行时引导表，随包体进 Resources） |
| `Assets/Revolution/Generation/RevResPath.cs` | 资源路径常量生成器 | 资源目录常量（业务写 `RevResPath.Data` 而不是手拼字符串） |
| `Assets/Revolution/Runtime/RevSoundSystem/Generated/RevSoundPath.cs` | 音效目录常量生成器 | 音效目录常量 |
| `Assets/link.xml`（业务侧） | 菜单「生成 IL2CPP 裁剪保护 (link.xml)」 | 开裁剪时的业务类型 / 原生模块保护；带"生成标记"，已有手写文件时弹窗确认。框架自带的 `Assets/Revolution/link.xml` 是手写的，**不是**生成物 |
| `AssetBundles/<平台>/BuildManifest.json` | 打包链 | 版本 / 平台 / 时间 / 各包体积 |

> UPM 只读安装时这些位置写不进去，框架会**跳过生成并提示**（`ABBuildSetting.EnsureWritableForGeneratedCode`）。

## 4. 自动化（GitHub Actions，仓库 `main` 上）

| 工作流 | 触发（改动路径） | 产出 |
|---|---|---|
| `sync-package-branch.yml` | `Assets/Revolution/**` | `package` 分支 = 框架本体（分支根即包根，可 clone / 子模块 / UPM git URL） |
| `sync-demo-branch.yml` | `Assets/Revolution.Demo/**` | `demo` 分支 = 示例工程（18 个可运行场景） |
| `sync-hotupdate-branch.yml` | `Assets/Revolution.HotUpdate/**` | `hotupdate` 分支 = 热更扩展包 |
| `sync-skill-branch.yml` | `Skills/**` | `skill` 分支 = 给 AI Agent 用的技能包（`git clone -b skill ... .codebuddy/skills`） |
| `pages.yml` | `Revolution.Document/**` | 文档站（https://yokino337088.github.io/Revolution/ ） |

**生成分支都是"整份重建、不保留历史"**（上表四条：`package` / `demo` / `hotupdate` / `skill`）：改代码请改 `main`，不要往生成分支上提交。

上表是同步 / 文档发布，不是编译或测试质量门禁。AB / Excel 的 `-executeMethod` 入口已存在，但质量 CI 尚未完成；未完成的 `.github/automation/` 草稿不得写成已上线流水线，也不要执行草稿中的命令来证明能力。

## 5. demo 场景（18 个）

`Assets/Revolution.Demo/<模块>.Demo/<模块>Demo.unity`，索引在 `Assets/Revolution.Demo/RevDemoSceneList.txt` 与 `Assets/Revolution.Demo/README.md`。
每个场景：打开 → Play → 左侧按钮逐个点，右侧是步骤日志（讲清每一步发生了什么）。
写新 demo 照 `templates/RevXxxDemo.cs.txt` 的骨架（OnGUI：左按钮 + 右日志 + 简单样式）。


## RevGame编辑器制作与操作参考

按 [RevGame编辑器制作与操作参考](revgame-editor-practice.md) 读取RevGame同类实现，核对版本、所有者、失败路径与已执行证据；不得把历史样本结果当当前项目通过。
