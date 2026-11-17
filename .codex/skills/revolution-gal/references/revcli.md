# 自然语言与 RevCLI

## 连接实际工程

从宿主的 Unity 工程与 CLI 路径出发。把下面的路径改为实际工作树位置。

```powershell
$unity = 'D:/Projects/RevGame/RevGame_Client/UnityProject'
$cli = "$unity/Revolution.RevCLI/rev.py"
$project = "$unity/RevGame.Unity"
py -3 $cli --project $project --mode live commands --live --query gal.
py -3 $cli --project $project --mode live commands --live --query gal.node.
```

有项目工具时优先使用。独立安装使用 Revolution 的 `automation` 发布分支或含 Gal 的 Codex 插件。离线清单只能说明客户端支持的参数。刚编译、重载或更换工程后重新发现。连接已有 Play 时先读状态，剧情和 prefab 在编辑态写入。

### 找到资产和节点

用 `asset.find` 和资产检查定位 `Revolution.Gal.RevGalStory`。按目录、文件名、剧情 ID 或已知逻辑路径收敛。逻辑路径 `Gal/Stories/Lantern` 对应 `<RevAB 资源根>/Gal/Stories/Lantern.asset`，以实际资源映射为准。若活动工作台已给出明确目标，可直接读取该资产。

```powershell
py -3 $cli --project $project --mode live --full asset find --query 't:RevGalStory Lantern' --limit 20
$story = 'Assets/GameRes/Gal/Stories/Lantern.asset'
py -3 $cli --project $project --mode live --full gal node.find --path $story --query '旅人' --kind Line --limit 10
py -3 $cli --project $project --mode live --full gal node.find --path $story --id intro
```

先核对资产搜索回传的 `items[].path/type`，再用查节点回执的 `data.storyId` 识别实际剧情。同标题资产可由路径、剧情 ID 和目标节点区分。局部修改从节点快照出发，整篇编辑、移植或同步源 JSON 时再导出全文。

`node.find` 可不传过滤器来分页列出节点。`--id` 精确且区分大小写。`--query` 匹配 ID、标题、说话人、正文、翻译键、信号、收藏 ID 和选项文字或翻译键中的子串。`--kind` 用 `Line/Choice/Branch/Signal/End`。过滤项同时生效。`--offset` 默认 0，`--limit` 默认 20，最大 200。结果按 SO 节点数组排序，剧情播放顺序要读 `Next` 和出口。

- `data.items[].node` 是完整节点，`data.items[].incoming` 给出起点 `Start`、前驱 `Next` 或带 `node/exit` 的选项 `Target`。
- `data.start/revision/storyId/hash/dirty` 标识当前定义。`hash` 是内容摘要，`Revision` 用于存档兼容。`total/truncated` 指示是否还要查下一页。
- 用户说“把旅人说的‘天快黑了’改成‘天色快暗了’”时，先以原句查询。确认段落后只更新 `Text`。若有 `TextKey`，还要核对显示文案是否来自宿主翻译表。

## 节点编辑

文档用 UTF-8 JSON 文件，通过文件路径传入中文、多行文本、引号及数组。节点字段大小写沿用 `Text/Next` 等剧情 JSON 规范。下面四个编辑命令都接受 `--check-only` 和 `--expected-hash <查询回传的 hash>`。

| 命令 | 参数 | 行为 |
| --- | --- | --- |
| `gal node.add` | `--path --document`，文档是一份完整节点 | 使用新 ID 并满足该类型契约。新增节点若还未从起点连到，会有可达性警告 |
| `gal node.update` | `--path --id --document`，文档是局部字段 | 现有 ID 作为锚点。仅修改列出的字段 |
| `gal node.remove` | `--path --id`，可选 `--redirect <节点 ID>` | 指定 redirect 时改接起点、Next 和选项入边。留有悬空入边时拒绝保存 |
| `gal node.patch` | `--path --document`，文档含有序 `operations`，可选 `start` 和 `revision` | 依序在副本执行，校验整图后一次保存。失败时保留原 SO |

对象递归合并，数组整表替换。`{"Text":"新一句","Stage":{"Left":"-"}}` 会改正文并清除左立绘，保留背景和 BGM。改一条出口的标签或条件时，从查询复制完整 `Exits` 并只改目标项，保留其他出口和顺序。`[]` 清空列表。`null` 可清空可空字段，数值和枚举须使用对应类型。

```powershell
py -3 $cli --project $project --mode live --full gal node.update --path $story --id intro --document $changes --expected-hash $hash --check-only
py -3 $cli --project $project --mode live --full gal node.update --path $story --id intro --document $changes --expected-hash $hash
py -3 $cli --project $project --mode live --full gal node.find --path $story --id intro
```

### 多节点请求示例

用户说“在开场和选择之间加一句旁白，其他分支按原样”。先核对 `intro.Next` 为 `route`，查重选定 `road_note`。一次提交下面的文档。无说话人的 `Line` 可以作旁白。

