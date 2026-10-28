# 怎么证明"改对了"（验证与测试）

> 这个框架的哲学是"**没有'猜它能不能工作'，只有'跑给看'**"。改完代码不要停在"应该没问题"。

## 0. Revolution.RevCLI 业务回归入口

先读 `automation-integration.md`，定位目标工程与真实能力。跨接口测试联合 `revolution-ai-automation` 使用 RevMCP；旧 GM 循环/采样继续 `scenario validate → scenario run`。先发现、再实际运行、核对业务断言/日志/清理并记录证据，不止列出命令。

使用未暂停图形Play与 `--mode live`，batch仅资产操作。刚补接线需等编译并从运行期 `gm list` 确认；场景每轮清理，保护用户原有Play与未保存场景。通过必须检查完整response、场景成功、轮次齐全、Unity错误为零且无取消。截图/AB/真机等未执行项单列，不被离线断言或历史数字替代。

## 1. 五级验证（由快到慢，按改动范围选）

| 级别 | 适用 | 怎么做 |
|---|---|---|
| ① 编译 | 任何改动 | Unity 里编译 0 error；纯 C# 部分可以先用工程外桩工程编译一遍（见第 3 节） |
| ② 跑 demo 场景 | 业务/模块用法 | 打开对应 `<模块>Demo.unity` → Play → 点按钮，看右侧步骤日志；**改了模块行为就必须跑它的 demo** |
| ③ GM 面板 / 场景内自检 | 快速复现与手工验证 | `Revolution.Tools/GM 指令面板`（Ctrl+Shift+G）；或加一条临时 GM 命令直接跑 |
| ④ **工程外纯 C# 断言** | 改了框架**内核**（路径/槽位表/池/日志/计时/解析/规则） | 链接那些不引用 UnityEngine 的文件到普通 .NET 工程，`dotnet run -c Release` 跑断言（见下） |
| ⑤ 真机 + 破坏性测试 | 资源/UI/热更等"只有真机才暴露"的改动 | Android 真机跑首包落地 + 增量更新；热更按第 4 节的破坏性测试逐项过 |

### 1.1 快速编译检查（不开 Unity 窗口也能看有没有编译错）

Unity 打开过工程后，仓库里会有 Unity 生成的 `*.sln` / `*.csproj`（**不入库**，在 `.gitignore` 里）。有它们时可以直接：

```text
dotnet build <工程目录>/RevolutionFrameWork_Unity.sln --no-restore -v minimal
```

- 看末尾的"0 个错误"即可；大量 `MSB3277` 程序集版本冲突警告是**既有噪音**，与你的改动无关。
- 只能证明"C# 语法 + 类型引用"成立；Unity 侧行为（生命周期、真加载、真机路径）仍靠 demo 场景与真机。
- `.csproj` 里没有的新文件要等 Unity 重新生成工程文件后才会被 `dotnet build` 看到（新增文件后先让 Unity 刷新一次，或在 Unity 里直接看 Console）。

### 1.2 "最近的 demo"在哪

工程内前缀为 `RevolutionFrameWork_Unity/Assets/`；已有场景通常在 `Assets/Revolution.Demo/<模块>.Demo/<模块>Demo.unity`（索引 `RevDemoSceneList.txt`）。2026-10-16 快照为 18 个独立场景；另有仅脚本示例，不能按目录数量推算场景数量。演示脚本通常用 OnGUI：左侧按钮逐个点，右侧步骤日志。

| 模块 | Demo | 模块 | Demo |
|---|---|---|---|
| RevLog | `RevLog.Demo` | RevInput | `RevInput.Demo` |
| RevTimer | `RevTimer.Demo` | RevDataLoad | `RevDataLoad.Demo` |
| RevPublicMono | `RevPublicMono.Demo` | RevSoundSystem | `RevSoundSystem.Demo` |
| RevObjectPool | `RevObjectPool.Demo` | RevStateMachine | `RevStateMachine.Demo` |
| RevEventSystem | `RevEventSystem.Demo` | RevResourceSystem | `RevResourceSystem.Demo` |
| RevServiceLocator | `RevServiceLocator.Demo` | RevSingleton | `RevSingleton.Demo` |
| RevTask | `RevTask.Demo` | RevUISystem | `RevUISystem.Demo`（一个场景演了使用说明每一章：三种接法 / 生命周期 / 全部 API / 带数据 / 六层与遮罩 / 动画 / Part） |
| RevScene | `RevScene.Demo` | RevActionSequence | `RevActionSequence.Demo` |
| RevGMCommand | `RevGMCommand.Demo` | RevHotUpdate | `RevHotUpdate.Demo`（需先跑菜单"一键：打包 + 清单 + 装配本地 CDN"） |

