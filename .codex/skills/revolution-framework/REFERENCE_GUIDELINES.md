# Revolution 代码审查指南（Reference Guidelines）

> 用途：**审查入口**。按本清单逐条过一遍，输出"严重问题 → 规范问题 → 潜在风险 → 优化建议"。
> 每条都对应真实的坑（详见 `references/pitfalls.md`）与代码规范（`references/code-style.md`）。

## 0. 输出格式（固定四段）

| 段 | 内容 | 例子 |
|---|---|---|
| **严重问题** | 会导致崩溃 / 泄漏 / 线上事故 / 数据错 | 事件订阅没 owner 且对象会销毁；热更包实现成"只从持久化读"；引用计数不归零 |
| **规范问题** | 违反本框架硬规范 | 方法括号换行；`if` 不带大括号；类名缺 `Rev` 前缀；绕过门面读文件 |
| **潜在风险** | 现在能跑、边界会出问题 | 同步 `Load` 用在真机首次；`RevTimer.Paused` 当局部暂停；`force` 卸载分组 |
| **优化建议** | 更好写法，不强求 | 提局部变量让调用单行；把纯计算抽到服务以便工程外断言 |

**结论要可执行**：每条给出"改哪一行 / 改成什么"，别只说"建议优化"。

## 自动化可验收检查

- [ ] 业务变更有正常路径、非法前置与结果数据断言，而不是只有GM成功或状态回Idle。
- [ ] 优先复用已安装的 Revolution.RevCLI CLI/Editor Bridge；未把它与可选Unity MCP混为一谈。
- [ ] GM只调用真实业务入口，注册/注销对称且限定开发环境；查询无副作用，参数有上限。
- [ ] scenario.setup只执行一次，每轮需要的数据由steps/cleanup正确复位，整场teardown能收尾。
- [ ] 当前注册表经过Play中gm list确认，脚本修改后完成编译；未保存场景和原始Play状态受保护。
- [ ] 已有完整作业response与scenario-result证据；未把编译/JSON校验/Editor帧间隔当业务或真机通过。
- [ ] 失败保留根因和原始报告，不放宽expected/门限、不无限重试；未验证项明确列出。

详细执行配方见 `references/automation-integration.md`。

## 1. 清理与生命周期

- [ ] 每个计时器、每帧回调与事件监听有明确归属和收尾：显式 `owner` + 对应清理、模块 `OpenScope()`，或开启 `TakeOverOwner` 的活动 `RevScope` 兜底 + `Exit`。不要把已由域接管的注册判为泄漏；显式 owner 优先，未 `Attach` 的显式 owner 不会因此改归当前域。
- [ ] 按委托退订时保留同一委托引用；匿名 lambda 可按 owner 或域清理，不能重新写一个 lambda 指望匹配。全局心跳、连接等跨域对象有显式 owner，避免被当时的当前域收走。
- [ ] 每个 `RevResManager.Load/LoadAsync` 都有对应 `Release`。
- [ ] 池对象：`OnPoolReturn` 清空字段；`Return` 后不再访问该对象；WebGL 用 `GetAsync`。
- [ ] 池按类型取：预制体主脚本写了 `[RevPoolPrefab]`（子类自己写、同一预制体只一个类声明）；清理区分对：`Clear*` 只清空闲实例，退出业务域用 `DestroyGroup` / `DestroyPool`（再 `Shutdown(group)`）。
- [ ] 生命周期类监听（`RevAppLifecycle.Resumed` 等）用 `owner: this` 注册，销毁时 `RemoveAllByOwner(this)`。
- [ ] `MonoBehaviour` 的 `OnEnable/OnDisable` 订阅与退订**成对且对称**（别在 `OnEnable` 订阅、在 `OnDestroy` 退订）。
- [ ] 静态字段/静态容器在关闭 Domain Reload 的场景下有复位钩子（照 `Support/*UnityHooks.cs`）。
- [ ] 服务（`RevServices`）有注销归属：随对象用 `owner: this` + `UnregisterAllOf(this)`，随域用显式域 owner 或 `Attach` + `Exit`；服务注册不会自动使用当前域。全局服务集中在启动入口，不用先注销再注册掩盖泄漏。
- [ ] 服务取用：**没有把 `RevServices.GetRequired<T>()` 的结果长期存成字段**（注销后它是已 `Dispose` 的对象）；`Register<I, T>()` 的 `T` 有公开无参构造（否则用工厂重载）；`owner` 传的是引用类型（通常 `this`）。
- [ ] 服务（Builder 写法）：只在需要"每局 / 每场景各一份"或单测隔离时才用；`AddScoped` 的服务放进 `CreateScope()` 的 `using`；`AddSingleton(instance)` 的实例有人释放；`Build()` 的结果缓存进 `static readonly`；**没有为了"一行注册"这种需求去手写 `static readonly RevServiceLocator` 再链式 `Build()`**（那是 `RevServices` 的活）。

