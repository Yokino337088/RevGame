# 选型与边界（Decision Guide）：该用谁？框架到底有没有这个能力？

> **用法**：①"两个模块都像能做这事"→ 查 §1 对照表；②"我想调一个 API，不确定它存不存在"→ 先查 §2 **不存在 / 不做**清单（防幻觉）；③"这个坑是已知的吗"→ §3。
> 来源：`Revolution.Document` 各模块《使用说明》《架构解析》的对照表与"本期没做"章节 + 对源码的逐项核实。
> 标 **[已核实]** 的，已在源码里搜过确认（存在 / 不存在）。

## 1. 选型对照表

### 1.1 时间与每帧：RevTimer / RevMono / RevTask / 协程

| 需求语义 | 选 | 反例（文档点名） |
|---|---|---|
| 多久之后 / 每隔多久 / 到某时刻 | `RevTimer.After / Every / At` | 用 `Every(0.016f)` 凑每帧：一帧可能跑两次或不跑 |
| 每帧回调、跟随、插值、扫描 | `RevMono.AddUpdate / LateUpdate / FixedUpdate` | |
| await 一帧、等加载、流程编排 | `RevTask` | |
| 手上已有 `yield` 协程 | `RevMono.StartCoroutine` | 同一段流程里协程和 `RevTask` 混着写 |

三者可以混用，各做各的。

**RevMono 三相位**：`Update`（输入采样 / 状态推进 / UI 刷新）· `LateUpdate`（相机或 UI 跟随，任何要等别人动完的事；放 Update 会**抖**）· `FixedUpdate`（物理 / 帧率无关推进；放 Update 会**飘**）。

**RevTimer 四时间域**：

| 域 | 用途 | 切后台 | `timeScale = 0` |
|---|---|---|---|
| `Scaled`（默认） | 表现 / 动画 / 战斗演出 | 不走 | **冻结** |
| `Unscaled` | UI 倒计时 / 请求超时 / 轮询 | 不走 | 照走 |
| `Fixed` | 帧率无关的战斗推进 | 走 | ⚠ 随 `FixedUpdate` 停止（见 §3） |
| `Server` | 活动结束 / 榜单刷新 / 防改设备时间（先 `SyncServerTime`） | 无关 | 照走 |

### 1.2 通知与依赖：直接调用 / RevEvent / 服务定位器 / 静态类

| 场景 | 选 |
|---|---|
| A 明确拥有 B（面板与自己的按钮、战斗系统与伤害计算器） | **直接调用**。判据：加第 4 个监听者时广播方要不要改代码？要改就别用事件 |
| 一个变化多处反应，广播方不该知道有谁 | `RevEvent` |
| 需要返回值 / 需要知道谁处理了 | 直接调用（事件不是 RPC） |
| 跨域共享能力、底层要调上层 | 默认 `RevServices`；顺序域服务显式归域 owner，多个同接口并存实例才用 `RevServiceLocator.CreateScope()` |
| 没有界面，只活一段玩法的业务 | `RevLogicBase` + `[RevLogic(业务域)]` + `RevLogic.Start<T>()`；FSM 管状态，`RevScope` 管域回收 |
| 只属于这一屏的流程 | 留在面板（`RevUIPanel<TData>`）；关屏后仍活但只活一段玩法用域 Logic，跨域长存才选服务 |
| 进程内唯一实现的简单工具 | `static class` |
| 只是为了少写一个构造参数 | 直接传参，不要上定位器 |
| 需要自动解析复杂依赖图 | 不属于本框架（DI 容器的活） |

### 1.3 状态机三形态 + 动作序列

| | 轻量 `RevLightFSM<T>` | 栈 `RevLightStackFSM<T>` | 重量 `RevHeavyFSM<枚举,宿主接口>` |
|---|---|---|---|
| 管什么 | 游戏流程 / 玩法阶段 | 界面栈 / 返回式流程 | Boss 或小怪 AI / 技能阶段 |
| 状态标识 | 类即状态 | 类 | 枚举 + 行为接口 |
| 驱动 | `Update(dt)` | `Update(dt)`，只驱动栈顶 | `UpdateState / FixedUpdateState / LateUpdateState` |
| 异步切换 | ✔ `ChangeToAsync` | ✘ **刻意不做** | ✔ `ChangeStateAsync` |
| 特色 | 异步切换、`StateTime` | `Push / Pop`、`OnSuspend / OnResume` | 切换历史 + 原因、三路驱动 |
| 分层 | 不做，要嵌套就组合多台 | 同左 | 同左 |

