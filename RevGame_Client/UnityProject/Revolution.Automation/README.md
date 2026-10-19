# Revolution 通用自动化工具

Python 3.10+，仅使用标准库。通过 CLI 调用 Revolution 的 Unity 编辑器工具、运行期 GM，以及由 GM 组成的通用业务场景。登录、背包、任务、商城、UI、场景和战斗都使用同一套执行机制。

**业务功能由游戏自己实现。** 工具负责调用、等待、断言、循环、采集、清理和报告。`examples/business.*` 是接入模板，里面的“业务/…”指令必须由业务开发者提供。现有 GM 演示示例只操作演示数据。

## 1. 目录与运行环境

| 位置 | 用途 |
|---|---|
| `Automation/rev.py` / `rev.cmd` | 命令行入口 |
| `Automation/revautomation/` | 可直接导入的 Python 客户端、编排和报告模块 |
| `Automation/commands.json` | 内置动作、参数白名单、配置字段及环境要求 |
| `Automation/examples/` | 业务模板、现有 GM 演示、编辑器流程、配置参数示例 |
| `Assets/Revolution/Editor/RevAutomation/` | Unity 主线程桥接、编辑器适配器、场景执行器 |
| `Assets/Revolution.HotUpdate/Editor/RevHotAutomation.cs` | 可选热更包的 CLI 适配器 |
| `Assets/Revolution.Demo/RevHotUpdate.Demo/Editor/RevHotDemoAutomation.cs` | 可选热更演示的 CLI 适配器 |
| `Unity工程/Library/RevolutionAutomation/` | 本机请求、进度、结果和日志；不入版本库 |
| `Automation/results/请求ID/` | CLI 自动导出的 HTML / CSV / JSON；不入版本库 |

Unity 桥接必须放在 `Assets` 对应程序集内才能编译，Python 工具统一放在 `Automation`。复制到其他项目时，要同时带上框架 Editor 桥接。只安装框架本体也可以使用工具，未安装 HotUpdate 或 Demo 时，其动作会明确报“未安装/动作不存在”。

当前执行目标是 **本机 Unity 编辑器**。资产操作支持在线编辑器和关闭工程后的 batch；GM 与场景执行需要图形编辑器中未暂停的 Play。尚未提供独立 Player、手机或浏览器小游戏的远程控制通道。

## 2. 最快开始

以下命令从仓库根目录执行。`rev.cmd` 默认使用 `py -3`；没有 Python Launcher 时可设置 `$env:REV_PYTHON` 为 Python 可执行文件，或直接用 `python Automation/rev.py`。

```powershell
.\Automation\rev.cmd --help
.\Automation\rev.cmd commands
.\Automation\rev.cmd doctor
```

编辑器已打开时，等待 Unity 编译完成即可连接。需要工具启动 Unity 时，明确指定本机实际安装路径：

```powershell
$env:UNITY_EDITOR = 'E:\你的Unity安装目录\Editor\Unity.exe'
.\Automation\rev.cmd --timeout 300 editor launch
.\Automation\rev.cmd --mode live editor status
.\Automation\rev.cmd --mode live editor play --scene 'Assets/你的启动场景.unity'
.\Automation\rev.cmd --mode live gm list
.\Automation\rev.cmd --mode live gm exec '背包/添加道具 1001 2'
.\Automation\rev.cmd --mode live editor stop
```

上面的背包指令只是业务命名示意。`gm list` 显示项目实际注册的命令、参数签名及高危标记；编辑模式列出注册入口的快照，Play 模式读取真实运行期注册表。自动化执行不会替业务偷偷注册命令。

所有全局选项放在子命令**前面**：`--project`、`--unity`、`--mode`、`--timeout`、`--confirm`、`--output`、`--no-graphics`。`--project` 默认指向本仓库的 `RevolutionFrameWork_Unity`，因此可以从其他工作目录调用脚本。

## 3. 一键流程

### 3.1 现有 GM 演示

