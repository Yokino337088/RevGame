# UI 与分层（RevUISystem 专题 + "要不要 System / BusinessLogic"）

> 权威文档：`Revolution.Document/UI系统/UI系统使用说明.md`、`UI系统架构解析.md`（**第七章**专门回答分层问题）。

## 1. UI 主链路（声明 → 绑定 → 打开 → 传数据 → 关闭）

```csharp
// ① 声明：特性写在面板类上（root 必填；name 省略 = 用类名）
[RevUIPanel(RevResPath.Data, RevUILayer.Normal, ExclusiveGroup = "BagTab")]
public sealed class BagPanel : RevUIPanel<BagData>
{
    [RevBind] private Button _btnClose;                 // 字段名 ↔ 节点名
    [RevBind("Top/Title")] private Text _title;         // 显式路径

    protected override void OnBindView() { }            // 只装配：找节点、挂事件
    protected override void OnRefreshView() { }         // 只落屏：把 Data 画上去（带数据面板必实现）
    protected override void OnDataChanged(BagData o, BagData n) { }   // 增量刷新（可选）
    protected override void OnClick(string path) { }    // 只转发：按钮点了该干什么
}

// ② 打开 / 传数据（业务侧）
RevUI.Open<BagPanel, BagData>(new BagData { UseCellLayout = true });     // 带数据（推荐）
RevUI.Open<BagPanel>(p => p.SetData(default));                            // 或者回调式
await RevUI.OpenAsync<BagPanel, BagData>(data);                           // await 版

// ③ 关闭
RevUI.Close<BagPanel>();      RevUI.Back();      RevUI.CloseAll(RevUILayer.Popup);
```

- 数据流是**单向**的：业务 `SetData` → 基类 `OnDataSet(object)` → 强类型 `Data` → `OnDataChanged(old, new)` → 可见则 `RefreshView()`。
- 带数据的 `RevUIPanel<TData>` 把 `OnRefreshView` 定成 **abstract**：有数据就必须写清"数据怎么落到界面上"，不允许糊过去。
- 复用时要连数据一起清（框架有 `InternalClearData`），否则会出现"上次的数据还在"。

### 1.1 刷新时机与 `SetData` 调用权（新手最易混，务必讲清）

**`SetData` 内部顺序**（源码 `RevUIPanel.SetData`）：① `Data` / `DataObject` 同步成新值 → ② `OnDataChanged(旧, 新)`（此时 `Data` 已是新值）→ ③ 面板为 `Opened` / `Opening` 才自动 `RefreshView()`（没开着不画，等打开那次统一画）。所以 `OnRefreshView` / `OnDataChanged` 里读 `Data` **一定是最新值**。

**`OnRefreshView` 什么时候被调**（框架**不监听**数据对象内容，不是双向绑定）：

| 场景 | 自动刷新？ | 你该做什么 |
|---|---|---|
| 打开面板（首次 / 池里复用） | ✅ | `OnRefreshView` 写好"怎么画 Data" |
| 面板开着时 `SetData(新对象)` | ✅ | 什么都不用做 |
| 对已开面板再 `RevUI.Open<T,TData>(新数据)` | ✅（内部走 `SetData` + 置顶） | 同上 |
| 面板没开就 `SetData`，之后才 `Open` | ✅（打开时统一画一次） | 同上 |
| **原地改内容**：`Data.Count++` / `Data.Items.Add(..)` | ❌ **不会** | **改完手动 `RefreshView()`** |

```csharp
Data.Count++; RefreshView();                                  // 写法 A：原地改 → 手动刷（class 数据才走得通）
SetData(new DetailData { Id = Data.Id, Count = Data.Count + 1 });   // 写法 B：换新对象 → 自动刷 + OnDataChanged 增量
```

