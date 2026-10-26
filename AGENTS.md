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

`Library/`、`Temp/`、`Logs/`、生成的 `*.csproj` / `*.sln`、外部 `AssetBundles/` 导出目录、安装包 —— 详见 `.gitignore`。美术资源、`Assets/`、`策划案/` 是**要**入库的。用户指定的 `Assets/StreamingAssets/` 平台资源允许入库。

## 战斗性能与模块目录（2026-10-18 起）

- 后续战斗修改先读 `RevGame_Client/文档/战斗性能优化规则/战斗性能优化规则.md`（同目录HTML为浅色交互阅读版）；先用固定输入测量，再优化，不承诺未经实测的零GC或手机帧率。
- 占格只通过 `BoardModel.Place/Move/Remove/Reset` 修改；气眼只在占格版本、加气或围困免疫变化时刷新。不能为了优化漏掉致死、补子、换位后的规则更新。
- 高频查询使用调用方自有缓冲区；AI一轮查询复用一张独立深快照，不共享全局可变草稿，不移动实战棋子来试算。
- 战斗视图/飘字/弹道走 `RevPool`；动画和异步回调必须有回收/过期检查。棋子用共享材质，禁止在每帧访问 `renderer.material`。
- 保留现有层级，层内按功能细分（如 `Battle/AI`、`Services/Battle/Preparation`、`View/Combat/Effects`、`UI/Panels/Battle`）；Editor仍统一放 `Assets/Editor/RevGame`。生成目录不擅自搬迁。
- AB配置通过项目 `GameResourceBuild.Configure` 串联框架工具；共享帧/Shader显式分包，重复依赖与循环依赖必须在构建前解决。
- 进入AB的项目Sprite图片必须通过 `GameSpriteAtlasBuild` 纳入唯一的Sprite Atlas V2；按实际使用范围/寿命区分常用与备用图集，禁止重新加载散图Texture2D。引擎生成字形纹理、内置UISprite和非Sprite采样纹理需按规范区分，不能错误打包。
- 图集通过 `GameSpriteAtlasService` + `RevResManager` 绑定；新代码不得逐棋子/逐帧GetSprite、克隆材质或绕开框架引用计数。必须验收真实AB、背景UV、动画与重复进出战斗。
- 核心类、接口契约、字段、方法和算法关键步骤写中文新手注释：说明用途、单位/范围、所有者、生命周期、失败路径及“为什么这样做”，不只逐句翻译代码。
- GPU实例化、动态合批、UGUI合批分别测量；不能把Shader实例化关键字或开启一个开关当作优化成功证据。禁止为合批破坏透明排序与玩法。
- 正式入口为 `Assets/Scenes/StartScene.unity`，大厅/选关留在StartScene，战斗在GameScene。跨场景只由GameFlow经RevScene异步交接，Bootstrap常驻且只初始化一次；过场面板必须在资源、背景、81格和六张手牌全部可画后淡出。
- `EnterBattle/ReturnHome` 返回true只代表接受请求，测试需等待 `IsTransitioning=false` 再核对Screen。资源变更必须冷启动真实AB验证UI，不能先加载全部图集再宣称首次白图已修复；新增回归入口为 `GameSceneTransitionChecks`。

## 编辑器自动化调用方式（2026-10-23 实测）

- 用户要求优先采用更快的通道。已有常驻MCP连接或需要连续多次操作时，优先复用同一个RevMCP stdio会话；只有一两条临时编辑器命令时，优先 `Revolution.RevCLI/rev.py --project <工程> --mode live call <真实动作ID>`，省去常驻服务的启动/握手成本。
- 不再逐条启动 `rev_mcp.py ... call editor ...` 作为常规调用方式：本次状态查询/AB扫描中，其耗时高于直接CLI和常驻MCP。短命令优先合并在一次工具调用或现有pipeline中，避免重复启动、重复读大清单；Unity资产修改、构建、Play仍串行执行。
- 会话首次使用、编译/域重载后按技能重新发现真实能力；不能为了速度跳过参数校验、业务断言、错误检查、资源依赖门禁或收尾。已有编辑器只用live，不另启batch Unity。
- 测量范围是调用通道开销；AB构建本体仍调用同一套Unity/Revolution工具，不能宣称切换CLI/MCP会同比缩短打包时间。数据与边界见 `RevGame_Client/文档/进度同步/CLI与MCP速度对比-2026-10-23.md`。

## 自动化报告约定（2026-10-26）

- 用户明确要求：测试报告不生成 ZIP 压缩包。文档目录保留 HTML 与必要截图/GIF，详细机器证据放忽略目录 `.codex/work/`；此要求优先于技能中的 ZIP 归档默认规则。
- `.codex/` 下所有内容都不得提交。
