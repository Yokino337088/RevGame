# 任务配方（Playbooks）：我要做 X → 怎么做

> **用法**：先在下表找到你的任务，只读那一节（每节自带"选什么 / 标准写法 / 坑 / 抄哪个 Demo / 怎么验"），**通常不需要再读别的文件**。
> 需要完整签名再去 `modules-core.md` / `modules-content.md`；选不准用 `decision-guide.md`；出问题用 `troubleshooting.md`。
> 代码片段里标"Demo 原样"的，来自 `Assets/Revolution.Demo/` 里真实存在的脚本，可放心照抄；其余是按框架现行 API 写的最小示例。

| # | 任务 | 一句话选型 |
|---|---|---|
| R1 | 延迟 / 循环 / 倒计时 | `RevTimer` |
| R2 | 每帧跟随 / 纯 C# 类每帧回调 | `RevMono` |
| R3 | 异步流程编排、桥接旧回调接口 | `RevTask` |
| R4 | 做一个带数据的界面 | `RevUIPanel<TData>` |
| R5 | 弹窗 / 过场时挡住世界输入 | `RevInput.OpenScope().Block()` |
| R6 | 一对多解耦通知 | `RevEvent` |
| R7 | 跨界面复用的业务能力（一行注册 / 取用 / 注销；每局一份才用 Builder） | `RevServices`（进阶 `RevServiceLocator`） |
| R8 | 切场景（带进度） | `RevScene` |
| R9 | 子弹 / 飘字等频繁创建销毁 | `RevPool` + `[RevPoolPrefab]` |
| R10 | 读配置表 | `RevDataTableManager` |
| R11 | 加载图标等资源并释放 | `RevResManager` |
| R12 | 播音效 / BGM | `RevSound` |
| R13 | 开箱演出等多段流程 | `RevSequence` |
| R14 | 流程 / AI 状态切换 | `RevLightFSM` |
| R15 | 加一条运行时 GM 调试指令 | `RevGM` |
| R16 | 新项目启动入口 + 切后台重连 | `RevInitialize` + `RevAppLifecycle` |
| R17 | 接资源热更 | `RevHotUpdate`（见 `hot-update.md`） |
| R18 | 给新模块写一个 Demo | `templates/RevXxxDemo.cs.txt` |
| R19 | 自定义域与大厅 / 战斗流程联动 | `RevScope` / `RevLogic` + FSM |
| R20 | 项目选择横屏 / 竖屏 UI | `RevUISetting.CanvasOrientation` |
| R21 | 为关键业务留 AI 可验证的 GM 测试接口 | `RevGM` + 项目业务服务/流程控制者 |
| R22 | 玩法数值 / 说明文案 / 概率 / 关卡阵容放哪（**不写死在 C#**） | Excel → 框架导表 → `GameConfig` 启动校验 → 强类型读取 |

---

## R1 延迟 / 循环 / 倒计时（`RevTimer`）

**选什么**：多久之后 / 每隔多久 / 到某个服务器时刻 → `RevTimer`。**不要**用 `Every(0.016f)` 凑每帧（一帧可能跑两次或不跑，每帧用 `RevMono`）。

```csharp
// Demo 原样（RevTimerDemo.cs）：循环并存句柄；回调带"第几次"
_everyHandle = RevTimer.Every(1f, i => Ui($"第 {i} 次触发（Every 1s）"), times: 5);

// Demo 原样：owner 防泄漏（销毁时一行清干净）
RevTimer.Every(3600f, () => { }, owner: this);
private void OnDestroy() { RevTimer.CancelAllOf(this); }

// Demo 原样：Server 域必须先校准，否则返回空句柄（原因 NoServerTime）
RevTimer.SyncServerTime(System.DateTime.UtcNow);                       // 真实项目：用服务器下发的 UTC
_atHandle = RevTimer.At(System.DateTime.UtcNow.AddSeconds(10), () => { });
```

- **UI 倒计时 / 请求超时 / 暂停菜单里的计时用 `RevTimeDomain.Unscaled`**（默认 `Scaled` 会被 `timeScale = 0` 冻结）。
- 句柄可读：`h.Left`（剩余秒）/ `h.Progress`（0~1）直接喂倒计时 UI；**过期句柄之后的任何操作都是安全空操作**。
- 上限 1024 个，满了返回 `Overflow` 空句柄（不静默淘汰）；每个计时器每帧最多触发一次；`After(0)` = 下一帧。
- ★ `owner` 别传**值类型**（装箱后 `CancelAllOf` 永远匹配不上，文档承认的未修复项）；`At` 计时器别指望 `Pause` / `Restart` / `Progress`（语义不成立）。见 `decision-guide.md` §3。
- **抄谱**：`RevTimer.Demo`（① After ② Every ③ 句柄暂停/重启/取消 ⑤ 全局暂停 ⑥ Server+At ⑦ owner 泄漏演示 ⑧ 秒表）。
- **验证**：Demo 场景点 ⑦ 再点 ⑦b，看 `CancelAllOf` 返回数；`RevTimer.Count` 应回落。

## R2 每帧跟随 / 纯 C# 类每帧回调（`RevMono`）

```csharp
// Demo 原样（RevPublicMonoDemo.cs）
bool added = RevMono.AddUpdate(OnUpdateTick, owner: this);            // 同一委托重复添加返回 false（去重）
_loopCo = RevMono.StartCoroutine(LoopCoroutine());                    // 纯 C# 类也能起协程
private void OnDestroy() { RevMono.RemoveAllOf(this); }              // 同时移除监听并停协程

// 作用域：出块全停（重复开前先收上一块，不泄漏）
_scope?.Dispose();
_scope = RevMono.OpenScope();
_scope.AddUpdate(() => { });
```

- **选对相位**：跟相机 / UI 的跟随用 `LateUpdate`（放 Update 会抖）；物理 / 帧率无关用 `FixedUpdate`（放 Update 会飘；一帧可能 0 次或多次）。
- 单相位最多 256 个回调，超出返回 false 并报 `Failed(Overflow)`；**监听者抛异常不会被自动摘除**（刻意：别让一次异常吞掉你的逻辑），要停自己 `Remove`。
- **抄谱**：`RevPublicMono.Demo`（②去重 ③FixedUpdate 对比 ⑤作用域 ⑥异常隔离）。