```powershell
.\Automation\rev.cmd --timeout 300 --output Automation/results/demo-pipeline.json pipeline Automation/examples/demo-gm.pipeline.json
```

流程会启动/连接图形编辑器、进入现有 `RevGMCommandDemo` 场景、执行演示金币流程，最后停止 Play。这里的金币是 Demo 假数据；该示例用于工具接线，不代表任何真实业务验证。本次开发没有执行该流程。

### 3.2 自己的业务

1. 按第 4 节提供真实业务 GM。
2. 修改 `examples/business.pipeline.json` 的场景路径。
3. 修改 `examples/business.scenario.json` 的命令、条件及断言。
4. 先运行少量轮次，确认业务状态和清理契约，再增加重复次数。

```powershell
.\Automation\rev.cmd scenario validate Automation/examples/business.scenario.json
.\Automation\rev.cmd --timeout 86400 --output Automation/results/business-pipeline.json pipeline Automation/examples/business.pipeline.json
```

`pipeline` 是 Python 顺序编排器：每步提供 `action` 和 `payload`，或用 `file` 读取参数。`file` 相对 pipeline 文件所在目录解析。`steps` 任一步失败就停止后续工作步骤，`finally` 逐项执行并记录失败，单个收尾请求最多等待 120 秒。它没有自动重试、事务回滚或断点重放；已经完成的导表/打包不会自动撤销。`--timeout` 是每次 Unity 请求的期限，pipeline 总时长可以更长。

```json
{
  "name": "资源检查",
  "steps": [
    { "action": "ab.scan" },
    { "action": "ab.validate", "payload": { "target": "Android" } },
    { "action": "platform.resources" }
  ],
  "finally": []
}
```

资产操作与业务场景可以编排在一起；在资产操作前先停止 Play，生成代码后等编译完成再进入 Play。流程不保存未保存场景，切换场景时发现未保存内容会返回失败。

## 4. 业务接入契约

`examples/RevBusinessGM.cs.txt` 提供只接收业务回调的注册模板。复制到业务目录后接入自己的服务，模板本身不实现登录、背包、战斗等逻辑。

| 指令角色 | 责任 |
|---|---|
| 准备 | 创建/选择测试账号、初始数据与业务环境 |
| 执行 | 发起一轮真实操作，可传 `{round}` 作为编号或随机种子 |
| 状态 | 无副作用查询，明确返回“运行中 / 完成 / 失败”等约定文本 |
| 断言 | 查询真实业务结果，满足预期时返回约定值；异常/拒绝给明确原因 |
| 复位 | 清理本轮对象、监听、计时器、句柄、临时数据；允许在半途失败后调用 |
| 收尾 | 恢复准备阶段的状态与资源；允许重复调用 |

**`RevGM.Execute` 是同步接口。** 命令返回成功只说明命令体执行完成；异步登录、网络回包、场景加载、动画或一场战斗结束，需要通过 `until` 轮询业务状态。业务跨帧流程继续使用框架的 `RevTask`，由业务服务持有生命周期和取消令牌。查询命令应快速返回，禁止在 Unity 主线程里忙等。

注册只在业务启动时执行一次，结束时按名字 `Unregister`，避免重复注册。编辑器的 `[RevGMEntry]` 入口只做注册，不能在枚举命令时启动真实业务。正式包是否保留 GM 由业务决定，示例模板限定 `UNITY_EDITOR || DEVELOPMENT_BUILD`。

`until` 与 `assert` 比较的是 GM 返回结果的 **Message 精确字符串**，区分大小写；不支持模糊包含、Python 表达式或自动解析任意业务 JSON。业务可以提供专门查询命令返回简洁且稳定的判定文本。需要复杂比较时，在业务查询指令内实现，或通过 Python 取 `gm.exec` 的结构化响应后自行判断。

## 5. 通用场景脚本

