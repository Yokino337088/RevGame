# 模块速查（一）：基础模块 / 游戏域 / 业务单元

> 本页公开入口均在 `namespace Revolution`；路径相对 `Assets/Revolution/Runtime/`。新增 `RevScope` / `RevLogic` 见 §12。
> 这里只列"最常用 + 最容易写错"的部分；完整 API 看源码 Facade 与对应《使用说明》（`references/docs-map.md`）。
> **阅读顺序建议**：写代码前先看该节"铁律"，再照"最小示例"写。

---

## 1. RevLog —— 日志（唯一日志出口）

- 门面：`Revolution.RevLog`（static）· `RevLog/Facade/RevLog.cs`
- 职责：五级别日志 + tag 过滤 + 重复抑制 + 环形缓冲 + 可插拔通道（文件/自定义 sink）。
- 常用 API：

```csharp
RevLog.Debug(string message, string tag = null)      // ★ 编译期删除（正式包不执行，参数也不求值）
RevLog.Info / Warn / Error(string message, string tag = null)
RevLog.Exception(Exception e, string message = null, string tag = null)
RevLog.MinLevel { get; set; }        RevLog.IsEnabled(RevLogLevel level, string tag = null)
RevLog.MuteTag(string tag, bool muted = true)
RevLog.EnableFileLog(string directory, string prefix = "revlog")   // 返回 RevFileSink
RevLog.Flush()                       RevLog.Recent(int count = 50)      RevLog.Dump(int count = 200)
RevLog.AddSink(IRevLogSink sink)     RevLog.SinkErrors { get; }
```

- 最小示例：

```csharp
RevLog.Warn("配置缺失，用默认值", "Config");
if (RevLog.IsEnabled(RevLogLevel.Info, "Net")) { RevLog.Info($"{state}", "Net"); }   // 热循环先判再拼
RevLog.Exception(e, "战斗开始失败", "Battle");
```

- 铁律 / 坑：
  1. `Debug` 是**编译期删除**，正式包看不到 —— 想留就定义 `REVLOG_DEBUG`。
  2. 热路径别直接拼字符串：贵的不是过滤，是参数构造 → 先 `IsEnabled`。
  3. 文件通道是**异步**的：切场景 / 退后台 / 出包前 `Flush()`。
  4. 连续相同日志会被抑制成"首条 + 重复 N 次"，看着像丢日志；`SinkErrors > 0` 说明某个通道在坏。
  5. **框架各模块的日志都汇到这里**（`RevTimer.Log` / `RevEvent.Log` / `RevPool.Log` / `RevScene` 失败原因…），排查从这里开始。
- 文档：`Revolution.Document/日志系统/日志系统使用说明.md` · 模块 README：`Runtime/RevLog/README.md`

---

## 2. RevTimer —— 计时器

- 门面：`Revolution.RevTimer`（static）· `RevTimer/Facade/RevTimer.cs`
- 职责：到点做事 / 每 N 秒做事 / 到服务器时刻做事；四时间域（Scaled / Unscaled / Fixed / Server），零配置自动建隐藏宿主。
- 常用 API：

```csharp
RevTimerHandle After(float seconds, Action callback, RevTimeDomain domain = RevTimeDomain.Scaled, object owner = null)
RevTimerHandle NextFrame(Action callback, object owner = null)
RevTimerHandle Every(float interval, Action callback, int times = -1, RevTimeDomain domain = ..., object owner = null)
RevTimerHandle Every(float interval, Action<int> callback, int times = -1, ...)      // 带第几次
RevTimerHandle At(DateTime serverUtc, Action callback, object owner = null)          // 需先 SyncServerTime
int CancelAllOf(object owner)      int Clear()      RevTimerScope OpenScope()
void SyncServerTime(DateTime serverUtc, double realtimeSinceStartup = ...)           // 登录后同步一次
bool Paused { get; set; }          int Count { get; }      RevStopwatch StartStopwatch(...)
```

- 最小示例：

```csharp
RevTimer.After(2f, () => RevUI.Close<LoadingPanel>(), owner: this);
RevTimer.Every(1f, i => tip.text = $"{i}/10", times: 10, owner: this);
private void OnDestroy() { RevTimer.CancelAllOf(this); }        // 一行防泄漏
```

- 铁律 / 坑：
  1. **循环计时器必须留"能停"的路**：`owner:` 或 `using (RevTimer.OpenScope())`。"忘停"是唯一能拖死这个模块的错法。
  2. **时间域别选错**：UI 倒计时 / 超时 / 暂停菜单用 `Unscaled`（默认 `Scaled` 会被 `timeScale=0` 冻结）。
  3. `At(...)` 前必须 `SyncServerTime`，否则报 `NoServerTime`（框架不瞎猜）；`Paused = true` 是**整模块**开关。
  4. 回调在主线程 Tick 里**同步**执行：别干重活；`After(0)` 表示"下一帧"。
