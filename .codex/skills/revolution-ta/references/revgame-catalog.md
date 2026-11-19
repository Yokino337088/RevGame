# RevGame Shader 全量索引

以 [RevGame 42f40924](https://github.com/Yokino337088/RevGame/tree/42f40924c07db3b9b2f09d727daf8e2bbdca821f) 静态盘点。项目有 **65 个 Shader、5 个共享 include**，共 90 份项目材质以 GUID 关联这些 Shader。索引记录源文件与制作入口，活动场景、实际引用和目标包以当前工程验证为准。

源文件相对于 [Assets/GameShader](https://github.com/Yokino337088/RevGame/tree/42f40924c07db3b9b2f09d727daf8e2bbdca821f/RevGame_Client/UnityProject/RevGame.Unity/Assets/GameShader)，材质样本相对于 [Assets/GameRes/Material](https://github.com/Yokino337088/RevGame/tree/42f40924c07db3b9b2f09d727daf8e2bbdca821f/RevGame_Client/UnityProject/RevGame.Unity/Assets/GameRes/Material)。同一 Shader 可能服务多份材质或场景。按精确 Shader 名、材质 GUID 和实际参数写入查消费者。

## 背景与大厅（8）

查 `GamePresentation.Chapters.cs`、`ChapterBackgroundLease.cs`、`GameBackgroundFieldBuild.cs` 与 `LobbySceneView.cs`。区分首章图集 UV、其余章节全幅 UV、大厅 RGB 遮罩以及关闭和换章时的租约。

| 源文件 | Shader 名 | 代表材质 | 用途与检查点 |
|---|---|---|---|
| `Background/ArcaneBackdrop.shader` | `RevGame/Background/ArcaneBackdrop` | `Background/ArcaneBackdrop.mat` | 公共旋涡背景。复用原画时核对 `_ArtUvRect`、扭曲与混合，观察半透明叠加。 |
| `Background/Chapter01_Tutorial_Shader.shader` | `RevGame/Background/Chapter01_Tutorial` | `Background/ArcaneScene.mat` | 首章星涡与原画交织。用第一章 Sprite 图集矩形，校验 `_EffectTime`、`_PreviewTime` 与首包独立性。 |
| `Background/Chapter02_Village_Shader.shader` | `RevGame/Background/Chapter02_Village` | `Background/Chapter02_Village_Material.mat` | 魔法村落日光、萤火与局部扰动。`_ChapterTex` 直接采样，`_Daylight` 调光照。 |
| `Background/Chapter03_Lake_Shader.shader` | `RevGame/Background/Chapter03_Lake` | `Background/Chapter03_Lake_Material.mat` | 湖泊波纹与水面位移。核对 `_WaveHeight`、`_FlowDistance`、`_ArtUVRect` 及湖岸的保护范围。 |
| `Background/Chapter04_Swamp_Shader.shader` | `RevGame/Background/Chapter04_Swamp` | `Background/Chapter04_Swamp_Material.mat` | 沼泽泥流、涡与喷涌。`_FlowNoise`、`_UseFlowNoise` 切换外置数据场，控制局部运动。 |
| `Background/Chapter05_Ice_Shader.shader` | `RevGame/Background/Chapter05_Ice` | `Background/Chapter05_Ice_Material.mat` | 冰胞、裂纹、雪点和流光。`_IceField`、`_UseIceField` 控制可选数据场，按线性数据读取。 |
| `Background/Chapter06_Temple_Shader.shader` | `RevGame/Background/Chapter06_Temple` | `Background/Chapter06_Temple_Material.mat` | 神殿的光束和几何符纹。测试 `_GodRays`、`_Strength` 及纹理对应的光照区域。 |
| `Background/LobbyMagicCottage.shader` | `RevGame/Background/LobbyMagicCottage` | `Lobby/MagicCottageMaterial.mat` | 小屋的天空、水面与发光分别读 `_RegionMask` 的 RGB，草木、小屋、烟囱读 `_NatureMask` 的 RGB。保住原画轮廓。 |

## 棋盘与落子提示（7）

查 `GameBoardMagicBuild.cs`、`GameChapterSurfacePreview.cs`、`GamePresentation.Chapters.cs` 与 `EndlessArena.Placement.cs`。章材质跟背景，落点材质从实际模式读取格宽和原点。核对 `_BossPulse`、`_BossTint` 与棋格。

| 源文件 | Shader 名 | 代表材质 | 用途与检查点 |
|---|---|---|---|
| `Board/Chapter01_Tutorial_SurfaceShader.shader` | `RevGame/Board/Chapter01_Tutorial` | `Board/Chapter01_Tutorial_Surface.mat` | 序章棋盘的星雾、星点与发光边缘。`_NebulaStrength`、`_StarDensity` 对照格子辨识。 |
| `Board/Chapter02_Village_SurfaceShader.shader` | `RevGame/Board/Chapter02_Village` | `Board/Chapter02_Village_Surface.mat` | 村落棋盘日照、斑驳光点和飞光。`_SunlightStrength`、`_DappleStrength` 对照棋子对比度。 |
| `Board/Chapter03_Lake_SurfaceShader.shader` | `RevGame/Board/Chapter03_Lake` | `Board/Chapter03_Lake_Surface.mat` | 湖泊棋盘水波、焦散、反射和涟漪。`_CausticStrength` 与 `_RippleStrength` 分开检查。 |
| `Board/Chapter04_Swamp_SurfaceShader.shader` | `RevGame/Board/Chapter04_Swamp` | `Board/Chapter04_Swamp_Surface.mat` | 沼泽棋盘泥流和扭曲。`_MudStrength`、`_FlowWarp` 关注棋格定位与局部强度。 |
| `Board/Chapter05_Ice_SurfaceShader.shader` | `RevGame/Board/Chapter05_Ice` | `Board/Chapter05_Ice_Surface.mat` | 冰面棋盘的低频光纹。保留棋格坐标和 Boss 冲击的覆盖，观察冷色底上的落点提示。 |
| `Board/Chapter06_Temple_SurfaceShader.shader` | `RevGame/Board/Chapter06_Temple` | `Board/Chapter06_Temple_Surface.mat` | 神殿棋盘的流光、呼吸和边缘亮度。重点核对 `_ShaftStrength` 与 `_BreathAmount` 下的棋子辨识。 |
| `Board/PlacementMagic.shader` | `RevGame/Board/PlacementMagic` | `Board/PlacementMagic.mat` | 落子格的程序魔纹。按所在模式写 `_CellSize`、`_Origin`，同一材质复用时棋格仍要对齐。 |

## Boss 死亡演出（6）

查 `GameBossVictoryBuild.cs`、`GameBossVictoryPreview.cs` 与 `BoardView.BossVictory.cs`。同一 Shader 有 Body 和 Aura 两份材质。核对真实 PNG 帧、`_FrameUV`、`_Progress`、`_Overlay`、胜利时序及提前退出。

| 源文件 | Shader 名 | 代表材质 | 用途与检查点 |
|---|---|---|---|
| `BossDeath/Chapter01_Stardust.shader` | `RevGame/BossDeath/Chapter01_Stardust` | `BossDeath/Chapter01_Stardust_Aura.mat` 等 2 份 | 星尘主题的身体溶散与覆盖光晕，使用 `_Progress` 与 `_Overlay` 分体。 |
| `BossDeath/Chapter02_Sunlight.shader` | `RevGame/BossDeath/Chapter02_Sunlight` | `BossDeath/Chapter02_Sunlight_Aura.mat` 等 2 份 | 日光主题的消散与外缘亮光，单独比较身体与叠加层轮廓。 |
| `BossDeath/Chapter03_Water.shader` | `RevGame/BossDeath/Chapter03_Water` | `BossDeath/Chapter03_Water_Aura.mat` 等 2 份 | 水波主题的溶解与外扩效果，检查边缘透明和采样帧矩形。 |
| `BossDeath/Chapter04_Swamp.shader` | `RevGame/BossDeath/Chapter04_Swamp` | `BossDeath/Chapter04_Swamp_Aura.mat` 等 2 份 | 沼泽主题的泥流式消散，检查深色区域、透边与图集切片边缘。 |
| `BossDeath/Chapter05_Ice.shader` | `RevGame/BossDeath/Chapter05_Ice` | `BossDeath/Chapter05_Ice_Aura.mat` 等 2 份 | 退场正向冻结，入场逆向融冰，保留原图颜色和细雪叠加。检查冰纹阶段及高光可读性。 |
| `BossDeath/Chapter06_Cross.shader` | `RevGame/BossDeath/Chapter06_Cross` | `BossDeath/Chapter06_Cross_Aura.mat` 等 2 份 | 末章十字圣光和身体切换，细调 `_BeamWidth` 与 `_BeamGlowWidth`，保住原有的阶段和排序。 |

## 卡牌（1）

核对卡牌 Prefab 中的实际材质、九宫格和遮罩。使用 `ArcaneUI.cginc` 的其它 UI 一起复查。

| 源文件 | Shader 名 | 代表材质 | 用途与检查点 |
|---|---|---|---|
| `Cards/CardGleam.shader` | `RevGame/UI/CardGleam` | `Cards/CardGleam.mat` | 卡牌外框高光与原画混合。保留 Sprite、Stencil、裁切和 Canvas 色值输入。 |

## 战斗反馈（6）

查 `BattleEffectView.cs`、`BattleEffectView.Pattern.cs`、`BossSkillFxView.cs`、`GameElementalEffectBuild.cs` 与 `GameFeelBuilder.cs`。现有 `_SpriteUV` 路径以最小和最大 UV 表示矩形。测试共享材质与 MPB 清理，保留棋种范围、预警和命中关系。

| 源文件 | Shader 名 | 代表材质 | 用途与检查点 |
|---|---|---|---|
| `Combat/BossSkill.shader` | `RevGame/Combat/BossSkill` | `Combat/BossSkill.mat` | 多种 Boss 预警、冲击波、激光、爆炸与法球效果由 `_Mode`、`_Shape`、`_Progress` 驱动，图集输入走 `_FxUV`。 |
| `Combat/ElementalImpact.shader` | `RevGame/Combat/ElementalImpact` | `Combat/ElementalFireImpact.mat` 等 3 份 | 火、水、圣光三个材质共用单 Pass 和 `_Element`、`_Progress`。核对美术关键帧、各自特征及局部作用面积。 |
| `Combat/MeleeImpact.shader` | `RevGame/Combat/MeleeImpact` | `Combat/AssassinSlash.mat` 等 3 份 | 斩弧、盾纹和局部格域都从 `_SpriteUV` 还原图案坐标。`_PatternCells` 以格数为单位，范围与业务命中分别验。 |
| `Combat/RangedProjectile.shader` | `RevGame/Combat/RangedProjectile` | `Combat/ArcherArrowBody.mat` 等 13 份 | 箭矢、弩弹等主体与尾迹共用 Shader。`_Style`、`_Trail` 和 `_SpriteUV` 由实际 Sprite 与表现写入。 |
| `Combat/RegionRift.shader` | `RevGame/Combat/RegionRift` | `Combat/RegionRift.mat` | 逐格预警、命中亮起、符文和外边界。`_Timing`、`_Age`、`_Edges` 由表现接收，保持真实伤害时序。 |
| `Combat/ScoreSpark.shader` | `RevGame/Combat/ScoreSpark` | `Combat/ScoreSpark.mat` | 积分飞星贴图和顶点颜色的轻量绘制。检查池归还及短效果结束，避免把每次反馈变成新材质实例。 |

## 无尽表现（10）

查 `GameEndlessBuild.Theme.cs`、`EndlessArena.cs`、`EndlessArena.Guard.cs` 与 `EndlessArena.Upgrade.cs`。敌潮与血条按可见批次绘制，背景和棋盘独立，盾卫和剑气保持自己的空间及生命周期。

| 源文件 | Shader 名 | 代表材质 | 用途与检查点 |
|---|---|---|---|
| `Endless/EndlessBatch.shader` | `RevGame/Endless/Horde` | `Endless/EndlessHorde.mat` | 敌潮的顶点色与 Sprite Atlas 纹理随合并 Mesh 提交。检查图集晚绑定、同纹理批次和透明顺序。 |
| `Endless/EndlessCardUpgrade.shader` | `RevGame/Endless/CardUpgrade` | `Endless/EndlessCardUpgrade.mat` | 升星与合成的程序环形光效。视觉进度交给表现，完成后收回物件及效果状态。 |
| `Endless/EndlessField.shader` | `RevGame/Endless/StarField` | `Endless/EndlessBackground.mat` | 神殿原画上的动态场。复用章节背景工具函数，用独立全幅纹理，神殿输入与九路围棋的共享包分开核验。 |
| `Endless/EndlessGrid.shader` | `RevGame/Endless/StarGrid` | `Endless/EndlessGrid.mat` | 象牙金不透明棋盘。`_Clock`、`_WarningLine`、`_Heart` 等叠加境界提示，检查深度与格线对齐。 |
| `Endless/EndlessHpBar.shader` | `RevGame/Endless/HpBar` | `Endless/EndlessHpBar.mat` | 敌潮血条 Mesh 的轻量顶点色显示。用实际可见数量、准确位置与填充验收。 |
| `Endless/EndlessShieldBubble.shader` | `RevGame/Endless/ShieldBubble` | `Endless/EndlessShieldBubble.mat` | 盾罩的透明包边。`_Base` 保持内部可辨识，验敌潮、棋子和血条的遮挡。 |
| `Endless/EndlessShieldOrbit.shader` | `RevGame/Endless/ShieldOrbit` | `Endless/EndlessShieldOrbit.mat` | 盾卫旋转盾牌。`_ShieldTex`、`_Orbit` 与 `_Scale` 控制采样形状及环绕空间。 |
| `Endless/EndlessShieldZone.shader` | `RevGame/Endless/ShieldZone` | `Endless/EndlessShieldZone.mat` | 盾卫覆盖区填充与边界。`_Fill`、`_Edge` 兼顾读格，核验重叠区域和深度次序。 |
| `Endless/EndlessSwordAura.shader` | `RevGame/Endless/SwordAura` | `Endless/EndlessSwordAura.mat` | 围绕单位的剑气刃与尾迹。`_BladeTex` 使用完整 UV，继承正式的 `_RingWidth` 和位移表现。 |
| `Endless/EndlessThrust.shader` | `RevGame/Endless/Thrust` | `Endless/EndlessThrust.mat` | 刺客突刺中的刃和轨迹合成。核对 `_BladeTex`、`_TrailTex` 与原有横向目标选择。 |

## 帮手与短效（14）

查 `GameHelperBuild.cs`、`GameHelperVfxChecks.cs`、`GameHelperSupportPlayChecks.cs`。六章晶体用独立主题 Shader 与材质。共享光束和短效分包，旋转图集与全 UV 的线纹理分开检查。

| 源文件 | Shader 名 | 代表材质 | 用途与检查点 |
|---|---|---|---|
| `Helper/HelperBeamImpact_Shader.shader` | `RevGame/Helper/BeamImpact` | `Helper/HelperBeamImpact.mat` | 光束落点的亮芯与外晕，用 `_Tint`、`_Intensity`、`_Speed` 控制短效。 |
| `Helper/HelperBlueBeam_Shader.shader` | `RevGame/Helper/HelperBlueBeam` | `Helper/HelperBlueBeam.mat` | 蓝色激光核心与脉冲。LineRenderer 的原画以完整 UV 采样，`_BeamWidth` 与 `_CoreWidth` 控线宽。 |
| `Helper/HelperChapter01_Shader.shader` | `RevGame/Helper/HelperChapter01` | `Helper/HelperChapter01.mat` | 第 1 章蓝色主题晶体的切面、符文和光圈，与本章独立包交付。 |
| `Helper/HelperChapter02_Shader.shader` | `RevGame/Helper/HelperChapter02` | `Helper/HelperChapter02.mat` | 第 2 章主题晶体。与其它章节沿用同类几何和流动结构，确认章专属 `_Tint`。 |
| `Helper/HelperChapter03_Shader.shader` | `RevGame/Helper/HelperChapter03` | `Helper/HelperChapter03.mat` | 第 3 章主题晶体。测试换章后新材质、轮廓和粒子时间。 |
| `Helper/HelperChapter04_Shader.shader` | `RevGame/Helper/HelperChapter04` | `Helper/HelperChapter04.mat` | 第 4 章主题晶体。验深色沼泽背景上的内核与外圈对比度。 |
| `Helper/HelperChapter05_Shader.shader` | `RevGame/Helper/HelperChapter05` | `Helper/HelperChapter05.mat` | 第 5 章主题晶体。验偏亮的冰雪背景以及章包切换后的表现。 |
| `Helper/HelperChapter06_Shader.shader` | `RevGame/Helper/HelperChapter06` | `Helper/HelperChapter06.mat` | 第 6 章浅金主题晶体，检验加色光晕与神殿的亮部重叠。 |
| `Helper/HelperGoldReflect_Shader.shader` | `RevGame/Helper/GoldReflect` | `Helper/HelperGoldReflect.mat` | 帮手金色扫光与反射。采样原轮廓，`_SweepSpeed` 和 `_BandWidth` 控制扫过的节奏。 |
| `Helper/HelperHealingBeam_Shader.shader` | `RevGame/Helper/HealingBeam` | `Helper/HelperHealingBeam.mat` | 治疗连接线的芯与边。`_BeamLength` 参与沿线表现，检查颜色与线宽。 |
| `Helper/HelperHealingImpact_Shader.shader` | `RevGame/Helper/HealingImpact` | `Helper/HelperHealingImpact.mat` | 治疗落点十字及光效，按 `_Tint`、`_Intensity` 调节清晰度。 |
| `Helper/HelperMushroomImpact_Shader.shader` | `RevGame/Helper/MushroomImpact` | `Helper/HelperMushroomImpact.mat` | 火烟蘑菇云程序形状。`_Fire`、`_Smoke`、`_Progress` 控制短期演出，校验遮挡。 |
| `Helper/HelperRune_Shader.shader` | `RevGame/Helper/Rune` | `Helper/HelperRune.mat` | 符文采样和染色，沿 `_MainTex` 与正式帮手特效包绑定。 |
| `Helper/HelperStorm_Shader.shader` | `RevGame/Helper/Storm` | `Helper/HelperStorm.mat` | 风暴不同阶段叠加图集中帧，`_FrameUV` 和 `_EffectKind` 适配图集实际切片。 |

## 棋子本体（1）

查 `GamePieceShaderBuilder.cs`、棋子 Prefab 与棋盘实际装配。重点查属性块与池复用，让同名材质的其它棋子不受某个出生或溶解状态干扰。

| 源文件 | Shader 名 | 代表材质 | 用途与检查点 |
|---|---|---|---|
| `Pieces/ArcanePiece.shader` | `RevGame/Pieces/ArcanePiece` | `Pieces/ArcanePiece.mat` | 棋子本体的颜色、出生、溶解与发光。共享材质与 Renderer 局部参数一起核对，注重安全区及棋格轮廓。 |

## 九路围棋与决斗（4）

查 `GamePvpBuild.ArenaBackgrounds.cs`、`GamePvpBuild.DuelArena.cs`、`PvpBoardView.cs` 与 `DuelStrikeEffects.cs`。区分九路围棋的正式 AB 与决斗 Build Settings 内置场景。

| 源文件 | Shader 名 | 代表材质 | 用途与检查点 |
|---|---|---|---|
| `Pvp/PvpArenaBackdrop.shader` | `RevGame/Pvp/ArenaBackdrop` | `Pvp/Board/PvpTemple.mat` 等 2 份 | 九路围棋和拳击擂台背景共用入口，以 `_Style`、`_Strength` 形成不同风格，场景检查完整 UV。 |
| `Pvp/PvpDuelArenaEnergy.shader` | `RevGame/Pvp/Duel Arena Energy` | `Pvp/DuelScene/DuelArenaEnergy.mat` | 内置决斗擂台的能量亮纹。核对场景对象引用、暗底边界与粒子排序。 |
| `Pvp/PvpDuelStrike.shader` | `RevGame/Pvp/DuelStrike` | 未找到引用 | 仓库存留的加色命中 Shader，提供 `_Phase`、`_Power`。基线没有找到指向其 GUID 的材质、Prefab 或场景，也未找到按名调用。当前仍有分包标记，接线前确认实际用途。 |
| `Pvp/PvpGoBoard.shader` | `RevGame/Pvp/IvoryGoBoard` | `Pvp/Board/PvpIvoryBoard.mat` | 象牙白九路线、眼位与分析点。检查 `_EyeTex`、`_AnalysisPoint` 和 Grid 的坐标映射。 |

## UI 与教程（6）

查 `GamePvpBuild.DuelPreparation.cs`、相关面板和教程视图。粒子或武器材质可能由内置场景使用，目录名表示维护归属，依赖以消费者为准。

| 源文件 | Shader 名 | 代表材质 | 用途与检查点 |
|---|---|---|---|
| `UI/ArcanePanel.shader` | `RevGame/UI/ArcanePanel` | `UI/ArcanePanel.mat` | 共享界面的奥术边框、发光及贴图混合。切换主题后读回 Stencil、Mask、裁切和现有画层。 |
| `UI/DuelPreparationRift.shader` | `RevGame/UI/Duel Preparation Rift` | `UI/PvpDuelPrep/DuelRift.mat` | 决斗选角裂隙。程序噪声在现有 UI 深度、遮罩和混合约定下展示。 |
| `UI/DuelPreparationSigil.shader` | `RevGame/UI/Duel Preparation Sigil` | `UI/PvpDuelPrep/DuelSigil.mat` | 决斗准备符印。核对 UI Stencil 参数、照明形状、Canvas 顺序和移动端安全区。 |
| `UI/EmberDuelSpear.shader` | `RevGame/UI/Ember Duel Spear` | `Pvp/DuelScene/EmberSpearGlow.mat` | 决斗长枪发光和招式显现，复用 `DuelStrikeReveal.cginc`，跟内置决斗场景交付材质。 |
| `UI/SlimeTutorialHighlight.shader` | `RevGame/UI/SlimeTutorialHighlight` | `UI/SlimeTutorialCell.mat` 等 3 份 | 教学格、提示框和高亮的填充与脉冲。检查 `_Width`、`_Fill`、`_PulseRate` 以及点击区域。 |
| `UI/YayaDuelFeather.shader` | `RevGame/UI/Yaya Duel Feather` | `Pvp/DuelScene/YayaFeatherGlow.mat` | 鸦鸦羽刃发光和招式显现，复用 `DuelStrikeReveal.cginc`，验角色贴图透明边缘。 |

## 鸦鸦收藏书（2）

查 `GameYayaMemoryBookBuild.cs`、`YayaMemoryBookPanel.cs` 与 `YayaMemoryIllustration.cs`。合成器用受面板持有的 RenderTexture，书页交割后归还临时图像和资源租约。

| 源文件 | Shader 名 | 代表材质 | 用途与检查点 |
|---|---|---|---|
| `YayaMemories/YayaBookPaper.shader` | `RevGame/YayaMemories/PaperCurl` | `UI/YayaMemories/BookCoverFace.mat` 等 2 份 | 书页翻卷与正反合成。`_Current`、`_Next` 及 `_HasCurrent`、`_HasNext` 定义页图边界，核对进度、方向和关闭断引用。 |
| `YayaMemories/YayaMemoryScene.shader` | `RevGame/YayaMemories/Illustration` | `UI/YayaMemories/MemorySceneComposite.mat` | 章图的气氛、接触影、投影和前景按 `_Layers` 合成。`_EffectClock` 支持可对照的动态画面。 |

## 共享 include

| 源文件 | 契约和影响范围 |
|---|---|
| `Background/ChapterBackgroundCommon.cginc` | 章节及无尽背景共用噪声、时钟、完整原画与矩形采样。修改后复验其所有使用者。 |
| `Board/BoardMagicCommon.cginc` | 六章棋盘和 PlacementMagic 共用格内坐标、边缘与 `_BossPulse` 混合逻辑。 |
| `BossDeath/BossDeathCommon.cginc` | 六章死亡 Shader 共用帧 UV、源采样、位移、身体衰减与叠加输出。 |
| `Common/ArcaneUI.cginc` | ArcanePanel 与 CardGleam 共用 UI 图像、颜色、遮罩和混合环节。 |
| `UI/DuelStrikeReveal.cginc` | EmberDuelSpear 与 YayaDuelFeather 共用招式显现和电流辅助函数。 |

更新时先枚举项目自有的 `.shader`、`.cginc`、`.hlsl`、`.compute`、`.shadergraph` 与变体集合。核对 Shader 声明、序列化和动态引用、实际材质、调用入口以及分包。历史或备用资源标出证据范围。
