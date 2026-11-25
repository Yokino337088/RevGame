# RevCLI 编辑器与资源迭代

搭场景、制作/修改 Prefab、UI、导入图片、材质、动画、编译、导表、AB 和出包时读本篇，并联动 [revolution-unity-editor](../../revolution-unity-editor/SKILL.md)。优先项目相邻的 `Revolution.RevCLI/rev.py`，真实游戏测试再切本项目要求的 RevMCP。

新建或迁移材质先在 `Assets/GameRes/Material/<模块>` 选目录，Shader 用 `Assets/GameShader`。保存、移动和删除后运行 `python tools/RevPolicy/check.py` 检查路径与 GUID，再按工程现有构建流程验证 AB 标记和引用。

## 连接、发现、保护现场

在仓库根执行；解释器按机器发现，不把本机路径保存成技能配置：

```powershell
$taskRepo = (Get-Location).Path
$taskProject = Join-Path $taskRepo 'RevGame_Client/UnityProject/RevGame.Unity'
$taskCli = Join-Path $taskRepo 'RevGame_Client/UnityProject/Revolution.RevCLI/rev.py'
$taskPython = (& py -3 -c "import sys; print(sys.executable)")
& $taskPython $taskCli --project $taskProject --mode live doctor
```

若没有 `py`，发现可用 Python 3.10+ 的真实解释器再调用。只读体检可用 `doctor`；真正开始编辑器工作或重启/断连后按联动技能执行 `connect`，核对编译和能力缺口：

```powershell
& $taskPython $taskCli --project $taskProject --mode live connect
& $taskPython $taskCli --project $taskProject --mode live commands --live --query prefab
& $taskPython $taskCli --project $taskProject --mode live commands --live --action prefab.modify
```

1. 读取真实 `editor.status`、场景列表/dirty 状态和 selection，记录本次操作目标。已有编辑器用 live，不另启 batch Unity 抢工程锁，不丢弃用户未保存场景。
2. `commands --query` 离线结果仅帮助定位；可调用性以 `commands --live` 返回为准。按具体 `--action` 读取 keys、环境、confirm、reload 契约；大型清单不全文反复读取。
3. `call <真实动作ID> --json <参数文件>` 传中文、数组、嵌套属性；参数文件放 `.codex/work/<任务>`。调用前 inspect/get，确认目标路径、组件类型、属性可写与身份唯一。
4. 编译/域重载后按真实等待入口等到编辑器稳定，重新发现，不复用旧对象 ID、GM 注册或 UI ID。超时先查作业，不自动重复变更类动作。
5. 同步且独立的查询可合成 `batch.call`；有依赖的制作走 `pipeline` / 顺序调用并保留等待与 finally。资产变更、编译、构建、Play 串行。

本技能制作时只读发现确认过 `prefab.modify`（path/objectPath/type/index/changes）与 `game.endless.build`（edit、无 keys）。这是编写依据，每次使用仍重新查询；下面其它 ID 是查找线索，不是当次已注册的声明。

