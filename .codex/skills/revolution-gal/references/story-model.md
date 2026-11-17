# 剧情数据与控制流

## 顶层合同

`RevGalStory` ScriptableObject 持有一份 `RevGalDefinition`。Unity 工作台、整篇 JSON、节点编辑和运行时使用同一模型。局部改动用 `node.find` 读取命中节点。编辑整个定义或同步剧情 JSON 时用 `story.export`。

| 字段 | 契约 |
| --- | --- |
| `Schema` | 当前为 `1`，整篇 JSON 导入需显式提供 |
| `Id` | 稳定剧情 ID，也是默认 PlayerPrefs 的档案命名空间 |
| `Title` | 展示名 |
| `Revision` | 正整数，默认 1，用于判断旧档是否兼容 |
| `Start` | 默认入口的节点 ID |
| `Variables` | `[{ "Id":"trust","Initial":0 }]`，Int32 数值，旗标可用 0/1 |
| `Nodes` | 节点数组，播放跳转以连线为准 |
| `StagePresets` | 章节和小节舞台方案，由节点的 `StagePreset` 引用，见[章节方案与面板设计](stages-and-layout.md) |

剧情、变量、节点、选项和信号 ID 使用 ASCII 字母、数字、点、下划线和短横线，最长 96 字符。引用按精确 ID 匹配。节点和变量分别在剧情内唯一，选项 ID 在各自节点内唯一。JSON 键区分大小写。枚举可用符号或已有 Unity 枚举数值，创作优先符号。

```json
{
  "Schema":1,
  "Id":"bridge",
  "Title":"过桥",
  "Revision":1,
  "Start":"intro",
  "Variables":[{"Id":"trust","Initial":0}],
  "Nodes":[
    {"Id":"intro","Kind":"Line","Text":"桥西亮起灯火。","Next":"end","Position":{"x":30,"y":60}},
    {"Id":"end","Kind":"End","Text":"走到桥西。","Position":{"x":330,"y":60}}
  ]
}
```

## 节点、出口、变量和舞台

- 节点包含稳定 `Id`、`Title`、`Kind`、工作台图坐标 `Position{x,y}`、`Speaker/SpeakerKey`、`Text/TextKey`、`Effects`、`Stage`、`Voice`、`Sound`。`Signal/Argument` 给信号节点，`GalleryId` 用于解锁插画。
- `Line` 是可推进的对白或旁白，需要 `Text` 或 `TextKey`，`Next` 指向后续。留空说话人即可作旁白。`End` 收口当前路线，`Next` 为空。
- `Choice` 有有序的 `Exits`，每项有 `Id`、`Label` 或 `TextKey`、`Target`、`Conditions`、`Effects`、`ShowWhenLocked`。条件不足时按 `ShowWhenLocked` 决定隐藏或锁定。所有选项都可能关闭时需要有效 `Next` 保底。若当前没有可选项，会直接走保底，即使有选项被设为可见但锁定。
- `Branch` 自动采用第一条条件满足的出口，再应用该出口的 Effects。无匹配走 `Next`。无条件出口会覆盖其后规则。
- `Signal` 用 `Signal` 和 `Argument` 把业务意图交给宿主，完成后走 `Next`。检查回调成功、失败和过期。信号等待不接受玩家的普通推进。
- `Conditions` 每项形如 `{"Variable":"trust","Comparison":"AtLeast","Value":1}`，同一出口的条件都需满足。比较名为 `Equal/NotEqual/Greater/AtLeast/Less/AtMost`。`Effects` 每项形如 `{"Variable":"trust","Operation":"Add","Value":1}`，支持 `Set/Add`。先应用出口 Effect，再应用进入节点的 Effect。变量须已在顶层声明。
- `Stage` 可填 `Background/BackgroundMaterial/Left/Center/Right/Music`。省略、`null` 或空串沿用上一幕，单个 `"-"` 清空槽位。清除节点的 `BackgroundMaterial` 后，默认界面恢复主题或 Prefab 材质。`Voice/Sound` 是当前节点音频，空表示无对应音频。图、材质和音频使用 RevRes 逻辑路径。解锁 `GalleryId` 时需显式填该节点的 `Stage.Background`。
- `SpeakerKey/TextKey` 以及出口 `TextKey` 交给 `RevGalOptions.ResolveText`。无本地化结果时用 SO 的字面文本。修改多语言可见文案时先找实际翻译来源。

整图检查引用、变量、条件、字段和可达性。不可达节点和没有可达结局会产生警告。引用合法的图也可能出现自动循环，运行时超过自动跳转上限会报错。改图后试读匹配、保底、锁定和结局路径。

## 存档兼容和工作台

`Revision` 变动会让已有槽和新会话不兼容。普通文案或音画替换可保持修订。删除发布节点、更换变量集合、改变存档语义时按宿主需求选择显式迁移或提升修订，并用具体旧档验证。已读、结局和图鉴按故事 ID 单独记录，修订号变更不会迁移历史 ID。已有槽继续位置由槽位里的节点决定。

在 **Revolution.Tools → Gal → 剧情工作台** 选择或新建 SO，左侧找节点与变量，中间拖连线与移动节点，右侧编辑文本、舞台与出口。问题列表能定位节点，可从起点或当前节点试读。图编辑允许草稿，发现 `dirty` 后先在工作台处理草稿。运行中的会话有独立快照，修改 SO 后重新打开才能验证新内容。