**仅脚本示例**：`Assets/Revolution.Demo/RevScope.Demo/RevScopeFlowDemo.cs` 演示 `RevScope` + 状态机 + `RevLogic` 联动，无独立 `.unity` 场景；在任意场景创建空物体挂脚本再 Play。

**没有独立 Demo 的**：`RevInitialize` / `RevAppLifecycle` / `RevStripGuard`（验证靠真机或手动切焦点；`RevStripGuard` 在编辑器下不跑，可手动 `RevStripGuard.Check()`）。

## 2. 能进工程外断言的文件（框架已有清单）

`README.md`（仓库根）「工程外验证」一节列了这些**不引用 `UnityEngine`** 的文件，可直接链接进 .NET 控制台工程：

```text
RevResourceSystem/Core/RevResPathUtil.cs            路径拼接 + 缓存键（含"两段键 == 完整路径键"不变量）
RevTimer/Core/RevTimerTable.cs                       计时器槽位表（代际号 / 延迟复用 / 句柄校验）
RevTimer/Core/RevServerClock.cs                      服务器时间（一次校准 + realtime 外推）
RevSoundSystem/Core/RevSoundVoiceTable.cs            声音槽位表（代际号 / 同帧去重 / 上限淘汰）
RevObjectPool/Core/RevPoolCore.cs                    池引擎（重复归还拦截）
RevLog/Core/RevLogRing.cs                            日志环形缓冲（定长 / 零分配写入）
RevLog/Implementation/RevLogCore.cs                  日志内核（过滤 / 重复抑制 / 通道隔离）
RevPublicMono/Implementation/RevMonoCore.cs          监听列表内核（去重 / 上限 / 快照派发 / 异常隔离）
RevServiceLocator/Facade/RevServices.cs + Implementation/RevServiceHub.cs + Interfaces/*.cs   服务定位器新入口（注册 / 取用 / 注销 / owner / 逆序释放 / Tick；Support 里的 Unity 钩子不要链）
RevServiceLocator/Facade/RevServiceLocator.cs + RevServiceBuilder.cs + Implementation/RevServiceRegistry.cs + RevServiceDescriptor.cs + Core/RevServiceLifetime.cs   Builder 写法（作用域）
Editor/RevResourceSystem/ABTool/Snapshot/ABLayoutSnapshot.cs   AB 布局快照差异（改名 / 换包 / 增删识别）
```

> `RevServices` 的 `ResetForNewSession` 是 `internal`（只给 Support 层调）：工程外断言用**反射**模拟"进 Play 复位"，不要为了测它去给产品代码加 `InternalsVisibleTo`。

以下是文档保留的**历史**实测记录，不代表当前检出有同等覆盖的持久测试，也不代表本次重跑通过。引用时注明来源、版本和测量环境；修改相关内核后用实际测试文件复验，不能把旧数字改成未经执行的新数字：

| 模块 | 断言 | 实测 |
|---|---|---|
| RevTimer | 55 / 55 | 1000 个计时器每帧 **0.0139 ms**；10 万次创建 + 停止 **< 4 KB** |
| RevLog | 41 / 41 | 10 万条日志 **0 分配**；环形缓冲硬顶 2048 条 |
| RevPublicMono | 25 / 25 | 10 万次派发 **32 B** |
| RevServices | 55 / 55 | 已创建服务的 `GetRequired` 10 万次 **0 B**（5 轮取最小；单轮会有测量噪声，别拿一次读数下结论）；1000 轮 注册→取用→注销 无残留 |
| ABTool 布局快照 | 24 / 24 | 改名 / 换包 / 增删识别 |

> 本仓库历史上用同样办法验过：事件系统（52 项行为断言 + 5 组种子 × 6000 步 fuzz，零结构违例）、
> GM 指令（112 项断言 + 20000 步随机注册/随机命令行的 fuzz）、热更清单与 URL 规则、ResMap 查表成本基准。
> **断言要覆盖"不变量"而不只是"正常路径"**：节点不重复、不双归还、计数不为负、状态归零、无残留标记。

### 2.1 当前入库的 RevServices 离线工程

- 路径：`RevolutionFrameWork_Unity/Tests/RevServices.Offline/RevServices.Offline.csproj`；直接链接 `RevServices.cs`、`RevServiceHub.cs` 和三个纯 C# 服务接口，不依赖 Unity 的 Support 层。
- 运行：在仓库根执行 `dotnet run --project RevolutionFrameWork_Unity/Tests/RevServices.Offline/RevServices.Offline.csproj -c Release`。9 项回归覆盖默认懒创建、首次事件前 `eager`、owner / 重复注册、构造 / OnInit / Tick 宿主失败回滚及重入登记保护；2026-10-19 本轮实跑 **9/9 通过**。此项不替代 Unity Play / AB / 真机验证。