## 2. 资源（RevResourceSystem）

- [ ] 路径用 `RevResPath.*` 常量或两段式参数，**没有**自己拼 `StreamingAssets` / `dataPath` / `Resources.Load`。
- [ ] 真机首次加载用 `LoadAsync`（不是同步 `Load`）。
- [ ] 核对实际回收组：开启 `TakeOverOwner` 的活动域内 `Unknown` 经 `EffectiveGroup` 使用域回收组；`Reclaim` 默认也是 `Unknown`，不代表自动获得按组回收。域外或关闭兜底时明确指定资源组。字符串 `RevDomain` 与枚举 `RevResGroup` 不混用。
- [ ] 没有用 `UnloadGroup(group, force: true)` 去省事清理；跨域共享资源显式保留，避免战斗退出误卸大厅资源。
- [ ] 新增资源打了 AB 标记，并重新生成了 `ResMap.txt` / `RevResPath.cs`。
- [ ] 失败分支检查 `handle.ErrorReason`；Android AB 加载失败及材质 Shader 风险同时查看 `RevLog` 的 `Res` 标签。
- [ ] Android 打包已过 `ValidateAndroidShaders` 预检；没有全量塞 Shader 进 Player 来绕过错误。变体剔除、渲染管线和图形 API 兼容仍有目标设备验证，不能以预检通过代替。

## 3. UI（RevUISystem）

- [ ] 面板有 `[RevUIPanel(root, layer)]`，root 用常量；`[RevBind]` 字段名与节点名对得上。
- [ ] `OnBindView` 只装配、`OnRefreshView` 只落屏、`OnClick` 只转发（没有在视图钩子里做业务/发请求）。
- [ ] 有数据的面板走 `SetData` 单向流；`OnRefreshView` 只读 `Data`，且用 `Data` 前**判空**（`SetData(null)` / 回池复用会清成 null）。
- [ ] **刷新时机**：原地改了数据内容（`Data.X++` / `Items.Add`）→ 有手动 `RefreshView()`；换数据走 `SetData`；`OnDataChanged` 里**没有**多余的手动 `RefreshView()`（会画两遍）；`struct` 数据没有"原地改 Data"的写法（读到的是拷贝）。
- [ ] `SetData` / `Open<T,TData>` 写在**业务侧数据产生处**（点击回调 / 网络回包），不是写在面板里。
- [ ] 控件事件：普通控件事件优先用 `[RevButtonClick]` 等方法特性；控件名与节点名一致（写错装配时就报错）；只有集中分发或 Profiling 确认的高频性能热点才改用重写回调；手动 `AddListener` 仅用于特性不覆盖的控件能力；同一件事不重复处理。
- [ ] 面板开关动画：自定义生命周期动画优先成对重写 `PlayOpenTransition` / `PlayCloseTransition`，传 `owner: this` 且两边结束时都调 `onDone`；不要与 `ShowAnimation` / `HideAnimation` 重复播放同一动画。
- [ ] 打开用 `Open<T>(onOpened)` / `OpenAsync<T>`；没有在真机路径上用"同步 `Open<T>()` 并假设它会加载"。
- [ ] 弹窗/常驻 HUD 正确设置 `InBackStack`；`ExclusiveGroup` 按语义设置；`RevUICacheMode` 与 `KeepAlive` 一致。
- [ ] 面板里没有跨界面复用的重逻辑（交给服务）；域内无界面业务用 `RevLogicBase`。面板没有未在 `OnReuse` 重置的缓存状态。
- [ ] 在首次打开或预加载前选择 `CanvasOrientation`；`Auto` / `Portrait` / `Landscape` / `UseDesign` 与项目需求一致。方向配置只影响运行时根画布缩放，不冒称自动转设备方向、重排页面或适配安全区。
- [ ] Prefab 静态预览没有被当成运行时方向验证；竖屏 Game 分辨率和根 `CanvasScaler` 设置已核对。