```json
{
  "name": "任意业务循环",
  "repeat": 10000,
  "warmup": 10,
  "stopOnFailure": true,
  "failOnUnityError": true,
  "setup": [{ "kind": "gm", "command": "业务/准备" }],
  "steps": [
    { "kind": "gm", "command": "业务/执行 {round}" },
    { "kind": "until", "command": "业务/状态", "expected": "完成", "timeoutSeconds": 60, "intervalSeconds": 0.1 },
    { "kind": "assert", "command": "业务/断言", "expected": "通过" },
    { "kind": "wait", "frames": 2 }
  ],
  "cleanup": [{ "kind": "gm", "command": "业务/复位" }],
  "teardown": [{ "kind": "gm", "command": "业务/收尾" }],
  "thresholds": { "maxFrameP95Ms": 0, "maxFrameMs": 0, "maxMonoUsedBytes": 0 }
}
```

| 字段/步骤 | 含义 |
|---|---|
| `setup` | 全场景执行一次 |
| `steps` | 每轮工作步骤；至少一个 |
| `cleanup` | 每轮后执行，工作失败后也尝试执行 |
| `teardown` | 场景结束时执行，准备失败后也尝试执行 |
| `gm` | 执行命令；返回失败则本步骤失败 |
| `until` | 轮询命令，Message 等于 expected 才继续；必须有正数 timeoutSeconds |
| `assert` | 执行一次查询并比较 expected |
| `wait` | 等待指定秒数与帧数；两者同时填写时都满足才继续 |
| `repeat` / `warmup` | 正式轮数 1..1000000；预热轮数 0..1000000 |
| `{iteration}` | 正式轮从 0 递增，预热阶段固定为 0 |
| `{round}` | 预热加正式的总轮编号，从 0 递增，适合唯一轮号 |
| `stopOnFailure` | 默认 true；false 时某轮失败仍先清理，再继续下一轮；总结果仍为失败 |
| `failOnUnityError` | 默认 true；Unity Error/Exception/Assert 计入失败，工作阶段转入清理 |
| `thresholds` | 0 关闭；非零时限制正式阶段帧 P95、最大帧耗时、采样托管内存峰值 |

步骤按游戏帧推进，最多每帧执行一次步骤/条件查询；`wait.seconds` 和 `until` 超时使用墙钟，不受 `Time.timeScale=0` 影响，但编辑器暂停会中断场景。预热失败也会使总结果失败。清理失败后继续尝试后续清理步骤。

```powershell
.\Automation\rev.cmd --mode live --timeout 86400 scenario run Automation/examples/business.scenario.json --repeat 10000 --warmup 10 --report-dir Automation/results/my-business
```

长场景应显式增大整体 `--timeout`。默认只有 120 秒。一万轮连续在 Unity 内执行，Python 不逐帧发送 IPC。

## 6. 编辑器功能覆盖

目前仓库中的 Revolution 编辑器菜单与主要窗口功能对应如下。菜单是 GUI 入口时，`editor menu --path` 并不存在：使用 `editor menu '菜单全路径'` 打开窗口；无人值守执行使用对应动作。

| 现有工具/功能 | CLI |
|---|---|
| GM 指令面板 | `gm list` / `gm exec`；窗口用 `editor menu` |
| RevAB 打包工具：打包、校验、映射 | `ab build` / `ab validate` / `ab map` |
| RevAB 分包浏览：资源归包/移动/移出 | `ab scan` / `ab mark` / `ab mark-source` |
| 分包改名、层级移动、移除标记、清理空闲名 | `ab rename` / `ab delete` / `ab clean-names` |
| 分包依赖与共享依赖分析 | `ab dependencies`；共享资源用 `ab mark` 移到单独包 |
| 体积页签 / 实际产物 | `ab sizes` / `ab artifacts` |
| 检查页签 / 映射检查 | `ab validate` / `ab check-map` |
| 快照页签 | `ab snapshot` / `ab diff`，baseline 可指定上次打包快照路径 |
| 打包配置、首包拷贝、标记同步、Project 显示 | `ab config` / `ab configure` / `ab copy` / `ab sync` / `ab overlay` |
| 资源/音效目录常量生成 | `ab res-paths` / `ab sound-paths` |
| 编辑器 AB 模式开关 | `ab mode` |
| 导表窗口：来源、输出配置、读取、预览计划 | `excel settings` / `excel configure` / `excel read` / `excel plan` |
| 两个快速导出菜单 | `excel export` / `excel export --data-only`；不传 source 使用已保存来源 |
| 导表数据目录标记、旧数据检查/清理 | `excel mark` / `excel orphans` / `excel clean` |
| UI 面板代码生成器 | `ui scan` / `ui preview` / `ui generate` |
| 平台 Resources 首包审计 | `platform resources` |
| 平台 link.xml 生成 | `platform linkxml` |
| 热更清单窗口的生成/自检 | `hot manifest` / `hot check` |
| 热更演示四个菜单 | `hot demo-prepare` / `hot demo-open` / `hot demo-clear` / `hot demo-urls` |
| 生成全部 UI 演示预制体 | `demo ui-prefabs` |