- **`struct` 数据**：`Data` 每次读是**拷贝**，"原地改 + RefreshView()"走不通，只能换新对象走 `SetData`（好处：数据不会被外部偷偷改）；**`class` 数据**可原地改，但改完必须手动刷，且 `SetData` 传同一引用时 `old == new`。
- **`OnDataChanged` 里不要手动 `RefreshView()`**：面板开着时框架随后会自动刷一次，手动再刷 = 每次换数据画两遍。起步只实现 `OnRefreshView`（全量重画）就完全正确，`OnDataChanged` 增量是后期优化。
- **`SetData(null)` 与回池复用都会把 `Data` 清成 null**：`OnRefreshView` 里用 `Data` 前先判空。
- **`OnRefreshView` 纪律**：只画界面——不发请求、不改数据（那些放 `OnOpen` / 交互回调），否则"刷新→改数据→再刷新"死循环。

**`SetData` 在哪里调（调用权归属）**：永远写在**业务侧"数据产生 / 变化的地方"**，面板只是消费方：

| 谁 | 调什么 | 典型位置 |
|---|---|---|
| 业务：打开面板那次交互 | `RevUI.Open<T,TData>(data)`（已开/没开不用区分） | 点列表项开详情 |
| 业务：面板已开想换内容 | `RevUI.Get<T>()?.SetData(新数据)` | 详情页点"下一个" |
| 业务：数据异步到达 | 回调里 `panel.SetData(..)`；面板没开就 `RevUI.Open<T,TData>(data)` | 网络回包 / 协程加载完成 |
| 面板内部 | 一般不调 `SetData`；原地改后 `RefreshView()` | — |

## 1.2 控件事件三种接法（优先用方法特性）

**项目默认规范：UGUI 控件事件优先用方法特性标注。** 对普通点击、Toggle、Dropdown 等低频交互，这是最清晰的默认写法；不需要字段和手动订阅/退订，也不容易因面板复用造成重复监听。只有事件特性无法覆盖的控件能力或经 Profiling 确认的高频性能热点，才选另外两种接法。

① **方法特性（默认首选）**（九个，标在面板/Part 任意方法上）：`[RevButtonClick("btnStart")] void OnStart()`；另有 `RevButtonLongPress` / `RevButtonLoosen` / `RevToggleChanged` / `RevSliderChanged` / `RevInputChanged` / `RevInputEndEdit` / `RevDropdownChanged` / `RevScrollChanged`。控件名 = 节点名（区分大小写；多级路径只取最后一段）；**写错第一次装配就报"找不到节点"**，不静默。参数形状只支持文档列出的几种（点击：`()` / `(string nodeName)`；Toggle：`()` / `(bool)` / `(string, bool)`…），其它装配时报错。
② **重写节点名回调**：`OnClick(string nodeName)` 等（集中处理，`switch (nodeName)`）。仅当想集中分发很多事件，或 ScrollRect / 每次键入等高频回调经 Profiling 确认为 GC 热点时优先；方法特性每次触发有小分配，但普通按钮等低频交互无需为了微小开销绕开特性。
③ **`[RevBind]` 字段 + 自己 `AddListener`**：仅用于特性与节点名回调都不能覆盖的 UGUI 其它回调（如拖拽/悬停）或第三方控件；必须按面板生命周期配对解绑。自研控件可用 `RevUI.RegisterAutoEvent<T>((dispatch, c) => …)` 接入框架事件绑定。
- **长按判定**：按住 ≥ `RevUISetting.ButtonLongPressSeconds`（默认 0.5s），**松开时**触发；只对 `Button` 生效；同一次操作也触发"松开"。
- 同一次事件里"重写回调"与"方法特性"**两条路都会走**——同一件事只放一处做。

## 1.3 动画（内置库，不依赖 DOTween）

