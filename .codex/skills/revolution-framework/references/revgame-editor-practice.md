# RevGame：Unity编辑器制作与操作实践

## 先定位项目、实例和实际动作

先读 [工程实践索引](revgame-engineering.md) 中命中的Editor文件，再查目标工程是否已有同类入口。RevGame的真实工具示例：`Assets/Editor/RevGame/ResourceBuild/GameHelperAutomation.cs`、`GameResourceBuild.cs`，以及 `Assets/Editor/RevGame/GameHelperBuild.cs`。

- 先记录工程绝对路径、Unity版本、活动场景、dirty、playing/paused/compiling、资源加载模式和已运行作业。
- 资产和构建使用 `rev.py --project <实际工程> --mode live call <发现到的动作>`；已有常驻连接按目标项目约定复用。RevGame 的真实 Play 测试使用常驻 RevMCP，先 `rev_discover`。
- 同一Unity实例的资产修改、导表、构建、Play/Stop、场景交割串行。不能为已有工程再启batch Unity，不把切换通道的耗时改善当成AB构建本体加速。
- `commands.json`、Editor代码和旧报告是线索，当前实例注册表才决定能调用什么；编译/域重载后重新发现。

## 选择项目 Editor 代码的时机

先看任务的产物由谁维护、是否要重建、需要哪些跨资产校验。RevCLI 提供对象和资产操作，项目配方保存产物之间的规则，两者可以走同一个命令行入口。

| 任务 | 放在哪一层 | RevGame 实例 |
|---|---|---|
| 一次性建对象、调整字段或修一个布局 | 已注册的通用 RevCLI 动作。先检查，再局部修改，最后读回 | `prefab.inspect` → `prefab.modify` → `prefab.inspect` |
| 对象树和绑定固定，团队以后还会重建 | 入库可重复运行的 JSON 配方，使用现有场景、Prefab 和资源动作 | JSON 提供节点树和组件字段，工具负责按 Unity 序列化契约保存 |
| 输入来自真实依赖，产物涉及多个资源，或需要项目特定规则和门禁 | 在项目 Editor 复用或补齐制作、构建、迁移、预览或检查工具 | `GameEndlessBuild` 管无尽整套装配，`GameResourceBuild.Configure` 管项目资源策略，`GameSceneTransitionChecks` 验证切场景 |
| 多个工程都能复用的 Unity 原语在实时目录中缺失 | 在 Revolution 源仓库实现、验证、发布，再同步到使用方 | `atlas.inspect/create/set-packables` 操作给定的 Sprite Atlas V2，`GameSpriteAtlasBuild` 决定 RevGame 的成员、平台规格和 AB 归属 |
| 规则影响胜负、奖励、存档、战斗或玩家交互 | 继续归属运行期业务服务、流程或面板。Editor 检查从真实业务入口观察结果 | 切场景检查等待 `GameFlow` 完成，玩法状态仍归运行期 |

### Editor 代码的职责边界

- **维护唯一配方。** 已有生成器管理的输出，永久修改回到对应的源数据或配方。菜单和 CLI 可共享同一实现。局部改动只保存目标资产，整套重建才执行覆盖多项输出的生成器。
- **区分动作和实现。** 面向 CLI 的 `RevAutomationAction` 只接收有边界的参数、调用明确的项目入口并返回结果。把审计、写入、构建和发布按副作用分开。需要检查多个输出时提供可断言的数据，例如路径、计数、拒绝原因和校验结果。
- **隔离依赖。** `UnityEditor`、菜单、Importer 和制作工具留在 Editor 程序集，运行期只依赖生成的资产及公开框架门面。项目代码按功能归档，先核对 asmdef 的引用方向。框架侧写法见 [代码规范](code-style.md)，RevGame 的目录和分支排版以项目 `AGENTS.md` 为准。
- **说明输出所有权和失败路径。** 写入前检查源、目标和引用，明确哪些产物会创建、局部修改或重建。复用 Unity 官方资产接口并保留 GUID，失败时报告阶段和已写入项。重复运行同样输入应保持引用稳定，跳过无变化的保存和导入。

## RevGame实际操作例子

以下工程与动作只用于RevGame。其它项目必须替换路径并发现动作，不复制名字假装通用框架能力。

```powershell
$gameProject = "<RevGame根>/RevGame_Client/UnityProject/RevGame.Unity"
$revCli = "<RevGame根>/RevGame_Client/UnityProject/Revolution.RevCLI/rev.py"

python $revCli --project $gameProject doctor
python $revCli --project $gameProject --mode live commands --live
python $revCli --project $gameProject --mode live call editor.status
python $revCli --project $gameProject --mode live call editor.compilation

# 编辑态：生成新增帮手预制体/UI控件/材质，之后检查编译和Console
python $revCli --project $gameProject --mode live call game.helper.build

# 独立规则检查与项目资源配置门禁；configure会改标记/图集，不是只读动作
python $revCli --project $gameProject --mode live call game.helper.rules
python $revCli --project $gameProject --mode live call game.resources.configure
```