| | 动作序列 `RevSequence` | 状态机 |
|---|---|---|
| 关注点 | 一条流程的推进（先 A 后 B、B 等条件、中途可取消） | 对象当前的形态 |
| 终点 | 有（跑完或被取消） | 通常没有 |
| 分支 | 弱 | 强 |
| 典型 | 开箱演出 / 登录流程 / 引导步骤 | AI / 页面栈 |

**判据**：能用"先 A 后 B、B 要等条件、中途可能取消"说清的用序列；必须说"如果…就回到第 3 步"的用状态机。常见组合：状态机管形态、序列管演出。

### 1.4 池与缓存

| 需求 | 选 |
|---|---|
| 场景里看得见的对象（子弹 / 飘字 / 列表项） | `RevPool`（按类型取用写 `[RevPoolPrefab]`） |
| 纯 C# 的数据或算法对象 | `RevRefPool` / `RevPoolCore<T>` |
| UI 面板 | **不用 `RevPool`**，用 UI 专用面板池（策略写在 `CacheMode`） |

面板池不能换成通用池：界面复用要重置的远多于"失活 / 激活"（监听、数据、滚动位置、输入内容、倒计时），所以复用必须走有契约的 `OnReuse`。

**池的清理动词**：`Clear* `（只清**空闲**实例，池与 prefab 引用保留）vs `DestroyPool* / DestroyGroup / DestroyAll`（连借出中、延迟回收中的实例与 prefab 引用一起收）。退出业务域顺序：`RevPool.DestroyGroup(g)` 在前，`RevResBootstrap.Instance.Shutdown(g)` 在后。

### 1.5 UI：刷新、事件、画布

| 场景 | 做法 |
|---|---|
| 整体换数据（打开详情 / 切角色） | `SetData(new …)`，自动刷新 + 触发 `OnDataChanged` 增量 |
| 频繁小改动（倒计时 / 金币） | 原地改 + 手动 `RefreshView()`，或只刷那一两个控件 |
| 普通控件事件 / 点一下做一件事（默认首选） | 方法特性 `[RevButtonClick]` 等 |
| 按钮很多想集中处理 | 重写 `OnClick(nodeName)` |
| UGUI 其它回调 / 第三方控件 | `[RevBind]` 字段 + 自己挂监听 |
| 高频事件（滚动 / 每敲一键） | 重写回调（方法特性走反射，每次有小分配） |
| 画布 | 默认**单 Canvas**；只有 Profiler 证明合批是主瓶颈且常规优化后仍不达标，才在首次开面板前切 `Split` |

### 1.5.1 官方游戏业务编程架构

**先读** `Revolution.Document/编程范式/Revolution官方编程架构规范.md`，把下列关系作为项目设计与代码审查基线：

| 问题 | 约定 |
|---|---|
| 常用能力如何实现 | 优先用现有 `Rev*` 门面；项目只写登录、关卡、经济等特有规则，禁止复制一套资源/定时/事件内核 |
| 谁管理登录、大厅、战斗、结算、退出 | 一个项目流程控制者持有 `RevLightFSM` 或 `RevLightStackFSM`；状态开关自己的 `RevScope`，`RevScene` 只负责物理场景加载 |
| 无界面局内业务 / 跨域能力 | `[RevLogic(域)]` + `RevLogicBase` / `RevServices`，按有效期选择；不为每一屏强加 System/BusinessLogic 层 |
| AI 如何验证关键业务 | 项目登记受控RevGM或RevRuntimeAutomation能力，共用玩家业务服务；经RevMCP发现、执行、断言、截图并清理 |

2026-10-22已提供RevMCP、Unity内控制台和开发态运行时桥；目标项目是否安装/连接仍需实际discover。没有游戏内交互控制台UI。RevMCP会检查HighRisk/confirm，但它不是业务授权；生产环境不注册变更类测试命令，业务权限由项目/服务端负责。

### 1.6 输入屏蔽

| 场景 | 用 |
|---|---|
| 弹窗 / 设置 / 暂停菜单 | `Block(World)`（默认） |
| 教程遮罩 | `Block(World)` + 自己判 `IsPointerOverUI()` |
| 过场 / 加载 | `Block(All)`（指针位置也读不到） |
| 这根手指归摇杆，别当点击 | `Block(Pointer, pointerId)` |
| 对象销毁兜底 | `OffAllOf(this)` / `UnblockAllOf(this)` |

### 1.7 资源热更三条路（框架本体不自带）