```json
{
  "operations": [
    {"op":"add","node":{"Id":"road_note","Kind":"Line","Text":"林间起了一阵风。","Next":"route","Position":{"x":280,"y":240}}},
    {"op":"update","id":"intro","changes":{"Next":"road_note"}}
  ]
}
```

用户说“把岔路提示删掉，所有进来的地方直接到旅店”，先查要删的节点、全部入边和实际旅店 ID。确认出入关系后用 `node.remove --id road_note --redirect inn`。需分开改接时，用 `node.patch` 更新每个前驱的 `Next` 或 `Exits`，再执行 `{"op":"remove","id":"road_note"}`。同一文档可指定新的 `start` 或明确的存档 `revision`。redirect 目标应已存在或由更早的 add 创建。

批处理的三种对象形状为 `{"op":"add","node":{...}}`、`{"op":"update","id":"intro","changes":{...}}`、`{"op":"remove","id":"road_note","redirect":"inn"}`。remove 的 redirect 可省。删除回执给出执行该步时剩余的入边，前面已经改接的入边可在原先的查询里追溯。

- `data.valid` 表示整图校验，`data.issues` 区分错误和警告。预检失败、摘要不匹配或存在未保存编辑时先定位原因，再读当前值决定下一次写入。
- `data.changed` 表示候选不同，`data.written` 表示该动作写回。内容相同时可以成功返回 `written=false`。预检返回当前 `hash`，提交后返回已保存的摘要。删除回执的 `operations[].incoming` 列出受影响的入边。
- 改 `Variables` 或其他故事级字段时，用 `story.export` 读取完整定义，编辑源 JSON 后 `story.import --overwrite`。如果项目以 JSON 为真源，同步其源文档和生成的 SO。

## 其他编辑态命令

各子命令都用 `gal <子命令>`。完整参数以实际动作清单为准。

| 子命令 | 作用和参数 |
| --- | --- |
| `story.generate` | `--document` 校验整篇 JSON 并按 ID 生成 SO。可用 `--path --bundle --check-only --overwrite`。返回 `logicalPath`，默认包名前缀为 `gal_story_` |
| `story.create` | `--path --id --title` 生成带两条路线的模板。可用 `--bundle` |
| `story.validate` | `--path` 整图检查，问题见 `data.issues` |
| `story.contract` | `--path` 读取自定义接入清单，含逻辑路径、变量、信号、媒体和本地解析状态。见[自定义宿主接入](custom-host.md) |
| `story.import` | `--document`。预检用 `--check-only`，保存用 `--path`，更新用 `--overwrite`，可用 `--bundle` |
| `story.export` | `--path --out <本机 JSON>` 输出可导入的文档。省略 `--out` 时读 `data.document` |
| `story.simulate` | `--path`。可选 `--entry <节点 ID>`，重复 `--step <操作>` 指定试读步骤 |
| `ui.create` | 在资源根生成默认 UI 和共用主题。可用 `--font --theme --bundle --overwrite`，回执给出 `path/themePath/logicalPath` |
| `editor.open` | 打开剧情工作台。可用 `--path <剧情 SO>` 定位 |

试读操作为 `advance/choose:<出口 ID>/read/rollback/capture/restore/complete`。`capture/restore` 在同一次试读内共用内存书签。被拒绝会带节点 ID 和操作名。`--entry` 从指定节点用初始变量进入，需要前序旗标时从实际起点走整个路线。

成功时读取 `data.node/kind/variables/choices/progress/history/trace`。`trace[]` 按顺序记录 `step/node/kind`。比如样例增加 `road_note` 与接好 `intro.Next` 后，第一次 `advance` 应到 `road_note`，再一次 `advance` 到 `route`。用同一条路径尝试 `choose:locked` 时预期失败，可核对错误消息里的当前节点和拒绝的操作。

## RevMCP 对应关系

`rev_call(target="editor", action="gal.node.update", arguments={...})` 调用同一动作。传 `path/id`，将 JSON 文档作为 `document` 的**字符串**，再传 `checkOnly/expectedHash`。`node.remove` 用 `redirect` 字符串。多步骤验证可用 `rev_run` 的 `steps/expect/until/finally`。轮询读查询，写入后核对回执。Play 的 `gal.open/state/close` 属于 `target="game"`，见[验收与排错](verification.md)。

下面是接入旁白后在 `rev_run.steps` 中的路径断言。将 `path` 换为真实剧情 SO。

```json
{
  "target":"editor",
  "action":"gal.story.simulate",
  "arguments":{"path":"Assets/GameRes/Gal/Stories/Lantern.asset","operations":["advance","advance","choose:lamp"]},
  "expect":[
    {"path":"/data/trace/1/node","equals":"road_note"},
    {"path":"/data/node","equals":"lamp_line"},
    {"path":"/data/variables/trust","equals":1}
  ]
}
```
