# RevGame_Server · Fantasy 服务端

本目录是 RevGame 的服务端，基于 [Fantasy](https://github.com/qq362946/Fantasy)（零反射、源生成注册、支持 Native AOT 的 C# 分布式服务器框架）。
**当前只做到框架接入**：工程能编译、客户端能拿到包、协议与配置工具就位，业务逻辑还没写。

- 客户端在 [`../RevGame_Client`](../RevGame_Client)；
- 玩法见 [`../RevGame_Client/策划案/玩法拓展策划案.html`](../RevGame_Client/策划案/玩法拓展策划案.html)；
- 项目第一目的仍是验证 [Revolution](https://github.com/Yokino337088/Revolution) 框架可行性（见根目录 README）。

---

## 一、目录怎么分（按功能）

```text
RevGame_Server/
├─ RevGame.Server/                    ← Fantasy CLI 生成的工程根（相对路径被工具硬编码，别乱搬）
│  ├─ Client/
│  │  └─ Unity/                        客户端侧产物：导出的协议代码、客户端接入说明落在这里
│  ├─ Config/                          部署 / 环境配置：Control Center 地址、多机多进程拓扑、本地化覆盖
│  ├─ Server/                          服务端程序集（解决方案本体）
│  │  ├─ Server.sln
│  │  ├─ Main/                           进程入口：AssemblyHelper → Fantasy Entry.Start()
│  │  ├─ Entity/                         业务实体与组件（含 Fantasy.config / Fantasy.xsd）
│  │  └─ Hotfix/                         热更逻辑层，引用 Entity
│  └─ Tools/                           官方工具（由 fantasy add 提取，随仓库保存）
│     ├─ NetworkProtocol/                 协议定义：Inner/Outer 的 .proto、OpCode.Cache、路由与漫游类型
│     ├─ ProtocolExportTool/              协议导出器（dotnet Fantasy.ProtocolExportTool.dll export）
│     ├─ NLog/                            NLog 日志配置
│     ├─ Fantasy.Net/                     框架源码镜像（备查；实际依赖走 NuGet）
│     └─ Fantasy.Unity/                   Unity 包镜像（实际安装走 UPM git 依赖）
└─ .gitignore                          bin/obj、IDE 缓存、日志不入库
```

**分工原则**：`Server/` 只放会被编译进服务端的代码；`Tools/` 只放工具与协议定义；`Client/` 只放给客户端的东西；
`Config/` 只放"换环境要改"的配置。业务模块以后建在 `Server/Entity`（数据与组件）与 `Server/Hotfix`（逻辑）里，不新开平行目录。

## 二、已完成的接入项

| 项 | 结果 |
|---|---|
| CLI 工具 | `dotnet tool install -g Fantasy.Cli` → `fantasy` 命令可用，版本 `2026.1.2003` |
| 服务端工程 | `fantasy init -n RevGame.Server` 生成，目标框架 **net8.0**，三程序集 Main / Entity / Hotfix |
| 官方组件 | `fantasy add` 提取 NetworkProtocol、ProtocolExportTool、NLog、Fantasy.Net、Fantasy.Unity |
| 启服配置 | `Server/Entity/Fantasy.config` + `Fantasy.xsd` 自动生成（单机单进程单 World 模板，注释完整） |
| Unity 客户端包 | `Packages/manifest.json` 增加 `com.fantasy.unity`，UPM git 依赖方式引入，锁定 tag `2026.1.2003` |
| Unity 解析结果 | Unity 2022.3.15f1c1 已解析成功并写入 `packages-lock.json`，编译无报错 |
| AI 技能包 | 官方 `Skills/fantasy-net` 复制到仓库 `.codex/skills/fantasy-net`（89 个文件） |
| 编译验证 | `dotnet build Server/Server.sln` → 0 警告 0 错误 |

> Unity 侧安装方式（已写进 `manifest.json`，无需手动操作）：
> ```
> "com.fantasy.unity": "https://github.com/qq362946/Fantasy.git?path=/Fantasy.Packages/Fantasy.Unity#2026.1.2003"
> ```
> 该包依赖 `com.unity.nuget.newtonsoft-json`，由 UPM 自动补齐。

## 三、常用命令

```powershell
cd RevGame_Server/RevGame.Server

# 编译
dotnet build Server/Server.sln

# 启动服务器（读 Server/Entity/Fantasy.config）
# launchSettings 已带 --m Develop；直接跑 dll 时必须自己补参数，否则报 "Command line format error!"
dotnet run --project Server/Main/Main.csproj
dotnet Server/Main/bin/Debug/net8.0/Main.dll --m Develop

# 导出协议（改完 Tools/NetworkProtocol 下的 .proto 后执行）
Tools/ProtocolExportTool/Run.bat
```

CLI 的两个坑（脚本化时必看）：

```powershell
# 1) 不设语言环境变量会弹交互式选择，非交互终端直接抛异常
$env:FANTASY_CLI_LANG="Chinese"

# 2) init 默认走交互向导，纯参数模式必须显式关掉
fantasy init -n RevGame.Server -p . --interactive false
```

## 四、已知限制（接入阶段，未做业务）

1. **启动必须带 `-m` 参数**：`dotnet Main.dll` 不给参数会直接抛 `Command line format error!`（`--m/--RuntimeMode` 是必填项）。
   开发期用 `--m Develop`（启动配置表里的全部 Process），发布期用 `--m Release --pid <进程号>` 只启动指定进程。
   本机已装 **.NET 8 SDK 8.0.426 / 运行时 8.0.32 + ASP.NET Core 8.0.32**，`--m Develop` 实测启动成功：
   `Process:1 Startup Complete SceneCount:1`，Scene 1001 监听 Inner TCP `127.0.0.1:11002`、Outer KCP `127.0.0.1:20000`。
2. **Unity 版本低于官方声明**：Fantasy 文档写 Unity 2022.3.62+，本工程是 2022.3.15f1c1。本次只验证"包能解析"，
   编译与连接实测待确认；若后续出现 API 缺失，优先怀疑这里。
3. **`Fantasy-Net` 版本是浮动的**：`Entity.csproj` 里是 `Version="*"`，每次还原可能拿到不同版本。
   接入业务前建议锁定为具体版本号并提交。
4. **`Tools/Fantasy.Net` 是框架源码镜像**：包含 `bin/obj/.idea` 等开发产物，仅作离线查阅；
   实际依赖来自 NuGet，不参与编译。
5. **协议与客户端尚未打通**：协议导出后要把生成代码放进 Unity 工程（`Client/Unity` 为落点备忘），
   还没做客户端连接、会话与登录流程。