## R3 异步流程编排 / 桥接旧回调（`RevTask`）

```csharp
// 顺序流程：Yield / Delay / WhenAll；入口 async void 整体 try/catch
private async void OnClickEnterBattle()
{
    try
    {
        await RevTask.Yield();
        await RevTask.Delay(100);
        await RevTask.WhenAll(LoadA(), LoadB());          // 任一子任务失败 / 取消 → 整批失败（已修）
        await RevScene.LoadAsync("Battle", p => bar.value = p);
    }
    catch (Exception e) { RevLog.Exception(e, "进入战斗失败", "Battle"); }
}

// Demo 原样（RevTaskDemo.cs 63-71）：把"稍后回调给结果"的旧接口改成可 await
private RevTask<string> WaitLegacyApi(string fakeResult)
{
    var source = new RevTaskCompletionSource<string>();
    CallLegacyApi(result => source.SetResult(result));   // 真实项目里这一行就是调旧接口
    return source.Task;
}
```

- **只做"等待与编排"，重活分帧**；`await` 之后回到主线程。续体每帧限量（默认 128），别指望"立刻全跑完"。
- 不 await 的任务显式 `.Forget()`（或 `_ = RunFlow();`）；`RevTask` 与 `RevTask<T>` 是两个独立结构体，**没有隐式转换**。
- 取消：`RevCancellationTokenSource` + 关键节点 `token.ThrowIfCancelled()`；宿主销毁 / 切场景要能取消。
- **小游戏 / WebGL 只能用 `RevTask`**（单线程；`System.Threading.Tasks.Task` 会崩）。
- **抄谱**：`RevTask.Demo`（顺序流程 / WhenAll / 异常传播 / CompletionSource 桥接）。

## R4 做一个带数据的界面（`RevUIPanel<TData>`）

1. 从模板起头：`templates/RevXxxPanel.cs.txt`（或菜单 `Revolution.Tools/UI/面板代码生成器` 按预制体生成骨架）。
2. 预制体放资源根目录对应路径，特性写目录 / 层级 / 名字：`[RevUIPanel(RevResPath.Data, RevUILayer.Normal)]`。
3. 三个钩子各管一件事：`OnBindView` 只装配、`OnRefreshView` 只落屏（先判 `Data == null`）、`OnClick` 只转发。
4. 业务侧喂数据（**`SetData` 写在数据产生的地方，不写在面板里**）：

```csharp
RevUI.Open<DetailPanel, DetailData>(new DetailData { Id = 1001, Title = "亚瑟" });   // 已开 / 没开都行，Open 一条路全包
RevUI.Get<DetailPanel>()?.SetData(BuildNext());                                     // 已开着，换内容不重开
// 原地改了 class 数据内容 → 必须手动 RefreshView()；换新对象走 SetData 才自动刷
```

- 真机首次打开必须异步：`RevUI.Open<T,TData>(data, onOpened)` / `await RevUI.OpenAsync<T,TData>(data)`；想秒开先 `RevUI.Preload<T>()`。
- `[RevBind]` 字段名必须等于节点名（写错第一次装配就报错）；事件三种接法见 `ui-and-layering.md` §1.2。
- 不要在 `OnBindView` 里开计时器 / 协程（那是"只一次"的）；每次打开要做的写 `OnOpen`；停计时器 / 协程写 `OnClose`（面板会被**缓存复用**）。
- **首次跑 UI Demo** 要先一键生成演示预制体：Demo 里点 "① 一键生成全部演示预制体"，或菜单 `Revolution.Tools/Demo/生成 UI 演示预制体（全部）`（产物 `Assets/GameRes/RevUIDemo/`；资源根目录没配置时先在 `RevAB 打包工具` 里设置）。
- **抄谱**：`RevUISystem.Demo`（覆盖使用说明全部章节：三种事件接法 / 8 个生命周期回调 / 全部 API / 带数据 / 六层与遮罩 / 互斥组 / 三 Canvas / 动画 / Part）。

## R5 弹窗 / 过场时挡住世界输入（`RevInput`）

```csharp
using (RevInputScope scope = RevInput.OpenScope())
{
    scope.Block();                 // 默认 World：挡世界动作与手势，轴读数归零，按下时间仍记录
    // ……弹窗存在期间……
}                                  // 出块自动解除；也可 RevInput.Block(kind, owner: this) + UnblockAllOf(this)
```

| 场景 | 用 |
|---|---|
| 弹窗 / 设置 / 暂停菜单 | `Block(World)`（默认） |
| 教程遮罩 | `Block(World)` + 自己判 `IsPointerOverUI()` |
| 过场 / 加载 | `Block(All)`（指针位置也读不到） |
| 这根手指归摇杆，别当点击 | `Block(Pointer, pointerId)` |

- ★ **`Block(World)` 会把 ESC / 返回键一起挡掉**（框架没有"系统动作例外"）；**不要靠停整个模块来挡弹窗**。
- 没绑过的动作**不报错，永远返回 false**——"按键没反应"先查有没有 `Bind`。`SetRepeat(delay ≤ 0)` = 关连发。
- 事件驱动接入：继承 `RevInputListenerBase`，`RevInput.AddListener(listener, owner: this)`；轮询与事件两种都行。
- **抄谱**：`RevInput.Demo`（轮询 / 事件驱动 / 屏蔽 / 手势 / 缓冲 / 改键存档 `SaveBindings` + 冲突检测 `FindConflicts`）。

## R6 一对多解耦通知（`RevEvent`）

```csharp
// Demo 原样（RevEventSystemDemo.cs）：owner 一行清理；priority 数值【大的先执行】
RevEvent.AddEventListener(BossDead, _onDrop, owner: this);
RevEvent.AddEventListener<string, int>(BossDead, OnStatistics, priority: 10, owner: this);
private void OnDestroy() { RevEvent.RemoveAllByOwner(this); }

int n = RevEvent.DispatchEvent(BossDead, "Boss01", 5);      // 返回"实际被调用的订阅数"；0 = 没人听
```

