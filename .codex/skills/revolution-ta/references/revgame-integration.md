# RevGame 的 Shader、材质与资源交付

这份参考对应 [RevGame 基线 42f40924](https://github.com/Yokino337088/RevGame/tree/42f40924c07db3b9b2f09d727daf8e2bbdca821f)。Unity 工程在 `RevGame_Client/UnityProject/RevGame.Unity`，使用 Unity 2022.3.15f1c1、Linear 色彩空间和 Built-in 管线。Graphics Settings 与各 Quality 档的 Render Pipeline Asset 均为空。用当前工程和实际运行实例核对版本、质量档和图形 API。

## 资产位置与维护入口

- 手写 Shader 和相应 include 位于 `Assets/GameShader/<模块>`，正式材质位于 `Assets/GameRes/Material/<模块>`。场景、UI、配置和 Editor 制作配方按用途持有引用。移动或改名走 AssetDatabase，带上原 `.meta` 保住 GUID。项目规则检查 `.mat`、`.shader` 的目录，第三方包和框架快照沿各自维护边界。 [RevGame 资产规则](https://github.com/Yokino337088/RevGame/blob/42f40924c07db3b9b2f09d727daf8e2bbdca821f/.codex/rev-policy.json)
- 用 [Shader 全量索引](revgame-catalog.md) 定位 Shader、材质和代码。查 `Assets/Editor/RevGame` 中谁生成或覆盖该材质。属于生成器的参数、导入或引用从源配方修改，再保存并读回。场景内的序列化引用与动态逻辑路径都要查。
- 功能层通过 RevUI、RevPool、RevResManager、RevScope 和 RevTask 使用资源。正式资源走逻辑路径或预制体引用，首次真机加载用异步。框架快照中的问题在 Revolution 源仓库修复，再按框架流程同步。 [资源加载门面](../../revolution-framework/references/resource-and-ab.md)

| 入口或资源 | 按用途核对 |
|---|---|
| `GameResourceBuild.Configure` | 计算最终分包，调用导入配方、图集配置、资源可达性、Android Shader 和依赖门禁，再生成 `ResMap` 和路径常量 |
| `GameResourceUsage` | 从启用场景、正式资源和动态媒体引用建立可达性，校验目录与分包。Gal 的媒体是逻辑路径，参与扫描。备用美术仅在有使用者时进入包 |
| `GameSpriteAtlasBuild` 与 `GameSpriteAtlasService` | Sprite 在用时唯一归属一个 Sprite Atlas V2，源图与图集同包。启动早装配图集请求服务，进入场景借用所需图集，退出归还。保留矩形采样的图集关闭旋转与紧密打包 |
| `GameBackgroundFieldBuild`、`GameResourceCompressionBuild` 与 `GameLobbyArtBuild` | 冰胞与沼泽数据场用线性、无损的 Default 纹理，关闭 CPU Read/Write。章节背景颜色原画与数据场采用各自导入设置。大厅 RGB 遮罩由 `LobbyMagicCottage` 采样 |
| `GamePresentation.Chapters` 与 `ChapterBackgroundLease` | 第一章按 Sprite Atlas 获取背景。第二到六章用独立材质与 `_ChapterTex` 采样，读取 Shader 名、支持性和纹理，换章验证就绪后再交割 |
| `StoryBackdrop`、`GameGalResourceBuild` 与收藏书面板 | StoryBook 沿主线背景租约。Gal 舞台按节点选择 Sprite 或背景 Material。收藏书纸面和合成纹理按该面板持有，逐页加载章图，禁止让书封面硬引用六章大图 |

源码入口位于 [GameResourceBuild](https://github.com/Yokino337088/RevGame/tree/42f40924c07db3b9b2f09d727daf8e2bbdca821f/RevGame_Client/UnityProject/RevGame.Unity/Assets/Editor/RevGame/ResourceBuild) 和 [GameScripts](https://github.com/Yokino337088/RevGame/tree/42f40924c07db3b9b2f09d727daf8e2bbdca821f/RevGame_Client/UnityProject/RevGame.Unity/Assets/GameScripts)。

## 选采样和包的归属

1. 同一目标平台，相同生产资源由一个 AB 持有实体，消费者通过引用或逻辑路径复用。先核对 GUID、内容、导入类型和压缩参数，确认满足用途才复用。Shader 复用一份资产，材质参数不同可有多份预设。
2. Sprite 或序列帧归它的真实图集。法线、LUT、噪声场、无尽剑气刃贴图、完整 UV 的擂台及章节原画、书页合成输入及运行期 RenderTexture 按各自采样职责交付。数据纹理参与图集会改变 UV 或数值语义。需要完整 0～1 UV 的效果在创建前就要确认该纹理是否该设为 Sprite。
3. 通用 Shader 默认归 `shared_shaders`。章节背景、棋盘面和章主题帮手 Shader 跟对应 `background_chapter*`。各章 Boss 死亡 Shader 和身体或光晕材质跟本章 Boss 图集包。共享帮手短效和光束按 `battle_helper_fx` 等已有归属。构建配置可能按顺序覆盖默认规则，用实际 `.meta`、依赖和目标 Manifest 核对最后归属。
4. 神殿原画通过 `background_temple_shared` 被无尽和九路围棋复用。PvP 的 `PvpDuelScene` 内置序列帧、专用材质及粒子模板由场景直接引用，`GameResourceUsage` 检查它们没有进入 AB 或跨入口引用。它们使用的 Shader 若同时供应其它模块，需分别核对 Player 与 AB 的实际依赖。
5. 按目标检查压缩。现行鸦鸦差分源画幅保留，生成配方统一 1024 长边导入，最终 Atlas PC BC7、Android ASTC 6×6。常规图集和薄透明特效有自己的覆盖参数。帮手四帧与风暴页设 RGBA32 属于透明边缘优先的特例，背景数据场也明确保留无损。调整个别源图前先看该组的实际像素、图集页尺寸和 AB。

## 读回和交付

- RevGame 的编辑器工具与 Unity 工程相邻。先用 `rev_discover` 或 RevCLI 的 `commands --live` 查签名。基线在线实例可见 `material.inspect`、`material.create`、`material.clone` 和 `material.set`。先检查源路径和当前值，再执行需要的写入。背景可查 `game.background.audit`，局内检测先确认对应 Play 动作。
- 变更包归属或图集布局时运行项目的 `game.resources.configure`，内容改动核对 `game.resources.validate` 和实际依赖。检查有无漏标、重名、循环、隐式重复、图集重复覆盖以及 Shader 或材质未正确归属。记录实时回执，保持目标包与映射匹配。
- 小范围包更新按同一显式目标执行 `ab.build-incremental` → `ab.check-map` → `ab.publish-incremental`。需要同步多个目标时逐目标构建和检查。若 `rebuildReason` 非空，记录全量回退。比较真实包字节，源文件统计单独记录。 [增量构建](../../revolution-unity-editor/references/incremental-ab.md)
- Windows Editor 真 AB 使用项目的 PC 包，示例目标 `StandaloneWindows64`。Android Player 验收用 Android 包及实际设备 API。在当前编辑器完成导入，最后一次影响编译的写入后执行完整重编译并确认工单，再从正式入口冷启动。资源和编译门禁、Player 画面与设备表现分别记实。改代码或资产执行 RevGame 的 `python tools/RevPolicy/check.py`，提交前查暂存区。
