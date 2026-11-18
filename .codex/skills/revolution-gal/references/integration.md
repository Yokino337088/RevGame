# 宿主与 Revolution 核心

## 安装面

- Gal 是可选扩展，面向 Unity 2022.3。工程需要匹配的 Revolution 核心、uGUI、`Revolution.Gal` 和仅编辑器使用的 `Revolution.Gal.Editor`。运行程序集依赖 `Revolution.Runtime`，编辑程序集还依赖 `Revolution.Editor`。
- Assets 安装从 Revolution 的 `package` 安装核心，再将 `gal` 分支根内容装到 `Assets/Revolution.Gal`，带原 `.meta`。也可以在已经安装核心的工程里以 Git UPM 地址 `https://github.com/Yokino337088/Revolution.git#gal` 安装扩展。工程保留一种 Gal 安装，以免生成两份同名程序集。
- 宿主业务 asmdef 仅在调用 Gal API 时引用 `Revolution.Gal`。不要把 Editor 程序集引用到 Player。核心快照按宿主约定同步。维护扩展源码用 Revolution 主仓库的 `RevolutionFrameWork_Unity/Assets/Revolution.Gal`，发布动作产出独立 `gal` 分支。
- CLI、RevMCP 和 Unity 桥均由实际能力发现。工具客户端较旧时同步 `automation` 或项目相邻 RevCLI。Unity 端缺动作时核对安装和程序集编译。更新工具或技能以匹配项目授权范围为准。
- 宿主的 Editor 构建代码可引用 `Revolution.Gal.Editor`，用 `RevGalHostContract.InspectMedia(story, resourceRoot)` 读取节点和舞台方案的媒体路径、类型及资产解析状态。分包、图集和资源目录继续由宿主管理。

## 嵌入业务

在宿主既有启动入口执行一次 `RevInitialize.Initialize()`。宿主 FSM 决定什么时候开、什么时候离开剧情。剧情 SO 的 `logicalPath` 交给 `RevGal.OpenAsync`，由 RevUI 载入 `UI/Gal/RevGalPanel`；该面板位于 Popup 层并在关闭时销毁。不要从业务通过内部加载器绕开资源门面。

```csharp
// 在项目已有的 RevInitialize 初始化之后运行。下面的本地化与信号回调由宿主实现。
var panel = await RevGal.OpenAsync("Gal/Stories/Lantern", new RevGalOptions
{
    ResolveText = ResolveStoryText,
    HandleSignal = HandleStorySignal,
    CharactersPerSecond = 42f,
    AutoDelaySeconds = 1.8f,
    WaitForVoice = true
});
var session = RevGal.Current;
// 流程结束、域退出或玩家取消时，由同一宿主调用 RevGal.Close()。
```

- `RevGalPanel` 接 RevUI 生存期，拥有会话和媒体。`RevGalView` 渲染只读帧、打字机与选项，回报按钮意图。`RevGalSession` 管图、变量、历史、回退、档案和进度。
- 自有界面使用 `RevGalPlayback`，从逻辑路径、已有 SO 或类型化定义取得独立会话。参见[自定义宿主接入](custom-host.md)的生命周期、帧订阅及按钮示例。原有的 `RevGal.LoadAsync` 和 `RevGal.Preview` 可继续使用。
- `RevGalPlayback.Preview(asset, entry)` 提供临时离屏试读。使用默认面板时可从 `RevGal.OpenAsync` 的 `slot` 恢复已有有效档。切场景时让会话和媒体随所属流程或面板退出。
- 用 `RevGal.OpenAsync(path, options, slot, cancellation)` 将默认面板的打开操作绑定到一次宿主请求。每次打开使用独立取消源，切域时取消尚在等待的请求。面板交付后，宿主关闭自己持有的实例，打开令牌到此结束。
- `RevGalOptions` 同时承载本地化、宿主信号、`Storage`、打字速度、自动等待、是否等语音、BGM/语音/SFX 音量、`RollbackLimit` 和 `HistoryLimit`。打字速度为 0 时一次呈现。默认回退 64、历史 512。媒体音量还乘以 `RevSound` 对应分类音量。宿主决定原场景音乐是否暂停及何时恢复。

### 宿主信号和退出

默认面板和自有界面可共用 `DispatchSignalAsync(frameToken)`。该入口等待 `HandleSignal(session, signal)` 的 `RevTask` 完成，再对相同 Token 继续。宿主自行处理信号时在业务成功后调用 `CompleteSignal(signal.Token)`。外部副作用按宿主业务实现幂等或取消。等待信号期间暂停保存或回退，完成信号后建立回退边界。没有消费回调时，默认面板保留等待态。

关闭会使迟到的打开请求失效。主题的图集克隆随 View 回收，舞台媒体由 `RevResScope` 持有，晚到媒体归还本次引用。宿主自有 UI 可以复用 RevUI、RevResManager 和 RevScope，并明确订阅、会话及媒体的 owner。资源更新前让旧会话与其媒体退出，更新后从新快照重新打开。Gal 通过核心的运行时逻辑路径加载。使用 RevHotUpdate 时按宿主现有的平台、清单、资源映射、激活时机和相容版本约定交付 SO 与 AB。

## 存档、回退和图鉴

- `IRevGalStorage` 通过 `ReadSlot/WriteSlot/ReadProgress/WriteProgress` 接入项目账户、云端或本地档案。默认 `RevGalLocalStorage` 按剧情 ID 与 slot 存 PlayerPrefs，`UseDefaultStorage=false` 可关闭默认注入。默认面板使用 `quick`，自有 UI 可以用自己的槽位和界面。
- `Capture/Restore` 管读写检查点。`SaveSlot/LoadSlot` 调用存储。档案核对 schema、剧情、修订、入口节点类型、变量集合、舞台逻辑路径和历史中的节点及选项引用。已锁定且无可选项的恢复位置会被拒绝。修改原有 ID 或条件后用具体旧档核对。
- 回退恢复节点、变量、历史和舞台检查点。已读、结局与图鉴是合并保留的进度。`MarkRead` 按当前 Token 标记。默认读过才能跳过，选项、信号、结局及未读对白会停止相应自动过程。
- `Progress().Gallery` 是已经解锁的 ID，`TryGetGalleryImage(id, out path)` 查对应逻辑图路径。宿主据此制作自己的画廊。结局进度按结局节点 ID 记录，档案和图鉴迁移由宿主决定。验收用隔离档案或开发态内存槽。
