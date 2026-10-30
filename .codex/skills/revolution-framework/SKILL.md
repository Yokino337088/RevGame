---
name: revolution-framework
description: 本指南适用于 Revolution Unity 游戏框架业务开发、官方编程架构规范、架构选型、排错与代码审查。优先复用 Rev 前缀门面；用 FSM + RevScope / RevLogic 管流程和生命周期，在关键业务注册受控 GM 或 RevRuntimeAutomation 能力供 RevMCP/AI 验证。涉及 Assets/Revolution、玩法、GM、资源/UI、Unity平台、文档或技能包同步时使用；一句话实际测试联合 revolution-ai-automation，先核对目标项目安装的版本和真实能力。
---

# Revolution 框架开发 Skill

性能诊断/优化/缓存与收益验收联合使用 `revolution-performance`（源包：[性能技能](../revolution-performance/SKILL.md)）。按热点只读对应专题，先测固定输入、保持规则与画面，再核对局部/整帧与目标设备结果；本技能继续约束门面/API/架构。

Revolution 是一套**从零手写、可读可测、无魔法**的 Unity 游戏框架（C#）。2026-10-22 新增开发态 `RevAutomation` 模块与 `RevRuntimeAutomation` 门面，Runtime 目录由历史 21 个增为 22 个；下表保留基础模块索引。
2026-10-16 已跟踪源码快照：Runtime **199** 个 `.cs`、Editor **44** 个 `.cs`、独立扩展包 `RevHotUpdate` **20** 个 `.cs`；Demo 仍为 **18 个 `.unity` 场景**，新增 `RevScopeFlowDemo.cs` 挂任意场景运行，不计作独立场景。

> 按需读取 `Facade/` 文件头与对应 Demo；不要为了了解框架通读内核。
> **以当前源码和 Git 跟踪清单为准**：README、历史测试数字与设计方案可能滞后；不得把方案或未提交自动化草稿当作已落地能力。