| 你的情况 | 建议 |
|---|---|
| 单机 / Demo / 内部工具 | 什么都不用做（本机 AB 或编辑器直读） |
| 要资源热更、不想自己写下载器 | 官方扩展包 `RevHotUpdate`（首选）；或 YooAsset 适配（要真上线的大项目） |
| 已有自己的下载 / CDN / 版本体系 | 实现 `IRevResPolicy` + `IRevResLoader` 两个接口，上层零改动 |
| 还要改逻辑代码不发版 | 代码热更（HybridCLR）+ 上面任一条；**本框架不做代码热更** |

### 1.8 数值放哪：Excel 导表 / 代码常量 / 预制体字段

| 这个数是… | 放哪 | 判据 |
|---|---|---|
| 血攻速、星级倍率、增益·事件·技能的数值/概率/冷却/持续时间、敌方成长系数、人数上限、关卡/章节/Boss 阵容、奖励权重与强化量、战斗限时与节奏 | **Excel → 导表**（`RevDataTableManager`） | 策划想调它，要程序改代码重编译吗？要 → 进表 |
| 玩家可见说明文案里的数字 | **同一行数值 + 模板占位符**（`{Amount%}`） | 文案与逻辑出现同一个数，只能有一个来源 |
| 棋盘边长、章节按钮数、手牌槽位数、排序层（改一个就要同步改预制体/场景/美术） | 代码常量 `Consts`，注释写明绑定对象；装配时校验表行数与之一致 | 改它是否必须同时改资源？是 → 留代码 |
| 动画 Tween 时长、飘字节流、池容量（纯表现，不影响结果） | 代码常量 / 预制体序列化字段 | 改它会不会改变战斗结果？不会 → 可留 |
| AI 目标评分里的 `+1000` / `+10000`（只表示相对优先级，无量纲） | 代码，注释说明 | 数字本身没有物理含义，改了会改变决策结构 → 留代码并在汇报中列出 |
| 美术资源路径、立绘命名 | `RevResPath.*` / 资源命名约定 | 不是数值 |

怎么做见 `playbooks.md` R22；写死的典型症状与代价见 `pitfalls.md` §33。

## 2. 不存在 / 不做清单（写代码前先过一遍，防止幻想 API）

> 格式：**模块：不做 X（替代做法）**。凡写"替代做法"的，都是文档里真实存在的 API。

**通用**
- 框架**不提供** `System` / `BusinessLogic` 层、第二套资源加载 API、DI 构造注入、全局静态服务入口（服务 + 约定，见 `ui-and-layering.md` §5）。
- 零第三方依赖：不引 UniTask / DOTween / Newtonsoft（动画用 `RevUIAnim`，异步用 `RevTask`）。