- 文档：`Revolution.Document/计时器系统/计时器系统使用说明.md` · 模块 README：`Runtime/RevTimer/README.md`

---

## 3. RevMono —— 公共 Mono（给纯 C# 类每帧回调 / 协程）

- 门面：`Revolution.RevMono`（static）· `RevPublicMono/Facade/RevMono.cs`
- 职责：**不摆物体**就能拿到 Update / LateUpdate / FixedUpdate 与协程能力。
- 常用 API：

```csharp
RevMono.AddUpdate(Action action, object owner = null)         bool RemoveUpdate(Action action)
RevMono.AddLateUpdate(Action action, object owner = null)     RevMono.AddFixedUpdate(Action action, object owner = null)
RevMono.Add(RevMonoPhase phase, Action action, object owner = null) / bool Remove(...)
int RemoveAllOf(object owner)      int Clear()      RevMonoScope OpenScope()
Coroutine StartCoroutine(IEnumerator routine)      void StopAllCoroutines()
int UpdateCount / LateUpdateCount / Count { get; }      bool IsRunning { get; }
```

- 最小示例：

```csharp
RevMono.AddLateUpdate(FollowCamera, owner: this);      // 跟随/相机用 LateUpdate
RevMono.AddFixedUpdate(TickPhysics, owner: this);      // 帧率无关用 FixedUpdate
private void OnDestroy() { RevMono.RemoveAllOf(this); }
```

- 铁律 / 坑：
  1. **选对相位**：跟相机 / UI 的跟随用 `LateUpdate`；物理、帧率无关用 `FixedUpdate`。
  2. 随对象销毁**一行清干净**：`owner: this` + `RemoveAllOf(this)`，或 `OpenScope()`。忘记移除 = 已销毁对象还在被回调。
  3. 回调跑在主线程 Update，**别做重活**（要异步用 `RevTask`）。
  4. 分工：`RevTimer` = 多久之后 / 每隔多久；`RevTask` = await 一帧 / 等资源；**本模块 = 每帧回调 / 协程**。
- 文档：`Revolution.Document/公共Mono模块/公共Mono模块使用说明.md`

---

## 4. RevPool / RevRefPool —— 对象池（两个门面）

- 门面：`Revolution.RevPool`（Unity 对象，`RevObjectPool/Facade/RevPool.cs`）、`Revolution.RevRefPool`（纯 C# 对象，`Facade/RevRefPool.cs`）
- 职责：一行取出、一行归还；Unity 对象池基于资源系统（端着 `RevResHandle`），C# 对象池靠 `IRevPoolable.OnPoolReturn` 复位。
- 常用 API：

```csharp
// ★ 推荐：按类型取（路径写在预制体脚本的 [RevPoolPrefab] 特性上；取用与清理都不写路径字符串）
[RevPoolPrefab("Battle/Bullet", "Bullet_Normal", Group = RevResGroup.Battle)]   // 名字省略 = 类名；特性不继承；同一预制体只能一个类声明
public class Bullet : MonoBehaviour { }
Bullet b = RevPool.Get<Bullet>(parent);                                   // 同步（池里有 / 已加载）
RevResHandle h = RevPool.GetAsync<Bullet>(b => b.transform.position = muzzle.position);   // 真机首次 / WebGL 必须用这个
int n = RevPool.Clear<Bullet>();           bool ok = RevPool.DestroyPool<Bullet>();      // 清空空闲实例 / 销毁整条池
// 类型上没写特性 → 报错（提示怎么加）并返回 null / 0 / false；回调版回调收到 null

// 手填路径的老写法仍可用（预制体上没有脚本、临时取一下时）
GameObject go = RevPool.Get("Battle/Bullet/Blue", firePoint);     // (rootPath, resName, parent, group)
T comp = RevPool.Get<T>(rootPath, resName, parent, group);        // 取组件
GameObject go2 = RevPool.Get(prefab, parent);                     // 持有 prefab 引用时（热路径更快；池不持有资源引用，prefab 生命周期由你保证）
RevResHandle h2 = RevPool.GetAsync(rootPath, resName, onFinished: go => {...});

bool RevPool.Return(GameObject item, int delayFrames = -1)        // delay=等 N 帧再回收（特效播完）；也有 Return(Component)
int RevPool.Clear(rootPath, resName) / ClearAll() / ClearGroup(group)       // 清空"空闲实例"，池与 prefab 引用保留
bool RevPool.DestroyPool(rootPath, resName) / DestroyPool(prefab)           // 销毁整条池 + 还掉 prefab 引用
int RevPool.DestroyGroup(group)       void RevPool.DestroyAll()              // 退出业务域 / 回登录用；DestroyGroup 后再 Shutdown(group)
int RevPool.MaxIdlePerPool { get; set; }   int RevPool.TrimAll(int keepCount)  // 空闲上限（默认 64，同时管 RevRefPool）/ 一次性收缩
RevPoolStats RevPool.GetStats(rootPath, resName)                  string RevPool.DumpStats()

T msg = RevRefPool.Get<ChatMessage>();                            // where T : class, IRevPoolable, new()
RevRefPool.Return(msg);      RevRefPool.Clear<T>() / ClearAll()      RevRefPool.GetStats<T>()
```

