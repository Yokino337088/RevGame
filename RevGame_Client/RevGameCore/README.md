# RevGameCore · 八角笼决斗内核

C++17 独立工程。参考 MiniGameCore 的 FrameWindow、CorePipeline 与战斗 PartA / PartB 分段，采用 RevGame 自己的双座位规则与通信契约。

## 确定性规则

- 逻辑帧固定为 **20 Hz（50 ms）**。内核只按连续封帧推进，Unity 用独立的表现时钟读取快照。当前 `frameLimit=0`，双方通过击倒分胜负。
- 水平位移、跳跃和命中距离都用 `FixedPoint`。每格 1000 个原始单位，乘法向零截断，超范围结果饱和到 int32 边界。血量、能量和帧号使用整数。
- 宿主按约定的 UTF-16 小端字节将统一决斗 ID 派生为种子。开局 FIFO 把种子交给原生实例，`DeterministicRandom` 用 SplitMix64 扩种并以 xorshift128+ 抽样。完整随机状态参加指纹。当前招式没有概率规则，已有命中和伤害值固定。
- `SeatOrder` 固定黑方在前、白方在后，移动、命中、能量和状态输出均按该顺序处理。FIFO 按连续帧号执行。`DuelDamage` 先读取两席命中，再统一提交扣血。同帧互击可以同时致死，免伤帧保留命中反馈。
- 帧指纹采用 MiniGameCore 的逐字节 FNV-1a 方式。按固定字段顺序混入小端帧号、胜负、两席状态、配置、初始种子、隐藏的垂直速度和完整 RNG 状态。内部使用 32 位，快照输出保留非负 31 位供现有整数协议传输。

## 构建与配置

从仓库根目录执行：

    py -3 RevGame_Client/RevGameCore/Config/GenerateDuelConfig.py
    cmake -S RevGame_Client/RevGameCore -B .codex/work/rev-duel-native -G "Visual Studio 17 2022" -A x64
    cmake --build .codex/work/rev-duel-native --config Release
    ctest --test-dir .codex/work/rev-duel-native -C Release --output-on-failure

把 Release 输出的 DLL 更新到 Unity 的 Assets/Plugins/RevGameCore/x86_64。Windows 服务端构建自动复制同一份 DLL。其它平台需要构建并部署相应的原生库。

Unity 2022.3.15f1c1 的 Android Mono 测试包使用 ARMv7。用该 Unity 自带的 NDK 配置 `-DANDROID_ABI=armeabi-v7a -DANDROID_PLATFORM=android-22 -DANDROID_STL=c++_static`，将 `libRevGameCore.so` 放入 `Assets/Plugins/Android/libs/armeabi-v7a`，再用 `game.pvp.android-duel-prepare` 装配导入设置。

`game.pvp.duel-atlas` 核对 14 组角色动作和切片引用，并打包随内置场景交付的 Sprite Atlas V2。Android 出包前构建并发布 Android AB，将 `Assets/StreamingAssets/PC` 临时移出工程，再调用 `game.pvp.android-duel-player`。构建动作会检查 PC 资源已隔离。完成后将 PC 目录和原有 `.meta` 归还到原位。

工程路径含中文时，可在纯 ASCII 的独立副本里保留 Android AB 并排除 PC AB，再以 Android 目标调用批处理入口 `RevGame.Editor.GamePvpBuild.BuildAndroidDuelTestBatch`。它输出到副本的 `Builds/AndroidDuelTest/Player`，供验包后复制。测试包锁定横屏，启动时请求 1920×1080 全屏渲染。

Unity 自带 AGP 7.1.2 的本地测试包可用 Android API 32 组装。如果自动选择 API 35 后 D8 读取 `android.jar` 失败，可在副本的 `Library/Bee/Android/Prj/Mono2x/Gradle` 中让 launcher 和 unityLibrary 都使用 API 32，再运行 `:launcher:assembleDebug`。验包时检查 Manifest 横屏声明、Mono 和 ARMv7 原生库，以及 APK 只含 Android AB。

开发版客户端在兼容且已连接的服务器或局域网棋局中显示「决斗测试」。服务器测试入口使用 Debug Gate，默认匹配地址为 `192.168.10.3:20000`。点击会请求权威直接切场，双方 Ready 后开始封帧。

