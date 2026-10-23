# 文档地图（权威文档在哪）

> **项目业务架构先读《Revolution 官方编程架构规范》**，再读对应模块《使用说明》；动框架设计前读《架构解析》。本 skill 只是索引；文档与源码冲突时以当前源码核实，技术方案未来目标不代表已实现。
> 文档根目录：`<仓库根>/Revolution.Document/`；在线站：https://yokino337088.github.io/Revolution/

## 1. 文档结构

```text
Revolution.Document/
├── index.html                 ← 文档站首页（带搜索；改文档后同步这里才会被站点收录）
├── README.md                  ← 文档目录（模块表 + 扩展包表）
├── 预览文档.cmd               ← 本地预览（等价在线站）
├── <模块>/
│   ├── <模块>使用说明.md / .html        ← 手把手：怎么用、边界、FAQ
│   └── <模块>架构解析.md / .html        ← 设计论证：为什么这么写、失败怎么收场
└── assets/                    ← 站点样式资源
```

- **md 与 html 都要改**（md 是权威版，html 是站点用的那版）；改完记得看 `index.html` 是否需要收录新页面。
- 文档写作口径：`> [!NOTE]` / `> [!WARNING]` 这类标注在 md 里保留；表格、代码块、目录锚点与现有文档一致。

## 2. 各模块文档（按模块找）

| 模块（门面） | 文档 |
|---|---|
| 启动 `RevInitialize` | `一键初始化/一键初始化说明.html`（**仅 html，无 md**） |
| 应用生命周期 `RevAppLifecycle` | `应用生命周期/应用生命周期使用说明.md`（md + html；暂无 Demo） |
| 代码裁剪 `RevStripGuard` / `link.xml` | `代码裁剪/代码裁剪使用说明.md`（含"代价怎么自己测"与症状反查表） |
| 日志 `RevLog` | `日志系统/日志系统使用说明.md`（另有模块 README：`Runtime/RevLog/README.md`） |
| 计时器 `RevTimer` | `计时器系统/计时器系统使用说明.md` · README：`Runtime/RevTimer/README.md` |
| 公共 Mono `RevMono` | `公共Mono模块/公共Mono模块使用说明.md` |
| 对象池 `RevPool` / `RevRefPool` | `对象池/对象池使用说明.md`、`对象池/对象池架构解析.md` |
| 事件 `RevEvent` | `事件系统/事件系统使用说明.md`、`事件系统/事件系统架构解析.md` |
| 服务定位器 `RevServices` / `RevServiceLocator` | `服务定位器/服务定位器使用说明.md`（第五章专讲"为什么用服务定位器而不是 DI 容器"；上手只读 `Facade/RevServices.cs`）、`服务定位器/依赖注入vs服务定位器.html`（构造传参 vs 定位器） |
| 异步 `RevTask` | **暂无专章**（看 `RevTask.Demo` 场景 + 源码注释 + 本 skill 的 `modules-core.md`） |
| 场景 `RevScene` | `场景系统/场景系统使用说明.md`、`场景系统/场景系统架构解析.md` |
| 输入 `RevInput` | `输入系统/输入系统使用说明.md`、`输入系统/输入系统架构解析.md` · README：`Runtime/RevInput/README.md` |
| 音效 `RevSound` | `音效系统/音效系统使用说明.md` · README：`Runtime/RevSoundSystem/README.md` |
| 状态机（三件套） | `状态机/状态机使用说明.md`、`状态机架构解析.md`、`状态机Demo示例讲解.md` |
| 资源 `RevResManager` | `资源加载系统/资源加载系统使用说明.md`、`资源加载系统架构解析.md` |
| 单例 `RevSingleton*` | **暂无专章**（相关：`公共Mono模块`、`服务定位器`） |
| UI `RevUI` / `RevUIPanel` | `UI系统/UI系统使用说明.md`、`UI系统/UI系统架构解析.md`（**第七章**：要不要做 System / BusinessLogic） |
| 作用域 / 业务单元 `RevScope` / `RevLogic` | `作用域系统/作用域系统使用说明.md`（第四节 RevLogic、第六节状态机联动；md + html）；实现归于 `Runtime/RevScope/`，不要把忽略入库的《设计稿》当已发布文档。示例是 `Assets/Revolution.Demo/RevScope.Demo/RevScopeFlowDemo.cs`，无独立 `.unity` 场景 |
| 动作序列 `RevSequence` | `动作序列/动作序列使用说明.md`、`动作序列架构解析.md` · README：`Runtime/RevActionSequence/README.md` |
| GM 指令 `RevGM` | `GM指令/GM指令使用说明.md` · README：`Runtime/RevGMCommand/README.md` |
| 配置表 `RevDataTableManager` | `导表工具/导表工具使用说明.md`、`导表工具架构解析.md`、`导表工具对比与设计说明.md` |
| 热更（扩展包）`RevHotUpdate` | `热更新/RevHotUpdate使用说明.md`、`RevHotUpdate架构解析.md`、`RevHotUpdate技术方案.md` · README：`Assets/Revolution.HotUpdate/README.md` + `Assets/Revolution.Demo/RevHotUpdate.Demo/README.md` |

**有模块级 README 的只有 6 个**：`RevLog`、`RevTimer`、`RevInput`、`RevSoundSystem`、`RevActionSequence`、`RevGMCommand`（其余模块的说明只在 `Revolution.Document` 下；`RevServiceLocator` 的 README 已删除，以《使用说明》与 `Facade/RevServices.cs` 文件头为准）。

