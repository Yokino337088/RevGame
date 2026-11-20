# RevGame 专项约定

## 工程事实

- Unity **2022.3.15f1c1**，工程在 `RevGame_Client/UnityProject/RevGame.Unity`；
- 框架本体是**快照**：`Assets/Revolution/`（来自 Revolution 仓库的 `package` 分支）。**要改框架请回 Revolution 仓库改 `main` 再重新导出**，不要在这里顺手改；
- 本仓库**没有** `Assets/Revolution.Demo/`（18 个 Demo 场景在 Revolution 仓库里）——技能里"抄哪个 Demo"的指引只作**写法参考**，不要去找这些路径；
- 启动入口只调一次 `RevInitialize.Initialize()`，其它模块（`RevEvent` / `RevTimer` / `RevUI` / `RevPool` …）已自动就位，重复初始化会清监听。

## 硬约定（违反即返工）

- 业务只碰门面（`RevUI` / `RevTimer` / `RevEvent` / `RevResManager` / `RevPool` / `RevScope` / `RevTask` …），不碰 `Core/` `Implementation/`；
- 注释中文、方法体必带大括号、方法签名的 `(` 与参数不换行。详见 [代码与新手注释守则](code-and-comments.md)。
- 资源只用逻辑路径（`RevResPath.*` 常量或两段式 `Load(根目录, 资源名, 类型, 分组)`），**禁止**自己拼 `StreamingAssets` / `Application.dataPath`；
- 异步只用 `RevTask`（不引 `System.Threading.Tasks` / 第三方异步库）；
- 面板里不直接 `Destroy(gameObject)`，关自己用 `CloseSelf()`；
- UI = 面板类 + 职责钩子（`OnBindView` 只装配 / `OnRefreshView` 只落屏 / 交互回调只转发）。

## 面板归属与切场

- `GameFlow` 持有各屏的域句柄，场景交割时退出旧域。旧域的活动面板、退场中的面板和空闲缓存同步归还租约，PvP 立绘在面板 `OnClose` 归还。显式解绑的过场遮挡跟随交割流程，流程关窗后销毁。
- 采用 [域内面板缓存](../../revolution-framework/references/large-ui.md#域内面板缓存) 的额度和诊断。开发态 `pvp.ui-cache-check` 可用真实 AB 执行每批 1 至 5 轮大厅进出，并用 `boundaries: true` 加验异域、退场动画和低内存。验收核对 `ui.cache-state`、专用包卸载、共享图集和大厅实际显示。

## 条件分支排版（2026-10-30用户要求）

项目业务代码所有 `if` / `else if` / `else` 分支必须展开：条件独占一行，左大括号独占下一行，代码体缩进，右大括号独占一行。禁止 `if (...) { return; }`。保留类、方法、属性的大括号独占一行，不可为了展开if将其它大括号合到行尾。此要求优先于技能中允许单行if的旧示例。

## 不要提交

`Library/`、`Temp/`、`Logs/`、生成的 `*.csproj` / `*.sln`、外部 `AssetBundles/` 导出目录、安装包 —— 详见 `.gitignore`。美术资源、`Assets/`、`策划案/` 是**要**入库的。用户指定的 `Assets/StreamingAssets/` 平台资源允许入库。

## 战斗性能与模块目录（2026-10-18 起）

- 后续战斗修改先读 `RevGame_Client/文档/战斗性能优化规则/战斗性能优化规则.html`（同目录HTML为浅色交互阅读版）；先用固定输入测量，再优化，不承诺未经实测的零GC或手机帧率。
- 占格只通过 `BoardModel.Place/Move/Remove/Reset` 修改；气眼只在占格版本、加气或围困免疫变化时刷新。不能为了优化漏掉致死、补子、换位后的规则更新。
- 高频查询使用调用方自有缓冲区；AI一轮查询复用一张独立深快照，不共享全局可变草稿，不移动实战棋子来试算。
- 战斗视图/飘字/弹道走 `RevPool`；动画和异步回调必须有回收/过期检查。棋子用共享材质，禁止在每帧访问 `renderer.material`。
- 保留现有层级，层内按功能细分（如 `Battle/AI`、`Services/Battle/Preparation`、`View/Combat/Effects`、`UI/Panels/Battle`）；Editor仍统一放 `Assets/Editor/RevGame`。生成目录不擅自搬迁。
- 项目自有材质资产统一放在 `Assets/GameRes/Material`，按模块建子目录；Shader 放在 `Assets/GameShader`。预制体和场景通过 GUID 引用材质，AB 归属按资源实际使用范围和寿命配置。
- AB配置通过项目 `GameResourceBuild.Configure` 串联框架工具；共享帧/Shader显式分包，重复依赖与循环依赖必须在构建前解决。
- 进入AB的项目Sprite图片必须通过 `GameSpriteAtlasBuild` 纳入唯一的Sprite Atlas V2；按实际使用范围/寿命区分常用与备用图集，禁止重新加载散图Texture2D。引擎生成字形纹理、内置UISprite和非Sprite采样纹理需按规范区分，不能错误打包。
- 图集通过 `GameSpriteAtlasService` + `RevResManager` 绑定；新代码不得逐棋子/逐帧GetSprite、克隆材质或绕开框架引用计数。必须验收真实AB、背景UV、动画与重复进出战斗。
- 核心类、接口契约、字段、方法和算法关键步骤写中文新手注释：说明用途、单位/范围、所有者、生命周期、失败路径及“为什么这样做”，不只逐句翻译代码。
- GPU实例化、动态合批、UGUI合批分别测量；不能把Shader实例化关键字或开启一个开关当作优化成功证据。禁止为合批破坏透明排序与玩法。
- 正式入口为 `Assets/Scenes/StartScene.unity`，大厅/选关留在StartScene，战斗在GameScene。跨场景只由GameFlow经RevScene异步交接，Bootstrap常驻且只初始化一次；过场面板必须在资源、背景、81格和六张手牌全部可画后淡出。
- `EnterBattle/ReturnHome` 返回true只代表接受请求，测试需等待 `IsTransitioning=false` 再核对Screen。资源变更必须冷启动真实AB验证UI，不能先加载全部图集再宣称首次白图已修复；新增回归入口为 `GameSceneTransitionChecks`。

## 功能子目录（2026-10-30）

同一层中不同演员/功能必须分目录：Battle/AI 按 Boss、Enemy、Helper、Player、Formation、Shared；Services/Battle/AI按角色，Diagnostics按Boss/Helper/Performance/Balance等用途；战斗UI按Hud/Boss/Preparation/Results，棋盘视图按Cells/Boss/Feedback/Helper/Magic。partial文件可按职责归档，类型与命名空间不随目录强制重命名。移动Unity资产必须连同原.meta保留GUID；生成目录与框架快照不随意搬迁。

## UI素材约定（2026-11-01用户要求）

- 新增和重制UI预制体必须使用项目已有UI图片，禁止无Sprite的白膜底板/按钮。优先对照原预制体，正确裁切Sprite并设置九宫格border，适用底板使用Image.Type.Sliced。
- UI Sprite必须纳入唯一Sprite Atlas V2；按大厅、章节共享/章节专属、无尽模式划分面板和图片生命周期，禁止所有面板塞入单一AB。
- 无尽采用用户提供的古希腊神殿原画和不透明象牙金棋盘；长BGM逐曲独立AB，临近结尾异步预载下一曲，结束后释放上一曲。
