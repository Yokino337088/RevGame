# 六章BGM与玩家声音设置 · 2026-10-27

第五、六章使用用户指定视频音频；首页、选关、战斗均提供设置按钮。

|章节|音乐|视频|MP3|Android/PC AB|
|---|---|---|---:|---:|
|5|L3V3LS - Ice Walls|https://www.bilibili.com/video/BV1b34y1F7bY|6.67 MiB|2.93 MiB|
|6|L3V3LS - Swing|https://www.bilibili.com/video/BV1kSXfBBE9i|3.85 MiB|1.78 MiB|

第五章时长约291.29秒，第六章约168.28秒。提取视频音频流后转192kbps、44100Hz双声道MP3，原视频流仅在本机忽略目录保留。用户可试听MP3存放在RevGame_Client/音乐；运行时副本位于Assets/GameRes/Audio/Bgm，分别进入audio_bgm_chapter05/06。Unity Streaming/Vorbis质量0.5、PC/Android一致，新增曲目不全部预热。

## 默认音量与设置

- 旧BGM单曲音量0.06，当前0.12基准×默认分类音量0.6=0.072，默认实际幅度提升20%。该数是音量幅度关系，不是“听起来响20%”的主观承诺。
- 滑杆0～100%，默认60%；选择静音时仅BGM分类变0，保留滑杆偏好；恢复默认回60%且解除静音。
- 滑杆立即生效；关闭面板、切后台和退出时保存PlayerPrefs。音效/主音量不改变；重进游戏恢复音量和静音。
- 通过GameAudioSettings长期服务管理玩家偏好，RevSound.SetVolume调分类；GameAudio仍按六章映射PlayBgm。
- AudioSettingsPanel使用RevUIPanel、RevBind、RevSliderChanged/RevToggleChanged/RevButtonClick，OnRefreshView只画数据，SetValueWithoutNotify防刷新循环。
- GameFlow统一打开设置：加载期间及面板可见时现有IsGameplayBlocked阻止战斗推进/棋盘输入，关闭后恢复；不改Time.timeScale。

## 预制体与素材

- 预制体：Assets/GameRes/UI/Panel/Settings/AudioSettingsPanel.prefab，独立ui_settings包约9KiB。
- UI使用项目的UI_Settlement_imgBK（浅色羊皮纸）、UI_Tavern_Background、UI_Tavern_Handle、UI_Setting_Background、UI_Setting_Checkmark、UI_Tavern_btnClose，全部由唯一图集工具管理。
- 首页HomePanel、选关LevelSelectPanel、战斗BattleHudPanel新增btnSettings。战斗顶栏返回与设置分开摆放。
- 制作入口game.audio-settings.configure只保存指定预制体，不切换用户场景。新增文字已纳入精简字库，目前1533字符。

## GM与MCP

```text
RevGame/声音设置
RevGame/BGM音量 60
RevGame/声音查询
```

变更GM仅开发/编辑器环境注册。打开返回accepted只代表请求接受，需要等待实际面板打开。制作/检查入口：game.audio-settings.configure、game.audio-settings.check、game.audio-settings.preview、game.audio.check。

## 本轮验收与边界

- Unity编译和Console Error为0。隔离PlayerPrefs临时键检查默认120%、持久化、静音、越界截断、NaN拒绝、恢复默认通过，最后删临时键。
- 真实PC AB设置预制体冷加载：滑杆42%通过同一属性回调更新服务与BGM分类；静音只影响BGM；默认60%及保存重建通过。
- 离屏布局使用实际AB图集Sprite做有限临时克隆，仅为验证素材与UGUI渲染；编辑态atlasRequested未可靠即时绑定，先前检查因此失败。夹具明确提供图集Sprite后通过，不代表正式RevUI首次自动图集绑定已经验收。
- 初始制作的滑杆Fill尺寸错误导致填充超宽，已通过stretch清空sizeDelta修复；底板已调整为已有浅色羊皮纸。
- 六章曲名映射、音频时长/流式Vorbis配置通过；PC/Android各40业务包构建并复制到StreamingAssets。
- 直接解析新包确认第五、第六章AudioClip为流式、44100Hz双声道Vorbis。新增两首音乐后Android AB约24.12MiB、PC约24.33MiB，包含旧MainTheme备用曲；不是APK体积。
- 未启动Play、未切换用户场景；真实RevUI按钮/拖动/自动图集绑定、跨章播放、扬声器听感、Android触摸/真机/APK及刘海适配尚未验收。未以工具成功宣称这些通过。
- 详细证据与下载原流仅在.codex/work/chapter56-settings-2026-10-27，不提交、不生成ZIP。

## 滑条尺寸调整（用户更换图片后）

保留用户选择的轨道/填充图片和静音、恢复默认按钮位置。轨道545×46；填充区域545×46，填充RectTransform的sizeDelta清零；手柄区域509×46，手柄sizeDelta为22×14，Slider纵向拉伸后实际显示22×60。清零手柄偏移，使其中心落在轨道上，保留当前音量值。

新增game.audio-settings.slider-layout，优先修改当前同一Prefab Stage，保持预览打开。game.audio-settings.configure不再覆盖已有设置预制体，防止重跑制作时恢复旧图片和布局。

实际PC AB离屏画面与滑杆42%/静音/恢复60%检查通过，PC/Android AB已更新。没有启动Play或切换用户场景；真机触摸拖动尚未验收。
