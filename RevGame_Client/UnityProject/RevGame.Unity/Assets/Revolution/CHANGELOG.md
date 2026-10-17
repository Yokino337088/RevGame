# 更新日志

## [未发布]

### 运行稳定性修复（2026-10-16）

- 修复静态审查的 **13 项严重问题 + 9 项潜在风险（F01–F13、R01–R09）**；完整对照与编译边界见仓库 `Revolution.Document/问题审查/Revolution框架问题修复记录_2026-10-16.md`（同名 HTML）。
- 资源与配置表：预加载监听异常隔离、Object 预热与具体类型并发兼容、表加载失败必有终态、按原始租约归还、卸载作废旧请求、新 Play 清理静态缓存、AB 依赖释放检查代际、补齐 WebGL/小游戏网络分支导入。
- UI 与声音：打开/关闭转场和实例化失败完整收尾、面板原始句柄防误释放；动画身份快照防重入越界，有限值校验与有界循环跳段防假死；池定期摘除已销毁借出对象；声音单名卸载回收 Pending 槽。
- 基础模块：日志上报异常与递归保护、任务调度宿主重建保留等待队列、分别注册的续体异常隔离与预算校验、服务/状态机/序列通知隔离、服务创建期间容器释放的回滚、修正裁剪自检类型名。
- 行为调整：`RevMono.Clear()` 仅清业务监听并保留框架驱动；状态/序列通知监听异常记录后继续派发；动画倍速负数/非有限数会抛参数异常。同步相关使用说明的 MD 与 HTML。
- 编译：真实 Unity 引用的 Runtime 全源码和 WebGL/微信/抖音条件分支均为 0 错误；完整生成解决方案附加 .NET 4.7.2 参数为 0 错误。默认 4.7.1 受现有 Rider 包引用版本阻断；未执行测试、PlayMode、IL2CPP 或真机回归。

### 新增

- **`RevServices`：服务定位器的"一行注册、直接取、可注销"入口**（`Runtime/RevServiceLocator/Facade/RevServices.cs`）：
  - 解决的痛点：原来的 `RevServiceLocator.Create()…Build()` 要自己写静态类 + `static readonly` 缓存容器 + 链式调用，而且 `Build()` 之后冻结、**没法注销单个服务**，注册过的服务会一直占着内存。
  - 用法：`RevServices.Register<ISound, Sound>()`（一行；另有"已有实例"和"工厂"两个重载，都可带 `owner: this`）· `RevServices.GetRequired<ISound>()` / `Get` / `TryGet` / `IsRegistered` · `RevServices.Unregister<ISound>()` / `UnregisterAllOf(this)` / `UnregisterAll()` · `RevServices.Dump()`（谁注册了什么、谁还占着）。
  - 语义：懒创建（第一次取用才 `new`，零反射）· 重复注册**当场报错**（不静默覆盖，换实现先 `Unregister`）· 注销时容器创建的实例 `Dispose`、业务传进来的实例**不碰** · 注销后再取会报错并提示"它曾被注销过" · 同一实例注册成多个类型时每帧只驱动 1 次、最后一个持有者注销才 `Dispose` · `OnInit` 失败回滚并 `Dispose`、注册保留可重试 · 个别 `Dispose` 抛错不影响其余（统一抛 `AggregateException`）。
  - 每帧驱动：实现了 `RevITickable` 的服务**自动被驱动**（Support 层按需向 `RevMono` 挂回调；没有可 Tick 的服务就一帧都不多跑；单个 `OnTick` 抛错只记日志、不影响其它服务）；进 Play 自动复位（关闭 Domain Reload 时上一局的服务会被清掉）。
  - 与 Builder 写法的关系：两套语义一致、可混用。**只有"同一个服务类型要每局 / 每个场景各一份（作用域）"或"单测另建隔离容器"才需要 `RevServiceLocator.Create()…Build()`**；`RevServices` 只管全局一份。原 Builder API 完全保留、行为不变。
  - 取舍（已写进文档 5.6）：`RevServices` 必须能注销，所以不再是"装配后只读"；纪律改为"全局服务集中在启动入口注册、跟着模块 / 场景走的服务谁注册谁注销（`owner`）"，并靠重复注册报错 + `Dump()` 把关。
  - 验证：工程外链接真实源码跑 55 条断言全过（含热路径 10 万次 0 字节分配）；**Unity 内编译与 Demo 场景未跑**（需要在 Unity 里刷新工程后确认）。
  - 同时：`RevServiceLocator.Demo` 新增按钮 A~D 演示新入口；《服务定位器使用说明》（md + html）第一章起重写为以 `RevServices` 为主线，新增 3.5「注销」，坑 / 报错 / 速查卡 / 文件清单同步；文档里指向已删除 README 的死链一并清掉。
