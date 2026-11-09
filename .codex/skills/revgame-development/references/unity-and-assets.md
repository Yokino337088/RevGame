# RevCLI 编辑器与资源迭代

搭场景、制作/修改 Prefab、UI、导入图片、材质、动画、编译、导表、AB 和出包时读本篇，并联动 [revolution-unity-editor](../../revolution-unity-editor/SKILL.md)。优先项目相邻的 `Revolution.RevCLI/rev.py`，真实游戏测试再切本项目要求的 RevMCP。

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
- 布局以 `GameBattleLayout` 的 1920×1080 为设计尺子；安全区保护交互，背景覆盖视口。图片的 Raycast Target 与按钮职责匹配，检查遮罩/排序、超长中文和多个屏幕比例。
- 检查 Sprite 引用与**实际图集像素绑定**。懒创建 `GameSpriteAtlasService` 可能错过首次 `atlasRequested`；继续由 Bootstrap 早装配服务，不能通过预载所有图集制造首次不白的假象。
- 技能默认保留无尽古希腊神殿原画与不透明象牙金棋盘。用户明确重制主题时按新需求替换相应资源与配方，不把历史素材选择当永久禁止修改。

## Editor Play 材质粉屏与 AB 平台

- 先记录活动构建目标、Editor 实际图形 API、AB 开关和主包来源。Windows Editor 的真实 AB 验收使用 PC 包，即使 Build Target 留在 Android。正式 Player 验收使用目标平台包。Device Simulator 的设备型号用于布局，不能决定包来源。
- 粉屏时查实际材质的 Shader 名称、`Shader.isSupported`、纹理绑定和画面。直读正常而 AB 异常时优先核对主包、共享 Shader 包、依赖和目标平台变体。直读作为定位对照，结论以原故障条件下的冷启动真 AB 为准。
- 2026-11-09 旧版 `RevABLoader.MainName` 在 Android 构建目标下把 Windows Editor 导向 Android AB。框架已改为 `UNITY_EDITOR` 优先选 PC。遇到类似回归先核对安装快照。框架选包问题回 Revolution 源仓库修复、导出并同步。
- 复测序章要显式开启隔离首启 `game.story.test-arm`（`flags=0`）或在大厅调用 `story.preview`。用 RevMCP 等 `story.state` 就绪并核对 `story.yaya-state` 的 Shader 支持、纹理绑定与截图。结束退出 Play、解除隔离并检查 Console。详见 [误加载 Android AB 粉屏复盘](../../../../RevGame_Client/文档/Bug踩坑记录/2026-11-09-EditorPlay误加载AndroidAB导致剧情粉屏.html)。

## 资源与 AB 完整链

1. **定义源与消费者。** 图片先明确是 Sprite/UI/序列帧还是 Shader 独立采样 Texture。使用中 Sprite 恰好属于一个 Sprite Atlas V2；字形纹理、内置 UISprite、RenderTexture、法线/LUT、章节 Default 采样纹理不能错误纳入图集。
2. **保留项目配置入口。** 分包与图集通过 `GameResourceBuild.Configure` 串联 `GameSpriteAtlasBuild` / `GameResourceUsage` / 压缩与字段配置，不自写 AB 加载器或直接调用框架内核。默认导入参数和特殊画质由当前配置/性能规则决定。
3. **按寿命分包。** 大厅、章节共享、章节专属、无尽面板/图/材质分生命周期；不用一个大 UI 包拖入全部模式。序列帧、共享 Shader 与字体显式归属；章节 Shader 采样图独立按章加载。新增图片不是“所有美术库都进 AB”。
4. **导表和文案联动。** `RevGame_Client/配置表/*.xlsx` → 实际 `excel.*` → `GameScripts/Generated` 与 `GameRes/Data` → `GameConfig` / `EndlessConfig` 校验；不得手改生成表顶替源 Excel。涉及文本/字库范围按 `GameFontSubsetBuild` 更新并验证，避免缺字。
5. **构建前门禁。** 当前动作候选为 `game.resources.audit/configure/validate`，先查契约；检查漏标、重复/循环依赖、图集唯一覆盖与源图同包、缺 Shader、字体校验。门禁失败先修源头，不跳过检查发布。
6. **构建与复制。** 用当前 `ab.*` 与项目 `GameResourceBuild.BuildAndCopy` 等固定入口。复制/清旧包由当前 Manifest 与经过绝对路径校验的指定平台目录决定，不自行递归清理配置路径。常规资源迭代不自动升级成整包 `ship`。
7. **真实资源验收。** 冷启动相应平台 AB，不用 Editor 直读代替；检查新增 Prefab、图集页面、背景 UV、透明边、九宫格、动画、字体、第一次打开和重复进出。引用/图集/队列回到相应基线。入口查 `GameSceneTransitionChecks` 与当前 `game.resources.play-check`；具体测试走 [验收指引](validation-and-delivery.md)。

运行期资源只用 `RevResPath.*` 或两段式门面。图集经 `GameSpriteAtlasService` 绑定；对象经 `GamePoolRequests`/`RevPool` 租用，晚到对象只能归还。按加载者契约成对 Release，不把 Release 等同立即清空进程内存。

无尽 BGM 参考 `EndlessMusicPlaylist`：长曲逐曲独立 AB，接近结束异步预载下一曲，完成后释放上一曲。不要一次加载/解压全部长音乐或用全局 Stop/Kill 清掉其它模式。外部素材保留项目来源/授权记录，用户未要求时不额外下载资源。
