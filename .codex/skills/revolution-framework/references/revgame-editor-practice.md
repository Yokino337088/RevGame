# RevGame：Unity编辑器制作与操作实践

## 先定位项目、实例和实际动作

先读 [工程实践索引](revgame-engineering.md) 中命中的Editor文件，再查目标工程是否已有同类入口。RevGame的真实工具示例：`Assets/Editor/RevGame/ResourceBuild/GameHelperAutomation.cs`、`GameResourceBuild.cs`，以及 `Assets/Editor/RevGame/GameHelperBuild.cs`。

- 先记录工程绝对路径、Unity版本、活动场景、dirty、playing/paused/compiling、资源加载模式和已运行作业。
- 已有RevMCP连接且要连续操作时复用会话并 `rev_discover`；一两条临时Editor命令用 `rev.py --project <实际工程> --mode live call <发现到的动作>`。
- 同一Unity实例的资产修改、导表、构建、Play/Stop、场景交割串行。不能为已有工程再启batch Unity，不把切换通道的耗时改善当成AB构建本体加速。
- `commands.json`、Editor代码和旧报告是线索，当前实例注册表才决定能调用什么；编译/域重载后重新发现。

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