- 面板 / Part 的生命周期动画，项目业务优先成对重写 `PlayOpenTransition(Action onDone)` 与 `PlayCloseTransition(Action onDone)`，用 `RevUIAnim.Play(this, 预设, duration: 0f, onDone: onDone, owner: this)` 接入框架动画。打开与关闭各自只会在钩子完成后推进状态；★ **两边都必须在动画结束时调用 `onDone`**，否则打开会一直停在 `Opening`，关闭则一直停在 `Closing`、无法回池。
- 简单标准效果可选 `ShowAnimation` / `HideAnimation` 一行预设（`FadeIn`、`PopIn`、`SlideInFromTop` 等）；不要同时设置预设属性并在钩子里播放同一段动画，框架先等待转场钩子、再播放显示/隐藏预设，会导致串播。需要自定义生命周期动画时，以成对钩子为主。
- 任意控件：`RevUIAnim.FadeIn / SlideIn / ScaleTo / FadeTo / Breathe / AddHoverFeedback / Play(...)`；**一定传 `owner: this`**（关闭/销毁时框架一行 `StopAllOf(owner)` 清干净）。
- 同一控件上只留一个动画（再起一个顶掉上一个）；动画走 `unscaledDeltaTime`（`timeScale = 0` 也播）；`RevUISetting.UIAnimationsEnabled = false` 一键关全部（预设直接写终态，业务零改动）；时长不传或 ≤ 0 = 预设默认时长。
- 透明度写 `CanvasGroup`（没有自动补），缩放/位移写 `RectTransform`；恢复基准态 `RevUIAnim.RestoreBase(控件)`。

## 1.4 Canvas 架构与根 Canvas

- 默认 **单 Canvas**（`RevUISetting.CanvasArchitecture = Single`），渲染模式默认 `ScreenSpaceOverlay`；框架默认加载 `Resources/RevUIPrefab/RevUICanvas.prefab`，**载不到会代码兜底建 Canvas 并打 Warning**（不会整屏起不来）。
- 只有"Profiler 证明 Canvas 合批是瓶颈 + 常规优化仍不达标"才切 `Split`（`Static` / `Dynamic` / `Common` 三画布，面板上写 `CanvasType`），且要在**第一次 Open / Preload 之前**切；只配一个属性不会自动把预制体里的控件分离，要拆成两个 Scene 面板；`Normal` / `Popup` / `Toast` / `Guide` / `Top` 层声明成 `Static` / `Dynamic` 会被放回 `Common` 并告警。

### 1.5 横竖屏设计方向（`CanvasOrientation`）

在**首次 `Open` / `Preload` 前**设置 `RevUISetting.CanvasOrientation`；默认 `RevUICanvasOrientation.Auto`，无需给所有项目强制设竖屏。

| 模式 | 根 Canvas 实例的缩放方案 |
|---|---|
| `Auto`（默认） | 按 `Screen.width / height` 判方向；运行时旋转或 Game 视图切换宽高后自动调整 |
| `Portrait` | 固定竖屏设计；横屏基准 `1920×1080` 换位为 `1080×1920`，Match 用 `PortraitMatchWidthOrHeight`（默认 `0`）；原设计已竖屏则保留原值 |
| `Landscape` | 固定横屏设计；原设计横屏则保留，原设计竖屏则换位；Match 恢复原设计值 |
| `UseDesign` | 使用原始 CanvasScaler 设计尺寸与 Match，不按屏幕宽高适配 |

- 实现是 `RevUIRoot` 保存原始设计值，初始化和 `Update` 检查方向，只在方向变化时改**运行时实例**，不改 prefab 原件。仅接管 `ScaleWithScreenSize` + `MatchWidthOrHeight`；其它 Scaler 模式不动。
- `ReferenceResolution` / `MatchWidthOrHeight` 只用于代码兜底创建 Canvas；加载 prefab 时以其 Scaler 原值作基准，不靠设置这两项覆盖 prefab。
- **选择 UI 设计方向 ≠ 强制设备方向**：框架不写 `Screen.orientation`；设备方向锁定由 Player Settings / 平台配置负责，也不自动做安全区、改锚点或重排布局。
- **Prefab 编辑模式不走这条运行时链路**：竖屏预览需手动设置预览 CanvasScaler（如 `1080×1920`、Match `0`）并选 Game 竖屏尺寸，或运行 `RevUI.OpenAsync` 的真实根 Canvas 链路；不要期待 Boot 中的设置直接改变 Prefab Mode。