### 2.2 当前入库的热更离线工程

- 路径：`RevolutionFrameWork_Unity/Tests/RevHotUpdate.Offline/RevHotUpdate.Offline.csproj`，`OutputType=Exe`、`TargetFramework=net10.0`；链接真实 `Assets/Revolution.HotUpdate/Runtime/Core/RevHotManifest.cs` 与 `RevHotBundleInfo.cs`。
- 入口：同目录 `Program.cs`，18 项断言覆盖清单身份（平台/渠道/环境）、依赖闭包/环、安全包名/版本、哈希算法/格式、序列化往返/截断、WebGL Hash128/CRC 门禁；未覆盖 Unity AB 真加载、候选激活回退、下载 Range 或跨启动续传。
- 运行：在仓库根执行 `dotnet run --project RevolutionFrameWork_Unity/Tests/RevHotUpdate.Offline/RevHotUpdate.Offline.csproj -c Release`；失败抛异常并非零退出，成功打印 `RevHotUpdate manifest offline: 18 assertions passed`。这是控制台断言程序，不能用 `dotnet test` 的零测试结果冒充验证成功。
- 入库范围：经 `git ls-files RevolutionFrameWork_Unity/Tests/RevHotUpdate.Offline` 核实仅 `Program.cs` 与 `.csproj`；`bin/obj` 被 `.gitignore` 排除，本机存在构建产物不等于提交了产物。

## 3. 搭一个工程外断言工程（模板见 `templates/offline-harness/`）

```text
1. 通用模板 TargetFramework=net8.0，区别于上面的持久 net10 工程；EnableDefaultCompileItems=false
2. 将 Harness.csproj.txt / Program.cs.txt 复制到 Assets 外并去掉 .txt；替换 <REPO>，仅链接要验的真实源码
3. 默认链接 RevResPathUtil；增加其他内核时补齐关联类型，需要 Unity 类型才自行补最小桩
4. 填写真实不变量断言；占位用 Skip，不用 That(true) 冒充通过；未实现项返回 2、断言失败返回 1
5. dotnet run -c Release；固定随机种子，记录本次通过/失败/未实现数量与实际命令
```

**注意**：桩工程只能证明"纯 C# 逻辑 + API 用法"成立；Unity 侧行为（MonoBehaviour 生命周期、AssetBundle 真加载、真机路径）仍要靠 demo 场景与真机验证。

## 4. 热更破坏性验收清单（待在 Unity / 真机执行，不是当前已通过记录）

1. 下载中**断网/取消/杀进程** → `.part` 和 `.part.meta` 保留；重算计划后仅同版本/包名/大小/SHA-256/URL 身份复用，不同源或内容必须清零。
2. **Range 异常** → 206 起点与总长正确才写；服务端返回 200 或 416 时从零 GET，不拼接完整响应；完整分片先校验再提交。
3. **清单损坏/空清单/跨平台/跨渠道/依赖环** → `ManifestInvalid`，不发布候选；只有已有验证可用基线时才按业务策略继续旧版，不能无条件进游戏。
4. **同名坏包/SHA 不符/未变包缺失** → 完整候选快照按目标 SHA 补齐；找不到一致来源则拒绝激活，不能混读任意旧目录。
5. **磁盘不足** → `DiskFull`；验证下载量与完整快照空间预检，区分可续传分片和垃圾，不能保证失败后目录绝对为空。
6. **资源持有/在途加载、候选激活失败/重装失败/激活中断电** → 拒绝不安全切换；验证 `.pending` 恢复旧指针，恢复失败必须停在错误而非 Ready。删目录不能代替真正的故障注入。
7. **首包兜底/WebGL 真加载** → 新装与清数据重新验证首包；WebGL 验证内置主包 Manifest + CDN 包 Hash128/CRC、ResMap 和域名/CORS。保留旧版不等于任意旧版兼容可回滚。
8. **跨大版本** → `AppVersionMismatch` / 强更回调交由业务处理，不静默硬更。另核实代表性资源加载探针：当前源码未提供 `ProbeBundles` 配置，不能把门禁或 `Init()` 等同业务资源可用。

记录每项平台、版本、输入破坏、期望/实际、日志和复现步骤；持久工程的 18 项断言不覆盖这些端到端行为。

