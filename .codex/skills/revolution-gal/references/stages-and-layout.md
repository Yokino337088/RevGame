# 章节方案与面板设计

## 章节、小节和节点的背景

剧情定义中 `StagePresets` 保存章节与小节的舞台配置，节点通过 `StagePreset` 选用。方案包含 `Id/Title/Parent/Stage`。一个小节可继承章节，再覆盖自己的背景图、Material 或 BGM。Material 本身携带 Shader、参数及纹理。

```json
{
  "StagePresets": [
    {"Id":"chapter_1","Title":"第一章","Stage":{"Background":"Gal/Media/Lake","BackgroundMaterial":"Gal/Materials/Day"}},
    {"Id":"chapter_1_night","Title":"夜晚小节","Parent":"chapter_1","Stage":{"BackgroundMaterial":"Gal/Materials/Night"}}
  ]
}
```

工作台先选 **剧情设置 / 变量 → 章节 / 小节背景**，添加章节，再点击章节中的 **添加继承此方案的小节**。在每个章节或小节的入口节点选择方案。沿途空白节点继续使用当前舞台，需要单句覆盖时改该节点的 Stage。

进入有方案的节点时，按父方案 → 小节方案 → 节点 Stage 顺序执行。字段留空沿用，短横线清掉该槽。重新指定同一方案也会重新应用它的默认值。回退和存档持有已经合并的舞台，所以恢复无需重新猜测章节。旧剧情省略方案时照常播放。

CLI 先用 `story.contract` 读取全部 `stagePresets`，保留用户已有方案，然后用 `node.patch` 一次替换方案列表并修改需要的节点。批量 JSON 的顶层字段为小写 `stagePresets`，数组元素与整篇定义使用相同字段。只改方案时可以用空 `operations`。

```json
{
  "stagePresets": [
    {"Id":"chapter_1","Title":"第一章","Stage":{"Background":"Gal/Media/Lake","BackgroundMaterial":"Gal/Materials/Day"}},
    {"Id":"chapter_1_night","Title":"夜晚小节","Parent":"chapter_1","Stage":{"BackgroundMaterial":"Gal/Materials/Night"}}
  ],
  "operations": [
    {"op":"update","id":"night_intro","changes":{"StagePreset":"chapter_1_night"}}
  ]
}
```

预检会拒绝无效或重复 ID、缺失父方案、继承循环、未知节点方案和无效逻辑路径。`story.contract` 会包括方案引用的媒体。试读测试的 `stage` 给出实际合并结果，真实 AB 再确认对应 Shader 显示。

## 用自然语言改布局

用户说“把对白框加宽，继续按钮缩小一些”，先从主题 SO 查询当前值。

```powershell
py -3 $cli --project $project --mode live --full gal ui.layout --path $theme --query "对白框"
py -3 $cli --project $project --mode live --full gal ui.layout --path $theme --query Advance
```

回执返回 `items[].part/label/rect/layout/override/editable`，以及主题 `hash`。`rect` 的原点是父控件的左上角，单位为设计像素。默认参考画布为 1920×1080。控件可能属于 Header、DialogueBox 等不同父节点，修改前读各自当前位置，保持所需相对关系。

将目标尺寸写入 UTF-8 JSON，以稳定英文 part 定位，无需猜数组序号。

```json
{
  "parts": [
    {"part":"DialogueBox","rect":{"x":80,"y":760,"width":1760,"height":250}},
    {"part":"Advance","rect":{"x":1560,"y":170,"width":140,"height":50}}
  ]
}
```

```powershell
py -3 $cli --project $project --mode live --full gal ui.layout-set --path $theme --document $layout --expected-hash $hash --check-only
py -3 $cli --project $project --mode live --full gal ui.layout-set --path $theme --document $layout --expected-hash $hash
py -3 $cli --project $project --mode live gal ui.designer --path $theme
```

整批通过后保存原主题，保持 GUID。预检、内容相同和实际保存分别看 `changed/written`。过期 hash、未保存编辑、未知 part、重复控件、错误数据或非正宽高会拒绝写入。`{"part":"DialogueBox","override":false}` 恢复 Prefab 原布局。`ChoiceButton` 由列表管理尺寸，可用 `choiceHeight`。全屏遮罩固定铺满以防点击穿透。

## 可视面板设计器

从工作台 **界面与接入 → 面板设计器**，或主题 Inspector 的 **打开面板设计器** 进入。左侧搜索控件，中间显示当前主面板、设置或回看页的矩形布局，右侧编辑所选控件。可直接改左上位置和大小，也可展开锚点、轴心和边距。应用后保存到共享主题，下次打开剧情面板时生效。图片、图集切片和九宫格使用该部位的现有样式。

设计器中的图是布局示意，运行时仍用真实 Prefab、素材和 RevUI。修改后读回矩形，在 Play 里检查文字、按钮和射线。用户自制 Prefab 的层级与默认面板不同，按其自身 RectTransform 和主题映射验证。