## 2. 同步 Open vs 异步 Open（最常踩）

- `RevUI.Open<T>()`（无回调那个重载）**不加载任何资源**：只在"已打开过"或"实例池里有"时拿得到实例；需要加载时返回 `null` 并报错。
- 真机预制体在 AB 里 → **首次打开必然异步**：用 `Open<T>(onOpened)` / `Open<T, TData>(data, onOpened)` / `OpenAsync<T>()`。
- 想"点开秒现"就先 `RevUI.Preload<T>()`（预热），之后再走同步 `Open<T>()`。

## 3. 面板池 / Back 语义

- **面板池是 UI 专用池**（不套通用对象池）：关闭优先回池（`KeepAlive`），池满或 `DestroyOnClose` 才真销毁；上限 `RevUISetting.MaxCachedPanels`。取出时必须走 `OnReuse` 钩子（重置界面状态）。
- `RevUICacheMode`：`Unspecified / DestroyOnClose / KeepAlive`…
- **`RevUI.Back()` 只关"最晚打开、且 `InBackStack = true`"的面板**；飘字 / 常驻 HUD 一律 `InBackStack = false`。
- `ExclusiveGroup`（特性参数）= 同组互斥（背包页签这类）。
- 关闭优先级：面板自己的 `RequestClose()`（可被拦截，例如"有未保存内容"）→ `CloseSelf()`。

## 4. Part / 宿主

| 类型 | 怎么用 | 什么时候用 |
|---|---|---|
| 节点级 Part | 不加特性，用 `[RevBind]` / `GetComponent` 拿 | 只是把节点上的组件包一层内聚逻辑，零加载 |
| 预制体 Part | 类上加 `[RevUIPart(root, name)]`，`RevUIPart.Create<T>(host, parent, onCreated)` | 可跨面板复用的界面块（弹窗、列表项） |

- 面板即宿主：`PartRoot => transform`、`IsHostOpened`、`RequestClose`、`NotifyPartChanged`。
- Part 状态变化 → 通过宿主通知（`IRevUIPartHost.NotifyPartChanged`），**不要**让 Part 直接找别的面板。

## 5. 分层：要不要做 System / BusinessLogic？（结论：不做）

| 判定项 | 结论 |
|---|---|
| 能力上缺不缺 | **不缺**：单屏流程 → 面板 + `TData`；跨域共享能力 → `RevServices` 服务；无界面、只活一段玩法域的业务 → `RevLogicBase`；通信 → `RevEvent` + Part 宿主中转；数据更新 → `SetData` + `OnDataChanged` |
| 框架已有哪种业务基类 | **已有 `RevLogicBase`**：承载无界面、随域开始 / 结束的业务单元；不要另造 `System` / `BusinessLogic`，也不要给每屏强加一套业务基类 |
| 额外分层的成本 | 每屏强拆 3~4 个文件、4 跳数据流、两套生命周期、排查路径变长；按真实生命周期选择承载点，不为形式拆层 |

**分工规则（一句可执行）**：只属于这一屏的留面板；跨域长期能力放服务；没有界面、只活一局 / 一段玩法域的业务放 `RevLogicBase`。
判断法：先问"**有界面且随该界面生灭吗**"？是 → 面板；再问"**只活这段玩法域吗**"？是 → Logic；跨域仍需存在的能力 / 数据 → 服务。跨面板复用本身不等于全局服务，先判断有效期。

分档落地：

