# 自定义宿主接入

## 根据用户已有界面选入口

| 宿主已经拥有的东西 | 入口 |
| --- | --- |
| 只需要创作、校验或向团队交付数据 | 工作台编辑 `RevGalStory`，`gal story.validate/contract/simulate` 校验，按需要用 `story.export` 输出完整剧情 JSON |
| 自有界面已有逻辑资源路径 | 在宿主初始化 Revolution 后 `await RevGalPlayback.LoadAsync(path, options, entry, slot, cancellation)` |
| 界面已持有剧情 SO | `RevGalPlayback.Create(story, options, entry, slot)` |
| 宿主已装配类型化剧情数据 | `RevGalPlayback.CreateFromDefinition(definition, options, entry, slot)` |
| 希望直接使用配套界面 | `gal ui.create` 生成默认资产，`RevGal.OpenAsync` 打开 |

前三种自定义运行入口返回独立的 `RevGalSession`。已有 SO 或定义可以直接创建会话，适合引用配置、资源已由宿主加载及编辑器试读。`LoadAsync` 从 RevRes 逻辑路径取得 SO，复制剧情后归还该资源引用。所有入口使用同一剧情结构校验、分支、变量与档案逻辑。`entry` 和 `slot` 分别指定入口节点或恢复档案，两者择一。`RevGalPlayback.Preview` 用临时会话试读，采用显式完成信号的方式。

## 在编辑器准备接入

1. 用工作台或 RevCLI 创作、改线、试读。已有剧情先 `gal node.find` 定位修改位置。`gal story.export` 可交付能往返的 JSON。Unity 用户可经 Gal 导入生成 SO。宿主自行解析 JSON 时应处理符号枚举和版本，再装配 `RevGalDefinition`。
2. 工作台选择剧情后点 **接入检查**。可复制清单 JSON。脚本走 `gal story.contract --path Assets/…/Story.asset`。读取 `id/start/revision/valid/issues`、`logicalPath/resourceRoot`、`variables`、`signals` 和 `media`。
3. `signals` 记录信号节点、名称和参数。宿主据此绑定自己的业务动作。`media` 按逻辑路径与所需类型归并，`uses` 返回引用节点及槽位。检查 `status/assetPath/bundle`，先分辨本地素材有无、类型、同名冲突和资源根。
4. 资源根未装配、素材由宿主外部系统提供或 SO 位于资源根之外时，仍可保存和试读。接入清单的本地资源状态用于定位素材，发布的 AB 或远端内容要按宿主实际资源链验收。

## 实现自定义界面

挂接会话后用 `Observe` 收到首帧和后续帧。帧携带说话人、正文、选项、舞台逻辑路径和信号。结束画面也由宿主处理。自定义界面决定是否显示打字机、背景、立绘、日志和存档。控制逻辑以帧数据为输入，使用 `Advance/Choose/MarkRead/CompleteSignal` 提交用户意图。

```csharp
// 由宿主流程持有；取消源在关闭或换故事时由宿主 Cancel。
using var session = await RevGalPlayback.LoadAsync(
    "Gal/Stories/Lantern", new RevGalOptions { UseDefaultStorage = false },
    cancellation: opening.Token);
using var subscription = session.Observe(frame => view.Present(
    frame,
    advance: () => session.Advance(frame.Token),
    choose: id => session.Choose(id, frame.Token)));
await view.WaitUntilClosedAsync();
```

上面的 `view.Present/WaitUntilClosedAsync` 由宿主实现。每批按钮或异步动画捕获它所属的**会话实例和帧 Token**。重用按钮前清掉旧监听，换故事后归还旧订阅和会话。`Advance(frameToken)`、`Choose(id, frameToken)` 拒绝同会话的旧页输入；Token 按会话编号。文字完整显示可调用 `MarkRead(frame.Token)`。选项按 `choice.Enabled` 展示，最终仍查看动作返回值来确认接受。

- 用 RevUI 的项目可以把会话交给自己的 `RevUIPanel<RevGalSession>`。`OnBindView` 绑定组件，`OnRefreshView` 读取当前 Frame 落屏，按钮将输入转给会话。绑定关系随 `OnDataChanged` 更新，`OnClose/OnRelease` 统一解除订阅和释放宿主持有的资源。会话由流程或面板其中一方持有并释放。RevGame 由现有流程接管进出和 `CloseSelf()`，项目已有 StoryBook 继续沿其业务入口。
- 使用 UI Toolkit、自有 Canvas、3D 对话或命令式文本时，订阅接口和输入命令相同。绑定函数只负责呈现。需要继续信号等跨帧业务时由宿主流程调度。
- 自有界面需要媒体时，从 `Frame.Stage` 和 `Voice/Sound` 取逻辑路径，经 `RevResManager` 加载。用 `RevResScope` 管理该界面拥有的引用。按会话实例与帧 Token 检查迟到回调，旧帧只归还其引用。生命周期可随宿主的 `RevScope` 统一管理。

### 信号和收尾

可让宿主通过 `RevGalOptions.HandleSignal` 注入业务回调，在收到信号帧时调用 `await session.DispatchSignalAsync(frame.Token)`。它按该帧去重，在回调成功后推进。异常交给调用者显示或按业务重试。宿主也可自己处理信号，成功后调用 `CompleteSignal(frame.Token)`。已脱离的会话或旧帧不能继续该故事。外部业务副作用的幂等、撤销和取消仍由宿主管理。

退出时取消仍在打开的请求，解除帧订阅，释放自有媒体和会话。`LoadAsync` 在发起调用前及资源返回后检查取消，迟到的资源引用随即归还。使用 `RevGalOptions.Storage` 接项目档案。省略时默认使用 PlayerPrefs，需要只读试播或隔离测试时设置 `UseDefaultStorage=false`。`Capture/Restore` 仍可用于宿主自己管理的检查点。默认面板的媒体、自动播放和皮肤由其呈现层负责，自有界面选择自己需要的行为。

## 冷态验收

在目标工程先检查当前资源模式，再走自定义面板的正常业务入口。打开剧情后核对首帧实际呈现、选择和变量、宿主信号回调次数、旧帧拒绝和退出回收。使用 `rev_discover` 找到宿主自己注册的开发态接口。默认面板的 `gal.open/state/close` 验证配套界面，测试自有界面时使用宿主自己的入口。选择只制作配置时以编辑器校验、接入清单和离屏路径验收为主。