- 最小示例：

```csharp
var bullet = RevPool.Get<Bullet>(firePoint);
RevPool.Return(bullet, delayFrames: 5);

var msg = RevRefPool.Get<ChatMessage>();
msg.Init(sender, text);
RevRefPool.Return(msg);        // OnPoolReturn 里必须把字段清干净
```

- 铁律 / 坑：
  1. **`OnPoolReturn` 必须把字段清干净**：池对象既不销毁也不重置，"上次的数据还在"是池化最常见事故。
  2. **取用成对、类型/variant 一致**：找不到池多半是 variant 不一致（两个池）。按类型取时 `Get<T>` / `Clear<T>` / `DestroyPool<T>` 解析的是**同一条池**。
  3. **WebGL（含小游戏）同步加载不可用 → 必须 `GetAsync`**。
  4. 池内部按**引用相等**匹配（业务别重写 `Equals`）；只允许主线程。
  5. **`Clear` ≠ `DestroyPool`**：`Clear*` 只清空闲实例（池保留，之后继续用）；`DestroyPool*` / `DestroyGroup` / `DestroyAll` 连借出中的实例、延迟回收中的实例、prefab 引用一起收。退出业务域按"先 `DestroyGroup`，再 `RevResBootstrap.Instance.Shutdown(group)`"的顺序。
  6. `RevScene` 切场景前默认自动清对象池（`AutoClearPool = true`），不用手动调。
- 文档：`Revolution.Document/对象池/对象池使用说明.md`、`对象池架构解析.md`

---

## 5. RevEvent —— 事件（字符串事件名）

- 门面：`Revolution.RevEvent`（static）· `RevEventSystem/Facade/RevEvent.cs`
- 职责：一对多解耦；0~4 参数泛型派发；派发中可安全增删（标记删除）。
- 常用 API：

```csharp
RevEvent.AddEventListener(string name, Action handler, int priority = 0, object owner = null)
RevEvent.AddEventListener<T1>(string name, Action<T1> handler, int priority = 0, object owner = null)   // T1~T4
int RevEvent.DispatchEvent(string name)      int DispatchEvent<T1>(string name, T1 arg1)               // 返回"实际被调用的订阅数"
bool RevEvent.RemoveEventListener(string name, Action handler) / RemoveEventListener<T1>(...)
int RevEvent.RemoveAllByOwner(object owner)      void Clear(string name) / ClearAll() / ResetAll()
int RevEvent.GetListenerCount(string name)       bool HasListener(string name)      List<string> GetAllEventNames()
bool RevEvent.RethrowOnException / LogNoListener { get; set; }      Action<string> Log / OnException
```

- 最小示例：

```csharp
public static class GameEventId { public const string HeroSkinAdd = "Hero.Skin.Add"; }
RevEvent.AddEventListener<HeroSkinAddArgs>(GameEventId.HeroSkinAdd, OnSkinAdd, owner: this);
int n = RevEvent.DispatchEvent(GameEventId.HeroSkinAdd, new HeroSkinAddArgs(heroId, skinId));
if (n == 0) { RevLog.Warn("没人监听 Hero.Skin.Add", "Event"); }
private void OnDestroy() { RevEvent.RemoveAllByOwner(this); }
```

- 铁律 / 坑：
  1. **同一事件名只能有一种签名**：注册成别的类型派发会明确报"签名不匹配"（不静默丢弃）。
  2. **匿名 lambda 注册后按委托 Remove 匹配不上** → 存成字段，或用 `owner` 批量移除。
  3. 派发中反注册是安全的（只标记，最外层结束才收尾），但**回调里别做重活**。
  4. 只在主线程注册/派发；跨线程先切回主线程。
  5. 返回值有用：`DispatchEvent` 返回 `0` = **没人听**（排查"为什么没反应"的第一现场）。
- 文档：`Revolution.Document/事件系统/事件系统使用说明.md`、`事件系统架构解析.md`