| 模块 | 不做什么（替代做法） |
|---|---|
| **RevLog** | 正式包不保留 `Debug`（编译期删除；要看加 `REVLOG_DEBUG`）；不接音效 / 资源失败（走 `RevSound.Failed` / `handle.ErrorReason`）；文件通道异步，切场景 / 退后台前手动 `Flush()`；WebGL 默认禁用文件通道；没有分类枚举（tag 是字符串，`MuteTag`） |
| **RevTimer** | 不是时间轮（1024 槽线性扫描）；**不自动补偿挂起 / 卡顿**（要补偿用 `At` + `SyncServerTime`）；回调抛异常**不会**自动停计时器（自己 `Stop()`；异常走 `Failed` / `OnException`）；每个计时器每帧最多触发一次；满 1024 返回 `Overflow` 不静默淘汰；`At` 前没校准返回 `NoServerTime` + 空句柄 |
| **RevMono** | 协程不用字符串 id（`StartCoroutine` 返回 Unity 的 `Coroutine`，谁起谁存）；单相位最多 256 个回调；监听者抛异常不被自动摘除；非 Play 态不创建宿主 |
| **RevPool** | WebGL / 小游戏不支持同步 `Get`（用 `GetAsync`）；`Get(prefab, parent)` 因没有 `RevResHandle` **不自愈**（prefab 被卸载后池不恢复）；没有 LRU，取用 LIFO；**池不自动 Trim**（切场景由业务显式调用 `TrimAll`）；**不替你重置字段**（`OnPoolReturn` 里清，可继承 `RevPoolableBase`）；`[RevPoolPrefab]` **不继承** |
| **RevEvent** | **没有 `AddOnce`** [已核实]（回调第一行 `RemoveEventListener`）；不做异步派发；参数只支持 0~4 个（多的用参数结构体）；事件键只有 `string`；不做去重、不设订阅数上限；派发中新增的监听者本轮不执行；没有局部事件路由 / 事件名生成器 |
| **RevServices / RevServiceLocator** | 不做构造注入 / `[Inject]` / Transient（直接 `new`）；不打日志（问题靠异常信息）；**重复注册直接报错不静默覆盖**（换实现先 `Unregister`）；`RevServices` **只有全局一份、没有 Scoped**（"每局一份"用 `RevServiceLocator.CreateScope()`，且作用域**只允许一层**、Scoped 在根容器上取会报错）；`RevServiceLocator` 的 `Build()` 后冻结、**不能单个注销**（想注销用 `RevServices`）；**不存在** `RevServices.AddSingleton` / `Create()` / `Build()` / `Replace` / `RegisterOrReplace`（别凭印象写）；注销后别人手里的引用**不会变 null**；`RevServices` 的 Tick 是框架自动驱动、`RevServiceLocator` 要自己调 `Tick` |
| **RevTask** | `RevTask` 与 `RevTask<T>` 是两个独立结构体、**无隐式转换**；引擎相关（有调度宿主），非纯 C#；不要 `async void`（想不 await 用 `.Forget()`） |
| **RevScene** | 切前**只自动清对象池**；不新起宿主；没有加载步骤 / 权重 / 状态机那套；不管热更；**切换进行中的后续请求被忽略并告警，不排队** |
| **RevInput** | **没有"系统动作例外"**：`Block(World)` 会连 ESC / 返回键一起挡；不用于点 UI；`ResetAll` / 切后台复位**不合成** `OnReleased`（收尾监听 `RevAppLifecycle`）；不内置虚拟摇杆 UI / 改键 UI / 手柄默认轴映射（用 `BindNamedAxis`）；没绑过的动作不报错永远返回 false；`FixedUpdate` 里轮询 `Pressed` 可能被多次读到 |
| **RevSound** | **没有状态查询**（"现在几条在播"，观察靠 `VoiceFinished` / `Failed`）；2D / 3D 由方法决定（`Play` 是 2D，`PlayOn / PlayAt` 是 3D）；不暴露 `RevSound.Root`（目录在 RevAB 窗口配）；`Scope` 只停声音不卸资源；只异步加载（首次有延迟 → `Preload`）；框架自身不打日志 |
| **状态机** | 公开类型为 `RevLightFSM` / `RevLightStackFSM` / `RevHeavyFSM`；宿主显式驱动，栈机不做异步切换；没有 `RevHeavyFSMGroup` / `RevHeavyFSM.TimeScale`；不内置 Scope 调度，业务状态负责联动；既有文件路径仍含旧名 |
| **RevScope / RevLogic** | 游戏域是字符串，用户定义常量即可；不是服务实例隔离容器。同名重入默认失败，关闭 `ReenterIsError` 只复用；`TakeOverOwner` 默认开但可关，显式 owner 优先，服务须显式归属；`Reclaim` 默认 Unknown 不按组收资源。Tick 暂停不等于停 Timer；缺域的 Logic Start 返回 null，不能调用任意不存在的自动启动 API |
| **RevResManager** | 资源组仍是 `RevResGroup` 枚举，不等于可自定义的字符串游戏域；加载传 Unknown 仅在活动域开启兜底且配置非 Unknown 回收组时补组。引用仍按契约配对，跨域共享不得被域整组误卸载；资源根需配置，Resources 只是随包基线/兜底 |
| **RevUI** | **不做代码热更、不做自动数据绑定**（改了 `Data` 内容手动 `RefreshView()`）；不使用 `RevPool`；**不让面板自设 `sortingOrder`**，预留参数 `RevUISetting.LayerStep` **改了没有任何效果** [已核实：字段存在但未接入]；不要手动 `Instantiate`；Part 的关闭是同步的，不自动刷新所有 Part；**本期没做**：MessageBox、常用 Part、面板清单窗口、打开优先级（面板代码生成器已有，见 `editor-tools.md`） |
| **RevSequence** | 不做条件分支 / 跳转 / 状态网络；不内置业务动作库；不做可视化编排；不内置区域检测（只给 `RevITriggerSource` 契约）；不提供状态 / 进度查询；`.Finally` 里不能放 `Wait` / `Tween`（`Build()` 报错）；`Build()` 之后再改会抛异常；全局时间缩放 / 暂停开关未做 |
| **RevGM** | **不做客户端权限**（`HighRisk` 只是提醒二次确认，真权限归服务端）；不支持拼音缩写联想（中文要打中文子串）；编辑模式（没点 Play）不执行命令；命令名不能含空白 / 不能只有分隔符；重名注册直接抛异常；不用编译宏裁剪 |
| **配置表** | 数据文件要有 AB 标记、扩展名不能被 `excludeExtensions` 排除（否则 `PathNotMapped`）；导表工具：只支持 `.xlsx`；字段只支持 `int / float / string / bool`（无数组 / 嵌套 / 枚举）；单主键且是第一个字段；不处理图表 / 批注 / 合并单元格，公式只取缓存值；坏表**被跳过而不是中止**；表名只做首字母大写；没有 schema 版本号（代码和数据要一起覆盖） |
| **RevAB 打包** | 没设资源根目录时「打包」按钮灰掉；`AutoByFolder` 会清掉手设标记（别和手动分包混用）；「分包」页签只读；体积页签是**源文件体积**不是打包后 AB 体积；漏标 / 空包会中止打包；**没有**出包链路（BuildPlayer / 切平台）/ Debug-Release 区分 / 首包体积硬门禁 |
| **RevHotUpdate** | **不做代码热更**；不做 AB 加密 / 二进制差量 / 灰度分流 / 按需下载（tag）/ 跳商店 / 整包下载 / 清单签名；**不自动热更**（显式 `InitializeAsync`，配置、失败处理、时机归业务）；`BundlePathResolver` 必须在 `Init()` 之前设置，返回完整路径 / URL / null、路径不含扩展名；小游戏 / WebGL 不落地不续传不做文件级校验（交给引擎 `unityHash` / `unityCrc`）；**`CoreBundles` / `IRevHotSink` 只存在于《技术方案》，代码未落地** [已核实] |

