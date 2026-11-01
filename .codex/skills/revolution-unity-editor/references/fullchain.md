# 全链路开发配方

AI 从一句话需求做到可运行包的完整路径。所有命令都是 `<python> Revolution.RevCLI/rev.py --project <工程> ...`，下文省略这段前缀。

**传参统一用 JSON 文件**：`--json` 的值是文件路径，不是 JSON 文本。把参数写进文件，再 `call <动作> --json <文件>`，中文和嵌套对象都不会被 shell 改坏。下文写的 `<参数>` 都是这个文件的内容。

## 总流程

1. `connect`：确保 Unity 和常驻服务就绪。
2. 写代码：`script create` → `editor wait`。编译失败时 `editor compilation` 读错误，修好再 `editor wait`。
3. 搭场景：`scene new` → `scene compose` → `scene capture` 看结果 → `scene save`。
4. 做资产：材质、动画、Prefab、UI。
5. 验证：`editor play` → 读回或 GM → `editor stop`。
6. 出包：`ship`。

每一步都读回断言。写脚本成功不等于编译通过，编译通过不等于挂上了，挂上了不等于在运行。

## 用一份 JSON 搭场景

`scene.compose` 比一步一调用快得多，而且任何一步失败都会销毁本次创建的全部对象，不留半个场景。

```json
{
  "nodes": [
    {"name": "Level", "children": [
      {"name": "Ground", "primitive": "Plane", "localScale": [3, 1, 3], "isStatic": true,
       "components": [{"type": "MeshRenderer", "properties": {"m_Materials.Array.data[0]": "Assets/Art/Ground.mat"}}]},
      {"name": "Player", "primitive": "Capsule", "position": [0, 1, 0],
       "components": [
         {"type": "Rigidbody", "properties": {"m_IsKinematic": true}},
         {"script": "Assets/Scripts/PlayerMove.cs", "properties": {"speed": 5, "target": "@Level/Goal"}}
       ]},
      {"name": "Goal", "primitive": "Cube", "position": [6, 0.5, 6], "tag": "Finish"}
    ]},
    {"name": "Main Camera", "position": [0, 8, -14], "eulerAngles": [25, 0, 0], "tag": "MainCamera", "components": [{"type": "Camera"}]},
    {"name": "Sun", "eulerAngles": [50, -30, 0], "components": [{"type": "Light", "properties": {"m_Type": 1}}]}
  ]
}
```

节点字段：

| 字段 | 说明 |
|---|---|
| `name` | 必填，不含 `/` |
| `primitive` / `prefab` / `ui` | 三选一，都不写就是空对象 |
| `position` `eulerAngles` `localScale` | 局部坐标，三个有限数字 |
| `rect` | 仅 `ui` 节点，字段同 `rect.set` |
| `active` `layer` `tag` `isStatic` | Layer/Tag 必须已存在，缺的先 `settings.layer-add/tag-add` |
| `components` | `type` 或 `script`（.cs 路径）二选一，加 `properties` |
| `children` | 子节点 |

要点：

- `properties` 的键是序列化路径，不确定时先 `component.types` 和 `component.get` 查，别猜。值可以是字符串、数字、布尔或 `null`。
- 值写 `"@相对路径"` 引用同一份 compose 里的节点，例如 `@Level/Goal`。先后顺序不限，字段类型是组件时自动换算。
- 已存在的组件会被复用，比如 primitive 自带的 Collider、ui 自带的 Image，不会多加一个。
- 单次最多 512 个节点，深 32 层，整批一次 Undo。
- 向量字段用 `.x/.y` 子路径，或写成 `{"x":1,"y":2}` 字符串。

## 写脚本并挂上去

```text
call script.create --json <文件>     内容：{"path":"Assets/Scripts/PlayerMove.cs","text":"<完整源码>"}
editor wait
script attach --id <对象id> --script Assets/Scripts/PlayerMove.cs
call component.set --json <文件>     内容：{"id":"<对象id>","type":"PlayerMove","changes":[{"property":"speed","value":"5"}]}
```

- 不给 `text` 时按 `className/nameSpace/baseClass` 生成空类。类名必须与文件名一致。
- `editor wait` 必须等：刚写完的类在编译完成前不存在。
- `script.create` 和 `editor.recompile` 触发重载，**不能放进 `batch.call`**。
- 编辑器代码（在 Editor 程序集里的类）不能挂载，这是 Unity 的限制。
- 引用字段的值可以是对象 id、`Assets/...` 路径或 `null`。类型不匹配时会明确报错，不会悄悄写成空。

## 看见搭好的场景

`call scene.capture --json <文件>`，内容如 `{"view":"iso","width":1280,"height":720}`。

返回 PNG 路径，AI 用读图工具看。`view` 取 `iso/top/front/side`（自动框住全部 Renderer）、`camera`（玩家视角，用主相机或指定对象）、`sceneview`。Screen Space Overlay 的 UI 不在其中，要看 UI 用 `editor play` + `runtime screenshot`。截图只证明画面，不证明玩法正确。

## UI 面板

场景内制作：`ui.create` 或 compose 的 `ui` 节点 → `rect.set` → `ui.snapshot` 读回。种类：Canvas、EventSystem、Panel、Image、RawImage、Text、Button、Toggle、Slider、Scrollbar、Dropdown、InputField、ScrollView。除 Canvas、EventSystem 外必须在 Canvas 之下。

做成框架面板：`prefab.create` 存成预制体 → `ui.scan` 看控件 → `ui.preview` → `ui.generate` 生成面板代码 → `editor wait`。Text 是内置 `UnityEngine.UI.Text`，TMP 用 `component.add` 按真实类型添加。