- **判据**：加第 4 个监听者时广播方要不要改代码？要改就别用事件（直接调用）。事件不是 RPC，**不返回值、不知道谁处理了**。
- ★ **同一事件名只能有一种签名**，否则报"签名不匹配"并跳过监听者；事件名常量集中在一个类里。
- 匿名 lambda 注册后**无法按委托移除**：存成字段或用 `owner`。派发中新增的监听者**本轮不执行**。
- 发了没人听：`RevEvent.LogNoListener = true` 自动提示。
- **没有 `AddOnce`**：想只触发一次，在回调第一行 `RemoveEventListener`。
- **抄谱**：`RevEventSystem.Demo`。

## R7 跨界面复用的业务能力：一行注册 / 直接取 / 用完注销（`RevServices`）

```csharp
// ★ 默认写法：一行注册（启动入口），任何地方一行取用，不用了一行注销 —— 不用静态类 / static readonly / 链式 / Build
RevServices.Register<IDemoAudio, DemoAudio>();                             // 类型对类型：第一次取用时才 new，零反射
RevServices.Register<IConfigService>(Config.Load());                       // 已有实例：注销时容器【不】Dispose 它
RevServices.Register<IDebugService>(s => new DebugService(s.GetRequired<IConfigService>().Level));   // 要构造参数 → 工厂

RevServices.GetRequired<IDemoAudio>().Play("ui_click");                    // 必须有：没注册当场炸，并列出已注册的服务
RevServices.Get<IDebugService>()?.Draw();                                  // 可选能力

RevServices.Unregister<IDemoAudio>();                                      // 不用了：摘表 + 容器创建的实例 Dispose（释放内存）

// 跟着模块 / 场景生灭的服务：谁注册谁注销（owner 一行清理）
private void Awake()     { RevServices.Register<ICombatContext, CombatContext>(owner: this); }
private void OnDestroy() { RevServices.UnregisterAllOf(this); }
```

- **依赖在 `OnInit(services)` 里取，不是构造函数**（实现 `RevIServiceInit`）；循环依赖会报错并打印依赖链。
- **重复注册当场报错**（要换实现先 `Unregister`）；重进场景报"已经注册过了"= 上次没注销，**补注销，别用"先试着注销一下"掩盖**。
- **不要把取到的服务长期存成字段**：注销后它是已 `Dispose` 的对象；要用就现取（一次字典查找，热路径 0 分配）。
- **类型注册默认懒创建**（首次取用才执行构造 / `OnInit` / `RevITickable` 驱动）。服务若在构造函数里订阅 Unity 的首次外部事件，必须抢在事件前调用 `Register<I, T>(owner: this, eager: true)`；旧写法“注册后立刻 `GetRequired<I>()`”也可。`eager` 创建失败会撤销本次登记并抛原始原因；默认懒创建失败仍保留登记供重试。实例/工厂重载不带这个参数。
- **全局服务集中在启动入口注册**；跟模块 / 场景走的，在它的 `Awake` 注册、`OnDestroy` 注销。`Debug.Log(RevServices.Dump())` 随时查"谁还占着"。
- **判据**：关屏就结束 → 面板；无界面、只活一局/一段玩法 → `RevLogicBase`；跨域长期共享 → 服务。别为少写构造参数引入服务定位器。
- **单测**：`[SetUp]` 里 `Register<IX>(new FakeX())`，`[TearDown]` 里 `RevServices.UnregisterAll()`。

**进阶：同一个服务类型要"每局 / 每个场景各一份"才用 Builder**（`RevServices` 只有全局一份）：

```csharp
// Demo 原样（RevServiceLocatorDemo.cs）：Builder 装配并冻结，自己缓存容器
_root = RevServiceLocator.Create()
    .AddSingleton<IDemoAudio, DemoAudio>()     // 类型对类型：第一次取用时 new
    .AddScoped<IDemoBattle, DemoBattle>()      // 每个作用域一份
    .Build();                                  // ← 冻结装配：之后改不了；不要重复 Build（会报错）

using (RevServiceLocator battle = _root.CreateScope())    // Scoped 服务必须放进 using
{
    var ctx = battle.GetRequired<IDemoBattle>();
    battle.Tick(Time.deltaTime);                           // 这套要自己调 Tick（RevServices 是自动驱动）
}
```

- 作用域只允许一层；根容器上取 Scoped 报错；释放后再取服务报错；Builder 冻结后没法注销单个服务。
- **抄谱**：`RevServiceLocator.Demo`（按钮 A~D = `RevServices` 的注册 / 取用 / 注销 / Dump；①~⑦ = Builder 写法）。

## R8 切场景（`RevScene`）

```csharp
// Demo 原样（RevSceneDemo.cs 47-50）：四个全局事件，在这里收自己的摊子
RevScene.OnLoadStart  += name => { /* 关 UI / 停音效 / 卸资源分组 */ };
RevScene.OnProgress   += p => bar.value = p;                 // 进度永远 0~1，不卡 90%
RevScene.OnLoaded     += name => { /* 关加载界面 */ };
RevScene.OnLoadFailed += why => RevLog.Error(why, "Scene");

await RevScene.LoadAsync("Battle", p => bar.value = p);      // 推荐；完成用 OnLoaded / await 接，别轮询 IsLoading
```

- 场景必须在 Build Settings 里；同步 `Load` 会卡帧；**切换进行中的后续请求被忽略并告警，不排队**。
- 切前**只自动清对象池**，UI / 音效 / 资源分组要自己在 `OnLoadStart` 收（`RevUI.CloseAll` / `RevSound.StopAll` / `RevResManager.UnloadGroup`）。
- 演示脚本本身会随场景销毁——常驻逻辑放 `DontDestroyOnLoad`。
- **抄谱**：`RevScene.Demo`（ReloadAsync 完整链路 / 四事件 / 场景名写错时的人话报错）。

## R9 子弹 / 飘字等频繁创建销毁（`RevPool`）

```csharp
[RevPoolPrefab("Battle/Bullet", "Bullet_Normal", Group = RevResGroup.Battle)]   // 写在预制体根节点主脚本上；特性不继承
public class Bullet : MonoBehaviour, IRevPoolable
{
    public void OnPoolGet()    { /* 重置动画、重播粒子、计时归零 */ }
    public void OnPoolReturn() { /* ★ 清空字段、停音效、解绑事件 —— 池不会替你重置 */ }
}

RevPool.GetAsync<Bullet>(b => b.transform.position = muzzle.position);     // 真机首次 / WebGL 必须异步
RevPool.Return(bullet, delayFrames: 5);                                    // 等 5 帧再回收（特效播完）
RevPool.DestroyGroup(RevResGroup.Battle);                                  // 退出战斗：先销池，再 Shutdown(Battle)
```