- **安卓 / 微信小游戏 / 抖音小游戏 投产适配（一批）**：
  - **`RevAppLifecycle`（新模块）**：前后台 / 焦点 / 退出事件（`Paused` / `Resumed` / `Quitting`）+ `IsBackground` / `LastBackgroundSeconds` / `PauseCount`。零配置自动就位（`RuntimeInitializeOnLoadMethod` 建隐藏宿主），并对移动端"Pause 与 Focus 同时到达"做状态去重（同一轮只派发一次）。
  - **IL2CPP 裁剪保护**：新增框架侧 `link.xml`（保 UI 反射装配链与单例反射构造），并新增编辑器工具 **`Revolution.Tools/平台/生成 IL2CPP 裁剪保护 (link.xml)`** —— 扫描工程内所有 `RevUIPanel` / `RevUIPart` / `RevSingleton` 派生类，生成业务侧 `Assets/link.xml`（已存在手写文件时弹窗确认，不静默覆盖）。
  - **WebGL / 小游戏自动禁用文件日志**：新增 `RevFileSink.SupportsBackgroundWriter`（WebGL 默认 false）—— 小游戏是单线程，`new Thread` 会直接抛异常，而原先的"目录可写探测"在虚拟文件系统上会误判为可用。
  - **平台名三处对齐**：`RevABLoader.MainName` / `RevHotPlatform.Name` 补小游戏宏分支（统一归 `WebGL`）；`ABBuildSetting.GetPlatformName` 把 `XxxMiniGame` 构建目标也归一到 `WebGL`（按名字判断而非枚举，避免老引擎没有该枚举时编译不过），消除"产物目录名 ≠ 运行时主包名"造成的首屏 404。
  - **AB 加载健壮性**：失败重试（`MaxAttempts` / `RetryDelayMs`）、超时兜底（`AttemptTimeoutSeconds` —— WebGL 上 `UnityWebRequest.timeout` 不生效，改为按帧轮询 + `Abort`）、**引擎缓存标识**（新增 `RevABLoader.BundleCacheKeyResolver` 钩子与 `RevBundleCacheKey` 类型；热更包从清单取 `unityHash` / `unityCrc` 注入，小游戏不再每次进游戏重下全部 AB）。
  - **热更**：`Concurrency` 默认按平台（小游戏 2 / 其余 3）；小游戏 / WebGL 上禁止明文 http（不受 `AllowHttp` 影响）、并发 > 4 在配置期直接报错；`SetActiveBundleKeys` 与"切版本"同步（换版本必换 hash 表）。
  - **首包体积审计**：新增编辑器工具 **`Revolution.Tools/平台/首包体积审计 (Resources)`** —— 列出所有 `Resources` 目录的文件数与体积（按体积排序，含最占地方的 10 个文件），供小游戏首包预算对照。
  - **音效平台能力表**：新增 `RevSoundPlatform`（`AudioManagedByPlatform` / `NeedsUserGestureUnlock` / `Note`），把"首次播放需用户手势、AudioClip 必须 Decompress On Load、切后台打断需重播"三条小游戏约束写进代码而不是口口相传。
  - **AB 打包选项**：新增 `deterministic`（默认开）与 `stripUnityVersion`（默认关；开了就要记住"换 Unity 版本先强制重打"），打包窗口同步暴露两个勾选框。
  - **文档**：新增《应用生命周期 · 使用说明》（md + html）。`RevAppLifecycle` 暂未配 Demo 场景。
