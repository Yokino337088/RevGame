# Revolution.Gal

Revolution.Gal 是 Unity 2022.3 的可选视觉小说模块。把 [Revolution 核心](https://github.com/Yokino337088/Revolution/tree/package) 和 [Gal 扩展](https://github.com/Yokino337088/Revolution/tree/gal) 装入同一工程。源码位于主仓库 `RevolutionFrameWork_Unity/Assets/Revolution.Gal`，`gal` 发布分支的根目录就是扩展包。可以将它放到 `Assets/Revolution.Gal`，也可以在安装核心后用 UPM Git URL `https://github.com/Yokino337088/Revolution.git#gal`。模块自带运行时和 Editor 两个程序集，宿主只有实际使用 Gal 时才需要引用运行时程序集。

## 在 Unity 写剧情

1. 打开 **Revolution.Tools → Gal → 剧情工作台**，新建剧情或选择已有的 `RevGalStory` SO。
2. 左边按名称、ID、说话人或正文找节点。中间拖端口连线，右边写正文、选背景或立绘、添加出口与变量条件。节点名称可以改。发布后的节点 ID 用于定位已读与存档。
3. 将主入口设为起点。对白和旁白连接下一节点。玩家选项可以设置显示与启用条件。自动分支按顺序匹配，未命中时走保底。结局收口当前路线。
4. 下方的问题列表可以跳到节点。从起点或当前节点试读，确认分支与变量，再保存 SO。
5. 首次接入默认 UGUI 时，在工作台点 **生成游戏面板**。需要项目字体或 AB 标记时，用对应的 RevCLI 参数。默认逻辑路径为 `UI/Gal/RevGalPanel`，文件生成在 RevAB 配置的资源根下。

舞台支持一个背景、左中右三位立绘、持续背景音乐、单节点语音和音效。舞台路径留空会沿用，填单个 `-` 会清除该槽。语音与单节点音效留空表示这一节点无该声音。给节点填收藏 ID 和明确背景，可在经过节点后解锁插画。项目的 AB 规则负责把剧情 SO、面板、图集和音频纳入目标平台的 ResMap。编辑器的资源选择器只写逻辑路径。

## Runtime 的三个职责

| 类型 | 职责 |
| --- | --- |
| `RevGalSession` | 从 SO 的独占快照推进节点，计算条件与效果，保存舞台、记录已读、历史和结局。宿主可以独立使用会话，也可以提供自有 Panel。 |
| `RevGalPanel` | 由 RevUI 管理安装、打开、关闭与销毁，接管默认界面的会话、媒体租约、自动和跳过调度，以及宿主信号。 |
| `RevGalView` | 挂在 Panel 的 View 子对象。消费只读帧并呈现打字机、文字、选项、背景、立绘和回看，向 Panel 发出按钮意图。 |

```csharp
// 正常游戏启动已经执行 RevInitialize.Initialize()。
var panel = await RevGal.OpenAsync("Gal/Stories/FirstMeeting", new RevGalOptions
{
    CharactersPerSecond = 38,
    AutoDelaySeconds = 1.8f,
    ResolveText = (key, fallback) => localization.Lookup(key) ?? fallback,
    HandleSignal = (session, signal) => ExecuteStorySignal(session, signal)
});
// 使用现成会话或 RevGal.Close() 收口。默认 Panel 关闭时归还媒体并保存进度。
```

自定义界面可先 `await RevGal.LoadAsync(逻辑路径, options)`，再用自己的 `RevUIPanel<RevGalSession>` 和 View。由该宿主释放 Session。背景、立绘和音频由 `RevResManager.LoadAsync` 获取，通过 `RevResScope` 归还。句柄迟到时只归还本次引用。默认音源随 Panel 生灭，音量乘 RevSound 分类音量。游戏可以按自己的转场暂停或恢复原场景音乐。

播放支持打字机、快显、自动、跳过已读、选项、回看、回退和快速存读档。自动和已读跳过在选项、信号与结局停下。已读跳过到达新对白时恢复手动。档案记录图的剧情 ID、修订、节点 ID、变量、舞台和历史。可通过 `SaveSlot/LoadSlot` 使用多个槽，并用 `IRevGalStorage` 接入业务档案。项目修改旧节点或变量的兼容契约时升级 Revision。回退恢复会话检查点，已读和解锁作为独立的玩家进度保留。

宿主信号只传稳定名称和参数。默认 Panel 等待 `HandleSignal` 结束后推进。自定义宿主可以观察 `Frame.Signal` 并在业务完成后调用 `CompleteSignal(signal.Token)`。等待信号时暂停存档与回退，完成信号会断开之前的回退链。异步业务应以 `session.IsDisposed` 与当前 Token 检查过期回调。

## RevCLI 与 RevMCP

请使用含 Gal 子命令的 RevCLI，并用 `commands --live --query gal` 核对目标 Editor 注册情况。

```powershell
python rev.py --project $project --mode live gal story.create --path Assets/GameRes/Gal/Stories/FirstMeeting.asset --id first_meeting --title "初次见面" --bundle gal_first_meeting
python rev.py --project $project --mode live gal story.export --path Assets/GameRes/Gal/Stories/FirstMeeting.asset --out first-meeting.json
python rev.py --project $project --mode live gal story.import --document first-meeting.json --check-only
python rev.py --project $project --mode live gal story.import --document first-meeting.json --path Assets/GameRes/Gal/Stories/FirstMeeting.asset --overwrite
python rev.py --project $project --mode live gal story.simulate --path Assets/GameRes/Gal/Stories/FirstMeeting.asset --step advance --step choose:lamp --step advance
python rev.py --project $project --mode live gal ui.create --bundle gal_ui
```

JSON 支持符号枚举和 Unity 数值枚举。模板导出可直接往返，未知字段会返回具体字段路径。导入先验证完整定义，再创建或更新 SO。更新保留 GUID，未保存的资产编辑需要先保存。试读支持 `advance`、`choose:ID`、`read`、`rollback`、`capture`、`restore` 和 `complete`。RevMCP 可调用同名 Editor 动作。开发态的 `gal.open`、`gal.state`、`gal.close` 使用真实默认面板，存档放在该 Play 会话的内存中。

## 设计参考

- 端口连线、节点定位与图面操作参考 [xNode](https://github.com/Siccity/xNode) 与 [NodeGraphProcessor](https://github.com/alelievr/NodeGraphProcessor)。
- 写作时可试读、校验问题可定位参考 [Inky](https://github.com/inkle/inky)。
- 正文、选项和宿主动作的表现分离参考 [Yarn Spinner Unity](https://github.com/YarnSpinnerTool/YarnSpinner-Unity)。
- 存读档、回退和画廊的玩法边界参考 [Ren’Py 存档与回退](https://www.renpy.org/doc/html/save_load_rollback.html) 与 [Ren’Py 图鉴](https://www.renpy.org/doc/html/rooms.html)。
