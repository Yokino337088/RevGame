# RevGame 实践 · PvP、原生决斗和 Agent

Unity 路径相对 Assets，服务端与 C++ 路径相对 RevGame 仓库。协议、网络库、原生模拟和服务端权威各有发布版本。先判断目标是本地人机、局域网、真实服务器还是回放。

## 九路围棋

1. 路由从 `GameScripts/Flow/States/GameFlow.Pvp.cs` 到 `GameScripts/Flow/Logic/Pvp/PvpLogic.cs`。选择模式面板、棋盘、连接和一轮业务状态有不同寿命。用真实模式入口开始，在退出和切换页面后核对旧连接及 UI。
2. `GameScripts/Pvp/Core/GoCore.cs` 管围棋规则，`GameScripts/Services/Pvp/PvpService.cs` 持有房间、棋谱、座位、连接与业务拒绝。查落子、停一手、认输、数子、恢复对局和回放对应的入口，确认模式、阶段、发起方和当前提交代次。
3. `GameScripts/Services/Pvp/PvpService.RoomSnapshot.cs` 投影同版本的棋手、比分、连接与操作许可。`GameScripts/UI/Panels/Pvp/PvpPanel.cs` 和 `PvpPanel.Navigation.cs` 观察业务并处理窗口交割。按钮禁用态和真正提交使用同一门禁；传输中的判断等待权威回包。快照区分房间 ID、棋谱版本与连接瞬态，展示的时钟也需刷新。
4. 局域网读 `GameScripts/Services/Pvp/Networking/Lan/LanPeerTransport.cs` 及同组 Discovery、Driver 和隔离用例。服务器读 `GameScripts/Services/Pvp/Networking/Fantasy/FantasyGoTransport.cs`、仓库 `RevGame_Server/RevGame.Server/Tools/NetworkProtocol/Outer/GoRelay.proto` 及服务器实际处理类。联机修复要同时核对握手、匹配、权威快照、增量基序号、重连、退场和连接对象身份。
5. 重连先验证原房间、接管连接和快照版本，再发布恢复后的可操作状态。迟到回包按旧连接及房间丢弃，拒绝与超时保留真实原因。ResumeToken 和配置凭据只由对应连接持有，测试与日志不输出。
6. 验收由 `GameScripts/Bootstrap/Diagnostics/Pvp/PvpAutomation.cs` 等登记实际动作。分别覆盖本地双人、人机、两端已连 LAN 和真实服务器。对比两侧已接受的序号、棋盘指纹、回合、确认、结果和退出。房间字段缺失、迟到、错座、旧版本和拒绝输入要查两端未误写，而不仅是页面有提示。

## 原生决斗

| 追踪节点 | 实际参考 | 工程决策 |
| --- | --- | --- |
| 人物选择、准备与切场 | `GameScripts/Services/Pvp/PvpService.DuelPreparation.cs`、`GameScripts/Pvp/Duel/DuelPreparation.cs` | 房间权威批准后进入决斗准备，验证双席身份、准备状态、取消和回大厅。 |
| 输入与逻辑帧 | `GameScripts/Services/Pvp/PvpService.Duel.cs`、`GameScripts/Pvp/Duel/DuelFrames.cs` | Unity 提交模拟所需的完整连续帧，输入确认、模拟帧、显示帧和指纹按各自契约处理。 |
| 原生 ABI 与模拟 | `GameScripts/Pvp/Duel/DuelCorePipe.cs`、仓库 `RevGame_Client/RevGameCore/Include/GameCoreExport.h`、`RevGame_Client/RevGameCore/Source/DuelWorld.cpp` | 核对 ABI、结构版本、整数约定、固定点、确定性随机及快照字节。由原生 Core 推进规则，Unity 读取表现。 |
| 校正和回放 | `GameScripts/Pvp/Duel/DuelReplay.cs`、仓库 `RevGame_Client/RevGameCore/Source/DuelCheckpoint.cpp` | 按权威版本恢复检查点，顺序重放已接受输入。验证前后指纹与终局，表现重建对齐当前帧。 |
| 发布与验收 | 仓库 `RevGame_Client/RevGameCore/CMakeLists.txt`、`tools/DuelPredictionChecks/Program.cs` | 离线断言覆盖算法与 ABI；真实联机、弱网、掉线和目标平台插件加载另外验收，明确客户端和服务端版本。 |

目标项目决定规则、预测窗口、承载库和平台。复用输入提交、快照边界与端到端证据。

## 外部 Agent

读 `GameScripts/Services/Pvp/PvpService.Agent.cs` 与 `GameScripts/Pvp/Agent/GoAgentPosition.cs`。外部输出提供建议，业务使用同一快照规则检查响应格式、局代次、位置合法性、当前回合与玩家是否允许托管。建议、托管、提交分开路由，关闭或切房使旧回复失效。验收覆盖关闭后回包、非法输出、超时、合法操作、真实提交计数和复位。测试使用隔离业务或已授权服务，供应商、密钥和外网能力按实际工程许可接入。