- **代码裁剪（IL2CPP managed stripping）：主推关掉，另配三层护栏**（完整说明见新增文档《代码裁剪 · 使用说明》md & html）：
  - **主推**：把 `Player Settings → Managed Stripping Level` 改成 `Disabled`（或至少 `Minimal`），然后重新出包。理由：代码裁剪省的是包体，赔的是"编辑器好好地、真机突然瘸"的不报错故障；而关掉的代价只有包体，**3 分钟就能自己测准**（出两包比差值）。
  - **框架 `link.xml` 固定保住 Unity 原生模块**（万一要开裁剪时的护栏）：`PhysicsModule` / `Physics2DModule` / `UIModule` / `AnimationModule` / `AudioModule` / `ParticleSystemModule`。这些模块真实现都在引擎原生侧（C++），托管层只是 wrapper —— 业务没直接调就会被裁掉，而场景里挂的组件还好好留着，于是变成最难查的一类故障：**编辑器一切正常，真机上刚体变"空壳"（不报错就是不响应）、`Physics.Raycast` 恒返回 false、动画不动、没声音**。注释里同时列出了其它同类模块（NavMesh / Terrain / VFX / Timeline / TMP / Input System…）的加法。
  - **新增 `RevStripGuard`（自检）**：进游戏（首个场景之前）逐个 `Type.GetType` 问引擎"这个类型还在吗"，缺了就用 `RevLog.Error` 报出【缺什么 + 什么症状 + 怎么修】，把"沉默的故障"变成"启动时一条明确的错误"。可手动调 `RevStripGuard.Check()`（出包配置变了 / 升级 Unity 后建议主动验一次）。★ 本文件刻意只用字符串查询、不直接引用被检查的类型 —— 直接引用会让裁剪器认为"有人用"，自检就永远通过了。
  - **生成工具自动补全**：菜单「生成 IL2CPP 裁剪保护 (link.xml)」现在除业务类型外，还会**扫一遍工程源码**，把代码里用到的原生模块（NavMesh / Terrain / 粒子 / VFX / TMP / Input System…）自动写进 `Assets/link.xml`；只写"本工程真的加载得到"的程序集，避免 link.xml 引用不存在的程序集导致构建失败。
