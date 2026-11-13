# Revolution 技术与代码约定

按当前模块读取对应条目，供业务实现与审查核对。

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
10. **编辑器 vs 真机**：编辑器默认**只**注册 `RevEditorResPolicy`（AssetDatabase 直读，AB/Resources 完全不参与）。真实 AB 验收开启 `RevResBootstrap.UseABInEditor`。桌面 Editor Play 应使用为当前宿主构建的 `PC` 包，即使活动构建目标是 Android。旧快照误选平台时按 [资源加载与 AB](resource-and-ab.md) §2.1 核对。
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
3. **逐类、逐字段、逐方法写面向新手的中文注释**：每个新增或修改的类型、成员、方法、构造函数、重载和回调都要说明契约。方法内部各职责段和重要算法步骤也必须解释原因、顺序、边界与失败处理。覆盖私有和内部成员，详情见 [代码与注释规范](code-style.md)。
4. 新框架类型用 `Rev` 前缀、FSM 缩写用大写；既有 FSM 物理文件名仍保留 `Fsm` / `StateMachine`，不要顺手改资产路径。框架命名空间 `Revolution`，扩展包 `Revolution.HotUpdate`，Demo 用 `Revolution.Demo.<模块>`；业务状态、业务域常量名按项目约定。
5. 中文注释、术语与文档保持一致（"门面 / 句柄 / 分组 / 逻辑路径 / 大版本锚定"…）。
6. 详细正反例与自检脚本思路见 `references/code-style.md`。
