# Revolution.Gal

Revolution.Gal 是 Unity 2022.3 的可选视觉小说模块。把 [Revolution 核心](https://github.com/Yokino337088/Revolution/tree/package) 和 [Gal 扩展](https://github.com/Yokino337088/Revolution/tree/gal) 装入同一工程。源码位于主仓库 `RevolutionFrameWork_Unity/Assets/Revolution.Gal`，`gal` 发布分支的根目录就是扩展包。可以将它放到 `Assets/Revolution.Gal`，也可以在安装核心后用 UPM Git URL `https://github.com/Yokino337088/Revolution.git#gal`。模块自带运行时和 Editor 两个程序集，宿主只有实际使用 Gal 时才需要引用运行时程序集。

## 在 Unity 写剧情

1. 打开 **Revolution.Tools → Gal → 剧情工作台**，新建剧情或选择已有的 `RevGalStory` SO。
2. 左边按名称、ID、说话人或正文找节点。中间拖端口连线，右边写正文、选背景或立绘、添加出口与变量条件。节点名称可以改。发布后的节点 ID 用于定位已读与存档。
3. 将主入口设为起点。对白和旁白连接下一节点。玩家选项可以设置显示与启用条件。自动分支按顺序匹配，未命中时走保底。结局收口当前路线。
4. 下方的问题列表可以跳到节点。从起点或当前节点试读，确认分支与变量，再保存 SO。
5. 首次接入默认 UGUI 时，在工作台点 **生成游戏面板**。生成的 Prefab 位于 RevAB 资源根下的 `UI/Gal/RevGalPanel.prefab`，并默认引用同目录的 `RevGalPanelTheme.asset`。需要指定字体、已有主题或 AB 标记时，使用 RevCLI 参数。

舞台支持一个背景、左中右三位立绘、持续背景音乐、单节点语音和音效。舞台路径留空会沿用，填单个 `-` 会清除该槽。语音与单节点音效留空表示这一节点无该声音。给节点填收藏 ID 和明确背景，可在经过节点后解锁插画。项目的 AB 规则负责把剧情 SO、面板、图集和音频纳入目标平台的 ResMap。编辑器的资源选择器只写逻辑路径。

## 只制作剧情或接入自己的界面

剧情工作台的 **接入检查** 显示当前 SO 的逻辑路径、变量、信号、媒体类型及本地资源解析结果。可复制 JSON，也可用 `gal story.contract --path Assets/…/Story.asset` 从 RevCLI 获取。工作台的试读和 `gal story.export` 都可以用于只有剧情配置的项目。

节点文案输入即时写入当前 SO 的编辑状态，点击 **保存** 写入磁盘。短暂停笔后，工作台更新该节点的图上摘要、大纲搜索结果和校验提示。舞台、变量修改及本地化等折叠区在首次展开时加载。

新工程可以从空工作台点 **快速创建并试读示例**，或用 `gal story.quickstart` 生成一份能跑通两条路线的剧情。配置了 RevAB 时会返回可打包的逻辑路径。维护分支时在 **剧情设置 / 变量** 里运行路线用例，或用 `gal story.test` 执行同一份 JSON。可参考 [快速开始和路线测试](https://github.com/Yokino337088/Revolution/blob/skill/revolution-gal/references/quickstart-and-tests.md)。

自有界面从 `RevGalPlayback.LoadAsync` 加载逻辑路径，或把已持有的 Story SO 交给 `RevGalPlayback.Create`。宿主自己装配类型化字段时用 `CreateFromDefinition`。这些入口都返回 `RevGalSession`，由宿主持有。`Observe` 立即交付首帧并订阅后续帧；用回调捕获的帧 Token 调用 `Advance(token)` 和 `Choose(id, token)`。宿主可使用 RevUI、UI Toolkit 或自己的显示系统。配置了 `HandleSignal` 后，用 `DispatchSignalAsync(token)` 执行并等待完成，或由宿主自行处理再调用 `CompleteSignal(token)`。

关闭或换故事时由宿主取消打开任务，释放帧订阅和会话。已有 `RevGal.LoadAsync/Preview/OpenAsync` API 保持兼容。示例、RevGame 生命周期和媒体引用约定见 [自定义宿主 Skill](https://github.com/Yokino337088/Revolution/blob/skill/revolution-gal/references/custom-host.md)。

## 共用面板主题

工作台的 **章节 / 小节背景** 可保存带父级的舞台方案。章节定义共用背景和材质，小节继承后覆盖自己的 Shader。入口节点选择对应方案，节点自身的 Stage 继续支持更细的覆盖。继承循环、缺失方案与错误路径会在校验时定位。

工作台的 **界面与接入 → 面板设计器** 可点选并调整控件尺寸、位置及图片。RevCLI 的 `gal ui.layout` 查询控件，`gal ui.layout-set` 根据控件名批量保存，支持预检和 hash 冲突检查。完整示例见[章节方案与面板设计](https://github.com/Yokino337088/Revolution/blob/skill/revolution-gal/references/stages-and-layout.md)。

工作台的 **编辑面板主题** 会选中默认面板引用的主题。在 Inspector 为整个主题指定图集和字体，再按部位选择图集切片或独立 Sprite。独立 Sprite 优先。按钮、对白框、选项、设置面板和回看可各自选图并设置颜色。带边距的 Sprite 可启用九宫格。背景和立绘的画面取自剧情节点，主题可以调整它们的布局。

共享主题可挂载背景 Material，或在剧情节点填 `Stage.BackgroundMaterial` 覆盖。材质携带 Shader 与参数。空路径沿用，短横线恢复主题或 Prefab 的默认材质。显式节点材质可以在没有 Sprite 时单独绘制，适用于程序化背景。用项目管线兼容的 UGUI Shader 并随目标平台的 AB 一起验收。

展开部位的 **布局**，勾选覆盖后修改相对父节点的锚点、轴心和边距。自动生成的主题以默认 Prefab 的位置为初值。选项行由滚动列表排版，主题可设置行高。设置和回看遮罩始终覆盖整个剧情面板。主题随 Prefab 引用加载，在下次打开面板时生效。更换主题图集后按宿主资源规则构建目标平台 AB，确保主题、字体和图集随面板交付。

播放中的 **设置** 位于顶部。自动、跳过已读、回退、回看、快速保存和快速读取归入同一个弹层。弹层打开时暂停打字与自动推进，拦截背后的选项。回看返回设置，成功回退或读档后返回剧情。已生成的旧面板可继续播放。要升级为新的默认布局，使用 `gal ui.create --overwrite` 重建生成树。重建时会沿用已绑定的主题 SO。

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

自定义界面可先 `await RevGalPlayback.LoadAsync(逻辑路径, options)`，再用自己的 `RevUIPanel<RevGalSession>` 和 View。由该宿主释放 Session。背景、立绘和音频由 `RevResManager.LoadAsync` 获取，通过 `RevResScope` 归还。句柄迟到时只归还本次引用。默认音源随 Panel 生灭，音量乘 RevSound 分类音量。游戏可以按自己的转场暂停或恢复原场景音乐。

默认面板也可用 `RevGal.OpenAsync(path, options, slot, cancellation)` 绑定一次宿主打开请求。宿主切换流程时取消该请求。面板交付后由面板自身或持有它的宿主关闭。宿主的 Editor 构建代码可用 `RevGalHostContract.InspectMedia(story, resourceRoot)` 将剧情和舞台方案的动态媒体交给自己的打包规则。

播放支持打字机、快显、自动、跳过已读、选项、回看、回退和快速存读档。自动和已读跳过在选项、信号与结局停下。已读跳过到达新对白时恢复手动。档案记录图的剧情 ID、修订、节点 ID、变量、舞台和历史。可通过 `SaveSlot/LoadSlot` 使用多个槽，并用 `IRevGalStorage` 接入业务档案。项目修改旧节点或变量的兼容契约时升级 Revision。回退恢复会话检查点，已读和解锁作为独立的玩家进度保留。

宿主信号只传稳定名称和参数。默认 Panel 等待 `HandleSignal` 结束后推进。自定义宿主可以观察 `Frame.Signal` 并在业务完成后调用 `CompleteSignal(signal.Token)`。等待信号时暂停存档与回退，完成信号会断开之前的回退链。异步业务应以 `session.IsDisposed` 与当前 Token 检查过期回调。

## RevCLI 与 RevMCP

请使用含 Gal 子命令的 RevCLI，并用 `commands --live --query gal` 核对目标 Editor 注册情况。

```powershell
python rev.py --project $project --mode live gal story.generate --document examples/gal-story.json
python rev.py --project $project --mode live gal story.create --path Assets/GameRes/Gal/Stories/FirstMeeting.asset --id first_meeting --title "初次见面" --bundle gal_first_meeting
python rev.py --project $project --mode live gal story.export --path Assets/GameRes/Gal/Stories/FirstMeeting.asset --out first-meeting.json
python rev.py --project $project --mode live gal story.import --document first-meeting.json --check-only
python rev.py --project $project --mode live gal story.import --document first-meeting.json --path Assets/GameRes/Gal/Stories/FirstMeeting.asset --overwrite
python rev.py --project $project --mode live gal story.simulate --path Assets/GameRes/Gal/Stories/FirstMeeting.asset --step advance --step choose:lamp --step advance
python rev.py --project $project --mode live gal ui.create --bundle gal_ui
```

一键生成会校验剧情结构，为 JSON 的 `Id` 创建 `<资源根>/Gal/Stories/<Id>.asset`，并返回 `logicalPath` 和 AB 名。可以用 `--path` 指定资源根内的 SO 位置，用 `--bundle` 指定包名。默认包名以 `gal_story_` 开头。已有包名会在未指定新包名时沿用。用 `--check-only` 预检而不写资产，用 `--overwrite` 更新已有 SO 并保留 GUID。制作好的主题可通过 `gal ui.create --theme Assets/.../主题.asset` 绑定到默认面板。

### 增删改查节点

```powershell
python rev.py --project $project --mode live --full gal node.find --path $story --query "旅人" --kind Line
python rev.py --project $project --mode live gal node.add --path $story --document new-node.json
python rev.py --project $project --mode live gal node.update --path $story --id intro --document fields.json --expected-hash $hash --check-only
python rev.py --project $project --mode live gal node.remove --path $story --id old_line --redirect next_line
python rev.py --project $project --mode live gal node.patch --path $story --document edits.json
```

`node.find` 返回完整节点、入边和当前 `hash`。`node.update` 用局部 JSON，例如 `{"Text":"天色快暗了。"}`。对象逐字段合并，数组整体替换。`node.patch` 接收有序 `operations`，可将新增节点、改线和删除放在一次整图校验里。写入支持 `--check-only` 及 `--expected-hash`，通过后保存同一资产。自然语言编辑、数据字段、宿主、UI 资源与验收流程见 [独立 revolution-gal Skill](https://github.com/Yokino337088/Revolution/tree/skill/revolution-gal)。

JSON 支持符号枚举和 Unity 数值枚举。模板导出可直接往返，未知字段会返回具体字段路径。导入先验证完整定义，再创建或更新 SO。更新保留 GUID，未保存的资产编辑需要先保存。试读支持 `advance`、`choose:ID`、`read`、`rollback`、`capture`、`restore` 和 `complete`。RevMCP 可调用同名 Editor 动作。开发态的 `gal.open`、`gal.state`、`gal.close` 使用真实默认面板，存档放在该 Play 会话的内存中。

## 设计参考

- 端口连线、节点定位与图面操作参考 [xNode](https://github.com/Siccity/xNode) 与 [NodeGraphProcessor](https://github.com/alelievr/NodeGraphProcessor)。
- 写作时可试读、校验问题可定位参考 [Inky](https://github.com/inkle/inky)。
- 自定义呈现参考 [Yarn Spinner 的 DialoguePresenter](https://docs.yarnspinner.dev/api/csharp/yarn.unity/yarn.unity.dialoguepresenterbase)，宿主驱动的会话参考 [Ink Runtime API](https://github.com/inkle/ink/blob/master/Documentation/RunningYourInk.md)，剧情数据与示例界面的分工参考 [Godot Dialogue Manager](https://github.com/nathanhoad/godot_dialogue_manager)。
- 存读档、回退和画廊的玩法边界参考 [Ren’Py 存档与回退](https://www.renpy.org/doc/html/save_load_rollback.html) 与 [Ren’Py 图鉴](https://www.renpy.org/doc/html/rooms.html)。