“新建空分包”在 Unity 中只是窗口中的临时名字；CLI 用 `ab mark` 为资源设置一个新包名，就完成实际分包。`ab delete` 只移除标记，不删除资源。窗口布局、折叠、排序、选择等展示行为不产生资产，CLI 通过结构化结果供 Codex 查询。

AB `scan`、`dependencies`、`sizes` 使用只读收集；`build` / `map` 按原工具收集链执行，配置为 AutoByFolder 时会重新标记资源。`ab sizes` 返回源文件及其依赖源文件字节估算；`ab artifacts` 返回真正产物大小，两者不能混作压缩率或运行时内存。

独立 WPF 导表与 COS 发布程序属于仓库配套应用，不属于 Unity 编辑器扩展，本次 CLI 未增加云上传命令。热更演示装配本地 CDN 目录后，仍需自行启动已有本地 HTTP 服务。

## 7. 参数示例与原工具约束

PowerShell 传嵌套 JSON 容易丢引号，优先使用 UTF-8 JSON 参数文件：

```powershell
.\Automation\rev.cmd ab configure --json Automation/examples/ab-config.json
.\Automation\rev.cmd --timeout 1800 ab build --target StandaloneWindows64
.\Automation\rev.cmd ab dependencies
.\Automation\rev.cmd ab artifacts --target StandaloneWindows64
.\Automation\rev.cmd ab mode --set enabled=true
.\Automation\rev.cmd ab snapshot
.\Automation\rev.cmd ab diff
.\Automation\rev.cmd excel read --source 'D:\策划表'
.\Automation\rev.cmd excel plan --source 'D:\策划表'
.\Automation\rev.cmd excel export --source 'D:\策划表' --data-only
.\Automation\rev.cmd ui scan --prefab 'Assets/GameRes/UI/RevExample.prefab'
.\Automation\rev.cmd ui preview --json Automation/examples/ui-panel.json
.\Automation\rev.cmd platform resources
.\Automation\rev.cmd platform linkxml
.\Automation\rev.cmd hot manifest --target StandaloneWindows64 --json Automation/examples/hot-manifest.json
.\Automation\rev.cmd hot check --target StandaloneWindows64
```

`--set KEY=JSON` 的字符串值必须包含 JSON 双引号；建议字符串、数组和控件参数直接写文件。`call 动作ID --json 参数文件` 可以调用同一动作或可选扩展动作。

配置动作的 `configJson` 是**JSON 字符串**，用于局部覆盖原 ScriptableObject 的字段。允许的配置字段见 `commands.json` 的 `configFields`。AB 枚举使用整数：`markMode` 的 0=ScanExisting、1=AutoByFolder；`compressMode` 的 0=LZ4、1=LZMA、2=None。导表 `userConfigJson` 可保存 `sources`、`includeSubfolders`、`autoReload`。`recursive` 控制本次读取是否递归，CLI 默认 true；需要和窗口的非递归设置一致时传 false。

