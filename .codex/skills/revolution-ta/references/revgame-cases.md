# RevGame 已验证的技术美术案例

案例是旧版本在给定环境下的证据。复用判断方法，当前参数和验收结果从 [接入流程](revgame-integration.md)、[Shader 索引](revgame-catalog.md) 和现有工程读取。

| 触发与观察 | 得到的做法 | 实测与边界 |
|---|---|---|
| Android Build Target 下桌面 Editor 剧情粉屏，材质仍能读出 Shader 名 | 同时查宿主图形 API 和实际主包。旧 `RevABLoader.MainName` 按编译符号走到 Android，正确选包后在 Windows Editor 读取 PC。 | 同一 Windows D3D11、Android 活动目标和真 AB 模式复测恢复。实际触发的本机开关变化未能从 Git 倒推。 [粉屏复盘](https://github.com/Yokino337088/RevGame/blob/42f40924c07db3b9b2f09d727daf8e2bbdca821f/RevGame_Client/文档/Bug踩坑记录/2026-11-09-EditorPlay误加载AndroidAB导致剧情粉屏.html) |
| 首页按钮有 Sprite，图集后来也加载成功，玩家仍看不到像素 | `GameSpriteAtlasService` 必须在 Unity 首次发出 `atlasRequested` 前实例化并订阅。对照回调的 `register` 和真实 Sprite 纹理，而不是只读资源句柄。 | 懒创建曾错过注册时机，现场刷新 Canvas 未解决。保留首帧就绪门禁和真实 AB 冷启。 [图集绑定复盘](https://github.com/Yokino337088/RevGame/blob/42f40924c07db3b9b2f09d727daf8e2bbdca821f/RevGame_Client/文档/Bug踩坑记录/2026-10-19-UI图集像素未绑定-懒创建错过图集请求.md) |
| 大厅、首章与其余章节共用依赖会提前带入大图 | 公共旋涡 Shader 和第一章 Shader 单独归属。第一章继续用原 Sprite GUID 和图集，第二到六章将各自的 Default 原画、材质和 Shader 按章交付。换图确认新画面后再交割旧租约。 | 当时六个章节包的 PC 与 Android Manifest 都做过独立依赖检查。后来新增的棋盘面、帮手和神殿共用包须按现行构建结果确认。 [六章背景分包验收](https://github.com/Yokino337088/RevGame/blob/42f40924c07db3b9b2f09d727daf8e2bbdca821f/RevGame_Client/文档/进度同步/章节背景独立Shader与六章分包-2026-10-23.html) |
| 全屏背景需要密集噪声或水流，手机像素负载过重 | 用离线数据场提供沼泽和冰胞的邻域信息，由 Shader 消费线性通道。原画与数据场各有导入契约。局部流动和几何遮罩可独立调节，大厅则把区域写入 RGB 两组遮罩。 | `GameBackgroundFieldBuild` 配置无损、关 Read/Write 和章包绑定，`LobbyMagicCottage` 使用独立色图。想改压缩或尺寸先比较数据误差、画质和 GPU 工作量。 [资源构建配方](https://github.com/Yokino337088/RevGame/tree/42f40924c07db3b9b2f09d727daf8e2bbdca821f/RevGame_Client/UnityProject/RevGame.Unity/Assets/Editor/RevGame/ResourceBuild) |
| 火、水、圣光需要更清楚的阶段，不想每次命中新增纹理和 Renderer | 用一个单 Pass、三份共享材质与 FullRect Sprite，MPB 传入元素、UV 和进度。视觉分段跟真实效果时间推进，低帧跳到对应段。 | 当次修复过 DX11 中 HLSL 参数名 `point` 的编译错误并补建包门禁。该报告未得到新版实拍及有效目标 GPU 耗时，不能拿旧图做收益对照。 [元素反馈验收](https://github.com/Yokino337088/RevGame/blob/42f40924c07db3b9b2f09d727daf8e2bbdca821f/RevGame_Client/文档/游戏玩法修改/火水圣光Shader分帧增强验收_2026-11-01.html) |
| 备用按钮图塞进常用图集，首屏反而携带更多页 | 以实际可达性和寿命分组。压缩前后都查看图集页尺寸、GPU 格式、实际包字节、采样和第一次落屏。 | 历史测试中混入备用图一版纹理量为 9,840,612 B，拆出后测得 7,219,172 B。数据限对应样本，实际帧率另测。 [战斗资源和渲染基线](https://github.com/Yokino337088/RevGame/blob/42f40924c07db3b9b2f09d727daf8e2bbdca821f/RevGame_Client/文档/战斗性能优化规则/战斗性能优化规则.html) |
| 鸦鸦差分绑在配置里，启动页面会加载其用不到的整组资源 | 配置只存资源标识和切片。各业务在显示前借用必要图集，序章按正文表情、战斗按本次互动、Gal 按当前及相邻已知差分。退出还租约，再由场景整理回收。 | 28 张差分保留源透明画幅，按 1024 长边导入。PC 六组从 26.61 MiB 降到 8.91 MiB，Android 构建 4.38 MiB，Android 未用同一设备测画质。实际测过大厅 0 组以及 Gal、无尽、PvP 退出。 [立绘分包与资源归还](https://github.com/Yokino337088/RevGame/commit/af7b4e8b6ba02034f7de7cadaa18cad34da9aa94) |
| 跨模式共享原画，但决斗场景按原始 UV 表现角色与擂台 | `GameResourceBuild` 为神殿原画设共享 AB。内置决斗场景引用自己的 Sprite 和全幅背景，制作器控制导入参数。普通 Sprite Atlas 与内置场景纹理分别验证。 | `GameResourceUsage.Validate` 会拒绝只归决斗场景的材质、粒子模板和图片带 AB 标记或被其它入口引用。实际玩家首开应按正式决斗场景确认。 [决斗制作配方](https://github.com/Yokino337088/RevGame/tree/42f40924c07db3b9b2f09d727daf8e2bbdca821f/RevGame_Client/UnityProject/RevGame.Unity/Assets/Editor/RevGame/Pvp) |

遇到类似缺陷，保留首次日志，查现行源码或资源归属，并在修复后走同一入口和目标模式。参考 [诊断和画面验收](diagnosis-and-verification.md)。