- 纯 C# 对象（消息体 / 伤害数字）用 `RevRefPool.Get<T>()`（`where T : class, IRevPoolable, new()`）。
- **`OnPoolReturn` 不清字段 = "上次的数据还在"**（池化最经典的事故）。取出顺序是先 `OnPoolGet` 再激活。
- `Clear*` 只清空闲实例（池保留）；`DestroyPool*` / `DestroyGroup` / `DestroyAll` 才连借出中的实例与 prefab 引用一起收。池不自动 Trim、没有 LRU，取用 LIFO。
- **UI 面板不要用 `RevPool`**（有专用面板池 + `OnReuse` 契约）。
- **抄谱**：`RevObjectPool.Demo`（取出 / 归还 / 池统计 `DumpStats` / 引用池生命周期 / 一键清空）。

## R10 读配置表（`RevDataTableManager`）

```csharp
HeroTable tbl = await RevDataTableManager.LoadAsync<HeroTable>();         // 失败抛 RevDataTableLoadException
if (tbl.FindByKey(1001, out Hero cfg)) { /* 用 cfg */ }
// 或：不想 try/catch → RevDataTableManager.LoadAsync<HeroTable>((table, reason) => { … });
```

- 表由导表工具生成（`Revolution.Tools/配置表/导表工具`，只改数值用"快速导出（仅数据）"，不触发脚本编译）。
- **数据文件必须有 AB 标记且扩展名不被 `excludeExtensions` 排除**，否则不进 ResMap，报 `PathNotMapped`（编辑器看不出，真机才炸）。
- 字符串取表用 `TableName`（如 `"Buff"`）不是类名；同类型并发请求共享同一份加载任务。
- 导表限制：只支持 `.xlsx`；字段只支持 `int` / `float` / `string` / `bool`；单主键且必须是第一个字段；坏表会被**跳过而不是中止**，导出后看日志有没有 `[提示]`。
- **抄谱**：`RevDataLoad.Demo`（手写版容器 / `LoadText` 容错解析 / 主键重复保留先出现的 / `FindByKey`）。

## R11 加载图标等资源并释放（`RevResManager`）

```csharp
RevResManager.LoadAsync<Sprite>(RevResPath.Data, "Hero_1002", s => image.sprite = s, RevResGroup.UI);   // 真机首次必须异步
RevResHandle h = RevResManager.LoadHandle<Sprite>(RevResPath.Data, "Hero_1001", RevResGroup.UI);        // 要失败原因用这个
if (!h.IsLoaded) { RevLog.Error($"加载失败：{h.ErrorReason}", "Res"); }
RevResManager.Release(RevResPath.Data, "Hero_1001");                                                     // 成对释放（面板 OnClose / 状态退出）
```

- ★ **路径是"相对资源根目录的逻辑段"**，不是工程全路径（写成 `Assets/…` 会拼成 `Assets/…/Assets/…` 找不到）。
- `Load<T>` 失败**静默返回 null**；判失败看 `ErrorReason`，别靠 `== null` 猜。同路径换一种类型加载会报 `TypeMismatch`。
- 引用归零进延迟释放队列，读条期 `FlushUnused()` 真卸；Unknown 仅在活动域启用 TakeOverOwner 且 Reclaim 非 Unknown 时补为域回收组，其它情况需显式分组。
- **抄谱**：`RevResourceSystem.Demo`（根目录 / Load 缓存 / 引用计数归零才卸载 / 句柄与失败原因）。

## R12 播音效 / BGM（`RevSound`）

```csharp
// Demo 原样（RevSoundSystemDemo.cs）：注册目录 + 失败可见
RevSound.Register("sfx_high", "demo/路径/sfx_high.wav", RevSoundKind.Sfx);
RevSound.Failed += (name, reason) => Ui($"[Failed] {name}：{reason}");     // 框架自身不打日志，要日志自己接 Failed

RevSound.Play("ui_click");                       // 2D
RevSound.PlayAt("explosion", hitPoint);          // 3D 定点
RevSound.PlayBgm("login", fadeSeconds: 1f);
RevSound.Preload("ui_click");                    // 首次播放是异步的，想零延迟先预加载
```

- **没有"现在有几条在播"的查询 API**，观察靠 `VoiceFinished` / `Failed` 事件；`PlayOn(name, target)` 的 `target` 不能为 null（`NoTarget`）。
- 小游戏 / WebGL：首次播放需用户手势解锁；切后台会被打断，`RevAppLifecycle.Resumed` 里重播 BGM（见 `startup-and-platform.md` §4）。
- `RevSoundScope` 只停声音不卸资源；资源用 `Unload` / `UnloadAll`（不会打断正在播的声音）。
- **抄谱**：`RevSoundSystem.Demo`（目录注册 / Play / PlayAt / PlayBgm / 音量体系 / 静音 / 失败可见）。

## R13 开箱演出等多段流程（`RevSequence`）

```csharp
static readonly RevSequenceDefinition ChestOpen = RevSequence.Create("宝箱开箱")      // 蓝图只 Build 一次并缓存
    .Do("播放开箱动画", ctx => ctx.SourceAs<Animator>().Play("open"))
    .Wait(0.5f)
    .Parallel("一起飞", p => p.Do("飘字", ctx => Tip.Show("+10 钻石")))
    .WaitTask(ctx => ctx.Require<IProtocolService>().RequestAsync(uid), "等服务器回包")
    .Finally(f => f.Do("恢复镜头", ctx => ctx.Require<ICamera>().Restore()))           // 跑完 / 取消 / 出错都执行
    .Build();

RevSequenceHandle handle = ChestOpen.Play(chestObject);                    // 一行播放；handle.Stop() 取消；handle.IsValid 查是否还在跑
bool completed = await ChestOpen.PlayAsync(chestObject);
```