UI 先 `scan` 取得控件路径和事件名。`controls` 不填采用原窗口默认选择；填空数组表示不选任何控件；每项为 `path`、`bind`、`events`，事件名使用 scan 返回的 Hook（如 Click）。`component` 是扫描返回信息，当前生成器按同一节点优先交互控件的既有规则选组件。代码生成仍走原名称、路径、重复节点和事件消歧校验。已有业务 `.cs` 拒绝覆盖；原生成骨架重生成需要 `--confirm`。

热更 `appVersion` 默认 `Application.version`，`resVersion` 必填；`target` 默认当前 BuildTarget。热更 Demo 准备使用原演示的当前目标和版本自增约定，不接受其他目标参数。

高危 GM、批量移除/改名、清空热更缓存、重生成演示资源，以及原工具要求确认的覆盖/删表，使用全局 `--confirm` 表达允许。缺少该参数返回原因。UI 业务脚本即使带 confirm 也不能覆盖。

## 8. 模式、超时、取消与重载

| 模式 | 行为 |
|---|---|
| `auto` | 有活跃图形编辑器桥接则在线调用，否则尝试 batch |
| `live` | 要求已打开工程并有桥接；GM/场景还要求 Play |
| `batch` | 仅工程关闭时启动子 Unity，执行资产任务并按结果退出；不带 `-quit` 抢先结束 |
| `editor launch` | 打开图形 Unity 并等待桥接；超时后保留该编辑器，日志位置写入错误信息 |

工具不会对已打开的工程再启动第二个 Unity。在线长打包会阻塞主线程心跳，工具仍检查进程/工程锁并拒绝另开编辑器。Unity 路径优先取 `--unity`、其次 `UNITY_EDITOR`、最后按 ProjectVersion/常见 Hub 目录定位；版本不明确时要求显式路径。`--no-graphics` 只影响 batch，不适合渲染性能评估。

每个请求有 ID、会话、期限和独立目录。超期/已取消的排队请求不会迟到执行。开始前保存 active 状态；脚本重载后只恢复 Play/Stop 的状态等待，其他已开始动作返回 `InterruptedByReload`，不重放有副作用的工作。收到该错误时先检查现有产物与逐轮 JSONL，再决定重试。

Ctrl+C 或整体超时会写取消标记。运行中的场景在下次更新进入清理，取消后的清理总共最多 10 秒。Play 已停止、编辑器暂停/关闭或脚本重载时，无法保证业务 GM 清理继续执行。同步 GM/打包调用卡住主线程时，文件标记无法抢占它；需定位阻塞业务。客户端只可能终止自己启动且取消后仍未退出的 batch 子进程，不会终止用户的在线图形编辑器。

```powershell
.\Automation\rev.cmd job 这里替换为32位请求ID
.\Automation\rev.cmd job 这里替换为32位请求ID --cancel
```

`job` 可从另一个终端查询进度或取消。在线等待超时后，Unity 可能仍在清理；通过 job 查看最终 response。CLI 没有自动补跑未完成轮次，避免重复发奖、扣资源或网络请求。

## 9. 报告与性能口径

`scenario run` 与 pipeline 内的 `scenario.run` 完成后自动导出 HTML / CSV / JSON；失败场景也导出已有结果。`--report-dir` 可指定场景报告目录。客户端超时/重载中断时，保留的 job 文件可供手动检查，已有最终结果可以重新导出：

```powershell
.\Automation\rev.cmd report 'Unity工程/Library/RevolutionAutomation/jobs/请求ID/scenario-result.json' --directory Automation/results/recovered
```

| 数据 | 定义与限制 |
|---|---|
| completed / failedIterations | 已记录正式轮数（含失败轮）及失败轮数；全局准备/收尾失败单独影响总 success |
| iterations.jsonl / CSV | 逐轮：成功、墙钟时长、帧数、帧 P95/最大值、轮末内存、GC 计数差和失败原因 |
| 帧均值/P95/最大值 | `Time.unscaledDeltaTime`，正式 steps 与 cleanup 阶段采集；含阶段边界帧及编辑器、业务和采集开销 |
| P95 | 0.1ms 直方图近似上界；200ms 及以上桶保守使用实际最大值，非精确分位数 |
| 内存 | Mono 已用字节、引擎总分配字节；每 30 帧及轮末采样的峰值，可能漏掉采样间短峰；含编辑器和工具开销 |
| GC | 每轮 `GC.CollectionCount(0/1/2)` 差值，包含编辑器和工具分配，非单个业务 GC Alloc |
| unity-errors.jsonl | 最多保留前 50 个 Error/Exception/Assert，含阶段、轮号、步骤和堆栈；总错误数另计 |