**容易被旧资料带偏的几处（以代码为准）**
- **动作序列有 `.WaitTask<T>(ctx => 带返回值的任务, (ctx, 结果) => …)`** [已核实]：旧版《架构解析》曾把它列在"本期没做"，旧版 Demo 注释还写过"`WaitTask` 只吃无返回值任务""`WaitTask` 载荷是构建期固定的、没法缓存"——**都已过时并已更正**（`WaitTask` 传的是**工厂** `ctx => 任务`，任务运行时才创建，清单可缓存）。如果在旧分支 / 旧 fork 里看到这些说法，别信。
- **`[RevGMEntry]` 只标"静态注册方法"**，作用是让 GM 面板在不进 Play 时也能列命令清单；**不是**"标在带参数的命令方法上就自动注册"。命令靠 `RevGM.Register` 注册（旧版根 `README.md` 曾写错，已更正）。
- `RevInitialize` 的文档只有 html 版（无 md）。

## 3. 文档承认的已知局限（遇到相关现象先别怀疑自己）

> 来源：`bug修复日志/` 末尾的"未修复 / 未验证"清单；**修复状态以 `Assets/Revolution/CHANGELOG.md` 为准**（有些后来已修）。

- **RevTimer**：`RevStopwatch` 同帧读数为 0，且 Fixed / Server 域被静默降级为 Scaled；`Fixed` 域在 `timeScale = 0` 时随 `FixedUpdate` 停止（与"照走"不符）；`At` 计时器的 `Pause / Restart / Progress` 语义不成立；同帧内被 `Pause / Restart` 的计时器仍可能触发；临近 1024 上限时"回调里先 Stop 再创建"可能误报 `Overflow`；**值类型作 `owner` 因装箱 `CancelAllOf` 永远匹配不上**；宿主 `HideAndDontSave`，退出 Play 后可能残留。
- **RevUI / RevInput**：修复多为**静态审计结论**（Unity 最终编译与 PlayMode 回归当时未完成；RevInput 的 EditMode 测试运行器 0 个用例，"Passed"是空套件；没有真机键鼠 / 手柄 / 多点触屏验证）。
- **RevHotUpdate**：候选门禁、完整快照真 SHA、跨启动分片身份复用和发布 `SelfCheck` 真 SHA 已落地；不要沿用“SelfCheck 不重算”的旧结论。当前没有 `ProbeBundles` / 经资源系统加载代表业务资源的探针链路，`RevHotResBridge.Reset()` 也未解除四委托；端到端验证范围见 `hot-update.md` §5.1。
- **状态机 / 动作序列**：没有行为断言工程（取代 / 取消 / 栈配对 / 历史裁剪只靠设计保证和代码审查）；动作序列"工程外无引擎编译未跑"。
- **投产评估 P2 未做**：Debug / Release 构建区分、出包链路、首包体积门禁、热更下载类 4 条内部加固。