## 4. 异步（RevTask）

- [ ] 没有引入 `System.Threading.Tasks.Task` / 协程写业务（协程仅用于桥接旧代码）。
- [ ] `async void` 只出现在事件/按钮入口，且**整体 try/catch**（否则异常静默）。
- [ ] 关键节点有 `token.ThrowIfCancelled()`；宿主销毁 / 切场景能取消。
- [ ] 不 await 的任务显式 `Forget()`；没有在异步里 `new` 大对象 / 做重活（该分帧）。
- [ ] 没有依赖"续体立即全部执行"（每帧限量 128）。

## 5. 事件 / 时间 / 输入

- [ ] 事件名来自常量类；同一事件名只有一种签名；派发返回值 `0` 时有提示（`LogNoListener` 或显式 Warn）。
- [ ] 派发/回调里没有重活、没有跨线程调用。
- [ ] 计时器时间域正确：UI 倒计时/超时用 `Unscaled`；`At(...)` 前有 `SyncServerTime`。
- [ ] 输入：用到的 action 都 `Bind` 过（未绑定会静默返回 false）；换场景 / 打开弹窗时 `ResetAll` 或 `OpenScope().Block()`。

## 6. 音频 / 状态机 / 动作序列 / GM / 数据表

- [ ] 音效：3D 播放传了 target；接了 `Failed` / `VoiceFinished`（框架自身不打日志）；逻辑名与路径不一致时先 `Register`。
- [ ] 状态机：状态实例注册复用（不是每次 new）；没有在 `OnEnter/OnExit` 里再切状态（防重入）；异步切换后检查了 `CurrentState`。
- [ ] 动作序列：蓝图静态缓存只 `Build` 一次；没有把状态存在构建期字段；占用资源在 `Finally` / `OnCancel` 归还。
- [ ] GM：注册集中在一处；命令名不含空白字符、不是只有分隔符；参数用 `args.Int/Float/Bool/Str/Enum` 取；业务拒绝抛 `RevGMUsageException`（不静默 return）。
- [ ] 配置表：走 `RevDataTableManager`（不直接读文件）；字符串取表用 `TableName`（不是类名）；失败分支处理了 `RevResLoadErrorReason`。
- [ ] **没有写死的策划数值**（铁律 21，属**规范问题**，审查时必须报出、不得因“能跑”略过）：搜 `const float/int`、`static readonly` 数组/字典里的血攻倍率、`case X: _v += 0.25f`、`Next(3, 6)`、`% 2 == 0` 这类带业务含义的魔法数字，以及写着“占位 / 等 Excel / TODO 接表”的注释。逐个判断“策划想调它是否要改代码”：要 → 报为规范问题并指向 `playbooks.md` R22；若文案与逻辑各写了一份同一个数，还会引发“提示与实际不一致”，按**潜在风险**一并列出。允许保留的只有预制体/场景绑定的结构常量、纯表现时长、AI 评分权重，且注释里要写明原因。
- [ ] 数值表接入后：说明文案与逻辑**同源**（文案用占位符，不手写 `25%`、`×2`、`每三秒`）；启动有统一装配校验（枚举覆盖、数值有限与区间、必填、多余键、占位符、跨参数约束）并一次性报全部问题；未装配读取会抛异常而不是返回 0；业务构造函数/字段初始化里没有读配置；`Consts` 里没有已迁移数值的旧副本；新增数据表已补 `ResMap`（AB/真机）与 `.meta`。

