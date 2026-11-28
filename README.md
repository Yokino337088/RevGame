# 🧪 **RevGame·围棋大作战**
# 实践是检验真理的唯一标准
### 认识从实践始，经过实践得到了理论的认识，还须再回到实践去

[**▶ 在浏览器试玩围棋大作战**](https://yokino337088.github.io/RevGame/)

RevGame 是用 Unity 制作的围棋主题游戏，也是 Revolution 框架的实际业务验证工程。项目把玩法、跨平台交付和自动化验收放在同一条开发流程里。想先玩可以直接打开上面的试玩页，想看实现和实测可以从 Wiki 进入。 [RevGame Wiki](https://github.com/Yokino337088/RevGame/wiki)

<a name="why-revgame"></a>

## 🧭 为什么要做 RevGame？

**RevGame 用一款真正可玩的游戏，验证 Revolution 能不能支撑从做功能到交付游戏的完整流程。** 玩家从大厅选关，进棋盘、看剧情、打 Boss、结算，再退回大厅。这条路上，界面得等资源到齐，战斗要按规则推进，退出后旧回调和特效要收掉。到了 Android 或浏览器上，还要用那个平台的实际游戏包复查。开发者可以顺着同一条路找到源码、测试计划、实测记录和云端构建结果。 [立项与工程导读](https://github.com/Yokino337088/RevGame/wiki/%E7%AB%8B%E9%A1%B9) · [Revolution 框架](https://github.com/Yokino337088/Revolution)

**游戏里遇到的问题，也会回到框架里解决。** 做 RevGame 时，接界面、查编译和反复进游戏验收的实际需求，推动了 RevCLI 和 RevMCP 的开发。遇到框架层的问题，就在游戏里复现，回 Revolution 修，再同步到游戏里验一遍。例如剧情粉屏最后追到了框架选错平台资源包。后来做同类功能的人，也能从这些案例里找到源码入口和复测办法。 [RevGame 工程实践索引](https://github.com/Yokino337088/Revolution/blob/main/Skills/revolution-framework/references/revgame-practice-map.md)

### 🛠️ 能帮游戏开发者解决的六个痛点

1. **功能做多了，按钮、业务规则和存档容易写成一团。** 在大厅把 BGM 调到 60%，进战斗再打开设置，滑杆仍该显示 60%。RevGame 的面板负责接滑杆和显示数字，音量服务负责持有和保存实际音量。照这个分工做商店或结算页，美术换布局时可以专心验画面，程序改规则时也知道该从哪里改起。 [界面和业务各管什么](https://github.com/Yokino337088/RevGame/wiki/%E6%9E%B6%E6%9E%8403-%E7%95%8C%E9%9D%A2%E5%92%8C%E4%B8%9A%E5%8A%A1%E5%90%84%E7%AE%A1%E4%BB%80%E4%B9%88)
2. **一退出副本，旧请求、计时器或特效还在工作。** RevGame 用 `GameFlow` 管当前阶段，用 `RevScope` 管这段玩法拥有的工作和资源。真实资源包验收计划会检查旧请求归零、迟到回调被拦住、失败交接拿到的资源被归还，以及大厅共用的预加载资源还可用。退场也就有了明确的验收条件。 [真实资源包验收计划](tools/automation/first-chapter-delivery.json)
3. **AI 说功能做完了，接手的人还得从头查怎么验。** RevGame 把检查分成几步。先按项目规则找到入口，用 RevCLI 把场景、预制体或组件接进 Unity，再核对编译结果。然后用 RevMCP 查当前游戏开放的测试动作，进游戏执行，核对实际页面和业务状态，最后回到大厅。下一位开发者可以沿同一份计划复跑。 [怎样把新功能接成可验收的流程](https://github.com/Yokino337088/RevGame/wiki/AI%E8%87%AA%E5%8A%A8%E5%8C%96%E5%B7%A5%E4%BD%9C%E6%B5%81-07-%E6%80%8E%E6%A0%B7%E6%8A%8A%E6%96%B0%E5%8A%9F%E8%83%BD%E6%8E%A5%E6%88%90%E5%8F%AF%E9%AA%8C%E6%94%B6%E7%9A%84%E6%B5%81%E7%A8%8B)
4. **编辑器看着正常，换资源包就出现粉屏。** RevGame 有一次剧情粉屏，是因为 Windows 上的 Unity 编辑器在构建目标留在 Android 时读到了 Android 资源包。修正框架的选包逻辑后，团队用真实 PC 资源包重新启动，核对剧情画面、绘制材质和纹理。类似问题可以先查**运行时到底读了哪个平台的哪份资源**。 [资源迭代与粉屏案例](.codex/skills/revgame-development/references/unity-and-assets.md)
5. **压力测试跑得顺，玩家正常打却还是卡。** 满盘 81 棋适合测高密度战斗，正常局还要经历选牌、落子、换回合和 Boss 施法。RevGame 用真实手牌在同一台安卓机上采集了 **60 个有效窗口、24,096 帧**。随后在第四章固定负载下，把背景从开启改为停绘，再恢复，GPU 帧时 P95（约 95% 的采样帧耗时落在这个值以内）依次是 **32.50、13.27、30.80 毫秒**。先用玩家走法找问题，再固定条件只改一项，就能看清是哪段绘制占了时间。 [安卓普通局实测与定位](https://github.com/Yokino337088/RevGame/wiki/81%E6%A3%8B%E6%B5%8B%E5%BE%97%E9%A1%BA%E6%97%B6%E5%AE%89%E5%8D%93%E6%99%AE%E9%80%9A%E5%B1%80%E4%B8%BA%E4%BB%80%E4%B9%88%E8%BF%98%E4%BC%9A%E5%8D%A1)
6. **包只能在某个人的电脑上打，测出问题也说不清是哪一版。** RevGame 给 Windows、Android、WebGL 分别准备了 GitHub Actions 的测试包和正式包入口。云端按选定的 Git 提交取代码和目标资源，启动 Unity，再打开成品逐项检查。WebGL 的两种包就核对了网页入口、浏览器执行的代码和同一提交的 **241 个资源文件**。团队可以从构建记录追到具体提交和交付包。 [云端构建与验包教程](https://github.com/Yokino337088/RevGame/wiki/GitHub%E4%BA%91%E7%AB%AF%E6%80%8E%E6%A0%B7%E6%8A%8ARevGame%E6%89%93%E6%88%90%E6%B8%B8%E6%88%8F%E5%8C%85)

## 🗣️ 作者原话

#### 其实我并不是为了做游戏而做游戏，而是因为我搞了一套具有革命性的Unity客户端框架，我为了验证这一套框架的可行性，才来做了这一个游戏，因为实践是检验真理的唯一标准，如果只是做框架的话，很容易变成纸上谈兵，所以必须要让自己站在一个独立游戏开发者的角度，从零开始使用Revolution框架做一个游戏，上线各大游戏平台，支持跨平台适配，跑通全链路的业务流程，才能说明这一套Revolution框架是可以投入到实际的生产当中的。并且能够在开发过程当中及时发现框架的问题，把问题回灌进框架当中，让框架更加完善。

#### Revolution框架源自于我在实习当中的技术积累，我的目的就是想把项目当中最精华、最优秀的设计哲学单独抽离出来，写成一套具有复用性的框架，Revolution一开始只有框架本体当中的代码模块和编辑器功能，并没有像现在的RevCLI、RevMCP这些用于AI自动化工作流的工具，这些自动化工具是在开发RevGame这个项目的过程当中，发现了各种效率问题才开发出来的，这也是我为什么要搞RevGame这个项目的原因，如果不做这个项目，那Revolution就永远只会停留在只提供代码门面模块的阶段，根本不会具有在Agent时代进行快速开发迭代的价值。

#### 这一个项目我还带了俩个徒弟一起做，一个负责美术工作流、一个负责策划，目的就是想让他们也从这个项目当中学到真正的技术，熟悉游戏开发的全流程，跑通整个业务流程，并且用这一段项目经历早日进入游戏行业实习。

---

## 🎮 可以玩什么

| 玩法 | 入口与内容 |
| --- | --- |
| **六章远征** | 从序章和教学进入 9 × 9 棋盘，布置不同棋子，经历自动交火、助阵、Boss、结算和奖励。 [远征玩法](RevGame_Client/策划案/玩法拓展策划案.html) |
| **无尽模式** | 面对持续敌潮，在选址、守点与升级之间作取舍。大量敌人的移动、显示和回收也用来验证框架在长时间压力下的表现。 [无尽模式策划案](RevGame_Client/策划案/无尽模式策划案_棋阵幸存者_2026-11-01.html) |
| **九路围棋与决斗** | 本地人机用九路围棋规则推进，对局可以进入选角和原生决斗。仓库内的联机实现包含棋力匹配、房间分配、断线恢复和服务端确认决斗帧，部署依赖对应服务器。 [对战玩法](RevGame_Client/策划案/九路围棋PvP策划案_2026-10-26.html) · [WebGL 人机与决斗验证](https://github.com/Yokino337088/RevGame/wiki/%E6%9E%84%E5%BB%BA-WebGL%E5%8E%9F%E7%94%9F%E6%8F%92%E4%BB%B6%E4%B8%8EPInvoke) · [联机部署与压测](RevGame_Server/RevGame.Server/Config/README.md) |

## 🌍 平台进展与性能证据

下面的数字跟着当时的设备、游戏包和实测窗口一起读。**平均帧率看一秒画了多少张，P95/P99（约 95%/99% 的帧间隔在多长时间以内）和长帧次数用来找慢的那几下。** 一套通用方法落到不同平台，要按各自的画面、设备和真实工作量重测。 [性能压测导论](https://github.com/Yokino337088/RevGame/wiki/%E6%94%BB%E5%9D%9A-%E6%80%A7%E8%83%BD%E4%BC%98%E5%8C%96%E5%AF%BC%E8%AE%BA) · [CPU、GPU 与内存](https://github.com/Yokino337088/RevGame/wiki/%E9%80%9A%E7%94%A8%E6%80%A7%E8%83%BD%E4%BC%98%E5%8C%96)

| 平台 | 当前有据可查的结果 |
| --- | --- |
| **Windows** | 有 Editor 诊断，以及 Mono 和 IL2CPP 独立包的实测。2026-10-31，Iris Xe、D3D11、1920 × 1080 窗口、默认 VSync=1，IL2CPP 版六章 81 棋每段热身 5 秒、实采至少 30 秒，平均 **59.86～59.93 FPS**，P95 **18.09～18.23 ms**。原记录同时保留了 48 棋变成 50 棋的失败样本。 [Windows 优化](https://github.com/Yokino337088/RevGame/wiki/%E6%94%BB%E5%9D%9A-%E6%80%A7%E8%83%BD%E4%BC%98%E5%8C%96-Windows%E4%BC%98%E5%8C%96) |
| **安卓** | 已有 IL2CPP ARM64 的 APK（安卓安装包）构建、签名和验包记录。宽屏 **1.0.1 Release 为 70.93 MiB**，对应当时的资源版本。现有真机反馈来自旧版，最终包的热态帧率和多轮退场内存仍需在目标机复测。 [安卓优化](https://github.com/Yokino337088/RevGame/wiki/%E6%94%BB%E5%9D%9A-%E6%80%A7%E8%83%BD%E4%BC%98%E5%8C%96-%E5%AE%89%E5%8D%93%E4%BC%98%E5%8C%96) · [安卓构建复盘](RevGame_Client/文档/安卓构建踩坑报告/安卓构建踩坑复盘_2026-11-08.html) |
| **WebGL** | GitHub Pages 提供网页试玩。性能对照覆盖面板首开、真实绘制尺寸、首次特效与飘字。第 4 章 **81 棋、1600 × 899、每轮两分钟**的同包轻量复测中，启用同批避让复用的三轮为 **59.769～59.956 FPS**，最慢飘字等待分别为 **216.6、182.6、168.1 ms**。发布和实测各有对应版本记录。 [Web 端优化](https://github.com/Yokino337088/RevGame/wiki/%E6%94%BB%E5%9D%9A-%E6%80%A7%E8%83%BD%E4%BC%98%E5%8C%96-web%E7%AB%AF%E4%BC%98%E5%8C%96) · [网页发布过程](https://github.com/Yokino337088/RevGame/wiki/%E6%9E%84%E5%BB%BA-GitHubPages%E9%83%A8%E7%BD%B2WebGL) |

## 🧩 RevGame 怎样验证 Revolution

项目用真实玩法来验证框架的使用方式。资源没到时界面能不能等，跨章后旧操作会不会晚到，特效用完能不能归还，这些都要在玩家会走的流程里检查。

- **场景和界面交接**。从 `StartScene` 进入首页，再到选关、战斗、结算或退出。由 `GameFlow` 管理阶段，`RevScope` 管理这一段流程的资源与事件，`RevUI` 等界面具备显示条件再交给玩家。 [项目架构](RevGame_Client/文档/架构/RevGame项目架构与Revolution框架讲解.html)
- **真实资源和显示**。通过 `RevResManager` 读资源包、`RevPool` 借还棋子与特效。让能等一帧的飘字和特效按预算显示，伤害、占格和计分沿实际业务入口结算。 [战斗性能优化规则](RevGame_Client/文档/战斗性能优化规则/战斗性能优化规则.html) · [全场景实测](RevGame_Client/文档/AI自动化测试报告/全场景性能优化/全场景性能优化实测_2026-11-19.html)
- **把问题反馈到框架**。从 RevGame 的真实入口复现，定位属于项目还是框架。框架层的问题回到 Revolution 源仓库改，发布后按范围同步。可复用的做法整理到官方 Skill，工作条件和正反实测留在 Wiki。 [Revolution 框架](https://github.com/Yokino337088/Revolution) · [官方性能 Skill](https://github.com/Yokino337088/Revolution/tree/skill/revolution-performance) · [RevGame Wiki](https://github.com/Yokino337088/RevGame/wiki)

## 🏁 本地开始

```bash
git clone https://github.com/Yokino337088/RevGame.git
cd RevGame
```

1. 用 **Unity `2022.3.15f1c1`** 打开 `RevGame_Client/UnityProject/RevGame.Unity`，等导入和脚本编译完成。
2. 打开 `Assets/Scenes/StartScene.unity` 进入 Play，沿序章、教学和首页走到目标玩法。
3. 开发阶段可用编辑器直读资源。验证首帧白图、图集晚绑定或退场归还时，用真实 PC AB（游戏资源包）从冷启动走正式入口。安卓与网页实包用各自目标的 AB。 [资源迭代与验证](.codex/skills/revgame-development/references/unity-and-assets.md)

## 🔍 自动化怎么验

**先发现已注册能力，再调用真实动作、断言业务结果，最后清理。** RevCLI 是命令行入口，RevMCP 通过 MCP（让工具发现并调用工程能力的协议）提供同类入口。进战斗时查的是场景与资源到位后能否开始，收尾时查是否正式回到首页，以及本局的对象和资源引用是否已归还。 [项目验收规范](.codex/skills/revgame-development/references/project-acceptance.md) · [RevMCP 使用说明](RevGame_Client/UnityProject/Revolution.RevMCP/README.md)

需要命令行时，使用 **Python 3.10+**，在仓库根目录运行下列命令。在线能力查询需要打开目标 Unity 编辑器。

```powershell
$gameProject = "RevGame_Client/UnityProject/RevGame.Unity"
$revCli = "RevGame_Client/UnityProject/Revolution.RevCLI/rev.py"

python $revCli --project $gameProject doctor
python $revCli --project $gameProject --mode live commands --live
```

远征规则和九路围棋规则也可用 **.NET 10 SDK** 跑离线回归。GitHub Actions 还验证项目规范和 Windows 原生决斗预测。 [CI 流程](.github/workflows/revgame-policy.yml)

```powershell
dotnet run --project RevGame_Client/Tests/CombatAttackRegression/CombatAttackRegression.csproj -c Release
dotnet run --project tools/PvpRulesChecks/PvpRulesChecks.csproj -c Release
```

## 📁 代码与资料从哪儿找

| 目录 | 内容 |
| --- | --- |
| [Unity 工程](RevGame_Client/UnityProject/RevGame.Unity/) | `Assets/GameScripts` 放玩法和界面，`GameRes` 放资源，`GameShader` 放绘制实现，`Assets/Revolution` 是框架的导出快照。 |
| [RevGameCore](RevGame_Client/RevGameCore/) | 九路围棋决斗的原生内核、检查点与跨平台编译。 |
| [RevGame_Server](RevGame_Server/) | 基于 Fantasy 的连接、匹配与权威对局。配置、构建和部署指引在服务端 README。 |
| [策划案](RevGame_Client/策划案/) · [配置表](RevGame_Client/配置表/) | 查关卡与玩法意图、数值源表。剧情资源与运行端按项目入口查。 |
| [项目 Wiki](https://github.com/Yokino337088/RevGame/wiki) · [项目 Skill](.codex/skills/revgame-development/SKILL.md) | Wiki 分立项、攻坚、构建和开发日志。项目 Skill 把做资源、调试和验收的人带到各自入口。 |

深入阅读可从 [新手代码导读](RevGame_Client/文档/架构/RevGame代码阅读指南-新手版.html)、[RevCLI 使用说明](RevGame_Client/UnityProject/Revolution.RevCLI/README.md) 和 [协作规范](AGENTS.md) 开始。旧版 README 里的早期回归数据、失败记录和鸦鸦待机验证可查阅 [历史 README](https://github.com/Yokino337088/RevGame/blob/262dcf11944c4519328f706db4a13e211081554a/README.md)。
