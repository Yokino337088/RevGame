# 启动引导 · 平台适配 · 代码裁剪（RevInitialize / RevAppLifecycle / RevStripGuard）

> 适用场景：写游戏启动入口（Boot）、处理切后台/重连、出包上真机、适配安卓与微信/抖音小游戏、排查"编辑器正常真机瘫"。
> 权威文档：`Revolution.Document/一键初始化/一键初始化说明.html`、`应用生命周期/应用生命周期使用说明.md`、`代码裁剪/代码裁剪使用说明.md`、`Revolution 投产评估（安卓 + 微信抖音小游戏）.md`。

## 1. 启动入口：`RevInitialize.Initialize()`（一次就够）

- 门面：`Revolution.RevInitialize`（static）· `Runtime/RevInitialize/RevInitialize.cs`
- **在启动场景的 Boot 脚本里调一次**：

```csharp
public sealed class RevBoot : MonoBehaviour
{
    private void Awake()
    {
        RevInitialize.Initialize();     // 幂等：重复调用直接返回；失败会恢复"未初始化"并重新抛出，修好原因可重试
    }
}
```

- 它**只装配资源系统策略**（等价于旧写法 `RevResBootstrap.Instance.Init()`，不要再直接调那个）；之后 `RevUI` / `RevPool` / `RevDataTableManager` 才能按资源路径加载。
- **不要**期待它"把所有模块都初始化一遍"：`RevEvent` / `RevInput` / `RevTimer` / `RevUI` / `RevPool` / `RevAppLifecycle` 等已经由各自的 `RuntimeInitializeOnLoadMethod` 钩子或首次使用自动就位，手动重复启动反而可能清掉监听、取消加载。
- `RevServiceLocator` 由**业务组合根**自己装配（框架不能替业务决定注册什么）。
- 扩展接缝：`Initialize(Action beforeResourceInitialization)` —— 需要在资源策略注册前装配扩展钩子（例如热更的 ResMap / 包路径钩子）时，把装配写进回调；回调只在本会话第一次成功初始化时执行。**回调里不能再调 `Initialize()`**（会抛 `InvalidOperationException`）。
- 骨架见 `templates/RevBoot.cs.txt`。
- UI 方向按项目需要，在首次 `RevUI.Open` / `Preload` 前设置 `RevUISetting.CanvasOrientation`（默认 `RevUICanvasOrientation.Auto`；可选 `Portrait` / `Landscape` / `UseDesign`）。这只选择运行时根 Canvas 的设计缩放，不强制设备方向，也不做安全区或布局重排；Prefab 编辑预览与运行时链路的区别见 `ui-and-layering.md` §1.5。
- 推荐基线仍为 **Unity 2022.3 LTS**；`ProjectSettings/ProjectVersion.txt` 是本地编辑器记录，已由 `.gitignore` 忽略，不指示用 `git checkout` 恢复所谓受控版本。版本 / 设置 guard 的现有冲突与验证边界见 `verify-and-test.md`，不要把 guard 告警直接当成当前改动引入的编译错误。

## 2. 应用生命周期：`RevAppLifecycle`（切后台 / 回前台 / 退出）

- 门面：`Revolution.RevAppLifecycle`（static）· `Runtime/RevAppLifecycle/Facade/RevAppLifecycle.cs`；零配置，自动建隐藏宿主。
- 本质是 3 个 `RevEvent` 事件名常量 + 3 个只读状态：

```csharp
RevAppLifecycle.Paused      // "App.Paused"   进后台 / 失焦（0 参数事件）
RevAppLifecycle.Resumed     // "App.Resumed"  回前台 / 重新获得焦点
RevAppLifecycle.Quitting    // "App.Quitting" 正在退出
bool  RevAppLifecycle.IsBackground       // 当前是否在后台
float RevAppLifecycle.LastBackgroundSeconds   // 最近一次后台时长（秒；UtcNow 差，不是 realtimeSinceStartup）
int   RevAppLifecycle.PauseCount         // 累计切后台次数（排查"怎么老掉线"先看它）
```

- 用法（业务订阅，`owner: this` 让对象销毁时自动摘）：

```csharp
RevEvent.AddEventListener(RevAppLifecycle.Resumed, OnResumed, owner: this);
private void OnResumed()
{
    if (RevAppLifecycle.LastBackgroundSeconds > 60f) { Reconnect(); }     // 长连接早断了
    // 体力/活动/签到按"后台时长"重算；校准服务器时间：RevTimer.SyncServerTime(...)
}
```