- **导表工具（Unity 编辑器版）**：菜单 `Revolution.Tools/配置表/导表工具`（另有「快速导出」一步导完、CI 入口 `Revolution.Editor.ExcelTool.RevExcelCI.Export`）。Excel 规则与 WPF 版完全相同，生成的代码与数据逐字节一致（除生成时间）。相比 WPF 版：拖入 Excel 即读、Excel 开着也能读、保存后自动重读并标出改动的表；全量数据预览（运行时解析不了的单元格标色、错误一键定位到行）；输出目录默认进 Generation 程序集 / `<资源根目录>/Data`，并在导出前检查代码目录所属程序集与数据目录是否可被运行时读到；只写内容有变化的文件（仅数据变化时不触发脚本编译）；新增 / 删除表后自动生成资源映射，数据没有 AB 标记时提示并可一键标记；导出会移除上次导出过的表时先确认，旧数据文件可一键清理。输出设置存 `ProjectSettings/`（团队共享），Excel 来源存 `UserSettings/`（个人）。
- **`ABMapGenerator`**：「仅生成映射」的唯一实现，打包窗口与导表工具共用。
- **动作序列：易用性**
  - 一行播放：`清单.Play(gameObject)` / `await 清单.PlayAsync(gameObject)`。
  - 新增步骤：`.Tween`（随时间变化，可带起始值与缓动 `RevEase`）、`.If`（运行时分支）、`.Finally`（跑完 / 取消 / 出错都执行的收尾）、`.Log`、`.Publish(ctx => 事件)`、`.WaitTask<T>`（拿到任务结果）。
  - Unity 现成步骤：`.MoveTo` / `.ScaleTo` / `.FadeTo` / `.SetActive`。
  - 省略名字：`.Do(ctx => …)` / `.WaitUntil(ctx => …)`，日志里用"文件名:行号"定位。
  - 上下文：`ctx.Require<T>()`（必需服务，取不到直接报错并说明怎么注册）、`ctx.SourceAs<T>()`、`ctx.SourceGameObject()` / `SourceTransform()`；服务按接口兜底查找（`Add(new Impl())` 后 `Get<IFoo>()` 也能取到）。
  - `WaitUntil` 新增 `onTimeout`；`WaitTask` 新增 `cancelOnFailure`；`RevSequencePlayer.UseUnscaledTime`（暂停时也推进）；`runner.ActiveCount`。

  - **配置表导出：新增「仅生成数据」模式（Unity 编辑器版 + WPF 版）**：只改了 Excel 数值、没动表结构时只重写数据 txt，一个代码文件都不碰 —— 导完不触发一次十几秒的脚本重编译。编辑器导出区新增「仅数据」按钮，▾ 菜单另有「全量生成代码和数据（强制重写全部文件）」与「仅生成数据文件（强制重写全部 txt）」；菜单新增「快速导出（仅数据，不生成代码）」；WPF 操作区改为 [仅生成数据] / [全量生成（代码+数据）] 两按钮（仅数据模式只需 TXT 数据目录）。

  ### 修复