---

## 6. RevServices / RevServiceLocator —— 服务定位器（依赖显式化）

**两个入口，先选对再写（90% 场景用第一个）**

| | `RevServices`（静态，**默认选它**） | `RevServiceLocator.Create()…Build()`（实例，进阶） |
|---|---|---|
| 写法 | `Register` / `GetRequired` / `Unregister` 三个静态方法；**不用静态类、不用链式、不用缓存字段** | 链式装配 + `Build()` 冻结，自己缓存容器 |
| 注销 | ✅ 随时注销，容器创建的服务会 `Dispose` | ❌ 冻结后不能改，只能整个容器 `Dispose` |
| 作用域（每局一份） | ❌ 只有全局一份 | ✅ `CreateScope()` |
| 每帧驱动 | ✅ **自动**（有可 Tick 服务时才挂，业务不调 `Tick`） | 自己调 `services.Tick(dt)` |
| 适合 | 全局服务、跟模块 / 场景生灭的服务、单测里换假实现 | 多局并存、单测里另建隔离容器 |

两套语义一致（懒创建 / `OnInit` 钩子 / 循环依赖报错 / 谁创建谁释放），可混用。

### 6.1 `RevServices`（推荐）

- 门面：`Revolution.RevServices`（**static**）· `RevServiceLocator/Facade/RevServices.cs`（文件头就是 3 分钟教程）；背后是内部类 `RevServiceHub`（活的服务表，不对业务暴露）。
- 常用 API：

```csharp
// 注册（一行；重复注册当场报错，不静默覆盖；都可加 owner: this）
void Register<TService, TImpl>(object owner = null, bool eager = false)  // 默认首次取用才 new；eager: true 注册返回前已创建并 OnInit；TImpl 要公开无参构造
void Register<TService>(TService instance, object owner = null)           // ★ 已有实例：注销时只摘表，【不】Dispose
void Register<TService>(Func<RevIServiceLocator, TService> factory, object owner = null)   // 需要参数 / 要先读配置；工厂里可取别的服务

// 取用
T GetRequired<T>() where T : class     // 必须有：没注册就抛异常（列出已注册的服务；注销过的会提示"曾被注销"）
T Get<T>() where T : class             // 可选：没有返回 null
bool TryGet<T>(out T service)          bool IsRegistered<T>()      // 只问有没有，不触发创建
RevIServiceLocator Locator { get; }    // 把"取服务入口"交给只认接口的底层模块

// 注销（释放内存）
bool Unregister<TService>()            // 摘表 + 不再被驱动 + 容器创建的实例 Dispose；没注册过返回 false（重复调用安全）
int  UnregisterAllOf(object owner)     // 对象销毁时一行清掉它注册的全部服务（逆创建序）
int  UnregisterAll()                   // 回登录 / 退出游戏；返回注销个数

// 驱动 / 诊断
void Tick(float deltaTime)             // Unity 下框架自动驱动，业务不用调；工程外测试可手动调
int  Count { get; }                    string Dump()               // 谁注册了什么、谁还占着内存
```

- 最小示例：

```csharp
RevServices.Register<ISoundService, SoundService>();                      // 启动入口：一行注册
RevServices.GetRequired<ISoundService>().Play("ui_click");                // 任何地方：一行取用

public sealed class BattleModule : MonoBehaviour                          // 跟着模块生灭的服务：谁注册谁注销
{
    private void Awake()     { RevServices.Register<ICombatContext, CombatContext>(owner: this); }
    private void OnDestroy() { RevServices.UnregisterAllOf(this); }
}
```

- 铁律 / 坑：
  1. **全局服务集中在启动入口注册；跟着模块 / 场景走的服务，谁注册谁注销**（`owner: this` + `UnregisterAllOf(this)`）。`RevServices` 是"活的"、不是只读的，靠这条纪律 + 重复注册报错 + `Dump()` 把关。
  2. **重复注册当场报错**：想换实现先 `Unregister` 再 `Register`；重进场景报"已经注册过了" = 上一次没注销，**补注销，别用"先试着注销一下"掩盖**。
  3. **谁创建谁释放**：`Register<I, T>()` / 工厂 → 注销时 `Dispose`；`Register<I>(实例)` → 只摘表。
  4. **注销后别人手里的引用不会变 null**（但服务已 `Dispose`）→ **不要把取到的服务长期存成字段**，要用就现取（一次字典查找，热路径 0 分配）。
  5. **服务默认第一次被取用时才创建**（也才开始 `OnInit` / 被 Tick）：想启动期就激活，可 `Register<I, T>(owner: this, eager: true)` 或注册后立刻 `GetRequired<I>()`。构造函数订阅首次引擎事件（如 Unity 图集 `atlasRequested`）必须在事件之前创建，等异步预热时再取会丢请求导致白图。`eager` 只在类型对类型重载上；立即创建失败撤销本次登记并原样抛异常，默认懒创建失败保留登记供重试。
  6. `owner` 传**引用类型**（通常 `this`），前后必须是同一个对象；别传值类型 / 临时对象。
  7. 不要在 `OnInit` / 工厂里注销自己（会报"创建过程中被注销"）。
  8. 同一个实例注册成多个服务类型：每帧只驱动 1 次；最后一个持有者注销才 `Dispose`。
  9. 进 Play 自动复位：关闭 Domain Reload 时上一局的服务会在新一局开始前被清掉（吞掉上一局 `Dispose` 的异常）。