- 铁律 / 坑：
  1. **去重已做**：移动端 `OnApplicationPause(true)` 与 `OnApplicationFocus(false)` 会一前一后到达，框架只在"前后台状态真翻转"时派发一次——**业务不要自己再去重**。
  2. **`Quitting` 移动端不保证触发**（被系统杀掉没有任何回调）→ 需要落盘的数据**随存随写**，别指望"最后存一次"。
  3. 后台时长必须用真实时钟（`DateTime.UtcNow`），引擎时间在后台会停走；自己算后台时长时别用 `Time.realtimeSinceStartup`。
  4. `SetBackground` / `NotifyQuitting` / `ResetForNewSession` 是**给宿主调的**，业务不要直接调。
  5. 暂无 Demo 场景（更新日志已声明），验证靠真机或手动切焦点。

## 3. 代码裁剪（IL2CPP Managed Stripping）：主推**关掉**

- **主推方案**：`Project Settings → Player → Other Settings → Managed Stripping Level` 改 `Disabled`（至少 `Minimal`）再出包。省的只是包体（代码占比通常不高），赔的是"**编辑器好好的、真机静默失效**"（刚体不响应重力、`Physics.Raycast` 恒 false、面板空白、动画不动、没声音）。代价只有包体，**出两包比差值 3 分钟就能测准**。
- 为什么 `link.xml` 也不够：有三类代码天然覆盖不全 —— **反射**（`[RevBind]` / 事件方法 / `[RevUIPanel]`）、**泛型**（`RevUIPanel<TData>` / `RevSingleton<T>`）、**场景里挂的原生组件**（刚体/碰撞体/Animator 的真实现在 native，托管 wrapper 被裁而场景那半边还好好挂着）。
- 万一必须开裁剪，框架有三层护栏：
  1. 框架自带 `Assets/Revolution/link.xml`：固定保住 `PhysicsModule` / `Physics2DModule` / `UIModule` / `AnimationModule` / `AudioModule` / `ParticleSystemModule` + UI 反射装配链与单例反射构造；
  2. 菜单 `Revolution.Tools/平台/生成 IL2CPP 裁剪保护 (link.xml)`：扫工程所有 `RevUIPanel` / `RevUIPart` / `RevSingleton` 派生类 + 源码里用到的原生模块，生成业务侧 `Assets/link.xml`（已有手写文件时弹窗确认，**不静默覆盖**；只写"本工程真的加载得到"的程序集）；
  3. `RevStripGuard`（自检）：见下。
- `RevStripGuard`（`Runtime/RevStripGuard/RevStripGuard.cs`）：真机进游戏（首个场景之前）逐个 `Type.GetType` 问"关键类型还在吗"，缺了用 `RevLog.Error` 报【缺什么 + 什么症状 + 怎么修】。

```csharp
int missing = RevStripGuard.Check();       // 0 = 通过；出包配置换了 / 升级 Unity / 加了新原生模块后，真机上主动验一次
bool ok = RevStripGuard.IsHealthy;         // IReadOnlyList<string> MissingTypes；string Dump()
```

  - ★ 编辑器下不跑（编辑器不裁剪，检查只会得到"全都好"的假结论）。
  - ★ **自检文件刻意只用字符串问 `Type.GetType`，不直接引用被检查类型**——直接引用会让裁剪器认为"有人用"，自检就永远通过。往 `WatchedTypes` 加条目时**保持字符串写法**。
  - 它是"降低故障概率的护栏"，**不是**"可以放心开裁剪"的许可证。

## 4. 小游戏 / WebGL / 安卓的约束清单（写代码前过一遍）