## 7. 热更（如果项目装了扩展包）

- [ ] `InitializeAsync` 放在"加载任何业务资源"之前；`UpdateAsync` 用 `CheckAsync` 的原结果。
- [ ] 来源遵守快照契约：有有效清单的业务包只能读当前完整版本，不混读任意旧同名包；无有效清单的兼容路径才回退首包。内置平台主包始终来自 App 基线。
- [ ] 上传顺序：内容先、清单最后；清单路径 no-cache、内容路径 immutable。
- [ ] 激活前核对大版本、平台、渠道、环境、依赖闭包与清单合法性，不能只比较资源版本。
- [ ] 完整候选校验在写正式版本指针前完成；失败恢复旧上下文，不提前删旧版本。当前没有 `ProbeBundles` 或经资源系统加载代表业务资源的探针链路，SHA/内置主包验证不等于业务可用。
- [ ] 四个桥接钩子指向一致的活动版本；不能只换路径而保留旧映射、依赖、Hash128/CRC。当前 `RevHotResBridge.Reset()` 仅重置标志，没有解除委托，不声称已有完整卸载入口。
- [ ] 跨启动续传按版本、文件身份和 hash 复用，失败不能把不同内容的 `.part` 拼在一起；发布 `SelfCheck` 通过只证明产物校验，不替代真机启动验证。

## 8. 编辑器 / 真机差异

- [ ] 只用编辑器直读验证过的逻辑，是否在 AB 模式下也成立（必要时开 `UseABInEditor` 复验）。
- [ ] 平台相关代码没有硬编码 `PC`（编辑器平台的默认值）；热更/AB 目录段与 `ABBuildSetting.GetPlatformName` 对齐。
- [ ] 生成物（`RevResPath.cs` / `ResMap.txt` / `RevSoundPath.cs`）没有被手改。

## 9. 代码规范（本项目硬规范）

- [ ] 没有"以 `(` 结尾"的行（括号后换行）；没有 `( 参数` 的多余空格。
- [ ] 所有 `if/for/foreach/while` 都带大括号。
- [ ] 新框架类型用 `Rev` 前缀；FSM 类型统一大写 `FSM`。既有 FSM 物理文件名仍保留 `Fsm` / `StateMachine`，不把类型重命名误扩展成资产改名。
- [ ] 新文件有文件头说明设计原因；关键/易错处有必要注释，不以注释代替验证。
- [ ] 新增 Unity 资产带 `.meta`，文件夹 meta 是同级命名；`Tests/` 工程与 Skills 文本不属于 Assets，不额外生成 Unity meta。
- [ ] 使用现有 `RevLogicBase` 承载域业务，不另造 `System` / `BusinessLogic` 层或第二套资源加载 API。
- [ ] 配对模块文档（md + html）与规模快照已同步；Skills 本身不需要同名 HTML。
- [ ] `ProjectVersion.txt` 保持本地忽略，不重新入库；检查包源、包锁定与其它设置变更是否为项目方有意修改，不擅自还原。
- [ ] 验证声明附实际命令与结果；0 用例测试、历史断言、未提交 AI 自动化草稿不能算作本轮回归通过。

## 10. 启动 / 生命周期 / 真机适配（RevInitialize · RevAppLifecycle · 代码裁剪）

- [ ] 启动入口**只调一次** `RevInitialize.Initialize()`；没有直接调 `RevResBootstrap.Instance.Init()`，也没有"手动初始化一圈模块"。
- [ ] 异步入口（`async void`）整体 `try/catch` + `RevLog.Exception`；全局服务集中在启动入口 `RevServices.Register<…>()`（一行一个）。
- [ ] 切后台 / 回前台用 `RevAppLifecycle` 事件，**没有**自己写 `OnApplicationPause` / `OnApplicationFocus`；长连接 / 计时 / 体力类逻辑按 `LastBackgroundSeconds` 处理了回前台；数据**随存随写**（不依赖 `Quitting`）。
- [ ] 小游戏 / WebGL 目标：没有 `Thread` / `Task.Run` / `System.Threading.Tasks.Task`；没有同步 `Load` / 同步 `Open<T>()` 的假设；音频首次播放有用户手势；热更用 https。
- [ ] 代码裁剪：已确认 `Managed Stripping Level`（主推 `Disabled`）；开了裁剪的已生成 `Assets/link.xml` 并看过真机 `RevStripGuard` 输出；`RevStripGuard` 的 `WatchedTypes` 新增条目保持**字符串写法**（不直接引用被检查类型）。
- [ ] 平台名对齐：小游戏目标产物目录 / 主包名 / 热更平台段都归 `WebGL`；编辑器里验证热更前 `UseABInEditor = true`。