编辑器动作跨域重载或断线时先读状态、编译诊断和已写入产物，再决定是否重试。Play 域重载可能丢 HTTP 回执，动作已生效时以实际状态与工单为准。同步框架能力时同时核对 Unity 桥、CLI 清单和实时注册表，先区分安装快照落后与真正缺少能力。历史样本见 [HTTP 域重载修复](https://github.com/Yokino337088/RevGame/commit/56c236d8) 与 [图集 V2 动作同步](https://github.com/Yokino337088/RevGame/commit/ba684251)。

## 从代码到可保存的 Prefab

| 任务 | 优先入口与读回 | 项目接线 |
|---|---|---|
| 场景和对象布局 | 查询 `scene.compose` / `scene.capture`、对象与 transform/rect 动作 | 多节点可用一份配方；保存场景并检查对象身份、相机、父级、排序 |
| 新脚本与组件 | 正常文件编辑或查询 `script.create`，编译等待后 `script.attach` | 源文件先放正确功能目录；Mono 类型与文件名一致；Editor 类型不挂运行期物体 |
| 修改 Prefab | 查询 `prefab.inspect` / `prefab.modify` 等 | 由 Unity 加载/修改/保存/卸载内容；读回节点、组件、序列化引用与保存结果 |
| 新 UI 面板 | 查询 `ui.create` / `rect.set` / `ui.snapshot` | `[RevUIPanel]` 逻辑路径、`RevBind` 节点和回调路由一起接通；沿用 Canvas 与安全区约定 |
| 材质、Shader、动画 | 查询 `material.*`、`shader.*`、`clip.*`、`animator.*` | 改共享资产与每实例参数的职责；池对象停旧动画；不逐棋子克隆材质 |
| 项目固定制作配方 | 查询 `game.*` 动作并检查被调用的项目 Build 类 | 小改用局部动作；只有要重建整套目标资产时执行完整生成器，先核对会覆盖哪些资产 |

需要维护制作配方或补动作时，按 [RevGame Editor 代码规范](editor-code.md) 选择 JSON、项目 Editor 或 Revolution 源框架，明确 asmdef、写盘范围和重建所有权。已有生成器管理的永久修改要进入源配方。只改一个按钮时，沿用局部 Prefab 编辑入口。

## UI 原画与真实显示

- 先查看原 Prefab 和 `GameRes/Art/UI` / `RevGame_Client/美术` 的已有原画，复用对应用途的 Sprite。新底板/按钮必须有真实 Sprite，禁止用无 Sprite 的白膜顶替。
- 正确导入 Sprite 与裁切：border、pivot、PPU、透明边、九宫格适配一起核对；适用底板用 `Image.Type.Sliced`。无尽同类装配参考 [GameEndlessBuild.Theme.cs](../../../../RevGame_Client/UnityProject/RevGame.Unity/Assets/Editor/RevGame/Endless/GameEndlessBuild.Theme.cs) 的导入/映射/白膜检查。
- 新建或修改 UI 预制体和面板时，默认按 [多画幅自适应布局](../../revolution-framework/references/ui-and-layering.md#多画幅自适应布局) 实现和验收。RevGame 以 `GameBattleLayout` 的 1920×1080 为设计框，背景等比铺满视口并按构图裁边。大厅标题、菜单和设置按钮共用内容框，棋盘 HUD 按面板声明使用安全边缘。决斗选角和加载页使用 RevUI 的内容区和原画组件，大厅、棋盘与剧情复用 RevUIViewport 的等比计算。核对立绘投影、真实字形、遮罩排序与 EventSystem 点按。
- 检查 Sprite 引用与**实际图集像素绑定**。懒创建 `GameSpriteAtlasService` 可能错过首次 `atlasRequested`；继续由 Bootstrap 早装配服务，不能通过预载所有图集制造首次不白的假象。
- 技能默认保留无尽古希腊神殿原画与不透明象牙金棋盘。用户明确重制主题时按新需求替换相应资源与配方，不把历史素材选择当永久禁止修改。

遇到预制体变白或批量排查时，按 [UI 预制体白块排查](ui-prefab-white-preview.md) 核对空图槽、编辑器图集缓存和运行时 AB。

## Editor Play 材质粉屏与 AB 平台

- 先记录活动构建目标、Editor 实际图形 API、AB 开关和主包来源。Windows Editor 的真实 AB 验收使用 PC 包，即使 Build Target 留在 Android。正式 Player 验收使用目标平台包。Device Simulator 的设备型号用于布局，不能决定包来源。
- 粉屏时查实际材质的 Shader 名称、`Shader.isSupported`、纹理绑定和画面。直读正常而 AB 异常时优先核对主包、共享 Shader 包、依赖和目标平台变体。直读作为定位对照，结论以原故障条件下的冷启动真 AB 为准。
- 2026-11-09 旧版 `RevABLoader.MainName` 在 Android 构建目标下把 Windows Editor 导向 Android AB。框架已改为 `UNITY_EDITOR` 优先选 PC。遇到类似回归先核对安装快照。框架选包问题回 Revolution 源仓库修复、导出并同步。
- 复测序章要显式开启隔离首启 `game.story.test-arm`（`flags=0`）或在大厅调用 `story.preview`。用 RevMCP 等 `story.state` 就绪并核对 `story.yaya-state` 的 Shader 支持、纹理绑定与截图。结束退出 Play、解除隔离并检查 Console。详见 [误加载 Android AB 粉屏复盘](../../../../RevGame_Client/文档/Bug踩坑记录/2026-11-09-EditorPlay误加载AndroidAB导致剧情粉屏.html)。

## 资源与 AB 完整链

导入前在 AssetDatabase 和 `Assets/GameRes` 核对已有资产的路径、GUID、逻辑路径、导入规格和包归属。候选文件改名或重新编码时，按需要比较 SHA-256 或解码后的内容。已有资产能满足用途时，各模块直接引用原资产或其逻辑路径。只补缺少的内容。只有实际需要不同的切片、采样或格式产物时才引入独立变体，并记录消费者和包体影响。清理重复副本前，先迁移 Prefab、SO、剧情配置或生成器的引用，再确认旧 GUID 和逻辑路径已无使用者。

1. **定义源与消费者。** 图片先明确是 Sprite/UI/序列帧还是 Shader 独立采样 Texture。在用 Sprite 恰好属于一个 Sprite Atlas V2，源图和图集归同一个 AB。字形纹理、内置 UISprite、RenderTexture、法线/LUT、章节 Default 采样纹理不纳入 Sprite 图集。
2. **保留项目配置入口。** 分包与图集通过 `GameResourceBuild.Configure` 串联 `GameSpriteAtlasBuild` / `GameResourceUsage` / 压缩与字段配置，不自写 AB 加载器或直接调用框架内核。默认导入参数和特殊画质由当前配置/性能规则决定。
3. **按寿命分包。** 同一资源在同一目标平台只归一个 AB。跨模块沿用原包或按生命周期明确归入一个共享包，消费包通过实际依赖复用。排查未显式归包的纹理、材质等依赖是否被 Unity 分别嵌入多个包。大厅、章节共享、章节专属、无尽面板/图/材质各有生命周期。序列帧、共享 Shader 与字体显式归属，章节 Shader 采样图独立按章加载。只将实际使用并应当交付的图片纳入 AB。
4. **导表和文案联动。** `RevGame_Client/配置表/*.xlsx` → 实际 `excel.*` → `GameScripts/Generated` 与 `GameRes/Data` → `GameConfig` / `EndlessConfig` 校验；不得手改生成表顶替源 Excel。涉及文本/字库范围按 `GameFontSubsetBuild` 更新并验证，避免缺字。
5. **构建前门禁。** 先发现 `game.resources.audit`、`game.resources.configure` 和 `game.resources.validate` 的真实注册。分包或图集布局改动后重新配置，构建前验证漏标、隐式重复、循环依赖、图集唯一覆盖与源图同包、Shader 和字体。门禁失败先修源头。
6. **增量构建与发布。** 先发现实际动作，再用 RevCLI 依次执行 `ab build-incremental --target StandaloneWindows64`、`ab check-map --target StandaloneWindows64`、`ab publish-incremental --target StandaloneWindows64`。每步成功后再继续。发布前核对目标平台的实际包内容、依赖、`ResMap` 和构建清单，确认相同资源只有一份打包内容。对照同平台基线的包名、SHA-256 和字节数，并在发布后读回实际替换数量。构建保留同平台缓存。发布失败且源构建已完成时可单独重试。Android 用 `--target Android` 单独走一遍。项目全套构建按需继续用 `GameResourceBuild.BuildAndCopy`。详见 [增量 AB 配方](../../revolution-unity-editor/references/incremental-ab.md)。
7. **真实资源验收。** 冷启动相应平台 AB，不用 Editor 直读代替；检查新增 Prefab、图集页面、背景 UV、透明边、九宫格、动画、字体、第一次打开和重复进出。引用/图集/队列回到相应基线。入口查 `GameSceneTransitionChecks` 与当前 `game.resources.play-check`；具体测试走 [验收指引](validation-and-delivery.md)。

### Gal 接入 RevGame

- 默认面板剧情从大厅或选关的稳定状态调用 `GameFlow.OpenGalAsync`。流程先卸载大厅并进入独立 `GalScene`，再于 Gal 域装配面板和首屏媒体。结局的返回按钮使用 `GameFlow.ReturnHome` 恢复大厅并释放剧情资源。按故事需要传入 `RevGalOptions` 的宿主信号或档案实现。编辑态制作与自定义界面见 [Gal 技能](../../revolution-gal/SKILL.md)。
- 正式剧情 SO 放 `Assets/GameRes`，建议沿用工具默认的 `Gal/Stories`。试读草稿可以放在资源根以外。默认面板与主题放 `UI/Gal`，用项目 CJK 字体。背景 Material 放 `Material/Gal`，专用语音放 `Audio/Voice/Gal`。节点和舞台方案通过已有逻辑路径复用角色立绘、背景、音乐及各自的 AB，只补项目缺少的剧情媒体。
- `GameResourceBuild.Configure` 按剧情 ID 分包，并把节点和章节方案中的媒体加入引用审计。图集归属、字体、包依赖和冷启动验证沿用本篇约定。需要重新生成字库时运行 `python tools/font-subset.py`。

运行期资源只用 `RevResPath.*` 或两段式门面。图集经 `GameSpriteAtlasService` 绑定；对象经 `GamePoolRequests`/`RevPool` 租用，晚到对象只能归还。按加载者契约成对 Release，不把 Release 等同立即清空进程内存。

无尽 BGM 参考 `EndlessMusicPlaylist`：长曲逐曲独立 AB，接近结束异步预载下一曲，完成后释放上一曲。不要一次加载/解压全部长音乐或用全局 Stop/Kill 清掉其它模式。外部素材保留项目来源/授权记录，用户未要求时不额外下载资源。