- 要"触发源绑定 / 事件绑定 / 自己控制 Tick"才用 `RevSequenceRunner`：`new RevSequenceRunner(services, new RevSequenceEventBus())`、`runner.Bind(source, seq)`、`runner.BindEvents<TEvent>(bus, seq, e => e.Opener)`；★ runner 的 `Tick(dt)` 是唯一驱动入口（快进 / 慢放 / 暂停由调用方决定，不读 `Time.time`）。
- **状态别存在构建期字段里**（用业务服务 / Tween 起始值 / 步骤状态槽），否则两个玩家同时触发会互踩；占用了什么就在 `.Finally` 里还回去。
- 不做条件分支 / 跳转：分支写进步骤里的 `if`，复杂决策用状态机。**判据**：能用"先 A 后 B、B 要等条件、中途可能取消"说清的用序列；必须说"如果…就回到第 3 步"的用状态机。
- **抄谱**：`RevActionSequence.Demo`（宝箱触发源 / 事件触发移动门 / 取消收尾 / `RunFinished` 观察）。

## R14 流程 / AI 状态切换（状态机）

```csharp
// Demo 原样（RevStateMachineDemo.cs 100-105）
Machine.Register(new DemoPatrolState(this));      // ★ 启动期注册一次，实例被复用；别每次 new
Machine.Register(new DemoChaseState(this));
Machine.StateChanged += (from, to) => Log($"{from.GetType().Name} → {to.GetType().Name}");
Machine.ChangeTo<DemoChaseState>();               // 重复切到当前状态会被框架拦下
Machine.Update(Time.deltaTime);                   // ★ 不自动驱动：宿主每帧调；纯 C# 宿主用 RevMono.AddUpdate
```

| 要什么 | 选 |
|---|---|
| 游戏流程 / 玩法阶段（要异步切换 `ChangeToAsync`） | `RevLightFSM<T>`（状态继承 `RevLightStateBase`） |
| 界面栈 / 返回式流程（`Push` / `Pop`，不做异步切换） | `RevLightStackFSM<T>`（`Change(A)` 只替换栈顶） |
| Boss / 小怪 AI（枚举 + 宿主接口，三路驱动，带切换历史） | `RevHeavyFSM<TStateEnum, TOwner>`，宿主实现 `RevIHeavyFSMOwner` |

- **防重入**：`OnEnter` / `OnExit` 里再切状态会抛异常。异步切换只保留最新目标，`await` 后用 `CurrentState` 确认是否真切过去。
- 不做分层：要嵌套就组合多台；没有可视化调试窗口 / 快照回放。
- **抄谱**：`RevStateMachine.Demo`（巡逻 → 追击 → 攻击自动流转 / `StateChanged` / 重复切换拦截）。

## R15 加一条运行时 GM 调试指令（`RevGM`）

```csharp
// ★ [RevGMEntry("这批命令是什么")] 标在【静态注册方法】上，作用只有一个：让 GM 面板在【不进 Play】时也能看到命令清单
//   （编辑期用 TypeCache 找到它、调用一遍、快照清单后立刻清空注册表）。命令本身仍然靠 RevGM.Register 注册。
[RevGMEntry("演示：经济 / 战斗 / 聊天 / 工具")]
public static void Register()                        // 必须 static；方法里【只做注册】，不要初始化业务
{
    // Demo 原样（RevGMCommandDemo.cs）
    RevGM.Register("经济/清空金币", "把金币清零", args => { _gold = 0; });
    RevGM.Register("经济/加金币", "给当前玩家加金币",
                   args => { _gold += args.Int(0, 1000); return $"金币 = {_gold}"; },
                   RevGMArg.Int("数量", 1000));
}
// 业务拒绝：throw new RevGMUsageException("为什么不行")  —— 原样显示在面板上，别静默 return
```

- **不加 `[RevGMEntry]` 也能用**：Play 模式下面板读运行期注册表，游戏自己注册了什么就是什么；加了只是让**编辑模式**也能看清单（`[RevGMEntry]` 方法非 static 会在面板里标红报错）。

- ★ **默认值是两处**：面板显示"(默认 1)"靠 `RevGMArg`，"不传时实际用多少"靠 `args.Float(0, 1f)` 的第二个参数；写成 `args.Float(0)`（= 0）就会"面板说 1、实际变 0"。
- 命令名不能为空 / 不能含空白字符 / 不能只有分隔符（分组用 `/`）；重名注册直接抛异常。`RevGMFlags.HighRisk` 只是提醒二次确认，**不是权限**（真权限归服务端）。
- 编辑器面板：`Revolution.Tools/GM 指令面板`（Ctrl+Shift+G）；**没点 Play 不执行命令**，只能看清单。中文联想要打中文子串（不支持拼音缩写）。
- **抄谱**：`RevGMCommand.Demo`（无参 / 整数 / 小数 / 开关 / 枚举 / 字符串参数 / 高危 / 隐藏）。

## R16 新项目启动入口 + 切后台重连

直接用 `templates/RevBoot.cs.txt`；细节与自检清单见 `startup-and-platform.md`。要点：`RevInitialize.Initialize()` 只调一次；切后台用 `RevAppLifecycle.Resumed`（`owner: this`），按 `LastBackgroundSeconds` 判断是否重连 / 重算 / 重播 BGM；数据随存随写（`Quitting` 移动端不保证触发）。

## R17 接资源热更

放在"加载任何业务资源之前"：`await RevHotUpdate.InitializeAsync(config, onProgress)`。要先在资源策略注册前装配钩子时走 `RevInitialize.Initialize(() => …)` 的前置回调。全流程与 8 项破坏性测试见 `hot-update.md` / `verify-and-test.md`；**先跑本地假 CDN Demo**（菜单 `Revolution.Tools/热更新/Demo/① 一键…` → `起本地CDN.cmd` → `RevHotUpdateDemo.unity`）。

- ★ Demo 里两个最容易踩的坑：**编辑器默认直读，热更下来的包永远用不到**（要 `RevResBootstrap.UseABInEditor = true`）；**更新完不 `Release` 再 `Load` 会命中旧缓存**。

## R18 给新模块写一个 Demo

1. 复制 `templates/RevXxxDemo.cs.txt` → `Assets/Revolution.Demo/RevXxx.Demo/RevXxxDemo.cs`；场景 = Main Camera + Directional Light + `RevDemoRoot`（挂脚本）。
2. 按钮从"最简 → 进阶 → 边界与失败"排列；**失败也要演示**（把 `ErrorReason` / 返回值打出来）；右侧步骤日志讲清每一步发生了什么。
3. `OnDisable` 里清 `owner: this` 注册的东西（计时器 / 事件 / 每帧回调）。
4. **登记**：`Assets/Revolution.Demo/RevDemoSceneList.txt` + `Assets/Revolution.Demo/README.md`（场景索引表）。
5. 常规 Demo 的 asmdef 引用 `Revolution.Runtime`，不访问 internal；热更 Demo 还需要扩展包程序集。命名空间用 `Revolution.Demo.<模块>`，不要用会遮蔽引擎成员的 `Input`。