### 10.1 游戏流程与域（RevScope / RevLogic / FSM）

- [ ] 项目域集中定义为业务 `const string`；`RevDomain` 仅提供预置名称，不修改框架才能加副本、公会战等域。
- [ ] 流程状态在 `OnEnter` 开自己的域、`OnExit` 关自己的域；栈状态在 `OnSuspend/OnResume` 控制 Tick。保存并检查 `RevScopeHandle`，不误关父级域；同名重入默认报错，关闭 `ReenterIsError` 只是复用已有域，不是新建嵌套层。
- [ ] `RevScope` 保持与 FSM 解耦；流程联动在业务状态中完成，不新增框架内的状态机调度依赖。
- [ ] `SetTickEnabled(false)` 只暂停域兜底每帧回调与 Logic 的 `OnTick`；Timer 和显式 owner 的普通 Mono 不会因此停，业务没有把暂停等同回收。
- [ ] `RevLogic.Start<T>()` 在特性声明的域打开后调用；`OnStop` 只做业务收尾，不重复制造新的域内监听。
- [ ] 域服务显式 `owner: RevScope.Current`，或对归属对象调用 `Attach`；未传 owner 的服务不自动归域。同类型服务多实例隔离仍使用 `RevServiceLocator.CreateScope()`。
- [ ] 用 `RevScopeFlowDemo.cs` 验证大厅 → 战斗 → 回大厅 → 退出；它是挂任意场景的脚本，没有独立 `RevScopeDemo.unity`。

### 10.2 官方业务编程范式与 GM 自动测试

- [ ] 先按 `Revolution.Document/编程范式/Revolution官方编程架构规范.md` 做选型：时间/资源/UI/事件等常用功能走既有门面，不另造平行系统；Login/Lobby/Battle/Settlement/Exit 由**一个流程控制者**管理 FSM，不在 UI、GM 和场景回调里分别维护一份流程状态。
- [ ] 明确“大厅切战斗后是否常驻”：线性选 `RevLightFSM`，保留大厅选 `RevLightStackFSM`；状态负责本域 Enter/Exit，场景加载交 `RevScene`，没有把场景切换当成 FSM 自动切换。
- [ ] 异步 FSM 准备期旧域仍有效；不提前关旧域。取消或晚到回调检查 `CurrentState`、原域 `IsOpen` 与取消信息；栈机没有异步 Push/Pop。
- [ ] 关键业务至少有受控 GM 查询、动作、拒绝案例；命令 handler 调**真实业务服务/流程控制者的同一入口**，不直接改私有状态/存档，也不执行内部资源卸载或任意代码。
- [ ] `[RevGMEntry]` 静态无参方法只注册命令，编辑模式编目时不触碰运行时服务；重复注册、默认参数、域未开、目标不存在等失败有 `RevGMResult.Success/Message` 可观察。
- [ ] 变更类 GM 只在开发/测试环境注册或执行；`HighRisk`/`Hidden` 都不是权限，直接 `RevGM.Execute` 不经面板确认。MCP/AI 需项目提供受控适配器、白名单、主线程调用、身份/审计，不能把第三方 Unity MCP 包当成已接通的 GM 桥或自动化测试通过的证据。

## 11. 对照示例（反例 → 正例）

### 例 1：事件订阅

