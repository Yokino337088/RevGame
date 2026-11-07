# RevGameCore · 八角笼决斗内核

C++17 独立工程。参考 MiniGameCore 的 FrameWindow、CorePipeline 与战斗 PartA / PartB 分段，采用 RevGame 自己的双座位规则与通信契约。

## 构建与配置

从仓库根目录执行：

    py -3 RevGame_Client/RevGameCore/Config/GenerateDuelConfig.py
    cmake -S RevGame_Client/RevGameCore -B .codex/work/rev-duel-native -G "Visual Studio 17 2022" -A x64
    cmake --build .codex/work/rev-duel-native --config Release
    ctest --test-dir .codex/work/rev-duel-native -C Release --output-on-failure

把 Release 输出的 DLL 更新到 Unity 的 Assets/Plugins/RevGameCore/x86_64。Windows 服务端构建自动复制同一份 DLL。其它平台需要构建并部署相应的原生库。

数值改在 Config/Duel.v1.json，运行生成器后同步更新两端。配置指纹包括棋盘资格阈值、固定步长和三招式的数值。Unity 的 game.pvp.duel-build 动作用实际编辑器 API 构建决斗场景。

## 帧流与生命周期

1. 中局手数和盘面估算分差均过门槛，劣势座位才可发起。Fantasy 服务端重放已校验棋谱再判资格。P2P 黑方宿主核对白方请求的棋盘指纹。
2. 决斗冻结围棋。GameFlow 用 RevScene 切到独立场景并保留原 PvP 域。场景加载完成才发送 Ready。服务器 Gate、P2P 黑方和本地宿主各自拥有对应场次的权威封帧器。
3. 每个座位每帧的第一条有效意图锁定，两席齐备才可封帧。重复包不改写历史，缺帧时停止推进。拉取从最后连续帧开始，每包最多 64 帧。电脑通过同一条命令链发输入。
4. C# 用 SafeHandle 持有每局实例。C ABI 只暴露生命周期及 FIFO 读写，消息采用小端定宽整数。C++ 只在完整帧通过校验后推进，按先双方移动与招式、再同时结算命中、最后产出快照的顺序运行。
5. HUD 插值、精灵播放和 DOTween 只读取显示快照。决斗结果独立展示，围棋 Elo 仍由原有围棋结算入口处理。服务端房间保留决斗命令供补帧，P2P 暂无宿主迁移和断线后重入。

## 线格式

| 方向 | 字段，均为小端 32 位整数 |
|---|---|
| 开局输入 | 协议版本 1，操作 1，24 项配置 |
| 整帧输入 | 协议版本 1，操作 2，从 1 开始的帧号，黑方意图，白方意图 |
| 帧输出 | 帧号、结果、两席状态、两侧命中伤害、世界指纹 |

意图低两位是方向，高两位是攻击槽，0 为空意图。每席状态依次为位置、生命、能量、动作、动作已过帧数和面朝方向。队列写满返回背压，缓冲不足不会消费输出。

验证覆盖 Windows x64。现有历史只供对局中的内存补帧，暂未加入持久化录像界面。公网弱网、移动端及 IL2CPP 需要独立验收。