| 约束 | 原因 | 怎么做 |
|---|---|---|
| **没有多线程** | 小游戏单线程，`new Thread` / `Task.Run` 直接抛 `PlatformNotSupportedException` | 异步只用 `RevTask`（单线程 Promise，零 `Thread`）；**不要引入 `System.Threading.Tasks.Task`** |
| **同步加载不可用** | 无本地文件系统，AB 走 `UnityWebRequestAssetBundle` | 一律 `LoadAsync` / `RevPool.GetAsync` / `RevUI.OpenAsync` |
| **文件日志默认禁用** | 小游戏 `persistentDataPath` 是虚拟文件系统，"目录可写探测"会误判可用 | 框架已处理：`RevFileSink.SupportsBackgroundWriter`（WebGL 默认 false）；`RevLog.EnableFileLog` 在 WebGL 不会崩，而是把原因报到 `RevLog.SinkErrors` |
| **音频约束** | 首次播放需用户手势解锁、`AudioClip` 必须 Decompress On Load、切后台会打断需重播 | 查 `RevSoundPlatform.AudioManagedByPlatform` / `NeedsUserGestureUnlock` / `Note`；切回前台用 `RevAppLifecycle.Resumed` 重播 BGM |
| **平台名统一归 `WebGL`** | 小游戏构建目标（`XxxMiniGame`）→ 产物目录名 / 运行时主包名必须一致，否则首屏 404 | 框架三处已对齐：`RevABLoader.MainName` / `RevHotPlatform.Name` / `ABBuildSetting.GetPlatformName`（按名字判断，不依赖枚举）；**编辑器里平台名恒为 `PC`** |
| **AB 加载要有重试与超时** | 弱网 | `RevABLoader.MaxAttempts` / `RetryDelayMs` / `AttemptTimeoutSeconds`（WebGL 上 `UnityWebRequest.timeout` 不生效，框架改为按帧轮询 + `Abort`） |
| **引擎缓存标识** | 否则小游戏每次进游戏重下全部 AB | 钩子 `RevABLoader.BundleCacheKeyResolver` + `RevBundleCacheKey`；热更包从清单的 `unityHash` / `unityCrc` 注入 |
| **热更并发与明文 http** | 小游戏限制 | `RevHotConfig.Concurrency` 默认按平台（小游戏 2 / 其余 3）；小游戏/WebGL **禁明文 http**（不受 `AllowHttp` 影响）、并发 > 4 在配置期报错 |
| **首包预算** | 小游戏首包有体积上限 | 菜单 `Revolution.Tools/平台/首包体积审计 (Resources)` 列 `Resources` 目录文件数/体积（按体积排序，含最占地方的 10 个文件） |
| **AB 确定性** | 换 Unity 版本、同内容不同包 | 打包选项 `deterministic`（默认开）；`stripUnityVersion`（默认关，开了就要记住"换 Unity 版本先强制重打"） |
| **Android 显式材质 Shader** | Prefab 已加载也可能因 Shader / 变体 / 图形 API 问题变紫 | 窗口与 `ABCIBuild` 已接 `ABValidator.ValidateAndroidShaders` 前置拦截；运行时查看 tag `Res` 的 Shader / `isSupported` 与主包、依赖包、目标包来源诊断；详见 `editor-tools.md` §2、`resource-and-ab.md` §5 |

> **源码能力 ≠ 真机验收**：2026-10-16 已核实 Android Shader 前置门禁与运行时诊断源码，不代表已跑过 Android 真机、验证了变体裁剪或实际设备兼容。AB / Excel 已有无头入口，但仓库质量 CI 未完成；Debug/Release 构建区分、完整 Player 出包链路与首包体积硬门禁仍不能宣称已有。历史《投产评估》的修复状态需与当前源码核对，热更加固范围见 `hot-update.md`。

## 5. 自检清单（上真机前）

- [ ] 启动入口调了一次 `RevInitialize.Initialize()`（不是到处 `RevResBootstrap.Instance.Init()`）。
- [ ] UI 方向在首次 `Open` / `Preload` 前按项目选定（默认 `Auto`）；设备方向、Prefab 预览、安全区 / 布局另行确认。
- [ ] Android：打包前 Shader 校验通过，并在目标设备核对材质表现与 tag `Res` 的包来源 / Shader 诊断；未实测时明确标注未做，不写成已验收。
- [ ] 有长连接 / 计时 / 体力的模块订阅了 `RevAppLifecycle.Resumed`，并用 `LastBackgroundSeconds` 判断是否要重连/重算。
- [ ] Managed Stripping Level 已确认（推荐 `Disabled`）；开了裁剪的话已生成 `Assets/link.xml` 且真机看过 `RevStripGuard` 输出。
- [ ] 小游戏目标：没有 `Thread` / `Task`；没有同步 `Load` / `Open<T>()` 假设；音频首次播放有用户手势；热更用 https。
- [ ] 打包产物目录名与运行时主包名一致（小游戏 = `WebGL`）；编辑器里验证热更前已 `UseABInEditor = true`。