```csharp
// ✗ 匿名 lambda：无法按委托移除；对象销毁后回调仍被调用
RevEvent.AddEventListener<int>("Hp.Change", v => hpBar.Set(v));

// ✓ 存字段 + owner，销毁时一行清掉
private Action<int> _onHpChange;
private void Awake() { _onHpChange = OnHpChange; RevEvent.AddEventListener("Hp.Change", _onHpChange, owner: this); }
private void OnDestroy() { RevEvent.RemoveAllByOwner(this); }
```

### 例 2：计时器

```csharp
// ✗ 没留"能停"的路 + 时间域选错（暂停游戏后倒计时冻结）
RevTimer.Every(1f, () => countdown.text = (--sec).ToString());

// ✓ owner 可整体取消 + UI 倒计时用 Unscaled
RevTimer.Every(1f, () => countdown.text = (--sec).ToString(), owner: this, domain: RevTimeDomain.Unscaled);
```

### 例 3：资源加载

```csharp
// ✗ 绕开资源系统：没缓存、没引用计数、真机路径不对、热更无处落脚
string path = Application.streamingAssetsPath + "/UI/icon.png";

// ✓ 逻辑路径 + 分组 + 异步
RevResManager.LoadAsync<Sprite>(RevResPath.Data, "UI_Icon", s => image.sprite = s, RevResGroup.UI);
```

### 例 4：失败可见

```csharp
// ✗ 静默返回：使用者永远不知道"为什么没反应"
if (cfg == null) { return; }

// ✓ 带原因：句柄字段 / 抛出人话异常 / GM 面板显示
if (cfg == null) { throw new RevGMUsageException("配置未加载：先执行 工具/重载配置"); }
```

### 例 5：热更语义

```csharp
// ✗ 只认持久化目录：新装机 / 清数据后全部 BundleLoadFail（首屏黑）
return Path.Combine(localRoot, bundleName);

// ✓ 覆盖式：持久化有就用、没有回退首包、都没有才交还框架默认
if (File.Exists(local)) { return local; }
return null;      // null = 让框架走 StreamingAssets/<平台>/<包名>
```

### 例 6：UI 数据刷新

```csharp
// ✗ 原地改了内容却指望界面自己动（框架不监听数据对象）；在 OnDataChanged 里又手动刷导致画两遍
Data.Count++;
protected override void OnDataChanged(Detail o, Detail n) { RefreshView(); }

// ✓ 原地改后手动刷；或换新对象走 SetData（面板开着自动刷，OnDataChanged 只做增量）
Data.Count++; RefreshView();
SetData(new Detail { Id = Data.Id, Count = Data.Count + 1 });
protected override void OnRefreshView() { if (Data == null) { return; } _count.text = Data.Count.ToString(); }
```

### 例 7：切后台

```csharp
// ✗ 自己写 OnApplicationPause / OnApplicationFocus：移动端两个回调前后脚到达，重连逻辑被触发两遍
private void OnApplicationPause(bool pause) { if (!pause) { Reconnect(); } }
private void OnApplicationFocus(bool focus) { if (focus) { Reconnect(); } }

// ✓ 订阅框架事件：只在"前后台状态真翻转"时派发一次，且能拿到后台时长
RevEvent.AddEventListener(RevAppLifecycle.Resumed, OnResumed, owner: this);
private void OnResumed() { if (RevAppLifecycle.LastBackgroundSeconds > 60f) { Reconnect(); } }
```

### 例 8：启动与小游戏异步

```csharp
// ✗ 到处手动初始化 + 用 Task 写异步（小游戏单线程会直接崩）+ async void 不兜异常
RevResBootstrap.Instance.Init();
private async void OnStart() { await System.Threading.Tasks.Task.Delay(100); await Load(); }

// ✓ 只调一次 RevInitialize；异步用 RevTask；async void 入口整体 try/catch
RevInitialize.Initialize();
private async void OnStart()
{
    try { await RevTask.Delay(100); await Load(); }
    catch (Exception e) { RevLog.Exception(e, "启动失败", "Boot"); }
}
```


## 工程实践审查入口

按 [工程实践审查入口](references/revgame-engineering.md) 读取RevGame同类实现，核对版本、所有者、失败路径与已执行证据；不得把历史样本结果当当前项目通过。
