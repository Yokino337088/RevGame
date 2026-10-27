# 六章BOSS关键帧接入 · 2026-10-27

用户指定六章美术角色：剑士、弓手、水系法师、刺客、盾卫、牧师。每组原始97张640×640帧，运行时保留8张关键帧、384×384透明副本，共48帧。

|章节|BOSS美术|帧数|原帧索引|Android AB|
|---|---|---|---|---:|
|1|剑士|97 → 8|0, 12, 14, 69, 72, 81, 85, 96|0.432 MiB|
|2|弓手|97 → 8|0, 15, 22, 54, 62, 74, 88, 96|0.466 MiB|
|3|水系法师|97 → 8|0, 7, 22, 32, 50, 59, 75, 96|0.454 MiB|
|4|刺客|97 → 8|0, 8, 23, 32, 71, 78, 89, 96|0.435 MiB|
|5|盾卫|97 → 8|0, 5, 17, 34, 36, 71, 76, 96|0.433 MiB|
|6|牧师|97 → 8|0, 7, 20, 37, 62, 75, 87, 96|0.464 MiB|

六章BOSS AB合计Android 2.68 MiB、PC 2.43 MiB。全部AB资源合计Android 26.80 MiB、PC 26.76 MiB；不代表最终APK体积。

## 关键帧与制作

原件位于美术/AI生成/新版/BOSS序列帧_透明，未修改或删除。选帧对预乘Alpha后的RGB+透明像素变化做分析，以8个时间节点最小化原动画的线性重建误差，再检查选帧预览。源帧号/源与制作副本SHA256保存在同级BOSS关键帧选取清单.json。

所有帧保持相同画幅和中心锚点，不逐帧裁图，不改变源动画的相对位置；缩到384px用于当前2D棋盘。部分武器在原图边缘已有裁切，本轮不补画。各组以0.18秒/帧往返播放，避免原末帧与首帧不一致的跳变。往返序列0～7～1是14次显示，保存的Sprite只有8张。

重复制作用例：python tools/boss-keyframes.py；只核对执行python tools/boss-keyframes.py --check。不会重算清单或随机更换帧号；清单与原件哈希不一致则明确失败。

## 游戏接入与资源

- 每章BossChapter01～06.asset位于Assets/GameRes/Config/Boss，配置8帧、外观职业、帧间隔、往返、身体子节点缩放和自带装饰开关。
- 每章独立SpriteAtlas V2：BossChapter01Frames～06Frames；配置、帧、图集同包atlas_bosschapter01frames～06frames。PC DXT5、Android ETC2_RGBA8保留透明，每个图集一张2048×1024或1024×2048纹理页。
- 普通棋子配置/预制体不引用六章Boss配置；其它业务包不依赖Boss图集，普通回合不预热它们。
- BoardView只在Boss回合租当前章配置和图集，使用RevResManager和GameSpriteAtlasService；多颗Boss共用帧数组。
- PieceView沿用现有20Hz动画检查，按配置间隔换Sprite，不引Animator，不逐帧GetSprite/克隆材质。重复血量、HUD和规则刷新不会重置关键帧。
- 新美术自带王冠/底座时不重复叠饰件，根节点体型、血条与占格保持原逻辑。回池清帧和身体缩放，普通棋子复用不串入Boss动画。
- 首次Boss美术未就绪时通过现有画面门禁暂停业务推进；换章/离场版本检查阻止晚到结果。消融中的旧Boss回池后再释放旧帧图集。
- 本轮只换Boss外观。现有ChapterConfig中的BossKind及数值、技能、攻击规则未改变，外观角色不是新增一套战斗职业配置。

## 验收

- Unity编译无错误；48帧、章节外观映射、配置/图集同包检查通过。最后一次Console检查有7条第三方MCP-FOR-UNITY WebSocket连接失败日志，未出现本次Boss代码/资源错误；RevMCP调用仍可用。未清空这些错误记录。
- PC/Android各46业务包构建并发布StreamingAssets，背景原有独立包约束继续通过。
- 直接解析实际AB：每组8个Sprite、1个SpriteAtlas、1个透明压缩纹理页；新包无依赖，其它业务包不依赖它们。
- 隔离冷加载真实PC AB，在真实PieceView上推进OnTick：六章8帧均改变GPU输出，往返端点正确；重复Bind/BindBossArt/尺寸刷新不重置动画；OnPoolReturn清帧，再绑定普通棋子没有Boss残留。
- 离屏图集测试使用有限GetSprite克隆作画面证据，正式代码仍使用配置数组。GIF来自实际PC AB的固定输入预览，不是截取原视频。
- 当前StartScene未切换、未启动Play；完整游戏内Boss出生/动画/死亡/跨章/取消加载、真实AB首次自动图集绑定及Android真机尚未端到端验收。现有异步/消融清理已实现并审查，不能用离屏检查冒充这些通过。

复验入口：game.boss-keyframes.check、game.boss-keyframes.preview、game.boss-keyframes.configure。帧间隔/身体缩放等纯美术参数在BossChapter资产Inspector调节。配置工具重跑保留已有帧速度等参数。

详细证据位于本机.codex/work/boss-keyframes-2026-10-27，报告只保留HTML/Markdown与必要截图/GIF，不生成ZIP。
