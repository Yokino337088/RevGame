# AGENTS.md · RevGame（围棋史莱姆）

本仓库是 **RevGame** —— 一款 2D 小游戏，**第一目的是验证 [Revolution](https://github.com/Yokino337088/Revolution) 框架在真实业务中的可行性**，不是商业项目。游戏好玩是加分项，**框架可行性结论才是第一交付物**。

## 开工前先读

| 要做什么 | 先读什么 |
|---|---|
| 写任何玩法 / UI / 流程代码 | Revolution 框架技能 —— 本机若已装入 `.codex/skills/revolution-framework/SKILL.md` 就读本地那份；否则读源仓库 [Revolution](https://github.com/Yokino337088/Revolution) 的 `Skills/revolution-framework/`。按它的「30 秒分诊」只读命中的那 1 篇 `references/`，**不要通读内核** |
| 对玩法有疑问 | `RevGame_Client/策划案/玩法拓展策划案.html`（v18，权威） |
| 不确定某个 API 存不存在 | 技能 `references/decision-guide.md` §2「不存在 / 不做」清单，**不要凭印象写** |

## 工程事实

- Unity **2022.3.15f1c1**，工程在 `RevGame_Client/UnityProject/RevGame.Unity`；
- 框架本体是**快照**：`Assets/Revolution/`（来自 Revolution 仓库的 `package` 分支）。**要改框架请回 Revolution 仓库改 `main` 再重新导出**，不要在这里顺手改；
- 本仓库**没有** `Assets/Revolution.Demo/`（18 个 Demo 场景在 Revolution 仓库里）——技能里"抄哪个 Demo"的指引只作**写法参考**，不要去找这些路径；
- 启动入口只调一次 `RevInitialize.Initialize()`，其它模块（`RevEvent` / `RevTimer` / `RevUI` / `RevPool` …）已自动就位，重复初始化会清监听。

## 硬约定（违反即返工）

- 业务只碰门面（`RevUI` / `RevTimer` / `RevEvent` / `RevResManager` / `RevPool` / `RevScope` / `RevTask` …），不碰 `Core/` `Implementation/`；
- 注释中文、方法体必带大括号、方法签名的 `(` 与参数不换行（详见 `references/code-style.md`）；
- 资源只用逻辑路径（`RevResPath.*` 常量或两段式 `Load(根目录, 资源名, 类型, 分组)`），**禁止**自己拼 `StreamingAssets` / `Application.dataPath`；
- 异步只用 `RevTask`（不引 `System.Threading.Tasks` / 第三方异步库）；
- 面板里不直接 `Destroy(gameObject)`，关自己用 `CloseSelf()`；
- UI = 面板类 + 职责钩子（`OnBindView` 只装配 / `OnRefreshView` 只落屏 / 交互回调只转发）。

## 不要提交

`Library/`、`Temp/`、`Logs/`、生成的 `*.csproj` / `*.sln`、`AssetBundles/`、打包产物 —— 详见 `.gitignore`。美术资源、`Assets/`、`策划案/` 是**要**入库的。