### 6.2 `RevServiceLocator`（进阶：作用域 / 多实例 / 单测隔离容器）

- 门面：`Revolution.RevServiceLocator`（**实例**，不是 static）+ `RevServiceBuilder` · `RevServiceLocator/Facade/`
- 常用 API：

```csharp
RevServiceLocator locator = RevServiceLocator.Create(int expectedServiceCount = 16)
    .AddSingleton<TService, TImpl>()                                  // 容器负责创建与释放
    .AddSingleton<TService>(TService instance)                        // ★ 你创建的实例，容器不释放
    .AddSingleton<TService>(Func<RevIServiceLocator, TService> factory)
    .AddScoped<TService, TImpl>()                                     // 每局/每作用域一份
    .Build();                                                         // 装配结束，之后不可改

T GetRequired<T>() where T : class      T Get<T>()      bool TryGet<T>(out T service)
RevServiceLocator CreateScope()         void Tick(float deltaTime)      void Dispose()
```

- 最小示例：

```csharp
static readonly RevServiceLocator Services = RevServiceLocator.Create()      // ★ 这套要自己缓存容器
    .AddSingleton<ISoundService, SoundService>()
    .AddScoped<ICombatContext, CombatContext>()
    .Build();

using (RevServiceLocator battle = Services.CreateScope())          // Scoped 必须放进 using
{
    var ctx = battle.GetRequired<ICombatContext>();                // 每局不同的一份
    battle.Tick(Time.deltaTime);                                   // 这套要自己调 Tick
}
```

- 铁律 / 坑：
  1. **装配只写在一处**（组合根），`Build()` 之后任何地方都改不了；想"注销单个服务"请用 `RevServices`。
  2. `GetRequired` 用于**必需**依赖；`Get` / `TryGet` 只用于可选能力。
  3. **谁创建谁释放**：容器释放"工厂创建的服务"；`AddSingleton(instance)` 传进去的由你释放。
  4. 根容器上取 `Scoped` 服务会**直接报错**，必须放进 `CreateScope()` 的 `using`；作用域**不嵌套**（并发多局开多个平行作用域）。
  5. 分工：需要"每帧驱动 + 生命周期 + 逆序释放"的业务能力用服务；一次性工具类用静态方法即可。
- 文档：`Revolution.Document/服务定位器/服务定位器使用说明.md`（第五章专讲"为什么用服务定位器而不是 DI 容器"）· Demo：`Assets/Revolution.Demo/RevServiceLocator.Demo/`（按钮 A~D 演示 `RevServices`）

---

## 7. RevTask —— 轻量异步（框架唯一异步基建）

- 入口：`Revolution.RevTask` / `RevTask<T>`（readonly struct，带 `[AsyncMethodBuilder]`）· `RevTask/RevTask.cs`
  调度器：`RevTaskScheduler`（自动创建的 DontDestroyOnLoad 宿主）· 取消：`RevCancellationTokenSource` / `RevCancellationToken`
- 职责：**可 await、可传播异常、可取消**的轻量任务；替代协程 / UniTask；资源与场景加载都返回它。
- 常用 API：

```csharp
static RevTask Completed { get; }      static RevTask Delay(int milliseconds)      static RevTask Yield()
static RevTask WhenAll(params RevTask[] tasks)
static RevTask FromException(Exception e)      static RevTask<T> FromResult(T value)
static RevTaskCompletionSource CreateSource()  static RevTaskCompletionSource<T> CreateSource()
void Forget()                                  // 不 await 时必须显式 Forget（否则编译器警告）
// RevTaskScheduler：MaxContinuationsPerFrame（默认 128）/ Post / Yield / Delay / WhenAll
// RevCancellationToken：IsCancelled / Register(Action) / ThrowIfCancelled()
// RevTaskUnityExtensions：AsyncOperation.ToRevTask() / await someAsyncOperation
```

- 最小示例：