| 档 | 典型需求 | 逻辑放哪 | 文件组织 |
|---|---|---|---|
| 简单 | 设置页、说明页、加载页 | 全在面板类 | 1 个面板 |
| 中等 | 背包、商店、邮件列表 | 面板 + `TData` 数据对象；跨域数据 / 能力从服务取 | 面板 + 数据 |
| 域业务 | 局内统计、新手引导推进、限时玩法判定 | 无界面流程放 `RevLogicBase`，面板只消费结果 | 按需加 Logic，不新增 System |
| 复杂 | 战斗结算、活动、跨界面流程 | 面板装配 / 落屏 / 转发；域内流程用 Logic，跨域能力用服务；必要时再拆 `XxxFlow` | 按职责拆，不固定每屏层数 |

- `RevLogicBase` 已提供 `OnStart` / 可选 `OnTick` / `OnStop`；通过 `[RevLogic(RevDomain.Battle)]` 归域并用 `RevLogic.Start<T>()` / `Stop<T>()` 控制，归属域关闭时自动停止。基类 `Listen` / `After` / `Every` 带 Logic 自身 owner，停止时先清其受控注册再业务收尾；资源释放仍遵循 `resource-and-ab.md` §4 的分组 / 持有者限定。
- 不用 Logic 承载面板显示、跨局进度或全局网络 / 配置能力；Logic 之间不互拿字段 / 持有对方引用，通信用事件。

**什么时候必须抽走（信号）**：

1. 同一套流程被 ≥2 个界面复用 → 先判有效期：只活玩法域放 Logic，跨域能力抽服务（如 `IPurchaseService`），**不是**新增 UI 层；
2. 无界面但要活一段时间、域结束要干净停止 → 用 `RevLogicBase`；需要单测 / 离线算 / 与服务端同规则的纯计算 → 抽不引用 `UnityEngine` 的普通类，不把“无 UI”一概等同服务；
3. 单屏逻辑 > 500 行或状态机复杂（多分支、可中断、可回退）→ 可抽 `XxxFlow`；若生命周期已跨出面板，则按域内 Logic / 跨域服务归属；
4. 将来真要上**代码热更** → 那时才需要"可热更逻辑 vs 不可热更表现"的物理分层。

**面板里出现这些就该抽走**：协议 / HTTP、配置表解析、跨面板共享状态、计时器与长任务、纯计算（公式 / 排序 / 筛选）、存档读写、跨模块事件路由。
抽的时候**不要去改面板基类**（改基类 = 把特例变成所有人的负担）。

## 6. 写 UI 的自检清单

- [ ] 面板类上有 `[RevUIPanel(root, layer)]`；路径用 `RevResPath.*` 常量，不手拼字符串。
- [ ] `[RevBind]` 字段名与节点名一致（或写全路径）；`OnBindView` 里不做业务。
- [ ] 有数据的面板：`OnRefreshView` 只用 `Data` 渲染，不在里面发请求 / 读表；用 `Data` 前判空（`SetData(null)` / 回池复用会清成 null）。
- [ ] 原地改了 class 数据内容 → 有手动 `RefreshView()`；换数据走 `SetData`；`OnDataChanged` 里**没有**多余的 `RefreshView()`。
- [ ] `SetData` / `Open<T,TData>` 写在业务侧（点击回调 / 网络回包），不是写在面板里。
- [ ] 重写 `PlayOpenTransition` / `PlayCloseTransition` 的，结束时**调了 `onDone`**；动画都传了 `owner: this`。
- [ ] 按钮点击统一走 `OnClick(path)` 或绑定的方法，不在多个地方散落逻辑。
- [ ] `ExclusiveGroup` / `InBackStack` / `RevUICacheMode` 按语义设置（弹窗不该进返回栈；常驻 HUD 不该来回销毁）。
- [ ] 打开一律 `Open<T>(onOpened)` 或 `OpenAsync`；需要秒开就先 `Preload`。
- [ ] 单屏逻辑留面板；无界面、只活一段玩法域的业务归 `RevLogicBase`；跨域能力归服务，不因跨屏复用就一律搬服务，也不新增 System / BusinessLogic。