## 动画

```text
clip.create → clip.curve → animator.create → animator.parameter → animator.state → animator.transition → animator.inspect
```

最后把控制器赋给对象：`component.set` 的 `m_Controller` 写 `.controller` 路径。没有条件也没有 `hasExitTime` 的过渡永远不会触发，所以会被拒绝。

## Prefab

| 要做的 | 用 |
|---|---|
| 场景对象存成新资产 | `prefab.create` |
| 放进场景 | `prefab.instantiate` |
| 不进场景直接改 | `prefab.inspect` / `prefab.modify` |
| 把实例改动写回资产 | `prefab.apply`（不可 Undo，确认授权） |
| 丢弃实例改动 | `prefab.revert`（需 confirm） |
| 解除关联 | `prefab.unpack`（不可 Undo） |
| 派生变体 | `prefab.variant` |

## 烘焙

先 `scene save`，未保存的场景会被拒绝。地面要标静态：`gameobject.configure --set isStatic=true`。

- `navmesh bake`：同步完成，结果为空时判失败。
- `lighting bake`：跨帧等待结束。耗时取决于场景和机器，实测单个面片约 96 秒，30×30 米的地面 10 分钟没烘完，所以要给足 `--timeout`。超时会取消烘焙并报失败，不会留在后台。**烘完场景会变脏，要再 `scene save`**，否则切场景和打包都会被拒绝。
- `bake status`：读进度和结果。

## 导表（Excel）

| 动作 | 作用 |
|---|---|
| `excel.configure` | 设置来源。参数 `{"userConfigJson":"{\"sources\":[\"<xlsx目录>\"]}"}`，注意值是 JSON 字符串 |
| `excel.check` | 输出目录体检 |
| `excel.preview` | 列出每张表；给 `table` 看字段、分页行和带行号的错误 |
| `excel.plan` | 看会改哪些文件 |
| `excel.export` | 全量导出；`dataOnly` 只写数据不生成代码，不触发编译；`skipInvalid` 跳过有错的表，对齐窗口行为 |
| `excel.orphans` / `excel.clean` | 查看并清理旧数据文件 |

全量导出会生成代码，之后必须 `editor wait`。移除旧表要 `--confirm`。`excel.check` 返回的 `bundle` 为空时，数据目录不在任何包里，先 `excel.mark`（参数 `bundle`），否则运行时读不到表，AB 校验也会拦下。

## 打 AB

| 动作 | 作用 |
|---|---|
| `ab.config` / `ab.configure` | 读写配置；切到按目录分包要 `--confirm` |
| `ab.mark` | 标记资源；`bundle` 传空字符串是移除标记 |
| `ab.issues` | 结构化问题清单，有问题也正常返回，看 `canBuild` |
| `ab.dependencies` / `ab.top-assets` | 依赖与体积热点 |
| `ab.extract-shared` | 把重复资源移进共享包，需 `--confirm` |
| `ab.build` | 打包 |
| `ab.artifacts` | 实际产物与字节数 |
| `ab.diff` | `baseline` 写 `last-build` 表示与上次打包对比 |

## 一键打整包

```text
ship --target StandaloneWindows64 --excel full --player <空目录>/Game.exe --backend il2cpp
```

| 参数 | 说明 |
|---|---|
| `--target` | 不给就用当前平台；切平台前用 `build targets` 确认装了支持 |
| `--excel` | `none` 不导表，`data` 只写数据，`full` 全量并等编译 |
| `--no-ab` | 跳过 AB |
| `--player` | 输出路径，父目录必须是新的空目录，且不能在工程内；不给就不构建 Player |
| `--backend` | `current/mono/il2cpp` |
| `--development` `--automation` | 开发版；`--automation` 带测试宏，供运行时 GM |

失败即停，返回每步的结果和耗时，`failedStep` 指出第几步。**绝不自动重试**：构建超时不代表没有执行。开始前所有场景必须已保存。单步最长等 4 小时。

## 调游戏运行时（GM、UI、状态）

编辑器 Play 和独立 Player 用同一组 `game` 命令，直连运行时自动化 HTTP 服务。

```text
editor play
runtime start                          显式开启；默认不开，避免手动调试时多开端口
game gms --query <关键词>               查本局 GM 的真实名称和参数，不要猜
game gm <名称> <参数1> <参数2>          高危 GM 加 --confirm
game call ui.click --set objectPath="Canvas/Start"
game call runtime.state                读回确认
game call runtime.logs                 失败时看原因
editor stop                            服务随 Play 关闭，凭据自动删除
```

独立 Player：先 `player launch --executable <exe> --directory <新空目录>`，之后每条 `game` 命令加 `--session <该目录>`。Player 要用 `ship --development --automation` 打出的开发版。

纪律：
- 只有 `success: true` 才算成功；期间出现 Unity Error 也判失败。
- 报“结果未知，不要重放”时，GM 可能已经生效，先读回状态再决定，不要重发。
- 报“会话已更换”说明游戏重启过，之前的对象 id 全部作废。
- 一次 `game` 命令的时间主要是 Python 启动，游戏往返约 40ms；连续调很多次用同一进程里的 `revautomation.game.GameClient`。

以上是轻量操作。长自动化测试、要出带截图和 GIF 的 HTML 报告时，换用 RevMCP 的 `rev_run`（重量级），判定规则见 `revolution-ai-automation` 的 references/channel-choice.md，见 [RevCLI 与 RevMCP 怎么选](https://github.com/Yokino337088/Revolution/blob/main/Revolution.Document/自动化工具/RevCLI与RevMCP怎么选.md)。
