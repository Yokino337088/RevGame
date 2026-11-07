# Config

留给"换环境才需要改"的部署配置（多机多进程拓扑、Control Center 地址、数据库地址、本地覆盖）。

注意区分两份东西：

| 配置 | 位置 | 说明 |
|---|---|---|
| `Fantasy.config` | `Server/Entity/Fantasy.config` | 框架启服配置，**必须与直接引用 `Fantasy-Net` 包的 `.csproj` 同级**，由框架在引用包后自动生成；改它不要挪位置 |
| 本目录 | `Config/` | 部署环境的副本 / 变体（例如本机、测试服、线上各一份），以及 Control Center 提供拓扑时的说明文件 |

启用 Control Center 后，`Fantasy.config` 只负责连 Control Center（`<controlCenter enabled="true">`），
Machine / Process / World / Scene / Database 的拓扑由 Control Center 下发，此时本目录放的是各环境地址与凭据说明。

## 在线围棋匹配与对局分流

一个 `MultiThread` Gate Root Scene 接入 KCP，统一持有匿名身份、恢复令牌和匹配队列。先按 `MatchKind + RulesJson` 分桶，再在同桶内比较段差和分差，距离相同时优先等待较久的人。参考了王者荣耀公开的近段匹配和随时间放宽，以下数值是 RevGame 九路 1v1 的配置。 [S42 公告](https://bbs.4399.cn/thread-view-tid-51696899)

| 双方中较短的等待时间 | 最大段差 | 最大分差 |
|---|---:|---:|
| 0 至不足 8 秒 | 0 | 100 |
| 8 至不足 16 秒 | 1 | 200 |
| 16 秒及以上 | 2 | 300 |

棋力初始 1500 分，每 200 分一段。双方确认数子或本人认输经同源 `GoCore` 校验后，按 Elo 更新（K=32）。客户端申报的掉线和超时沿用原对局流程。Unity 客户端在同一次游戏运行中、同地址同规则下复用匿名令牌。服务端将已计分的棋力档案在内存保留最多 30 分钟，容量 4096；旧房间仍按原重连窗口释放。大厅每秒复查队列，所以放宽匹配不依赖新的玩家入队。

大厅选择当前分配对局数最少的 `GoBattle` Root Scene，写入固定 Address。四个 `MultiThread` Battle 各占独立线程，每个接收最多 128 个活跃房间。满载时保留队列。Gate 用 1 毫秒定时器合并发往同一个 Battle 的操作，达到 32 手立即发出，同一局最多一手在途。Battle 独占棋盘和规则校验。结算或退出释放计算席位，重连和同步仍通过原 Gate。部署中对外仅开放 Gate 的 KCP 端口，Battle 只使用内网地址。

新客户端握手声明 `SupportsDelta` 后，普通操作的回包和推送只带当前一手。序号出现缺口时客户端主动请求完整同步。握手、重连、主动同步和未协商增量的客户端仍接收完整棋谱。

当前拓扑在单机单进程内把棋局计算分散到多线程，Gate 仍集中处理客户端连接、排队和棋谱推送。跨机器的扩容及故障接管需要共享房间归属、身份和恢复状态。Fantasy Control Center 提供服务发现，当前静态配置不启用它。

### 本机 KCP 压测

编译 `Server/Main/Main.csproj` 和 `Tools/GoRelayProbe/GoRelayProbe.csproj` 到两个独立输出目录。测试服务器的五个 Scene 都绑定回环地址，分别使用不同的内网端口，Gate 使用空闲外网 KCP 端口。探针的 `Fantasy.config` 只保留一个无外网监听的 Scene（`outerPort="0"`），内网端口也与服务器不同。

从测试服务器输出目录启动 `dotnet Main.dll --m Develop`。等待日志中出现 `Startup Complete SceneCount:5`，在另一个终端进入探针输出目录并执行下列命令。端口要对应刚启动的回环测试实例。

```powershell
$env:REVGO_LOAD_ENDPOINT = "127.0.0.1:22000"
$env:REVGO_LOAD_PLAYERS = "1000"
$env:REVGO_LOAD_BUCKETS = "100"
$env:REVGO_LOAD_TURNS = "30"
dotnet GoRelayProbe.dll --m Develop
```

将桶数设为 `1` 可跑单池突发，设为 `100` 可验证规则和场次交错时不会串房。人数须为桶数两倍的整数倍。探针发真实 KCP 握手、匹配、逐波落子、计分和心跳，验证满载重连及离席，输出 JSON 延迟分位数和实收棋谱条目数。旧 TCP 探针命令保持原样。测试完停止本次启动的服务器。

需要分段计时时，只在隔离压测进程中设置环境变量。服务端设置 `REVGO_MOVE_TRACE` 为 Gate CSV 的绝对路径，并设置 `REVGO_MOVE_TRACE_COUNT` 为预计样本数（500 局每局 30 手加 4 条数子操作即 17000）。探针设置 `REVGO_LOAD_TRACE` 为客户端 CSV 的绝对路径。两份证据按房间和序号关联，跨进程时间差只在同机同一单调时钟下计算。

最新压测见 [围棋落子延迟优化测试](围棋落子延迟优化测试_2026-11-07.html)。早期记录见 [围棋棋力匹配与房间分流测试](围棋棋力匹配与房间分流测试_2026-11-07.html) 和 [单 Gate 围棋千人匹配与并发压测](围棋服务端千人匹配与并发压测_2026-11-07.html)。