## R19 游戏流程 × FSM × 自定义域

1. 业务侧集中定义 `GameDomain` 的 `const string`；可继续用预置 `RevDomain.Lobby/Battle`，不用给框架新增枚举值。
2. **大厅常驻、进战斗后可返回**：选 `RevLightStackFSM<T>`。大厅 `Push` → `OnEnter` 开大厅域；战斗 `Push` → 大厅 `OnSuspend` 关闭大厅 Tick，战斗 `OnEnter` 开战斗域并启动 `[RevLogic(战斗域)]` 业务。
3. 战斗结束 `Pop` → 战斗 `OnExit` 只关保存的战斗域句柄 → 大厅 `OnResume` 恢复 Tick；退出登录 `Clear` 自顶向下触发各状态 `OnExit`。每帧由宿主调用 `Flow.Update(dt)`，Logic 的 `OnTick` 由框架驱动。
4. **Boot → 登录 → 大厅 → 战斗互斥阶段**：选 `RevLightFSM<T>`；每次 `ChangeTo` 旧状态 `OnExit` 关旧域、新状态 `OnEnter` 开新域。异步切换先确认实际目标已交割，不抢先关闭旧域。
5. 保存并检查 `Enter` 返回句柄不是 `RevScopeHandle.Empty`；同名重入默认失败，复用模式也不新建嵌套层。状态只操作自己的域；关父域会先关闭上方子域。
6. 明确归属：域兜底需 `TakeOverOwner=true`；资源整组回收需非 `Unknown` 的 `Reclaim`。显式 owner 优先，需跟域结束的对象用 `Attach`，域服务显式 `owner: RevScope.Current`。跨域共用资源/全局心跳不要误归战斗域。
7. **暂停不等于回收**：`SetTickEnabled(false)` 停域 Mono/Logic Tick，不停 Timer，也不控制显式 owner 的普通 Mono。额外暂停需求用对应模块契约。

- **真实示例**：`Assets/Revolution.Demo/RevScope.Demo/RevScopeFlowDemo.cs`，挂任意场景点 Play；没有独立 `RevScopeDemo.unity`。不要在 Scope 内核接入 FSM，保持模块独立。
- **验收**：大厅 → 战斗 → 回大厅 → 退出登录；查看 `RevScope.Dump()`、判空后的 `LastCloseReport.ToText()` 与 `RevLogic.Count`。确认战斗归属被回收、大厅状态保留、全局心跳仍存活，退出后无域/Logic 残留；不宣称未执行的 PlayMode 已通过。
- 详细签名见 `modules-core.md` §12，权威说明 `Revolution.Document/作用域系统/作用域系统使用说明.md` 第六节。

## R20 项目选择横屏 / 竖屏 UI

- 在首次 `RevUI.Open` / `Preload` 前设置 `RevUISetting.CanvasOrientation`；默认 `Auto`，固定竖屏用 `RevUICanvasOrientation.Portrait`，横屏用 `Landscape`，完全保留 Scaler 用 `UseDesign`。
- 这是运行时根 Canvas 的设计缩放方案；不改 prefab 原件、不改设备方向、不自动重排面板或处理安全区。
- 静态 Prefab 预览手动配置预览 CanvasScaler 与竖屏 Game 分辨率；运行时验收走实际 `RevUI.OpenAsync`，不能把 Boot 设置当 Prefab Mode 自动适配。
- 完整规则和自带竖屏设计基准的边界见 `ui-and-layering.md` §1.5。

## R21 给关键业务留 GM 测试接口，供项目 AI/MCP 自动验收

先按 `Revolution.Document/编程范式/Revolution官方编程架构规范.md` §3–4 确定真实业务服务、受控测试环境和命令白名单。**项目自行实现的流程控制者**持有 FSM 并负责域切换；GM 只调用它与玩家入口共用的方法。一个业务路径至少提供“查询前置/后置状态 + 执行受控操作 + 非法前置应失败”三种可断言证据，不要把 GM 当第二套业务逻辑。

```csharp
// 项目示意：IRevGameFlow 和实现它的服务由业务方定义，不是框架自带 API。
public static class GameFlowGmCommands
{
    [RevGMEntry("受控游戏流程测试命令")]
    public static void Register()
    {
        RevGM.Register("流程/快照", "读当前业务流程", args => RevServices.GetRequired<IRevGameFlow>().Snapshot());
        RevGM.Register("流程/进入战斗", "测试环境从大厅进战斗", args =>
        {
            IRevGameFlow flow = RevServices.GetRequired<IRevGameFlow>();
            if (!RevScope.IsActive(RevDomain.Lobby)) { throw new RevGMUsageException("大厅域未打开"); }
            if (!flow.TryEnterBattle(out string reason)) { throw new RevGMUsageException(reason); }
            return flow.Snapshot();
        }, RevGMFlags.HighRisk);
    }
}
```

- **注册时机**：`[RevGMEntry]` 方法必须静态、无参、只做命令注册（编辑期会调用它生成目录）；handler 到 Play 时才取服务。Boot 在**受控测试环境**注册一次业务服务与这些命令，避免重复注册；参数描述 `RevGMArg.Int("数量", n)` 与 handler `args.Int(0, n)` 的默认值保持一致。
- **机器判定**：让项目自建适配器在主线程调用 `RevGM.Execute("流程/快照")` 等白名单命令，读取 `RevGMResult.Success/Message/ElapsedMs`，对比操作前后 FSM、`RevScope.Dump()`、判空后的 `LastCloseReport`、`RevLogic.Count` 与错误日志；非法转换要失败且状态不变。只注册命令不等于自动测试已经跑过。
- **执行通道与安全边界**：先读取 `automation-integration.md`。工程已安装 Revolution.Automation 时，使用 CLI + Editor 文件桥在图形 Play 中调用真实 GM 与 scenario，不重复新造文件桥；Unity MCP 是另一个可选观察通道，不假设已有 GM→MCP 工具或独立Player控制台。核对当前版本 CLI 的 confirm/环境门禁；`RevGMFlags.HighRisk` 不是权限控制，直接 `RevGM.Execute` 不应绕过项目授权。正式环境不注册变更类命令，不暴露任意代码执行、删档、强制卸载、真实支付或敏感数据。
- **验收**：合法登录/战斗/结算/退出与非法转移、异步取消都记录输入、结果和域/资源收尾证据；离线断言、Unity Play、AB/设备验收分别标记，MCP 未连通就明确写“未执行”。

