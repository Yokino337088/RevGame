# UI 与资源

## 生成和重用 UI

从工作台点 **生成游戏面板**，或在实际工程里运行 `gal ui.create --bundle gal_ui`。检查回传 `path/themePath/logicalPath`。默认 Prefab 位于 `<AB资源根>/UI/Gal/RevGalPanel.prefab`，运行时逻辑键是 `UI/Gal/RevGalPanel`。默认主题位于同目录。`--font <Assets/...>` 指定能渲染目标语言的 Font，`--theme <Assets/.../Theme.asset>` 引用已有主题。

点工作台的 **编辑面板主题** 找到实际绑定的主题。新版生成 Prefab 有 `View/Story`、`SettingsOverlay` 和 `Backlog`，公共字段由生成器绑定。旧生成 Prefab 的运行时仍用其原按钮，确实要升级布局时使用 `--overwrite`。重建会更新生成层级并沿用已绑定主题。若项目手调过旧 Prefab，先读现有控件、布局和引用，按其需求移入主题或在自有 Prefab 上适配。

### 在 Inspector 设置共享主题

1. 绑定共用 Sprite Atlas 与可选 Font。共用主题随 Prefab 引用加载。一个主题可以供多篇剧情共用。
2. 在部位中选择图集切片名或拖入独立 Sprite。独立 Sprite 优先。选择图片后可用覆盖颜色与九宫格。九宫格使用图片自身的 Border。Unity 的图集切片命名应能唯一定位所需图片。
3. 布局的锚点、轴心和 Offset 相对部位父 RectTransform。生成的主题带默认位置初值。勾选覆盖后应用主题值，关闭覆盖时使用 Prefab 的布局。主题在下次打开面板时生效。
4. 选项共用一个按钮模板，行高由 `ChoiceHeight` 调整，选择区域可更改布局，列表由滚动布局组拉伸。设置、回看和全屏底板的遮罩几何固定铺满，保护输入遮挡。调设置内容用 `SettingsPanel` 与内部按钮的布局。

### 背景 Material 和 Shader

所有剧情共用一种效果时，在 **编辑面板主题** 绑定 **共用背景材质**。需要程序化材质在没有背景图时独立绘制，可勾选 **没有图片时绘制共用材质**。Prefab 的 Background Image 上手工挂的材质也可作为默认值。

需要某幕切换效果时，在工作台节点的 **舞台、音频与收藏 → 背景材质** 选择 `Material`。通过 `gal node.update` 也可提交以下局部文档。

```json
{"Stage":{"Background":"Gal/Media/Lake","BackgroundMaterial":"Gal/Materials/Ripple"}}
```

材质携带 Shader、关键字、纹理及参数。节点材质优先，短横线清掉节点的覆盖并恢复主题或 Prefab。留空沿用上一节点覆盖。图片和材质独立配路径，清掉图片后，明确设置的节点材质仍可绘制一张全屏四边形。

选用与项目渲染管线和 UGUI Canvas 相容的 Shader。与 Sprite 合成时检查主纹理、Tint 和 Alpha 的处理。有 UI Mask 时检查 Stencil。材质与 Shader 依赖放入项目的正式资源体系，跟剧情 SO、面板和主题一起验真实 AB。`gal story.contract` 的媒体项会报告该 Material 的本地类型与包名。Play 中可用 `gal.state` 读取 `backgroundHasSprite/backgroundMaterial/backgroundShader`，再以实际画面为准。自有面板可从 `Frame.Stage.BackgroundMaterial` 取逻辑路径并管理自己的 Media 租约。

| 部位组 | 可配置内容 |
| --- | --- |
| 舞台 | 底板、背景区域、左/中/右立绘；剧情帧决定背景和立绘 Sprite，主题调整位置和颜色 |
| 主界面 | Header、标题、播放状态、关闭、打开设置、对白框、说话人、正文区域、继续、提示 |
| 选项 | 选择区域、按钮图片、颜色和行高 |
| 设置 | 遮罩、面板、标题、状态、提示、返回、自动、已读跳过、回退、回看、快存、快读 |
| 回看 | 遮罩、标题、正文滚动区域、页码、更早、更新、返回 |

通用的 RevCLI `asset.inspect` 可读取主题的实际序列化属性。写入使用 `asset.set` 的 `changes:[{property,value}]`，`value` 是字符串，Vector2/Color 值用内联 JSON 字符串。编辑数组项前先读该项 `Part`，取得实际索引和字段路径，例如 `parts.Array.data[9].Layout.AnchorMin`。图集和 Sprite 对象引用用目标工程可解析的资产路径或已发现对象 ID。修改前核对类型，保存后重新读取，布局最终以真机或 Editor Play 画面为准。

按控件名称修改尺寸和位置时，使用 `gal ui.layout/layout-set` 与可视面板设计器。章节和小节的 Shader 继承也可在工作台直接配置。具体字段与 AI 编辑步骤见[章节方案与面板设计](stages-and-layout.md)。

## 媒体、图集与 AB

- 用 `ab.config` 或打包窗口确定资源根。SO 写入路径为 `Assets/.../*.asset`，节点及游戏打开用去掉资源根和扩展名的逻辑路径，如 `Gal/Media/ForestLake`。检查路径大小写、单个文件名冲突及类型。舞台图片用 `Sprite`，背景效果用 `Material`，音频用 `AudioClip`。旧 StoryBook 数据遵循其自己的资源链。
- 图片先按项目习惯导入为 Sprite，原图、类型和图集成员用 `asset.importer`、`atlas.inspect` 等实际动作读回。项目采用 Sprite Atlas V2 时可使用 `atlas.create`。遵循宿主已有图集和 AB 分组，避免把同一张发布 Sprite 再加进另一个同用途图集。可直接指定已在项目图集中受管的 Sprite。主题 Prefab、主题 SO、显式图集、字体、舞台和音频的依赖都要随对应包交付。
- `story.generate` 会为剧情设置 AB 名。手工 Story 或其它资源按宿主分包规则标记。使用扫描手工标记模式且要求全标记时，核对新增 SO、Prefab、图集、纹理和字体。使用自动目录分组时遵循实际配置。`--bundle` 设置资源标签，实际 PC/Android 产物由构建链产生。
- 对目标平台用同一个显式 `--target` 顺序运行 `ab build-incremental`、`ab check-map`、`ab publish-incremental`。构建回执标明有无全量回退，确认映射和发布的更新计数。Windows Editor 的真实 AB 用 `StandaloneWindows64` 产物，Player 使用自己的目标平台。RevGame 资源改动另走项目的资源与图集检查。
- 项目直读适合创作试读。AB 冷启动再确认首次打开时文字、SpriteAtlas 像素、背景、角色与音频实际出现。重开后再次验证选择区域、按钮射线与清理。字体、翻译和控制布局要用实际语言及目标分辨率检查。