帧数据**不是独立 CPU/GPU 耗时，也不等于真机性能**；帧率上限、VSync、失焦限帧、GameView 状态和 Profiler 都会影响结果。稳定性循环与性能测量应固定同一环境，正式真机性能仍需设备侧采集。没有帧样本时 0 表示未测得；启用帧门禁却没有样本会判失败。预热轮不纳入正式性能汇总。

采集使用固定大小直方图并复用每轮缓存；逐轮写盘避免把一万轮结果堆在内存。写盘、JSON 序列化和错误记录仍有成本，需要将工具本身算入测量环境。

## 10. Codex 与 Python 使用

可直接告诉 Codex：“列出可用 GM；按背包脚本运行 20 轮；查看断言与 Unity 错误；给出报告路径”。Codex 调 CLI，根据结构化返回值继续工作。视觉位置、界面外观或系统对话框需要单独的电脑/浏览器控制能力，CLI 不会替鼠标点击自动推断业务完成。

Python 入口：`from revautomation.client import RevClient`，`client.call(action, payload, confirm=False)` 返回带 success/error/message/data/jobDirectory 的字典；Unity 业务失败用 `success=false` 表达，客户端环境/超时错误抛异常，调用者必须检查返回值。`examples/business_runner.py` 演示进入 Play、执行、导出、finally 停止的完整写法。

CLI stdout 输出 JSON，场景进度写 stderr。退出码：0 成功；1 Unity 动作/场景或流程失败；2 参数/客户端/超时错误；130 用户中断。`--output` 同时保存完整正常响应；命令在等待阶段抛出的客户端错误通过 stderr 返回，job 文件仍保留。

可选 Editor 扩展通过 `[RevAutomationAction("自己的动作ID")]` 注册一个静态、接收 `RevAutomationRequest`、返回可被 JsonUtility 序列化对象的方法。适配器引用 `Revolution.Editor`，只在编辑器程序集使用；框架本体不反向引用业务或可选包。

## 11. 本次交付检查范围

已做源码检查、Python 语法/CLI 入口检查及使用真实 Unity 引用的 C# 编译。未自动启动业务场景、运行演示场景或执行一万轮压测；项目目前没有用户自己的业务 GM。Unity 实际 Play、Domain Reload、各平台打包、视觉交互与真机表现需要接入实际业务后再运行。

## 附录：内置动作参数速查

以下清单来自 `commands.json`；`call` 可直接使用动作 ID。client 表示 Python 本地操作；edit 表示非 Play 的在线/batch 操作；live 表示图形编辑器；either 表示在线/batch 均可。