### 4.1 游戏业务流程的 GM / AI 验收（项目方接线）

官方架构规范：`Revolution.Document/编程范式/Revolution官方编程架构规范.md`。关键业务必须设计“查询前置 → 受控动作 → 查询后置 → 非法前置应拒绝”的 GM 命令，handler 走真实业务服务或流程控制者，而不是改 UI、FSM 内核或存档。验收至少覆盖登录、大厅、战斗、结算、退出、重复同名域与异步取消，记录输入命令、`RevGMResult.Success/Message`、FSM 状态、`RevScope.Dump()`、判空后的 `LastCloseReport`、`RevLogic.Count` 与异常日志。

- 2026-10-22 已提供 `Revolution.RevMCP`、Unity 内 RevMCP 控制台和 `RevRuntimeAutomation`。实际 GM/业务能力经主线程调用并校验 Schema/confirm，开发版 Player 显式开启本机 HTTP；优先加载 `revolution-ai-automation` 跑接口、断言、日志、截图与清理。项目仍需自己注册业务，不能从离线文档假定目标工程已安装新版。
- “命令已注册”“AI 写出了测试方案”“离线断言通过”都不等于 PlayMode/设备/MCP 验收完成；报告按实际执行环境和证据分别标明通过、失败或未执行。变更类测试命令不得仅靠 `Hidden` / `HighRisk` 保护正式环境。

## 5. 文档站与分支（交付验证）

### 5.1 当前 CI 与编辑器版本记录的边界

- 2026-10-16 已跟踪 `.github/workflows/` 包含文档部署、生成分支同步、编辑器版本 guard；没有已入库的质量门禁 CI。正在讨论/中断中的 A 级工作不算完成，工作流存在也不等于编译、18 项断言或真机验收已经执行。
- `ProjectSettings/ProjectVersion.txt` 已从 Git 跟踪删除，`.gitignore` 明确忽略 `RevolutionFrameWork_Unity/ProjectSettings/ProjectVersion.txt`；本机 Unity 再写此文件不应当入库。
- **现存矛盾**：`.github/workflows/guard-editor-version.yml` 仍无条件 `cat "$FILE"` 并检查旧的团队 `EXPECTED_VERSION`；干净 checkout 没有被跟踪的版本文件，触发该工作流时可能失败。不要声称 guard 已适配当前忽略策略；本 skill 只说明矛盾，不修改 workflow。

### 5.2 发布与提交纪律

- 改了 `Revolution.Document/**` → 推 `main` 后 `pages.yml` 重新部署，在线站可见（https://yokino337088.github.io/Revolution/ ）；
- 改了 `Assets/Revolution/**` / `Assets/Revolution.Demo/**` / `Assets/Revolution.HotUpdate/**` / `Skills/**` → 对应生成分支（`package` / `demo` / `hotupdate` / `skill`）自动整份重建，可 `git fetch origin <分支>` 核对提交与文件数。
- **推送目标**：工作副本配了多个远程（个人 fork + 原仓库）时，先 `git remote -v` 确认要推哪一个再推，别默认 `origin`。
- 提交信息统一中文；PowerShell 下用 `git commit -F <UTF-8 文件>`，**不要** `-m "多行中文"`（会乱码 / 解析失败，已踩过）；临时信息文件提交后删掉，别被 `git add -A` 误带入。
- 修已推送提交的信息需要 `commit --amend` + `push --force-with-lease`——**属于改写历史，先得到人的明确同意**，并先核对该提交的作者是不是本人。
- **完成即交付（自动推送）**：一次任务里完成的功能改动，按上面规则确认暂存范围后，**直接提交并推送一次，不等用户催**。推送目标按上一条先 `git remote -v` 确认（本仓库 `main` 跟踪 `upstream/main`）。与功能无关的 Unity 生成噪声（`ProjectSettings*`、仅换行符差异的 `packages-lock.json`、`TimelineSettings.asset`、来历不明的空文件等）**不提交**，留在工作区并向用户说明；文档/Skill 改动推完即视为交付完成，生成分支由 CI 自动重建。

## 6. 收尾习惯

- 仅将实际执行得到的断言/性能数字更新到文档，注明版本与环境；规模统计注明快照日期。历史结果与当前覆盖分开，未执行不写“已通过”；
- 发现"文档与代码不一致"时，**先确定哪个是对的**再改——多半是代码迁移过、文档漏改（例如目录改名、模块路径）。


## 真实业务验证参考

按 [真实业务验证参考](revgame-ai-workflow.md) 读取RevGame同类实现，核对版本、所有者、失败路径与已执行证据；不得把历史样本结果当当前项目通过。