## R22 玩法数值 / 说明文案 / 概率 / 关卡阵容：Excel → 导表 → 启动校验 → 强类型读取（**不写死在 C#**）

> 来由：RevGame 早期把 27 份棋子血攻、16 种增益的百分比、敌方成长系数、奖励权重、Boss 阵容全写在 `PieceCatalog.cs` / `Consts.cs` / `BattleService.*.cs` 里，注释还写着“占位，等 Excel 接入”。这违反“策划调数不该改代码”，也让文案（“攻击+25%”）和逻辑（`+= 0.25f`）各写一份。本节是把它整体迁到导表的做法，已在 RevGame 验证。

**1. 先判断什么必须进表（铁律 21）**

| 进 Excel | 留在代码（须注释写明原因） |
|---|---|
| 血/攻/走法/射程、星级倍率、增益与事件的数值·概率·冷却·持续时间、敌方成长系数、人数与上限、关卡/章节/Boss 阵容、奖励权重与强化量、战斗限时与节奏、**文案里出现的数字** | 与预制体/场景/美术绑定的结构常量（棋盘边长、章节按钮数、手牌槽位数、排序层）；纯表现的 Tween/飘字节流；算法内部的相对评分权重（如 AI 目标打分 +1000）；枚举本身 |
| 判断法 | 策划想调这个数，是否要程序改 C# 重编译？要 → 进表 |

**2. 表怎么分（一个工作表 = 一张表；第 1 行字段名 / 2 行类型 / 3 行说明 / 4 行起数据；第一列是主键；类型只有 `int/float/string/bool`）**

- **实体表**：一行一个实体（`PieceConfig` 按棋子外形、`PrepBuffConfig` 按增益、`ChapterConfig` 按章节）。**主键用代码枚举的名字**，装配时与枚举做“恰好覆盖一次”校验。
- **通用参数表**：`BalanceRule(Id, Value, Note)`，放整局通用标量，键名按“模块.参数”（`Battle.DurationSeconds`）。不要为每个小数字新开一张表。
- **规则表**：把“if/switch 分支”变成数据，例如补兵规则 `Every/Offset/MinIntelligence/Priority`、出场名单 `Core/Filler`、事件轮换 `Step→Kind`。
- 枚举值（职业、走法、事件种类）在 Excel 里写**枚举名字符串**，C# 里用 `Enum.TryParse` 校验后转换；**禁止存数字**（`Enum.TryParse("99")` 会通过，必须额外拒绝以数字/负号开头的串）。
- 一个“新增列”比“新增表”便宜：数值差异小的同类实体共用一张表，用 `Amount/Extra/Threshold` 这类通用列 + 说明页逐行解释含义。

**3. 文案与数值同源（防“文案写 25%、实际 30%”）**

说明文字写成模板，数字用占位符引用**同一行**的数值，加载时渲染：

```text
Detail:  本轮全体攻击加成+{Amount%}，之后落下的棋也享受      Amount: 0.25   →  “…加成+25%…”
```

`{名字}` 取数值本身，`{名字%}` 按百分数显示；未知占位符、未闭合 `{` 在装配时报错。**日志、战报、HUD 文案里出现的倍数/秒数同理**：`"（锁死×2）"`、`"每三秒集火"`、HUD 的 `* 10` 都要改成读参数生成。百分数显示先 `Math.Round(v*100, 3)` 再格式化，避免 `0.35f` 显示成 `34.99999%`。

**4. 运行时：一个入口装配 + 一个入口读取**

```csharp
// 启动加载阶段（GameFlow）：逐张异步加载 → 一次性装配校验。失败走过场失败出口，显示中文问题清单。
await RevDataTableManager.LoadAsync<PieceConfigTable>();   // …每张表一行
GameConfig.Initialize(new GameConfigTables { Pieces = RevDataTableManager.Get<PieceConfigTable>(), /* … */ });

// 业务读取：强类型只读，没装配立刻抛 InvalidOperationException（绝不静默返回 0）
float limit = Balance.BattleDurationSeconds;
PieceDef def = PieceCatalog.Get(PieceKind.Shield, StarTier.Two);   // 目录类保持原 API，内部改读配置快照
```

- `GameConfig.Initialize` 必须做的校验，**全部攒成一份清单一起抛出**（策划一轮改完，而不是玩一步炸一步）：表是否加载且非空；枚举 Kind 覆盖（缺行、多行、不认识的行都报）；数值有限（**必须显式拒 `NaN/Infinity`**，`NaN` 与任何数比较都是 false，会绕过区间检查）；区间；整数参数不能是小数；必填数值缺失；模板占位符；跨参数约束（`Min ≤ Max`）；`BalanceRule` 里**代码没读到的多余键**（多半是键名拼错，改了不生效）；与预制体绑定的行数（章节数）。失败时旧配置保持不变。
- 业务服务在启动入口注册时配置还没加载，所以**构造函数与字段初始化里不得读配置**；需要初始状态的用惰性属性（RevGame：`_unlockedKinds` 首次使用时从 `PieceConfig.UnlockedAtStart` 创建）。
- 战斗热路径不要按名称反复查表：领取/应用时把数值**缓存进字段**（RevGame 的 `_prepXxx`），热路径只读字段。
- 退出时先 `GameConfig.Reset()` 再 `RevDataTableManager.Unload<>()`（它会清数据并还掉资源引用），之后迟到的读取立刻报错而不是读到半卸载数据。
- `LoadAsync<T>()` 本身幂等（已加载直接返回缓存，在途请求共享同一个任务），但"启动只装配一次"的开关要在**成功之后**才置位，这样加载失败时玩家点返回还能重试真实加载。
- ★ **编辑器断言/工程外测试要直读 txt，就必须用编译开关把那个类挡在真机包外**（`#if UNITY_EDITOR || <自定义离线符号>`）。原因有两层：出包后 `Data/*.txt` 在 AB 包里，磁盘上没有散文件，`File.ReadAllText` 必然失败；而且自己拼路径读资源会绕过缓存、引用计数、分组卸载和热更（铁律 4）。它与运行时共用同一批生成表类、同一个解析器和同一个校验入口，差别只有"文本从哪来"，所以断言结论对真机仍然成立。**验证方法**：复用 Unity 的 `.rsp` 但去掉 `-define:UNITY_EDITOR` 再编译一次运行时程序集，0 错误才算证明运行时不依赖这个类。
- 非 Play 的编辑器断言之所以拿不到资源系统，是因为它没执行过 `RevInitialize.Initialize()`，策略链未装配；不要为了跑断言在非 Play 下硬初始化框架。