- **动作序列**：
  - 在步骤 / 回调 / await 续体里调用 `StopAll` / `Clear` / `Dispose` 会让 Tick 下标越界、已归还池的运行实例被继续推进 → Tick 改为按快照遍历，期间的取消统一在本次 Tick 末尾收尾。
  - 在步骤里 `Stop` 自己后，同一帧剩下的步骤仍会执行、最终按"完成"结束 → 每进下一步前检查取消（并行 / 重复 / 分支内部同样）。
  - `RunFinished` 订阅者抛异常时 `PlayAsync` 永不完成 → 各环节独立 try/catch，等待者在最后必然被唤醒。
  - DEBUG 下步骤抛异常会一直留在活跃表里、每帧重复抛出并阻塞其它序列 → 统一为"记日志 + 按取消收尾"。
  - 嵌套序列的 `ctx.Source` 被错误设成了父序列的上下文对象 → 新增 `Play(definition, context)` 重载，子序列沿用父序列的触发者 / 服务 / 事件总线。
  - 场景里的全局驱动者随场景销毁后，全局引擎再也没人 Tick → 访问 `Default` 时自动补建；关闭域重载时静态状态每次进入播放模式复位。
  - 同一个步骤实例被放进两条序列会互相覆盖状态槽 → `Build()` 时报错。
  - 收尾步骤里的 `Parallel` 不会执行子步骤、某一步抛异常会跳过其余收尾 → 收尾每步执行后再推进一次，逐步隔离异常；收尾里放等待步骤在 `Build()` 时报错。
  - 等待超时、异步任务失败、收尾 / 回调异常以前被静默吞掉 → 经 RevLog 输出（tag = `ActionSequence`），写明序列名与步骤。

  - **计时器**：
  - 到期回调里调用 `Restart()` 想"重新计时"，实际会被同帧回收（一次性 / 循环最后一次回调均中招）→ Restart 撤销待回收标记，重试类写法恢复可用。
  - 全局暂停期间校准服务器时间会把暂停时长整个丢掉（恢复后 Server 域整体偏快，`At` 全部错位）→ 校准锚点改用暂停时也更新的实时读数（暂停冻结剩余时间读数的语义不变）。
  - **服务定位器**：
  - 作用域 `Dispose` 后仍挂在根容器的跟踪表里（长跑越积越多）→ 释放时自摘；根容器释放改为先摘列表再倒序逐个释放（防遍历中列表左移漏放）。
  - `OnInit` 失败回滚只把实例从表里摘掉、不释放它 → 回滚时对容器拥有的实例补 `Dispose`（不吞原始异常）。
  - 同一实例注册到多个服务类型会重复挂载（每帧 `OnTick` 跑两遍、释放时 `Dispose` 两遍）→ 重复实例只补类型映射不重复挂载。
  - **公共 Mono**：
  - `RemoveAllOf` 用跨相位累计值判断脏标记：只动 Update 相位也会把 LateUpdate / FixedUpdate 标脏，下一帧白白重建快照（破坏稳态零分配）→ 改按相位内计数。
  - 进 Play 复位漏清 `Failed` 事件订阅：关闭域重载时上一局订阅的处理器跨局存活（对已销毁对象报错 / 引用泄漏）→ 复位一并放掉。
  - **场景**：
  - 发起加载抛异常（典型：buildIndex 越界直接抛而不是返回 null）后没有任何收尾，`IsLoading` 永久卡 true、之后所有请求被"拒绝并发"拦死 → 异常转统一失败收尾（State=Failed + OnLoadFailed），同步 / 异步两条路都兜住。
  - 同步 `Load` 把旧场景名当"已进入的新场景"广播（同步切换帧末才生效）→ 直接广播目标名，`OnLoaded(name)` 的契约恢复成立。
  - 退出 Play 时若正在异步加载，`IsLoading/State` 残留到下次运行（重进 Play 后一切加载被拦死且无日志）→ 补进 Play 复位。
  - **输入**：
  - `SetRepeat` 的 `delay = 0` 语义矛盾（注释承诺"0 = 不连发"，实现却当成"立即开始连发"）→ `delay ≤ 0` 才是关连发。
  - 改键存档往返丢连发配置 → 文本格式新增 `repeat:延迟/间隔`，`SaveBindings ⇄ LoadBindings` 无损往返。
  - 屏蔽 World 期间轴读数未归零（弹窗里角色照样移动）→ 轴一并归零；屏蔽中按下时间仍记录（缓冲不丢）。
  - 冲突检测漏轴键 → `BindAxis` 使用的键与键位 / 轴键双向互查。
  - `Failed` 事件订阅跨局残留（关闭域重载时打到已销毁对象）→ 复位放掉。
  - 监听者回调里注销自己（如回调里 `OffAllOf`）会越界并炸到驱动的 Update → 倒序 + 实时计数遍历。
  - **UI 系统**：
  - 泛型面板 `RevUIPanel<TData>.Data` 永远是 null / 旧值（管理器走基类 object 通道，泛型 `SetData` 是方法隐藏调不到）→ 新增 `OnDataSet` 统一出口，泛型类同步强类型数据；`OnRefreshView`"只画 Data"恢复成立。
  - 打开 / 关闭转场完成回调没有状态守卫：打开途中被关闭后仍执行"打开完成"（业务收到正在关闭 / 已回池的面板），关闭侧对称 → 全部完成回调加状态守卫。
  - `ClosePart(false)` 关掉的 Part 再打开时不会重新出现（复用路径只刷新不激活）→ 复用前检查激活状态。
  - 动画采样回调里停掉动画自己，会错删换位到同一槽的别的动画、并把已回池（甚至已复用）的运行对象提前"完成"再回收 → 收尾前双重确认存活与版本号。
  - `ShutdownAll` / 场景卸载后到达的关闭完成回调会往空池塞死实例、资源引用泄漏 → 根已不在时只还资源引用。
  - `TopOf` 只按打开顺序取，与 `CloseTopOf` 的"先画布后顺序"双标 → 对齐同一套规则（Split 三画布下返回真正的视觉最上层）。
  - `RevUIAnim.RestoreBase` 永远停不掉正在播的动画（owner 传错对象）→ 改按句柄停。
  - 注释失实两处：Canvas 渲染模式默认值表述修正为 Overlay（行为不变）；`LayerStep` 如实标注为未接入的预留参数。
  - **日志**：
  - 文件大小按字符数累计（UTF-16），中文日志实际能涨到上限约 3 倍才轮转 → 改按 UTF-8 字节统计。
  - 主线程 Flush / Dispose 的兜底路径与后台线程并发写同一个 `StreamWriter`（交错 / 异常丢日志 / 误计通道故障）→ 写段整段互斥，并删掉 Flush 末尾裸调的二次刷缓冲。

  ### 变更