数值改在 Config/Duel.v1.json，运行生成器后同步更新两端。配置指纹包括棋盘资格阈值、固定步长、两席的命中距离及跳跃参数。黑方绯焰读取六招的距离，白方鸦鸦读取追加的六项距离。Unity 的 game.pvp.duel-build 动作用实际编辑器 API 构建决斗场景。

## 帧流与生命周期

1. 中局手数和盘面估算分差均过门槛，劣势座位才可发起。Fantasy 服务端重放已校验棋谱再判资格。P2P 黑方宿主核对白方请求的棋盘指纹。
2. 决斗冻结围棋。GameFlow 用 RevScene 切到独立场景并保留原 PvP 域。场景加载完成才发送 Ready。服务器 Gate、P2P 黑方和本地宿主各自拥有对应场次的权威封帧器。
3. 每个座位每帧的第一条有效意图锁定，两席齐备才可封帧。重复包不改写历史，缺帧时停止推进。拉取从最后连续帧开始，每包最多 64 帧。电脑通过同一条命令链发输入。
4. C# 用 SafeHandle 持有每局实例。C ABI 提供生命周期、FIFO 读写和帧边界检查点，消息采用小端定宽整数。C++ 只在完整帧通过校验后推进，按先双方移动与招式、再同时结算命中、最后产出快照的顺序运行。
5. HUD 插值、精灵播放和 DOTween 只读取显示快照。决斗结果独立展示，围棋 Elo 仍由原有围棋结算入口处理。服务端房间保留决斗命令供补帧，P2P 暂无宿主迁移和断线后重入。

## 线格式

| 方向 | 字段，均为小端 32 位整数 |
|---|---|
| 开局输入 | 协议版本 7，操作 1，72 项配置，64 位种子的低字和高字 |
| 整帧输入 | 协议版本 7，操作 2，从 1 开始的帧号，黑方意图，白方意图 |
| 帧输出 | 帧号、结果、两席状态、两侧命中伤害、世界指纹、两席跳跃高度 |

协议 7 保留原来的意图编码。低两位是方向，接下来三位是攻击槽，第六位是跳跃。玩家可发普攻、技能一、大招和技能二，0 为空意图。普攻二、三段由内核根据命中和取消窗口派生。受击硬直、已经使用的技能、连击数、跳跃高度和速度都参加指纹与检查点。队列写满返回背压，缓冲不足保留输出。客户端和服务端需要配套更新。旧版检查点与录像须用原协议和规则版本读取。

## 联机帧指纹对账

当前每 **40 个逻辑帧**采样一次，终局补采一次（终局恰好在周期帧时只采一次）。本地计算的样本按帧号附在现有联机请求中，未确认时每半秒最多补送一次。权威端用原生历史核对每席的同帧指纹，再把双方共同通过的帧号送回两端。重复报告只重新确认，跳过应报的中间帧会被拒绝。首个有效样本不一致时，权威停止封帧，两端得到具体分歧帧号。终局以后仅以低频读请求等待双方确认。

## 检查点与逻辑录像

- `DuelAuthority.SaveCheckpoint()` 导出最后一个已执行帧的原生世界。格式包含配置、种子、帧号、胜负、黑白完整状态和随机状态。保存、恢复都要求原生输入与输出 FIFO 已排空。恢复需要同局标识和配置，成功后从检查点的下一帧继续。恢复帧的命中属于旧事件，返回的显示基线将命中值清零。
- `DuelAuthority.CaptureReplay()` 复制已执行的权威帧。录像包含对局 ID、协议与配置指纹、固定步长、显式帧号、两席输入和逐帧预期结果。通过 `DuelReplay.Serialize()` 与 `Deserialize()` 读写小端字节，调用方可选择保存位置。
- 录像导入和导出以一小时的帧数为上限，以约束文件分配。活跃对局的权威历史继续保留，供延迟客户端补帧。
- `DuelReplay.ReplayInto()` 在独立的原生实例里重放。从检查点续跑时先核对该帧指纹，再应用后续帧。播放复用已经录下来的电脑输入，逐帧比较世界指纹和命中。分歧时报告帧号，调用方应丢弃该播放实例。
- 检查点只覆盖原生战斗世界。网络中尚未封口的输入、权威房间的接收状态与 Unity 表现不属于这个格式。服务端与 P2P 在线补帧仍使用现有历史游标。

验证覆盖 Windows x64。录像 API 已可用，录像管理界面、跨设备联网恢复、移动端及 IL2CPP 需分别接入和验收。