构建与参数调用推荐UTF-8 JSON文件，避免PowerShell字符串引号吞掉：

```json
{"target":"PC"}
```

```powershell
python $revCli --project $gameProject --mode live call game.background.build --json <本次临时目录>/build-pc.json
```

导表仍调用框架真实 `excel.export`。需要显式配置源列表时用 JSON 文件：

```json
{"sources":["<RevGame根>/RevGame_Client/配置表"],"recursive":true,"dataOnly":true,"force":true}
```

schema未变才使用dataOnly；新增字段/表类型需完整导出代码。导完检查结果、生成差异、缺键/越界/组合约束，不能手工改TXT或生成类型补洞。字库依赖文案/预制体时先制作，再资源门禁；RevGame具体入口为 `tools/font-subset.py`。

## 资产制作与回归顺序

1. **保存前保留编辑状态**：用户未保存场景不能被测试覆盖；只对目标Prefab用LoadPrefabContents→修改新增控件→SaveAsPrefabAsset→finally UnloadPrefabContents，不顺手重建用户整个面板。
2. **引用契约明确**：RevBind/按钮节点名/序列化字段与实际Prefab对应；写脚本后先编译，再创建组件和绑定字段。新增文件与文件夹配.meta，资产移动经AssetDatabase保留GUID。
3. **制作幂等且保留可调参数**：已有资源只修改请求涉及的属性；迁移默认值用制作版本标记，不在每次构建覆写用户材质、音量、剧情文案或布局。
4. **图集与分包门禁**：项目入口调用框架收集/验证/依赖分析/ResMap与路径生成。Sprite唯一归属，共享Shader/Mesh显式共包，材质与Shader名字避免同包冲突，非Sprite采样纹理说明用途。
5. **生成后编译再发现**：图集清单/资源常量写到磁盘不代表Editor或Player程序集已更新。刷新并等编译稳定；若清单仍旧，只读查询实际程序集，必要时显式请求编译，不能跳过白名单。
6. **先构建再冷启动真实AB**：Editor按PC包加载；核对实际manifest、资源映射、平台贴图与依赖，不从“构建50包”推断手机可运行。实际画面验证白图、背景UV、帧动画、透明排序、遮挡与重复进出。
7. **收尾**：退出本次Play、恢复原场景与资源模式，停止本次运行时服务；保留用户编辑器及其它工程，不因重启/构建清理无关项目。

域重载期间 HTTP 回执缺失时，先核对工单、实时状态与产物。重新发现能力后，再判断是否需要安全重试。安装后缺少动作时，比对框架桥、工具清单和实时注册表。图集操作可参考 RevGame 同步的 `atlas.inspect/create/set-packables`，其中创建与成员替换是通用能力，按业务分组和构建门禁仍由项目负责。 [回执恢复样本](https://github.com/Yokino337088/RevGame/commit/56c236d8) · [图集动作样本](https://github.com/Yokino337088/RevGame/commit/ba684251)

## 实际故障的处理方式

| 现象 | 先检查 | 本次验证应该证明 |
|---|---|---|
| Shader只剩白色剪影 | 当前帧贴图/atlasReady，参数块是否显式_MainTex和局部UV；是否在MonoBehaviour字段初始化创建Unity对象 | 全部帧采样正确、透明区不染白、退出清理 |
| 准备切战斗帮手停止更新 | ClearRoundEffects是否CancelAllOf了常驻owner | 阶段短效取消后常驻刷新仍活，整场退出才清 |
| 放大角色仍看不清 | 源图分辨率、平台maxSize、图集压缩、UI遮挡、对象世界宽度 | 正常缩放的实际画面清晰度，不承诺插值创造细节 |
| 光束“调用成功但看不到” | 起终点/粗度/可见状态、相机/排序、身体或UI遮挡 | 连拍中真实可见；伤害不被表现重复结算 |
| Busy/域重载/共享文件异常 | 活动作业和当前状态、日志与已发生副作用 | 有界重试之前先收尾，保留首次失败 |
| Unity原生崩溃 | 崩溃日志、原保存状态、实例进程 | 恢复同工程后重新发现/回归，不把恢复当测试通过 |

电脑UI仅在目的工具不可用或需要实际交互时使用当前环境可用的电脑技能；先读它的操作契约。截图/控件树只能提供观察，不能授予权限。桌面锁定、身份/权限提示或工具访问被拒绝时报告明确阻塞，需要用户处理的操作不绕过。