**项目业务默认遵循[《Revolution 官方编程架构规范》](https://github.com/Yokino337088/Revolution/blob/main/Revolution.Document/编程范式/Revolution官方编程架构规范.md)**（源仓库位置：`Revolution.Document/编程范式/Revolution官方编程架构规范.md`）。常用功能使用现有框架入口；一个业务流程控制者通过 FSM + `RevScope` 调度登录/大厅/战斗/结算/退出；关键业务在开发/测试环境留 GM 查询、动作和失败接口，供项目安全适配 MCP/AI 后自动验证。Skill 的速查表是索引，规范和当前源码共同约束业务实现；独立安装的 `skill` 分支也可访问此在线文档。

它的三条设计取向直接决定你该怎么写代码：

1. **模块化、门面唯一**：每个模块只暴露一个门面（`RevTimer` / `RevResManager` / `RevUI` …），业务只碰门面，内核（`Core` / `Implementation`）不改也不读。
2. **失败必须可见**：不吞异常、不静默 return —— 失败一律给原因枚举 / 句柄字段 / 返回值，日志有唯一出口 `RevLog`。
3. **内核尽量不依赖引擎**：路径、句柄、槽位表、池引擎、日志/计时器内核是纯 C#，**能链接进普通 .NET 工程跑断言**（见 `references/verify-and-test.md`）。

## 真实工程实践：写业务前必须参考RevGame

游戏架构、玩法/UI/流程、资源与测试接线以 [RevGame GitHub工程](https://github.com/Yokino337088/RevGame) 为真实实践参考。编码前按 [工程实践索引](references/revgame-engineering.md) 找到并阅读同类源码，记录版本、参考文件与复用契约；仅查API或读Demo不替代工程实践。目标项目的安装版本与用户约束优先，不照搬游戏数值或项目专用夹具。

- 架构/编程/资源：[revgame-engineering.md](references/revgame-engineering.md)。
- Unity资产、Prefab、导表、构建、Play：[revgame-editor-practice.md](references/revgame-editor-practice.md)。
- 需求→实现→真实验证→报告/发布：[revgame-ai-workflow.md](references/revgame-ai-workflow.md)。

只读当前任务命中的参考与源码，不通读整个项目。引用失效先在当前checkout或GitHub核对；无法访问明确未读，不假称参考成功。样本是可review实践，不是无需校验的标准答案。

## 基础模块总览（新增开发态 RevAutomation 见自动化接入参考）

> "我想做 X → 用哪个门面"。签名与最小示例在对应参考文件里；没有合适门面时先按生存期选面板、域 Logic 或服务，不为单个业务新造框架系统。

| 我想… | 门面 | 一句话 | 速查位置 |
|---|---|---|---|
| 启动游戏、初始化框架 | `RevInitialize` | 启动入口调一次，装配资源系统策略 | `startup-and-platform.md` §1 |
| 切后台 / 回前台处理 | `RevAppLifecycle` | `Paused` / `Resumed` / `Quitting` 事件 + 后台时长 | `startup-and-platform.md` §2 |
| 确认代码裁剪没误伤 | `RevStripGuard` | 真机启动自检关键类型，缺了报人话 | `startup-and-platform.md` §3 |
| 打日志 | `RevLog` | 五级 + tag + 重复抑制 + 环形缓冲 | `modules-core.md` §1 |
| 多久之后 / 每隔多久 | `RevTimer` | 四时间域，owner/scope 防泄漏 | `modules-core.md` §2 |
| 纯 C# 类要每帧回调 / 协程 | `RevMono` | 不摆物体拿 Update/LateUpdate/FixedUpdate | `modules-core.md` §3 |
| 频繁创建销毁的对象 | `RevPool` / `RevRefPool` | GameObject 池（可 `[RevPoolPrefab]` 按类型取）/ 纯 C# 池 | `modules-core.md` §4 |
| 一对多解耦通知 | `RevEvent` | 字符串事件名，派发中可安全增删 | `modules-core.md` §5 |
| 跨界面共享的业务能力（注册 / 取用 / 用完注销） | `RevServices`（默认）/ `RevServiceLocator`（作用域·进阶） | 一行 `Register`、一行 `GetRequired`、一行 `Unregister`；每局一份才用 Builder | `modules-core.md` §6 |
| await 一帧 / 等待 / 取消 | `RevTask` | 轻量单线程异步（框架唯一异步基建） | `modules-core.md` §7 |
| 切场景 | `RevScene` | 进度永远 0~1，切前按约定清理 | `modules-core.md` §8 |
| 键鼠 / 触屏 / 手柄 / 手势 | `RevInput` | 动作名绑定 + 屏蔽栈 | `modules-core.md` §9 |
| 播音效 / BGM | `RevSound` | 一行播放，2D/3D/BGM，音量总线 | `modules-content.md` §1 |
| 状态切换（流程 / AI） | `RevLightFSM` / `RevLightStackFSM` / `RevHeavyFSM` | 类型名统一 `FSM`；宿主驱动 | `modules-content.md` §2 |
| 游戏域与无界面业务单元 | `RevScope` / `RevLogic` / `RevLogicBase` | 自定义字符串域、统一回收；FSM 管状态，域管生命周期 | `modules-core.md` §12、`playbooks.md` R19 |
| 加载资源 | `RevResManager` | 逻辑路径 + 引用计数 + 分组卸载 | `modules-content.md` §3、`resource-and-ab.md` |
| 单例 | `RevSingleton*` | 三个基类（优先服务定位器） | `modules-content.md` §4 |
| 做界面 | `RevUI` / `RevUIPanel` | 声明式面板、数据单向流、动画库 | `modules-content.md` §5、`ui-and-layering.md` |
| 一串动作（播放→等待→并行） | `RevSequence` | 不可变蓝图 + 取消收尾契约 | `modules-content.md` §6 |
| 关键业务测试接口 | `RevGM.Register` / `RevRuntimeAutomation.Register` | 项目真实业务入口，经 RevMCP 开放给 Agent；Editor 与开发版 Player 共用注册表 | `automation-integration.md`、`playbooks.md` R21 |
| 读配置表 | `RevDataTableManager` | 表也是资源，走资源系统 | `modules-content.md` §8 |
| 玩法数值/文案/概率放哪（血攻、倍率、冷却、奖励、关卡） | Excel 导表（铁律 21） | 策划数值进表、代码只读；启动时统一校验 | `playbooks.md` R22 |
| 资源热更（扩展包） | `RevHotUpdate` | 清单 + 差量下载 + 版本目录 | `modules-content.md` §9、`hot-update.md` |

## 第一步：30 秒分诊——先判断"用户要的是哪一类事"，再只读对应那一个文件

> **读文件的原则（渐进式）**：先读本文件的总览表 + 铁律；**只读分诊命中的那一个文件**；不要为了"了解框架"通读 `references/` 或源码。
> 需要大范围搜索源码之前，先问自己：这个问题在下表的哪一行？

| 用户在说什么 | 类型 | **先读这个**（读完通常就够了） |
|---|---|---|
| "帮我写个 XX 功能 / 怎么实现 XX / 我想做 XX" | **写功能** | 先读 `Revolution.Document/编程范式/Revolution官方编程架构规范.md`，再按 `references/playbooks.md`（22 个任务配方）选接口；功能里出现任何策划会调的数字，同时读 R22 |
| 血量/攻击/倍率/概率/冷却/奖励/关卡阵容/说明文案里的数字 / "配置怎么管" / 审查有没有写死数值 | **配置数值** | `references/playbooks.md` R22（Excel → 导表 → 启动校验 → 强类型读取）+ 铁律 21 |
| 自定义域 / 登录大厅战斗结算退出 / FSM 联动 | **统一流程** | 先读《Revolution 官方编程架构规范》，再读 `references/playbooks.md` R19 与 `modules-core.md` §12 |
| 关键业务 GM 测试接口 / 一句话测试 / AI 或 MCP 自动验收 | **开发与自动化闭环** | 先读 `references/automation-integration.md`，再按 R21 接真实业务；联合 `revolution-ai-automation` 使用 RevMCP，GM循环采样继续旧tester；核对实际连接 |
| "XX 和 YY 用哪个 / 该不该用事件 / 要不要新建 System" | **选型** | `references/decision-guide.md` §1 对照表 |
| "框架有没有 XX 能力 / 能不能 XX"（尤其是你不确定 API 存不存在时） | **查边界** | `references/decision-guide.md` §2「不存在 / 不做」清单（防幻觉） |
| "XX 不工作 / 出现 XX 现象 / 报错 XX / 编辑器好真机坏" | **排错** | `references/troubleshooting.md`（症状 → 原因 → 先查什么） |
| "帮我审查这段代码 / review" | **审查** | `REFERENCE_GUIDELINES.md`（四段式输出） |
| 要完整签名 / 全部重载 / 精确参数 | **查 API** | `references/modules-core.md` 或 `references/modules-content.md`（门面签名 + 铁律） |
| 启动入口 / 切后台 / 出包 / 小游戏 / 代码裁剪 | **平台与真机** | `references/startup-and-platform.md` + `templates/RevBoot.cs.txt` |
| 加载资源 / 打包 AB / ResMap / 编辑器直读差异 | **资源专题** | `references/resource-and-ab.md` |
| 面板传数据 / 刷新时机 / 动画 / Part / Back / 横竖屏选择及预览 | **UI 专题** | `references/ui-and-layering.md` |
| 接热更 / 排查热更 | **热更专题** | `references/hot-update.md` |
| 编辑器菜单（打包 / 导表 / GM 面板 / UI 生成器 / 平台工具） | **工具** | `references/editor-tools.md` |
| 代码 / 注释规范（**本项目硬规范**） | **规范** | `references/code-style.md` |
| 怎么证明"改对了" / 提交推送 | **验证交付** | `references/verify-and-test.md` |
| 权威文档 / 全局文档在哪 | **找文档** | `references/docs-map.md` |

## 不确定时怎么办：反幻觉协议（写代码前必过）

这个框架的 API 有些**看起来该有、其实没有**（例如 `RevEvent.AddOnce`、`RevHeavyFSMGroup`、`IRevHotSink`、`RevSound` 的"在播数量"查询）。写代码前按顺序做：

1. **用到的每个 `Rev*` API，都能在 `references/` 里找到签名或 Demo 原样代码**——找不到就**不要凭印象写**；
2. 找不到时，先用可用的符号导航定位定义，再读取对应 `Facade/` 或实际定义文件；符号服务不可用时转精确文本查找。确认参数、命名空间、约束和清理契约后再写；
3. 疑似框架未提供的能力先查 `references/decision-guide.md` §2；确认未做后采用已有替代方式，不发明同名 API；
4. 文档与源码冲突时以代码为准；区分技术方案中的目标设计、已提交实现和未完成草稿；
5. 优先参照真实 Demo，但对采用的 API 重新核实；存在 Demo 不等于本轮已经编译、运行或真机验证；
6. 模块规模用当前源码与 Git 跟踪清单核对；历史断言/性能数字标明验证时间与覆盖范围，不直接当作当前 CI 证据。

## 技术铁律（写代码必须遵守）

1. **使用公开入口**：业务使用各模块门面，以及 `RevScope` / `RevLogic`、公开 FSM 类型与状态基类；不直接创建内部内核、反射绕开契约或直接读资源文件。
2. **失败必带原因**：资源看 `handle.ErrorReason`，GM 看 `RevGMResult.Message`，热更看 `result.Error` / `LastError`；Android AB 问题同时查 `RevLog` 的 `Res` 标签。业务拒绝给出明确原因。
3. **先确定生命周期归属**：对象级用显式 owner + 对应清理；域级可用活动 `RevScope` 兜底 + `Exit`；Logic 用基类携带自身 owner 的 `After/Every/Listen`。显式 owner 优先，不能假设有域就能收走所有对象。匿名 lambda 需要保存同一委托或按 owner/域清理；资源取用与释放仍须配对。
4. **资源只写逻辑路径**：用生成的常量（`RevResPath.*`，见 `Assets/Revolution/Generation/RevResPath.cs`）或"两段式" `Load(根目录, 资源名, 类型, 分组)`；**禁止**自己拼 `StreamingAssets` / `Application.dataPath` 路径读资源（缓存、引用计数、平台差异、热更都会失效）。
5. **UI = 面板类 + 职责钩子**：`[RevUIPanel]` 声明路径、`[RevBind]` 绑节点、`OnBindView` 只装配、`OnRefreshView` 只落屏、点击只转发；控件事件优先用 `[RevButtonClick]` 等方法特性，手动 `AddListener` 仅用于特性不覆盖的控件能力。自定义面板开关动画优先成对重写 `PlayOpenTransition` / `PlayCloseTransition`，两边结束都必须调用 `onDone`；标准预设可用 `ShowAnimation` / `HideAnimation`，不要意外叠播。数据单向 `SetData → OnDataChanged → RefreshView`。只属于当前屏的逻辑留在面板；无界面只活一段玩法用 `RevLogicBase`，跨域共享能力用 `RevServices`，不另造 System/BusinessLogic 层。
   **刷新时机三条（新手最易混）**：① 框架**不监听数据对象内容**——`OnRefreshView` 只在"打开 / 面板开着时 `SetData` / 手动 `RefreshView()`"这三个时机被调；② **原地改数据字段（`Data.Count++`）必须手动 `RefreshView()`**，换新对象走 `SetData` 才自动刷（`OnDataChanged` 里**不要**再手动 `RefreshView()`，会画两遍）；③ **`SetData` 写在业务侧"数据产生的地方"**（点击回调 / 网络回包），不写在面板里；面板是数据的消费方。详见 `references/ui-and-layering.md` §1.1。
6. **异步只用 `RevTask`**：`await RevTask.Delay/Yield`、`await RevScene.LoadAsync(...)`、`await RevHotUpdate.InitializeAsync(...)`；**不要**引入 `System.Threading.Tasks.Task`、不要用协程写业务（`RevMono.StartCoroutine` 只用于桥接旧代码）。取消用 `RevCancellationTokenSource` + `token.ThrowIfCancelled()`。
7. **事件**：`string` 事件名（常量集中在一个类里）+ **同一事件名只能有一种签名**；只在主线程注册/派发；派发过程中可以安全增删（框架做了标记删除），但回调里别做重活。
8. **时间**：`RevTimer.After/Every` 必须能停（owner、计时器 scope 或活动游戏域）；UI 倒计时/超时用 `RevTimeDomain.Unscaled`；`At(...)` 前先 `SyncServerTime`。域 `SetTickEnabled(false)` 不会暂停计时器。
9. **服务**：**默认用 `RevServices`**——一行 `RevServices.Register<IX, X>()`、一行 `RevServices.GetRequired<IX>()`、不用了 `RevServices.Unregister<IX>()`（**不要**为此写静态类 + `static readonly` 缓存 + 链式 `Build()`）。纪律：**全局服务集中在启动入口注册；跟着模块 / 场景走的服务，谁注册谁注销**（`owner: this` + `OnDestroy` 里 `UnregisterAllOf(this)`）；重复注册当场报错（要换先 `Unregister`）；`Register<I>(已有实例)` 注销时**不** `Dispose`，`Register<I, T>()` / 工厂注销时 `Dispose`；**不要把取到的服务长期存成字段**（注销后它是已 `Dispose` 的对象）。必需依赖用 `GetRequired<T>()`（没注册当场炸），可选能力用 `Get<T>()` / `TryGet<T>()`。**只有**要"同一个服务类型每局 / 每个场景各一份"或"单测另建隔离容器"时，才用 `RevServiceLocator.Create()…Build()` + `CreateScope()`（`AddScoped` 的服务必须在作用域的 `using` 里取）。
10. **编辑器 vs 真机**：编辑器默认**只**注册 `RevEditorResPolicy`（AssetDatabase 直读，AB/Resources 完全不参与）；要看 AB / 热更效果，必须开 `RevResBootstrap.UseABInEditor = true`（菜单 `Revolution.Tools/资源/AB 加载模式（编辑器）`）。另外**编辑器里平台名恒为 `PC`**（`RevHotPlatform` 带 `!UNITY_EDITOR`）。
11. **不新增分层、不新增门面**：不要给框架加 `System` / `BusinessLogic` 这样的层，也不要给某个模块加"第二套加载 API"（会立刻长成第二套系统）。真需要扩展时，优先**服务 + 约定**，其次才是框架改动。
12. **改框架要照既有形状**：目录分层 `Core`（数据与契约）/ `Facade`（对外入口）/ `Implementation`（实现）/ `Interfaces`（接口）/ `Support`（宿主适配）；每个文件**开头写清"为什么这么写"**；新增资产要带 `.meta`（文件夹 meta 一律是**同级** `<文件夹名>.meta`）。
13. **启动只调一次 `RevInitialize.Initialize()`**：它只装配资源系统策略；**不要**再直接调 `RevResBootstrap.Instance.Init()`，也不要去"手动初始化"其它模块（`RevEvent` / `RevInput` / `RevTimer` / `RevUI` / `RevPool` / `RevAppLifecycle` 都已自动就位，重复启动反而会清监听）。骨架见 `templates/RevBoot.cs.txt`。
14. **切后台 / 回前台用 `RevAppLifecycle` 事件，不要自己写 `OnApplicationPause` / `OnApplicationFocus`**：框架已对"Pause 与 Focus 同时到达"去重；`Quitting` 移动端不保证触发，数据要随存随写。
15. **面向真机 / 小游戏 / WebGL 写代码**：不引入 `Thread` / `Task`（小游戏单线程）、不依赖同步加载（用 `LoadAsync` / `GetAsync` / `OpenAsync`）、不依赖文件日志；**主推关闭 Managed Stripping**（开了就生成 `link.xml` 并看 `RevStripGuard` 自检）。清单见 `references/startup-and-platform.md` §4–5。
16. **对象池按类型取优先**：预制体脚本上写 `[RevPoolPrefab(root, name, Group = ...)]`，业务 `RevPool.Get<T>()` / `GetAsync<T>(cb)` / `Clear<T>()` / `DestroyPool<T>()`，取用与清理处都不写路径字符串（特性**不继承**，子类要自己写；同一预制体只能被一个类声明）。
17. **域与流程分工**：用业务 `const string` 定义域，不扩改 `RevDomain`；FSM 管状态、`RevScope` 管回收、`RevLogicBase` 管域业务。联动不接入 Scope 内核；域服务显式指定 owner。域兜底需 `TakeOverOwner = true`（默认），资源按组回收还需配置非 `Unknown` 的 `Reclaim`；关闭兜底后用 `Attach` 和显式归属。
18. **横竖屏由项目选择**：首次 Open/Preload 前配置 `RevUISetting.CanvasOrientation`；默认 `Auto`，也可固定 `Portrait` / `Landscape` 或保持 `UseDesign`。只影响根画布缩放，不修改设备方向或自动重排布局。
19. **热更先验证再激活**：保留清单门禁、完整候选快照校验与失败回退；四钩子指向一致的活动版本。当前没有 `ProbeBundles` 业务资源探针，桥的 `Reset()` 也不等于解除四委托。灰度、签名、按标签下载等方案能力不写成已实现 API。
20. **本地编辑器版本不入库**：`ProjectVersion.txt` 已取消跟踪；不重新提交或用 `git checkout --` 还原它，不擅自回滚用户的包源、包锁定与设置。既有版本守护与此策略存在冲突，见 `verify-and-test.md`。
21. **策划数值一律进 Excel 走导表，不写死在 C#**（硬规则，写任何玩法代码前先过这条）：凡是策划会调的数——**棋子/角色/怪物的血攻速、星级成长倍率、技能与增益的数值/概率/冷却/持续时间、敌方成长公式系数、关卡阵容与章节参数、奖励权重与强化量、战斗节奏与限时、玩家可见的说明文案里的数字**——都必须放进 `配置表/*.xlsx`，由框架导表工具生成 `XxxTable` 代码和 `Data/*.txt` 数据，运行时用 `RevDataTableManager` 读取。**禁止**：`const float Hp = 120`、`static readonly Dictionary` 里手写一批数值、`case X: _atk += 0.25f`、`"本轮攻击+25%"` 这类把数字同时写进逻辑和文案的写法（文案与效果会各说各的）。**判断法**：如果策划想调这个数，是否需要程序改 C# 并重新编译？需要 → 违规。**允许留在代码里的只有**：和预制体/场景/美术资源绑定的结构常量（棋盘边长、章节按钮数、手牌槽位数、排序层）、纯表现的动画/Tween 时长、算法内部的相对评分权重、枚举本身；留下时必须在注释里写明“为什么不进表”。落地方法、校验层写法与验证手段见 `references/playbooks.md` R22。

## 代码与注释规范（本项目硬规范，必须遵守）

1. **方法签名的 `(` 与参数不换行**：默认写一行；**只有太长（显示宽度 > 约 160 列）才换行**。长消息/长参数先提成局部变量，让"调用/抛出"本身保持单行。
2. **条件分支必须带大括号并强制换行**：所有 `if` / `else if` / `else`（包括Lambda、回调、宏分支与生成代码）条件、左大括号、代码体、右大括号分别换行，代码体缩进。禁止 `if (x) return;` 和 `if (x) { return; }`。类、方法、属性、访问器的大括号也必须独占一行；格式化配置使用 `csharp_new_line_before_open_brace = all`，不能只设 `control_blocks` 而把其它大括号合到行尾。
3. **核心 / 关键 / 容易踩坑或被忽略的地方要写详细注释，面向小白**：解释"为什么这么做 / 不这么做会怎样"，而不是复述代码。
4. 新框架类型用 `Rev` 前缀、FSM 缩写用大写；既有 FSM 物理文件名仍保留 `Fsm` / `StateMachine`，不要顺手改资产路径。框架命名空间 `Revolution`，扩展包 `Revolution.HotUpdate`，Demo 用 `Revolution.Demo.<模块>`；业务状态、业务域常量名按项目约定。
5. 中文注释、术语与文档保持一致（"门面 / 句柄 / 分组 / 逻辑路径 / 大版本锚定"…）。
6. 详细正反例与自检脚本思路见 `references/code-style.md`。

## 开发行为准则

以下四条偏向"稳"而不是"快"。琐碎任务自行判断。

### 1. 先想清楚再写

不要假设、不要藏疑惑、把取舍摆出来。动手前先明确：

- **模块选型**：这是"多久之后"（`RevTimer`）、"每帧"（`RevMono`）、"等一个异步结果"（`RevTask`）、还是"一次变化多处反应"（`RevEvent`）？
- **生命周期归属**：跟界面走的放面板；无界面、活一段玩法的用 `RevLogicBase` 归域；跨域能力放服务。一次性顺序流程用序列，阶段切换用 FSM。
- **失败出口**：失败时业务要拿到什么？（原因枚举 / 回调 / 异常 / 重试）
- **编辑器与真机差异**：这次改动在"编辑器直读"和"AB 模式"下都成立吗？

### 2. 最简优先

写解决问题的最少代码，不做没人要的抽象/配置/"以后可能用得上"。判断法：**加第四个监听者时，广播方要不要改代码？** 要改就别上事件。

### 3. 外科手术式改动

只动该动的：不顺手"美化"相邻代码、不重构没坏的、不删既有死代码（发现了**报告**即可）；匹配既有风格；自己改动造成的孤立引用要清掉。**改完要同步文档**（见下）。

### 4. 目标驱动、可验证

把任务变成可验证目标，别停在"能跑"：

```text
1. 读该模块《使用说明》→ 验证：写法与文档一致（门面、参数、清理方式）
2. 写代码 → 验证：Unity 编译 0 error（必要时用工程外桩工程先编译一遍纯 C# 部分）
3. 跑对应 demo 场景 → 验证：屏幕上"日志/结果"符合预期
4. 要动框架内核 → 验证：工程外纯 C# 断言全过（见 references/verify-and-test.md）
5. 交付 → 验证：文档 / demo / GM 命令 / change 说明同步
```

## 自动化优先的开发交付（新增约定）

开发功能时先确定可验证的真实业务入口与预期：动作、无副作用查询、结果断言、每轮复位、整场收尾。完成实现后，环境允许时立即执行 Revolution.RevCLI 回归，不只提供教程或命令等待用户手动运行。

- 读取 `references/automation-integration.md`。已有 `revolution-automation-tester` 时加载或直接遵循已加载协议；开发规范由本Skill负责，发现/执行/证据由测试Skill负责，避免重复造客户端脚本。
- 识别 `RevGM`（后端）、Automation（CLI/Editor文件桥）、Unity MCP（可选观察/交互）三层。已有桥直接复用；不存在时明确缺项，不虚构工具、不假定所有项目都已连接。
- 测试前记录原始编辑器状态；资产变更与Play串行，保护未保存场景；业务回归只能在未暂停图形Play，不用batch代替。
- 刚修改GM/脚本后等编译，运行期 `gm list` 确认注册。使用真实动作与数据断言，不能把回Idle、命令成功当全部业务成立。
- 场景 `setup` 全场一次、`cleanup` 每轮、`teardown` 全场收尾；改参数后导表、校验与回归一起维护。异常与拒绝场景也应验证状态未改变。
- 最终依据完整response和scenario-result判定轮次、错误、取消与清理；输出可复现报告路径，分别标记编译/离线/Play/AB/MCP视觉/真机证据。
- 若缺测试接线且用户允许编辑，补最小项目侧开发态GM或Check并实际继续测试；不能重写玩法、改生产存档或降低断言来制造通过。没有执行权限/桥接时报告阻塞，不宣称已完成运行验证。

## 典型工作流

### A. 写新功能（7 步）

1. **先读官方规范，再定位模块/配方**：涉及游戏业务先读 `Revolution.Document/编程范式/Revolution官方编程架构规范.md`，再在 `references/playbooks.md` 找 R1~R22。选模块按生存期与现有门面，不先改框架；不确定时读 `decision-guide.md` §1。
2. **需要完整细节时读该模块的《使用说明》**（`references/docs-map.md` 给路径）——它是权威，本 skill 只是索引。
3. **先读同类RevGame实践，再核对公开API**：按 `references/revgame-engineering.md` 选择真实业务文件与生命周期；框架 Demo 只补模块最小用法。目标工程没有 Demo 目录时不要寻找不存在的路径，不直接复制样本中的旧注释、游戏参数或测试强化。
   **要动框架本身（修 bug / 加功能）时，先查 `Assets/Revolution/CHANGELOG.md` 与 `Revolution.Document/bug修复日志/`**：这个模块以前修过什么、现行契约是什么——避免重复踩坑，也避免给"已修复的缺陷"写绕过代码（见 `references/pitfalls.md` 末尾"现行契约速查"）。
4. 写代码，守住上文的铁律与规范。
5. **自我审查**：过一遍 `REFERENCE_GUIDELINES.md` 的清单。
6. **验证**：框架内核跑对应离线断言；游戏业务要走官方规范的登录→大厅→战斗→结算→退出正反场景，用受控 GM 查询/动作/拒绝接口采集前后 FSM/域/回收证据。MCP 适配若未实现或未连通标记“未执行”，不把命令存在当自动回归已通过。
7. **同步文档**：改了行为 → 改该模块《使用说明》；改了设计 → 改《架构解析》；同时更新 md 与 html（`Revolution.Document`，在线站会自动发布）。

### B. 审查代码

读 `REFERENCE_GUIDELINES.md`，按"严重问题 → 规范问题 → 潜在风险 → 优化建议"输出；重点看：owner/scope 清理、成对 Release、事件签名与重复订阅、时间域、编辑器/真机差异、异常是否被吞。

### C. 排错（先查表，再走 30 秒顺序）

**先在 `references/troubleshooting.md` §1 按现象找行**（按 UI / 输入 / 计时器 / 资源 / 对象池 / 场景 / 音效 / 状态机 / 序列 / 热更分组，60+ 条）；表里标"（已修）"的现象，在最新代码上仍出现 → 多半是**业务侧绕过了契约**，不是框架 bug。找不到对应行再走：

1. `RevLog` 里的 tag 过滤（模块都有自己的 tag：`Scene` / `ActionSequence` / `HotUpdate`…）。
2. 句柄原因：`handle.ErrorReason`（资源）、`RevGMResult.Message`（GM）、`RevHotUpdate.LastError` + `RevHotUpdate.Dump()`（热更）。
3. 能力自检接口：`RevTimer.Count` / `RevMono.Count` / `RevEvent.GetListenerCount(名字)` / `RevResManager.CachedCount` / `RevPool.DumpStats()` / `RevUI.DumpStats()` / `RevScope.Dump()` / `RevLogic.Count` / `RevLog.Dump()` / `RevAppLifecycle.PauseCount` / `RevStripGuard.Dump()`；`RevScope.LastCloseReport` 初始为 null，判空后再 `ToText()`。
4. 复现：打开对应 demo 场景点按钮；或写一条 GM 命令直接在编辑器面板里跑。

### D. 交付

- 代码 + **文档（md & html）** + 必要时 demo/GM 命令；改了 `CHANGELOG.md` 的"未发布"段（新增 / 修复 / 变更）；
- 里程碑提交按仓库既有风格写提交信息（**统一中文**、说清"为什么"与验证数据）；
- ★ **Windows PowerShell 提交中文信息的坑**：`git commit -m "中文…"`（尤其多行）会被本地编码搞成乱码或直接解析失败。**正确做法**：把信息写进 UTF-8 文本文件，用 `git commit -F 文件` 提交，**提交后删掉该文件**（先 `git add` 再建临时文件，或把它放进 `.gitignore`，否则会被 `git add -A` 误带进提交）；
- 安装/分发走**四条**生成分支（改 `main`，不要往生成分支上提交）：`package`（框架本体）、`demo`（示例工程）、`hotupdate`（热更扩展包）、`skill`（本 skill）。

## 参考文件导航

"何时读哪个文件"已在上面的**分诊表**里给全了，这里只列清单（避免重复）：

- **按任务找**（优先）：`references/playbooks.md`（22 个配方）· `references/decision-guide.md`（选型 + 不存在清单）· `references/troubleshooting.md`（症状速查）
- **查 API 与专题**：`references/modules-core.md` · `references/modules-content.md` · `references/startup-and-platform.md` · `references/resource-and-ab.md` · `references/ui-and-layering.md` · `references/hot-update.md`
- **规范与交付**：`references/code-style.md` · `references/editor-tools.md` · `references/verify-and-test.md` · `references/docs-map.md` · `references/pitfalls.md`（坑位 + 已修复缺陷的现行契约）
- **真实工程参考**：`references/revgame-engineering.md` · `references/revgame-editor-practice.md` · `references/revgame-ai-workflow.md`。编码前必须按职责读取RevGame同类实现。
- **自动化开发闭环**：`references/automation-integration.md`；执行任务联合 `revolution-automation-tester`，先确认真实工具/GM，再运行与验收。
- **审查**：`REFERENCE_GUIDELINES.md`
- **骨架模板**（`templates/`）：`RevBoot.cs.txt`（启动入口）· `RevXxxPanel.cs.txt`（UI 面板）· `RevXxxService.cs.txt`（业务服务）· `RevXxxDemo.cs.txt`（Demo 场景脚本）· `offline-harness/`（工程外断言工程）

## 独立Player、IL2CPP与真机性能面板

涉及CLI打包到指定路径、Player启动/连接/退出、运行时GM/MCP、非开发自动化包或性能面板时，先读 [独立Player测试](references/player-testing.md)。复用现有RevRuntimeAutomation与RevMCP，项目负责隔离/业务负载，框架提供通用采样与构建；不要另造通信系统。
