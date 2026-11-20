# RevGame 实践 · 故事、Gal、UI 与资源生产

Unity 路径相对 RevGame Assets。先查当前正式消费者和实际打包目标，再决定生成、复用或导入。业务不能临时猜名字后把资源级错误拖到首屏。

## 剧情与教学

- 既有剧情从 `GameScripts/Flow/States/GameFlow.Story.cs` 读章节解锁、交互和关闭，使用 `GameScripts/Data/Story/StoryBook.cs` 与 `GameScripts/UI/Panels/Story/StoryView.cs` 观察文案、立绘与播放。 `GameScripts/Flow/Logic/TutorialLogic.cs` 连接真实规则与教程步骤，教学提示成立要看到真实玩家动作和业务结果。
- 内容制作可查 `Editor/RevGame/Story/GameStoryMarkdown.cs`、`Editor/RevGame/Story/GameStoryBuild.cs`。保留源文案、生成的 SO 数据、场景引用和已交付的 Prefab 身份。验证实际播放路径、缺图、暂停、关闭和复入。
- Gal 是独立扩展。看 `GameScripts/Flow/States/GameFlow.Gal.cs` 如何从游戏入口交付会话和退出权，再看 `GameScripts/UI/Panels/Story/GalStoryPlayback.cs` 的宿主接线。仓库 `RevGame_Client/剧情配置/` 提供项目稿件。追查节点、背景与立绘、选择或跳转时，还要核 Gal 自己的资产引用和预览布局。已有 StoryBook 和普通游戏过场沿各自入口。
- 鸦鸦说明、收藏进度与立绘载入分别读 `GameScripts/Data/Yaya/YayaMemoryBook.cs`、`GameScripts/Services/Progression/YayaMemoryProgress.cs`、`GameScripts/UI/Panels/YayaMemories/YayaMemoryBookPanel.cs`、`GameScripts/View/Resources/GamePortraitScope.cs`。验证单页、状态恢复、缩放与回退、新旧图集共用和关闭后租约归还。内容生成的页资产与图集引用保持现有业务键。

## 资源与包怎么交付

| 阶段 | 实际源入口 | 确认事项 |
| --- | --- | --- |
| 设计依赖 | `Editor/RevGame/ResourceBuild/GameResourceUsage.cs` | 从实际消费者逆向找依赖；图集收录自身不是使用证据。保留逻辑路径、原设备导入配置和 GUID。 |
| 导入与去重 | `asset.import-plan`、`game.resources.audit`、项目资产编排 | 结合内容、导入用途、Sprite 切片、格式和目标平台比较。资源共用一份按包依赖引用，先查明已存在的绑定。标签、AssetDatabase 与正式输出分开核对。 |
| 图集与资源索引 | `Editor/RevGame/ResourceBuild/GameSpriteAtlasBuild.cs` → `GameScripts/View/Resources/GameSpriteAtlasService.cs` | 生成和缓存使用实际 Atlas V2 与归属，需要同一在用 Sprite 唯一生效图集。Runtime 观察纹理真正绑定和引用续期，区分 Sprite、Shader 源纹理和字体。 |
| 运行时租约 | `GameScripts/View/Resources/GamePoolRequests.cs`、`GamePortraitScope.cs` | 视图、池、临时预热持有分开计数。切场、关闭、取消、同名域重入都校验实际持有者及晚到请求的归还。 |
| AB 构建 | `Editor/RevGame/ResourceBuild/GameResourceUsageAutomation.cs` 和现有 RevAB 门面 | 分平台校验唯一归属、隐含复制和清单依赖。增量失效以正式回执选择全量。显式 target 串行 build、check-map、publish，再查实际 manifest 与改动字节。 |
| 冷启动 | `Editor/RevGame/Scenes/GameSceneTransitionChecks.cs` | 真实发布 AB 与无预热首进，读活动场景、首屏像素、域、引用和退场快照。后台图集、采样材质、错误占位和配置接线分开检查。 |

## 画面、动效、声音与日常制作

- 编辑器生产脚本是可重复步骤的样本。查 `Editor/RevGame/GameAssetBuilder.cs` 和具体模块构建器，确定源资产、输出目录、Prefab、逻辑路径、GUID 与引用账本。变更分为复用、确定性生成、保留手写步骤；导入和写盘前看平台与目标文件。
- 表现从 `GameScripts/View/Combat/Effects/BattleEffectView.cs`、`BattleEffectView.Pattern.cs` 追条件、材质、局部 UV、当前图集帧与池寿命。使用共享材质与每实例数据的边界由当前 Shader 实现确定。诊断先在实际管线核对 Shader 编译、图集页、透明排序和 GameView，再考虑绘制优化。
- 声音素材、格式与平台规则查 `Editor/RevGame/ResourceBuild/GameAudioCompressionBuild.cs`，业务态音量和保存查 `GameScripts/Services/Progression/GameAudioSettings.cs`。BGM、短时音效、序列语音与重复触发预算分开观测。模式退出核对当前声源和引用归还。
- 预制体生成后用 `ui.binding-check`、实际组件、窗体显示和点击，再验证关闭和池复用。项目美术路径、UI 设计空间及平台纹理格式由目标项目决定。

按任务继续读 [TA 的 RevGame Shader、材质与资源实践](../../revolution-ta/references/revgame-integration.md)、[Editor 实践](revgame-editor-practice.md) 和 [交付与证据](revgame-delivery.md)。