- **动作序列**：`OnCancel` / `OnCompleted` / `OnCancelled` 多次调用改为叠加（以前后一次覆盖前一次）；`RevSequenceDefinition.FinallySteps` 现在表示 `.Finally` 的步骤，取消收尾改名为 `CancelSteps`；触发者（Unity 对象）被销毁时序列自动取消（`runner.StopWhenSourceDestroyed = false` 可关闭）。

  - **配置表装载改名：DataLoad → RevDataLoad**。目录与全部类型加 Rev 前缀：`RevDataTable` / `RevIDataTable` / `RevDataTableManager` / `RevDataFieldParser` / `RevDataTextFormat` / `RevDataTableLoadException`（文件与 meta 同步改名，guid 不变）。导表工具的生成模板已同步 —— 重新导出一次即无缝衔接；此前生成的旧容器文件里的 `: DataTable<` 写法仍可被编辑器识别（用于删除提示）。
- **打包工具更名：LiteAB → RevAB**。菜单改为 `Revolution.Tools/资源/RevAB 打包工具`（与 `RevAB 分包浏览`）；日志 tag 改为 `RevAB`（静音过 `LiteAB` 的请改成 `RevLog.MuteTag("RevAB")`）；本机偏好键与快照目录（`Library/Revolution/RevAB/`）在首次使用时从旧名字自动迁移。
- **ABTool 按职责拆成子目录**：`Core` / `Pipeline` / `CodeGen` / `Snapshot` / `Integration` / `Window`（`.meta` 随文件移动，GUID 不变；`ABCIBuild.Build` 的调用方式不变）。
- **「分包」页签重做（对标 AssetBundle Browser）**：包树（`/` 分层、多选、搜索、F2 改名、Delete 删除、右键菜单、拖包调层级）+ 多列资源表（排序、标记来源、双击定位、拖到别的包 = 换包）+ 详情面板；从 Project 拖资源到空白处 = 以资源名新建包；包名统一小写，改名 / 删除后自动清理未使用的包名。
- **「打包」页签重做**：目标平台可选（不必先切平台，本机偏好）、输出目录可一键打开、主按钮置顶；配置按分组折叠、选项中文化；切到「按目录自动分包」前先确认；打包结果显示产物体积与用时。
- **检查类页签**：「检查」标题带问题数；共享资源 / 漏标资源可一键移进一个包，空包名一键清理；包名可点击跳转到「分包」页签；依赖分析改为下一帧带可取消进度条执行，不再卡住窗口；体积等数据按扫描结果缓存。

## [0.1.0] - 2026-09-26

### 新增

- **运行期（13 个模块 / 121 个 `.cs`）**：资源加载、对象池、UI 系统、动作序列、状态机、音效系统、事件系统、GM 指令、配置表、服务定位器、异步任务、单例
- **编辑器工具（17 个 `.cs`）**：LiteAB 打包工具（分包浏览 / 依赖 / 体积 / 漏标检查 + `ResMap` 与路径常量生成）、GM 指令面板
- **文档**：每个模块的《使用说明》（手把手）与《架构解析》（设计论证），见仓库 `Revolution.Document/`
- **安装方式**：`package` 分支（框架本体在根，可直接 clone / submodule 进工程 `Assets/`）、UPM git URL
