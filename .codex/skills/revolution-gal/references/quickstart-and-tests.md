# 快速开始和路线测试

## 一键建第一份剧情

在 Unity 打开 **Revolution.Tools → Gal → 剧情工作台**。空工作台的 **快速创建并试读示例** 会生成两条路线的入门 SO 并打开试读。从 RevCLI 运行同一流程时可指定路径及 ID。

```powershell
py -3 $cli --project $project --mode live --full gal story.quickstart --check-only
py -3 $cli --project $project --mode live --full gal story.quickstart --id chapter_intro --title "初次见面"
```

默认 ID 为 `first_story`。目标默认是 `<现有 RevAB 资源根>/Gal/Stories/<ID>.asset`，未设置资源根时在 `Assets/Gal/Stories` 存放编辑态草稿。可用 `--path` 指定位置。放在资源根时返回 `logicalPath` 并设置默认包名，`--bundle` 可以覆盖。已存在相同路径、同 ID 的剧情会返回 `reused=true`，保留作者编辑。`written` 指示本次是否新建。

按需要从工作台试读、`story.export`、`story.contract` 或接入自己的面板。用户选用默认界面时，可从工作台生成游戏面板或用 `gal ui.create`。正式 AB 产物按项目的资源链构建。

## 把常用分支保存成回归用例

对同一个 SO 运行 `py -3 $cli --project $project --mode live --full gal story.test --path <剧情 SO> --document <用例 JSON>`。项目相邻 RevCLI 的 `examples/gal-routes.json` 可对 `gal-story.json` 生成的资产直接跑。编辑器也可在工作台左侧选 **剧情设置 / 变量**，点 **运行路线用例…** 选择文件。两者共用真实 `RevGalSession`。

```json
{
  "cases": [
    {
      "name": "灯光结局",
      "operations": ["advance", "choose:lamp", "advance", "complete"],
      "expect": { "node": "lamp_end", "variables": { "trust": 1 } }
    },
    {
      "name": "信任不足",
      "operations": ["advance", "choose:locked"],
      "expectRejectedStep": "choose:locked",
      "expect": { "node": "route", "choiceEnabled": { "locked": false } }
    }
  ]
}
```

- 每条路线起一个独立的离屏会话，沿正式图推进。`entry` 可指定起点。支持 `capture/restore` 等现有试读步骤，`complete` 模拟宿主确认完成信号。
- `expect` 是对实际试读结果做结构比对。对象只需填写关注的字段，数组需匹配元素顺序和长度。常用字段为 `node/kind/variables/choiceEnabled/visited/stage/progress/history/trace`。
- `expectRejectedStep` 需对应最后一步。引擎异常、操作拼写错误和提前拒绝会失败。可用空 `operations` 验首帧，但每条用例需有预期。
- 读回 `data.passed/failed/cases`。某条失败时工具会返回失败状态及其他用例结果，`cases[].message` 定位首个不一致字段。保留典型路线、锁定条件、回退及存档恢复用例，将新功能补进现有用例集。

RevMCP 使用 `rev_call(target="editor", action="gal.story.test", arguments={path, document})`。`document` 为内联 JSON 字符串。从项目正常入口加载主题、AB、Sprite 和 Shader 的验证用 Editor Play，按[验收与排错](verification.md)执行。