**模块文档之外的"全局文档"**（找背景 / 现状 / 决策时读）：

| 文档 | 位置 | 内容 |
|---|---|---|
| 官方编程架构规范 | `Revolution.Document/编程范式/Revolution官方编程架构规范.md`（同名 `.html`） | 项目开发优先读：门面复用、FSM × RevScope 统一流程、GM 测试接线与 MCP 现状/安全边界 |
| 更新日志 | `Assets/Revolution/CHANGELOG.md` | "未发布"段按 新增 / 修复 / 变更 分类；**动框架前先扫一遍** |
| Bug 修复日志 | `Revolution.Document/bug修复日志/` | 按模块记录成因与修复说明；并非每条都有持久、可重复执行的测试证据，需另核对测试文件、命令和运行结果 |
| 投产评估 | `Revolution.Document/Revolution 投产评估（安卓 + 微信抖音小游戏）.md` | 平台就绪度、P0/P1/P2 修复状态（哪些**已修**、哪些**未做**） |
| 框架总览 | 仓库根 `README.md` | 模块表 / 规模统计 / 热更决策 / 工程外验证 / 出包设置建议 |
| Demo 索引 | `Assets/Revolution.Demo/README.md` + `RevDemoSceneList.txt` | 18 个可运行场景 |
| 在线文档站 | https://yokino337088.github.io/Revolution/ | 所有模块一页看全，带搜索与阅读顺序建议 |

## 3. 仓库与安装（交付面）

```text
仓库：https://github.com/<owner>/Revolution         （main 是唯一开发分支）
生成分支（整份重建、不要在上面提交）：
  package    ← Assets/Revolution/**            框架本体
  demo       ← Assets/Revolution.Demo/**       示例工程（18 个可运行场景）
  hotupdate  ← Assets/Revolution.HotUpdate/**  资源热更扩展包
  skill      ← Skills/**                       给 AI Agent 用的技能包（克隆进 .codebuddy/skills 等目录即可）

工程内安装（两种都行）：
  git clone -b package   https://github.com/Yokino337088/Revolution.git Assets/Revolution
  git clone -b demo      https://github.com/Yokino337088/Revolution.git Assets/Revolution.Demo
  git clone -b hotupdate https://github.com/Yokino337088/Revolution.git Assets/Revolution.HotUpdate
（也可以子模块 / UPM git URL；框架本体零第三方依赖）

UPM 安装注意：包目录只读时，`RevResPath.cs` / `ResMap.txt` / 音效路径常量这类**生成物写不进去**，
工具会跳过并提示（见 `ABBuildSetting.EnsureWritableForGeneratedCode`）。
```

## 4. 目录速览（工程内）

```text
RevolutionFrameWork_Unity/Assets/
├── Revolution/                 框架本体（Runtime 模块目录含 RevScope + Editor 工具 + Generation/Resources + link.xml + CHANGELOG.md）
│   ├── Runtime/<模块>/{Core,Facade,Implementation,Interfaces,Support}   （RevInitialize / RevStripGuard 等小模块是单文件，无分层目录）
│   ├── Editor/RevResourceSystem/ABTool/…  RevAB 打包工具（Core/Pipeline/CodeGen/Snapshot/Integration/Window 六个子目录）
│   ├── Editor/RevExcelTool/…              导表工具（编辑器版）
│   ├── Editor/RevPlatform/…               平台工具（link.xml 生成 / 首包体积审计）
│   ├── Editor/RevUISystem/…               UI 面板代码生成器
│   ├── Editor/RevGMCommand/…              GM 指令面板
│   ├── Generation/RevResPath.cs           生成的资源路径常量（勿手改）
│   └── Resources/ResourceSystem/ResMap.txt 引导映射表（勿手改）
├── Revolution.HotUpdate/       扩展包：资源热更（独立程序集，可整体删除）
├── Revolution.Demo/            示例工程（现有独立场景 + RevScope.Demo 仅脚本示例）
├── GameRes/                    资源根目录（默认 resRoot，逻辑名相对它计算）
└── StreamingAssets/<平台>/      打包产物拷贝目标（首包基线；由打包工具自动拷）

<仓库根>/Revolution.Document/   文档（md 权威 + html 站点）
<仓库根>/Revolution.ExcelTool/  导表工具（WPF 版，独立工程）
<仓库根>/Revolution.Could/      腾讯云 COS 热更发布工具（WPF，独立工程）
<仓库根>/Skills/                给 AI Agent 用的技能包（本文件所在处）
<仓库根>/RevolutionFrameWork_Unity/Tests/RevHotUpdate.Offline/  持久 net10 控制台清单断言（非 Unity 场景测试）
<仓库根>/README.md              框架总览（模块表 / 工程外验证 / 热更决策）
```

## 5. 找不到文档时

1. 先看模块的 `Facade/` 文件头注释（框架要求每个文件开头写清"为什么这么写"）；
2. 再看对应的 demo 场景脚本（`Assets/Revolution.Demo/<模块>.Demo/*.cs`）——**它能编译、能运行，是最可信的用法示例**；
3. 然后是本 skill 的 `references/modules-core.md` / `modules-content.md`；
4. 最后才是读内核（`Core` / `Implementation`）——**写业务时不需要读内核**，读了反而容易越界。