| 动作 ID | payload 字段 | 环境 | 说明 |
|---|---|---|---|
| `editor.launch` | — | client | 打开图形 Unity 并等待桥接 |
| `editor.status` | — | either | 工程、Play、编译和 GM 状态 |
| `editor.play` | scene | live | 进入 Play，可指定场景；等待两帧 |
| `editor.stop` | — | live | 停止 Play |
| `editor.refresh` | — | edit | 刷新 AssetDatabase |
| `editor.menus` | — | either | 发现实际安装的 Revolution 菜单 |
| `editor.menu` | path | live | 打开六类工具窗口；执行操作用专用动作 |
| `ab.config` | — | edit | 读取 AB 配置 |
| `ab.configure` | configJson | edit | 局部修改 AB 配置字段 |
| `ab.scan` | — | edit | 读取分包布局 |
| `ab.validate` | target | edit | 资源与平台 Shader 校验 |
| `ab.build` | target | edit | 收集、校验、构建、映射与首包拷贝 |
| `ab.map` | — | edit | 仅生成映射和路径常量 |
| `ab.res-paths` | — | edit | 生成资源路径常量 |
| `ab.sound-paths` | — | edit | 生成音效路径常量 |
| `ab.check-map` | target | edit | 检查运行期映射一致性 |
| `ab.dependencies` | — | edit | 包依赖、共享资源、循环依赖 |
| `ab.sizes` | — | edit | 按包估算源文件及其依赖源文件体积 |
| `ab.artifacts` | target | edit | 列出实际打包文件与字节数 |
| `ab.snapshot` | — | edit | 记录当前布局为基准 |
| `ab.diff` | baseline | edit | 比较基准与当前布局 |
| `ab.mark` | assets, bundle | edit | 创建/移动资源到分包；空 bundle 清除显式标记 |
| `ab.mark-source` | path | edit | 查询资源的显式/继承标记来源 |
| `ab.rename` | bundle, newBundle | edit | 按包名前缀改名或移动层级 |
| `ab.delete` | bundle | edit | 按包名前缀移除标记，资源仍保留 |
| `ab.clean-names` | — | edit | 清理闲置包名 |
| `ab.sync` | — | edit | 强制同步标记和缓存 |
| `ab.overlay` | enabled | edit | 切换 Project 窗口包标记显示 |
| `ab.copy` | target | edit | 按已启用配置拷贝首包 |
| `ab.mode` | enabled | edit | 切换编辑器 AB 加载模式 |
| `excel.settings` | — | edit | 读取团队及个人导表设置 |
| `excel.configure` | configJson, userConfigJson | edit | 修改团队及个人导表设置 |
| `excel.read` | sources, recursive | edit | 读取并校验 Excel |
| `excel.plan` | sources, recursive | edit | 预览导出变更 |
| `excel.export` | sources, recursive, dataOnly, force | edit | 全量或仅数据导出，移除旧表需要 confirm |
| `excel.mark` | bundle | edit | 标记配置表数据目录 |
| `excel.orphans` | sources, recursive | edit | 列出旧数据文件 |
| `excel.clean` | sources, recursive | edit | 移除已确认的旧数据文件 |
| `ui.scan` | prefab | edit | 扫描预制体控件及支持的事件 |
| `ui.preview` | prefab, className, nameSpace, outputFolder, layer, useAttributes, controls | edit | 预览面板代码 |
| `ui.generate` | prefab, className, nameSpace, outputFolder, layer, useAttributes, controls | edit | 生成面板代码；禁止覆盖业务文件 |
| `platform.resources` | — | edit | 首包 Resources 体积审计 |
| `platform.linkxml` | — | edit | 生成业务裁剪保护；覆盖手写文件需要 confirm |
| `hot.manifest` | target, outputDir, appVersion, resVersion, channel, env | edit | 生成热更清单、首包清单并自检 |
| `hot.check` | target, outputDir | edit | 读取已发布清单并与产物自检 |
| `hot.demo-prepare` | — | edit | 打包并装配本地 CDN 演示 |
| `hot.demo-clear` | — | edit | 删除当前工程客户端热更缓存 |
| `hot.demo-urls` | — | edit | 返回演示 URL 与本地目录 |
| `hot.demo-open` | — | edit | 打开本地 CDN 目录；batch 返回目录 |
| `demo.ui-prefabs` | — | edit | 生成全部 UI 演示预制体 |
| `gm.list` | — | either | 列出实际 GM 及参数、高危标记、注册错误 |
| `gm.exec` | command | live | 执行一条 GM；高危指令需要 confirm |
| `scenario.run` | name, repeat, warmup, setup, steps, cleanup, teardown, stopOnFailure, failOnUnityError, thresholds | live | 通用业务循环、条件、断言、清理与采样 |
| `scenario.validate` | — | client | 离线校验场景脚本格式，不执行游戏 |