```csharp
private async void OnClickEnterBattle()
{
    try
    {
        await RevTask.Delay(100);
        token.ThrowIfCancelled();                       // 关键节点检查取消
        await RevScene.LoadAsync("Battle", p => bar.value = p);
    }
    catch (Exception e) { RevLog.Exception(e, "进入战斗失败", "Battle"); }
}
```

- 铁律 / 坑：
  1. **续体每帧限量执行**（`MaxContinuationsPerFrame`，默认 128）：一次唤醒几千个任务不会卡帧，但也别指望"立刻全跑完"。
  2. 不要在 awaiter 结构体里捕获状态（结构体拷贝 → 状态不共享）；需要共享状态用 `RevTaskCompletionSource`。
  3. 取消是"主线程轻量标记 + 回调"，不是 `CancellationTokenSource` 那套线程语义。
  4. 异步里只做"等待与编排"，重活分帧；`await` 之后的代码回到主线程。
  5. 事件/计时器/资源回调里不要 `await` 出长流程（先切场景/关面板会一脚踩空）—— 用 `owner` + 取消。
- 文档：**暂无专章**（写法见 `RevTask.Demo` 场景与源码注释；`RevScene` / `RevResManager` 的 async API 都以它为返回类型）

---

## 8. RevScene —— 场景

- 门面：`Revolution.RevScene`（static）· `RevScene/Facade/RevScene.cs`
- 职责：一行切场景，进度可接，切之前按约定清理（默认自动清对象池）。
- 常用 API：

```csharp
RevTask RevScene.LoadAsync(string sceneName, Action<float> onProgress = null, float minSeconds = -1f)
RevTask RevScene.LoadAsync(int buildIndex, Action<float> onProgress = null, float minSeconds = -1f)
void RevScene.Load(string sceneName)                 // 同步：会卡帧，只在"必须立刻切"时用
RevTask RevScene.ReloadAsync(Action<float> onProgress = null, float minSeconds = -1f)
string CurrentName / int CurrentIndex / bool IsLoading / float Progress / RevSceneLoadState State
Action<string> OnLoadStart / Action<float> OnProgress / Action<string> OnLoaded / Action<string> OnLoadFailed
bool AutoClearPool = true      float DefaultMinSeconds = 0f      bool VerboseLog
```

- 最小示例：

```csharp
RevScene.OnLoadStart  += name => loadingPanel.Open(name);
RevScene.OnLoaded     += name => loadingPanel.Close();
RevScene.OnLoadFailed += why  => RevLog.Error(why, "Scene");
await RevScene.LoadAsync("Battle", p => bar.value = p);           // 推荐：异步 + 进度
```

- 铁律 / 坑：
  1. 场景必须已加进 **Build Settings → Scenes In Build**，否则报错。
  2. `RevScene.Load` 同步切**会卡帧**；大场景一律 `LoadAsync` 且不要忘 `await`。
  3. `AutoClearPool = true` 已自动清对象池；你自己的 **UI / 音效 / 资源分组** 要在 `OnLoadStart` 里按约定收（`RevUI.CloseAll(...)` / `RevSound.StopAll()` / `RevResManager.UnloadGroup(...)`）。
  4. 发起 / 推进 / 收尾整体兜异常，否则模块会永久死锁（框架已修过这个 bug，业务侧别在回调里再抛）。
- 文档：`Revolution.Document/场景系统/场景系统使用说明.md`、`场景系统架构解析.md`

---

## 9. RevInput —— 输入

- 门面：`Revolution.RevInput`（static）· `RevInput/Facade/RevInput.cs`
- 职责：一行绑定、一行查询（键盘/鼠标/触屏/手柄/手势）；业务不写 `Input.GetKeyDown`。
- 常用 API：

```csharp
bool RevInput.Bind(string action, params RevKey[] keys)          bool Bind(string action, RevMouseButton button)
bool RevInput.BindAxis(string action, RevKey negative, RevKey positive)      bool BindNamedAxis(...)
void RevInput.SetDeadzone(string action, float deadzone)         void SetRepeat(string action, float delay, float interval)
bool RevInput.Pressed(string action) / Held / Released / Repeat(string action)
bool RevInput.PressedBuffered(string action, float windowSeconds = ...)      // 缓冲（连招/容错）
float RevInput.Axis(string action)      RevInputVector2 Vector(string action)
bool RevInput.Tap(out RevGestureEvent e) / Swipe(out ...) / HasGesture(RevGestureKind) / float PinchScale
RevInputBlock RevInput.Block(RevInputBlockKind kind = RevInputBlockKind.World, ...)      RevInputScope OpenScope()
void RevInput.ResetAll(string reason = null)
bool RevInput.IsPointerOverUI(int pointerId = ...)               string SaveBindings() / bool LoadBindings(string) / string FindConflicts()
```