**5. 导表：必须用框架导表工具生成，不手写、不手改生成物**

- 在 Unity：`Revolution.Tools/配置表/导表工具`，把 `配置表/` **文件夹**（不是单个 xlsx）加进来；只改数值点“仅数据”（不触发脚本编译），增删字段/表点“导出”。
- 生成的 `*.cs` 在 `Generated/`、`*.txt` 在 `<资源根目录>/Data/`，**都要提交**；新增表后 Unity 编辑器版会自动生成资源映射，WPF 版或无头导出必须手动补 `ResMap.txt` 的 `Data/<表名>|<数据包名>|<表名>` 并跑一次 RevAB“仅生成映射”，否则**编辑器好用、真机 `PathNotMapped`**。
- **数据表要进 AB 包，AB 标记打在 `Data` 文件夹上，不要逐文件打**：Unity 的子资源继承文件夹的 `assetBundleName`，所以每个 `*.txt.meta` 里的 `assetBundleName` 应当是**空的**（新建 meta 时照抄同目录现有数据文件的 meta，不要自己填包名，填了反而和项目约定不一致）。包名由项目的打包脚本集中维护（RevGame 是 `GameResourceBuild.Groups` 里的 `{ "Data", "data_tables" }`，`markMode = ScanExisting` + 先清空再按目录打标），所以**新增表不需要改打包脚本也不需要改 meta**，自动落进同一个数据包。
- 装配前先核对三件事能对上，可写脚本离线检查（不需要开 Unity）：① 生成表 `base("Xxx")` → `ResourceRoot`（默认 `"Data/"`）+ `ResourceName` 推出的逻辑路径；② `ResMap.txt` 里有这条 `逻辑路径|包名|资源名`；③ 包名与打包脚本里该目录的包名一致。三者任一不符，真机就是 `PathNotMapped` / `FileNotExist`。
- **无头导出**（Unity 已占用工程时不能再开第二个批处理 Unity）：导表核心 `Assets/Revolution/Editor/RevExcelTool/Core/*.cs` 是纯 C#，加上运行时的 `RevDataTextFormat.cs` 即可编成控制台壳，按 `RevExcelService.Read → ExcelExportPlanner.Plan → Write` 的顺序调用，产物与 Unity 内导出一致。用它之前**先对一份已有表回归**：导出到临时目录，与工程里现有生成物逐字节比对（忽略“生成时间”行），一致再用。
- Excel 注意：`openpyxl` 写布尔会被读成 `TRUE/FALSE`（运行时 `ToBool` 认）；空的可选列留空即可（行内始终补齐到表头列数，不会被当坏行）；以 `#` 开头的工作表是说明页，不导出；**表名=工作表名=生成类名前缀，不要和已有类型重名**（导表会报）。

**6. 怎么证明“搬进表没改行为”和“改表真的生效”（三类证据缺一不可）**

1. **等价性**：搬迁前，把旧逻辑层（纯 C# 目录：Core/Data/Board/Pieces/Battle/Services）原样拷到工程外控制台，写一个**新旧共用的转储程序**，输出静态数据（图鉴、关卡、文案、参数）+ 固定种子的整局模拟的**全部事件流水**（含辅助开/关、多种子、Boss、奖励）。迁移后用同一程序跑新逻辑层，**逐字节比对**。允许的差异必须逐条白名单并说明（例如文案里“三秒”改成参数 `3`）。RevGame 实测：约 430 万行事件流水仅 1 种预期差异。
2. **生效性与防错**：写反向测试——改某个表值，断言图鉴/文案/时长/Boss 阵容/奖励随之变化；故意填错（枚举拼错、键缺失/多余、整数填 2.5、概率填 7、`NaN`、占位符拼错、删一行、编号改坏），断言装配抛出带原因的中文错误；未装配读取抛异常；装配失败不污染旧配置。**夹具里写“删一行”的精确文本时用数据文件真实的 `\n` 换行**（不是 `\r\n`），否则替换不到、测试自己变红。
3. **真实编译**：Unity 在占用工程时，复用 `Library/Bee/artifacts/*/Assembly-CSharp.rsp`（246 个引用 + 全部宏），把源文件换成磁盘最新脚本，用 Roslyn `csc` 编出临时 dll（输出到临时目录，不碰 `Library`）。纯逻辑层的 harness 编过 ≠ UI/Flow/Bootstrap/Editor 检查能编过——RevGame 就是这一步抓出 3 处只有真实编译才暴露的错误。

**7. 常见坑（都踩过）**

- 把 `bool` 开关改成“带数值字段”后，所有 `if (_flag)` 要改成 `if (_value > 0f)`，**消费点和复位点**都要同步；自测/诊断代码直接访问私有字段，改字段名会让它们编不过，也要一起改。
- 迁移时顺手把断言里的字面量也改成读配置（`== 2f` → `== 1f + 配置值`），否则策划一调数，自测就变红。
- 常量折叠与运行时计算**逐位一致**（`0.6f/1.35f`、`1f+0.3f==1.3f`），但别想当然：先写个控制台小程序验证再放心替换。
- 文案参数化会改变日志字面量，等价性比对里必须显式白名单，不能为了让比对变绿而偷偷改基线。
- 不要在 `Consts` 里留“已迁移数值的旧副本”：删掉它，让编译器替你找出所有遗漏的引用点。
- 命令行里直接写中文（PowerShell 参数、`python -c`）会被转码导致匹配失败；把逻辑写成脚本文件执行，中文路径用通配符/目录枚举定位。