- 最小示例：

```csharp
RevInput.Bind("Jump", RevKey.Space, RevKey.JoystickButton0);
RevInput.BindAxis("MoveX", RevKey.A, RevKey.D);
if (RevInput.Pressed("Jump")) { Jump(); }
float move = RevInput.Axis("MoveX");
RevInput.ResetAll("换场景");                                     // 宿主已处理切后台，换场景再补一次
```

- 铁律 / 坑：
  1. **先绑后用**：没绑过的动作**不报错**，永远返回 false（"按键没反应"的第一嫌疑）。
  2. 切后台 / 失焦宿主已自动复位，换场景再调 `RevInput.ResetAll("换场景")`。
  3. **弹窗用屏蔽、不要停模块**：`RevInput.OpenScope()` + `scope.Block()`；停模块会把 ESC / 返回键一起停掉。
  4. 不要用本模块点 UI（UI 走 UI 系统），用 `RevInput.IsPointerOverUI()` 分流。
  5. **`SetRepeat(action, delay, interval)` 里 `delay <= 0` = 关连发**（不是"立即开始连发"）；`SaveBindings ⇄ LoadBindings` 现已无损往返连发配置（文本格式有 `repeat:延迟/间隔`）。
  6. 屏蔽 `World` 期间**轴读数一并归零**（弹窗里角色不会继续移动），但按下时间仍记录（输入缓冲不丢）。
- 文档：`Revolution.Document/输入系统/输入系统使用说明.md`、`输入系统架构解析.md` · 模块 README：`Runtime/RevInput/README.md`

---

## 10. RevInitialize —— 统一启动门面

- 门面：`Revolution.RevInitialize`（static）· `RevInitialize/RevInitialize.cs`
- 职责：在启动入口调一次，**装配资源系统策略**；幂等；失败回滚为"未初始化"并重新抛出（修好原因可重试）。
- 常用 API：

```csharp
void RevInitialize.Initialize(Action beforeResourceInitialization = null)   // 前置回调：资源策略注册前装配扩展钩子（如热更），仅第一次成功初始化时执行
bool RevInitialize.IsInitialized { get; }
```

- 最小示例：`RevInitialize.Initialize();`（放在 Boot 脚本的 `Awake` / `Start`；完整骨架见 `templates/RevBoot.cs.txt`）
- 铁律 / 坑：
  1. 只管资源系统；**不要**期待它初始化所有模块（其它模块自动就位，手动重复启动反而清监听）。
  2. 前置回调里**不能递归调 `Initialize()`**（抛 `InvalidOperationException`）。
  3. 新代码别再直接调 `RevResBootstrap.Instance.Init()`。
  4. 关闭 Domain Reload 时，每次新 Play 会话会自动复位门面状态（`SubsystemRegistration` 钩子）。
- 文档：`Revolution.Document/一键初始化/一键初始化说明.html`（仅 html）· 详见 `startup-and-platform.md` §1

---

## 11. RevAppLifecycle —— 应用生命周期（前后台 / 焦点 / 退出）

- 门面：`Revolution.RevAppLifecycle`（static）· `RevAppLifecycle/Facade/RevAppLifecycle.cs`；零配置自动建隐藏宿主。
- 职责：把 Unity 的应用级回调收敛成 3 个 `RevEvent` 事件 + 3 个只读状态；对"Pause 与 Focus 同时到达"去重。
- 常用 API：

```csharp
const string RevAppLifecycle.Paused  = "App.Paused"       // 进后台 / 失焦（0 参数事件）
const string RevAppLifecycle.Resumed = "App.Resumed"      // 回前台
const string RevAppLifecycle.Quitting = "App.Quitting"    // 正在退出（★ 移动端不保证触发）
bool IsBackground { get; }   float LastBackgroundSeconds { get; }   int PauseCount { get; }
```

- 最小示例：

```csharp
RevEvent.AddEventListener(RevAppLifecycle.Resumed, OnResumed, owner: this);       // owner 让销毁时自动摘
private void OnResumed() { if (RevAppLifecycle.LastBackgroundSeconds > 60f) { Reconnect(); } }
```

- 铁律 / 坑：
  1. 去重已内置，**业务别自己写 `OnApplicationPause` / `OnApplicationFocus`**（会重复触发重连逻辑）。
  2. `Quitting` 不可靠 → 数据随存随写；后台时长用 `UtcNow`（引擎时间在后台停走）。
  3. `SetBackground` / `NotifyQuitting` / `ResetForNewSession` 是给宿主调的。
  4. 暂无 Demo 场景。
- 文档：`Revolution.Document/应用生命周期/应用生命周期使用说明.md` · 详见 `startup-and-platform.md` §2

---

## 12. RevScope / RevLogic —— 游戏域与业务单元

- 入口：`RevScope/Facade/RevScope.cs`、`RevLogic.cs`；业务基类 `RevScope/Core/RevLogicBase.cs`；域名与配置见 `RevDomainNames.cs`、`RevScopeDefine.cs`。
- 分工：FSM 管当前流程状态，Scope 管这段玩法的生命周期，Logic 管没有界面、只活一段玩法的业务；服务容器的 `CreateScope()` 是实例隔离，**不是游戏域**。
- 自定义域：在业务侧集中声明 `const string`（例如 `GameDomain.Dungeon`、`GuildWar`）；`RevDomain` 只是预置 `Global/Lobby/Battle`，不必修改框架，也不等于枚举资源组 `RevResGroup`。

```csharp
RevScopeHandle RevScope.Enter(string domain, RevResGroup reclaim = RevResGroup.Unknown)
RevScopeHandle RevScope.Enter(string domain, RevScopeOptions options)
int RevScope.Exit(string domain) / Exit(RevScopeHandle handle) / ExitAll()
void RevScope.Attach(object owner)      int RevScope.Detach(object owner)
void RevScope.SetTickEnabled(string domain, bool enabled)
bool RevScope.IsActive(string domain) / IsTickEnabled(string domain)
RevScopeHandle RevScope.Current / Get(string domain)      int RevScope.Count
RevScopeCloseReport RevScope.LastCloseReport      string RevScope.Dump()

T RevLogic.Start<T>() / Start<T>(object param) where T : RevLogicBase, new()
int RevLogic.Stop<T>() where T : RevLogicBase      int RevLogic.StopAll(string domain)
T RevLogic.Get<T>() where T : RevLogicBase      bool RevLogic.TryGet<T>(out T logic)
int RevLogic.Count      string RevLogic.Variant / Dump()
```

最小接法：先 `RevScope.Enter(GameDomain.Dungeon, RevResGroup.Battle)`，检查返回值不是 `RevScopeHandle.Empty`；业务类加 `[RevLogic(GameDomain.Dungeon)]` 并继承 `RevLogicBase`，再 `RevLogic.Start<T>()`。离开时 `RevScope.Exit(保存的句柄)`，或句柄 `Dispose()`。启动被拒绝/`OnStart` 抛错时返回 null 并记原因；同类型重复 Start 返回正在运行的实例。

- `OnStart()` / `OnStart(object)` 装配，`OnTick(float)` 可选且由框架驱动，`OnStop()` 只做业务收尾。使用基类 `After/Every/Listen/Listen<T>` 自动携带 `owner: this`；Stop 先解绑该 Logic 的事件、Timer、Mono、服务，再调 `OnStop`。资源与自持有对象仍需按契约收尾，不把基类解绑当作全部资源自动释放。
- 默认 `TakeOverOwner = true`：未显式传 owner 的 Timer/Event/Mono 归当前域，**显式 owner 优先**；显式归属对象需随域结束时用 `Attach(owner)`。全局连接/心跳显式 owner，避免误归业务域。
- `Reclaim` 默认 `Unknown`，不按组清资源/池；配置非 `Unknown` 才让资源加载漏斗的 `EffectiveGroup` 补上域回收组。关闭 `TakeOverOwner` 后不再兜底；不要把共用 UI/Config/Sound 组作为战斗整组回收目标。
- 服务注册**不自动归域**：域服务明确 `owner: RevScope.Current` 或给其 owner 调 `Attach`；多个相同接口并行实例仍用 `RevServiceLocator.CreateScope()`。
- 同名重入默认报错并返回 Empty；`ReenterIsError = false` 只是复用已有域。关闭非栈顶域会先关它上方的域；状态应保存句柄、只关自己的域。
- 关闭顺序：关域门 → Logic → UI → Mono → Timer → Event → Pool → Res → Services → 扫尾；步骤异常隔离。`LastCloseReport` 关过域才有值，排查前先判空。
- `SetTickEnabled(false)` 只停域兜底 Mono 和域 Logic 的 `OnTick`；**不暂停 Timer，也不控制显式 owner 的普通 Mono 回调**。暂停不是回收。
- `RevScope` 本体不依赖 FSM；联动写在业务状态的 `OnEnter/OnExit/OnSuspend/OnResume`。示例 `Assets/Revolution.Demo/RevScope.Demo/RevScopeFlowDemo.cs` 挂任意场景；没有独立 `.unity`。配方见 `playbooks.md` R19，文档 `Revolution.Document/作用域系统/作用域系统使用说明.md`。
