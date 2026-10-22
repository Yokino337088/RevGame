# RevGame · 围棋大作战
## 起源：伟大的革命领袖毛泽东同志说过，实践是检验真理的唯一标准

> **光做框架可不行，做到最后很容易变成纸上谈兵，所以必须得做一个可以稳定迭代的游戏项目来研制这一套框架的可行性，所以 RevGame 这个项目是被"造"出来验证 [Revolution](https://github.com/Yokino337088/Revolution) 我资源的这一套框架可行性的** ——
> 用一款完整可玩的 2D 小游戏当"压力测试用例"，把框架从"Demo 能跑"推到"真实业务能跑"，再把暴露的问题回灌给框架。
> 游戏本身好玩是加分项，**框架的可行性结论才是这个仓库的第一交付物**。

📄 [玩法拓展策划案](RevGame_Client/策划案/玩法拓展策划案.html) · 🧩 [框架仓库 Revolution](https://github.com/Yokino337088/Revolution) · 🤖 [协作者约定 AGENTS.md](AGENTS.md)

---

## 📚 目录

- [📖 为什么要有这个项目](#-为什么要有这个项目)
- [🎮 玩法一览](#-玩法一览)
- [👹 Boss 与技能](#-boss-与技能)
- [🚀 快速开始](#-快速开始)
- [🧩 框架能力落到哪了](#-框架能力落到哪了)
- [🧪 三层验证体系](#-三层验证体系)
- [📁 工程结构](#-工程结构)
- [📊 配置表](#-配置表)
- [📚 文档导航](#-文档导航)
- [🛠 常用命令](#-常用命令)
- [📌 当前进度](#-当前进度)
- [🤝 贡献约定](#-贡献约定)

---

## 📖 为什么要有这个项目

但"每个模块都能单独跑 Demo"不等于"这套框架能撑起一个真实游戏"。真实项目会同时施加这些压力：

- 💾 **资源要真的加载、真的释放**（不是 Demo 里直读一个预制体）；
- 🎨 **UI 要真的开开关关、传数据、做转场**（不是点一下弹个提示）；
- 🔀 **流程要真的分阶段**（首页 / 选关 / 战斗 / 结算 / 退出），而且要能被中断与回退；
- ⏱ **时间、事件、对象池、异步**同时在一个场景里高频工作；
- ♟ **2D 玩法逻辑**本身就复杂（围棋的气 / 眼 / 连断），业务代码量会迅速逼近框架的边界。

所以需要一个**真项目**来回答一句话：**这套框架能不能在真实业务里撑住？**

`RevGame` 就是这个答案的载体。当前工程里有两套代码：

| 代码 | 规模 | 说明 |
|---|---|---|
| `Assets/Revolution/` | 250 个 `.cs` | 框架本体（`package` 分支快照，**不在这里改**） |
| `Assets/GameScripts/` | 88 个 `.cs` | 本游戏的全部业务代码 |

## 🎮 玩法一览

一句话：**9×9 的围棋盘上打"史莱姆军团"战争 —— 你只负责落子，棋子自己会走、自己会打。**

| 项 | 设定 |
|---|---|
| 棋盘 | 9×9 / 81 格（沿用围棋） |
| 棋子 | 9 种外形 · 4 职业（守卫 / 法师 / 游侠 / 刺客）· 3 档星级（★ → ★★ → ★★★） |
| 关卡结构 | 6 章 × 每章 11 回合，第 11 回合固定为 Boss 回合 |
| 一个回合 | **准备**（看敌方布阵 → 落最多 6 张牌）→ **战斗**（全自动推进）→ **结算**（布局与状态全部保留） |
| 局外流程 | 首页 → 选关 → 编队 / 强化 → 战斗 → 结算 → 章节奖励 → 下一章 |
| 局内要记的事 | 2 件（围棋：围住就锁死 / 做两只眼锁不死；成长：同名三合一升星） |
| 已砍掉 | 行动力条、商店、装备 / 遗物 / 天赋树、天梯、打劫、多页签选人 |

**围棋内核被压成了四件套**：`连` / `断` / `围` / `眼` —— 气被填满就锁死（不能动、不能打、挨双倍伤害），做出两只眼就围不死；玩家不需要学规则，颜色与脚下小点会自动提示。

**结构参考《小丑牌》**：只取"一局之内分章推进 + 章节末 Boss 改规则"的骨架，不取商店与构筑内脏；另有战斗事件（随机天降的"帮手"）与"跳过关卡换一档强化"的取舍。

完整数值、回合结构、难度阶梯、Boss 设计与平衡取舍见 📄 **[玩法拓展策划案（精简版 v18）](RevGame_Client/策划案/玩法拓展策划案.html)**。

## 👹 Boss 与技能

第六章 Boss 拥有全部 9 个技能，其余章节按 `UnlockChapter` 逐步解锁；技能表在 `配置表/战斗弹幕配置.xlsx`，程序按枚举名读取。

| 技能 | 中文名 | 一句话 | 解锁章节 |
|---|---|---|---|
| `Cleave` | 横扫震地 | 身边一圈即将受击，把棋子散开 | 1 |
| `Snipe` | 致命狙击 | 红色准星锁定一颗棋子，移出射线 | 1 |
| `Bombard` | 三连轰炸 | 远处区域依次落下三发炸弹 | 2 |
| `BulletRing` | 弹幕风暴 | 全屏暗红魔法球，沿径向同时扩散 | 3 |
| `BlinkBlast` | 瞬影爆破 | 瞬移到阵中空位并爆炸 | 4 |
| `LaserSpin` | 天光回旋 | 激光环绕 Boss 旋转，三段伤害 | 5 |
| `SoulDrain` | 灵魂汲取 | 残血棋子被吸走生命，Boss 回血 | 5 |
| `Summon` | 召唤影卫 | 召唤影卫增援，先清理它们 | 6 |
| `Enrage` | 狂暴怒吼 | 进入二阶段，技能冷却大幅缩短 | 4 |

每个技能都有**蓄力预警**（`Windup` 期间画出危险区域，玩家可以挪棋子）与**命中表现**（序列动画 + 冲击波 + 飘字），全部由 `BossSkillFxView` 走对象池播放，不克隆材质、不逐帧分配。

## 🚀 快速开始

### 环境要求

- **Unity**：**2022.3.15f1c1**（Unity 中国版）。用其它版本打开会改写 `ProjectVersion.txt` / 包源 / 包锁定，污染协同；
- **平台**：Windows 编辑器下开发验证；**Android / iOS / WebGL / 小游戏**为框架支持目标，尚未做真机回归；
- **可选**：`.NET 8 SDK`（跑离线断言）· `Python 3`（跑业务流程自动化）· `com.coplaydev.unity-mcp`（AI 助手 ↔ 编辑器桥接）。

### 三步跑起来

1. 克隆仓库：
   ```bash
   git clone https://github.com/Yokino337088/RevGame.git
   ```
2. 用 **Unity Hub** 以 **2022.3.15f1c1** 打开 `RevGame_Client/UnityProject/RevGame.Unity`（首次导入几分钟）；
3. 打开 `Assets/Scenes/StartScene.unity` 直接 Play —— 正式入口是 `StartScene`（首页 / 选关 / 编队 / 强化），战斗切到 `GameScene`，跨场景只由 `GameFlow` 经 `RevScene` 异步交接。

> ⚠️ `Packages/manifest.json` 里 `com.coplaydev.unity-mcp` 是 **git 依赖**，需要本机有可用的 `git`；
> 网络受限导致解析失败时，可在 Package Manager 里重试，或临时移除该行（不影响游戏本体）。

> 📌 框架代码是**从 `package` 分支装入 `Assets/Revolution` 的快照**：改框架请回 [Revolution](https://github.com/Yokino337088/Revolution) 仓库改 `main`，再重新导出；本仓库不反向同步框架。

## 🧩 框架能力落到哪了

| 框架能力 | 在本项目里被怎么验证 |
|---|---|
| `RevUI` + `RevUIPanel` | 局外整条线：关卡选择 / 编队 / 结算 / 强化选择，全部走"一个面板 = 一个脚本 + 职责钩子" |
| `RevScope` + `RevLogic` + `RevLightFSM` | 一局游戏的 准备 → 战斗 → 结算 → 退出 四段流程，域内业务与状态机联动 |
| `RevTimer`（四个时间域） | 战斗推进节拍、Boss 回合、UI 倒计时（unscaled） |
| `RevEvent` | 棋子死亡、气归零、连 / 断标记、战斗事件（随机"帮手"）等广播 |
| `RevPool` / `RevRefPool` | 棋子、血条、飘字、格子、Boss 特效的高频创建 / 回收 |
| `RevResManager` | 美术、棋子数据、配置表的逻辑路径加载与引用计数释放 |
| `RevTask` | 回合推进、结算动画的异步编排（不引第三方异步库） |
| `RevSequence` | 回合内的表现序列：落子 → 移动 → 攻击 → 结算 |
| `RevSound` | 落子、锁死、击杀、Boss 出场音效与 BGM |
| `RevDataTableManager` | 14 张数据表（棋子 / 星级 / 章节 / Boss 技能 / 增益 / 奖励 …） |
| `RevGM`（受控业务指令） | 关键业务留 GM 查询 / 动作 / 拒绝接口，供自动化与 MCP 验收 |
| 编辑器工具链 | RevAB 打包、导表、UI 生成器、GM 面板在真实项目里跑一遍 |
| Sprite Atlas + AB | 图集通过 `GameSpriteAtlasService` + `RevResManager` 绑定，验收过真实 AB 冷启动与重复进出战斗 |

> 结论会以"哪些能力直接可用 / 哪些需要补 / 哪些设计需要改"的形式反馈回框架仓库。

## 🧪 三层验证体系

这个项目不只"能跑"，每一步都要能**复现**。验证分三层，越往下越接近玩家：

| 层 | 怎么跑 | 覆盖什么 | 当前结果 |
|---|---|---|---|
| 🧠 **离线断言**（不开 Unity） | `dotnet run -c Release` | 战斗结算、气 / 眼 / 围困、Boss 九技能段数与副作用、公平反馈缓冲 | **182 个场景 / 4 171 条断言全通过** |
| 🎬 **PlayMode 场景**（真 Unity） | `rev.cmd scenario run <场景.json>` | 真实 Play、真实界面点击、真实 AB / 资源，可连拍截图取证 | Boss 弹幕在 1920×1080 / 2560×1080 / 1440×1080 / 1080×2340 四种画幅取证通过 |
| 🕹 **业务流程自动化** | `python RevGame_Client/Tests/BusinessFlowAutomation/run_flow.py …` | 首页 → 选关 → 落牌 → 开战 → 真实结算 → 章节奖励 → 下一章 → 回首页 | 第一章 **64/64 步、11 轮真实胜利、158 次指针点击、Unity 错误 0** |

- 🧠 离线断言工程：[`RevGame_Client/Tests/CombatAttackRegression/`](RevGame_Client/Tests/CombatAttackRegression/README.md)
- 🕹 业务流程执行器：[`RevGame_Client/Tests/BusinessFlowAutomation/`](RevGame_Client/Tests/BusinessFlowAutomation/README.md)
- 📊 各轮实测报告归档在 [`RevGame_Client/文档/进度同步/`](RevGame_Client/文档/进度同步/)
- 📏 战斗改动前先读 [`战斗性能优化规则`](RevGame_Client/文档/战斗性能优化规则/)：先用固定输入测量，再优化，不承诺未经实测的零 GC 或手机帧率。

> 说明：测试强化（如我方 +500%）只在 Editor / Development Build 生效，不改 Excel、存档、棋子定义与 `Time.timeScale`，报告里 `boostReleased=true` 才算真正结束。

## 📁 工程结构

```text
RevGame/
├─ RevGame_Client/                         客户端
│  ├─ UnityProject/RevGame.Unity/           Unity 工程（2022.3.15f1c1）
│  │  ├─ Assets/
│  │  │  ├─ Revolution/                    ★ 框架本体（package 分支快照，250 个 .cs）
│  │  │  ├─ GameScripts/                   业务代码（88 个 .cs）
│  │  │  │  ├─ Battle/   Board/   Pieces/    战斗流程 / 棋盘 / 棋子
│  │  │  │  ├─ Services/  Data/   Core/      业务服务 / 配置模型 / 结构常量
│  │  │  │  ├─ Flow/     Bootstrap/          GameFlow 状态与加载 / 启动入口与 GM 诊断
│  │  │  │  ├─ UI/       View/              面板与视图（含战斗特效）
│  │  │  │  ├─ Automation/                 业务自动化入口（计划 / 执行器 / 指针注入 / GM）
│  │  │  │  └─ Generated/                  图集与资源路径常量（工具生成，不手改）
│  │  │  ├─ GameRes/                       美术、图集、音效、Data/（导表产物）
│  │  │  ├─ GameShader/                    战斗特效 Shader（BossSkill 等）
│  │  │  ├─ Scenes/                        StartScene（首页 / 选关）· GameScene（战斗）
│  │  │  ├─ StreamingAssets/               平台 AB 产物（由打包工具生成）
│  │  │  └─ Editor/                        RevGame 自己的编辑器工具与打包串联
│  │  ├─ Revolution.Automation/             自动化客户端（`rev.cmd`：CLI / 场景 / 报告）
│  │  ├─ Packages/ · ProjectSettings/
│  ├─ Tests/
│  │  ├─ CombatAttackRegression/            离线断言工程（纯 C#，不开 Unity）
│  │  └─ BusinessFlowAutomation/            业务流程自动化（Python + JSON 计划）
│  ├─ 配置表/                               5 份 Excel（策划数值的唯一来源）
│  ├─ 策划案/                               玩法策划案、美术需求清单
│  ├─ 文档/                                 架构 / 进度同步 / 性能规则 / Bug 记录 / 素材授权
│  └─ 美术/                                 原版参考素材 + 可替换素材
├─ RevGame_Server/                          服务端（预留，暂空）
├─ tools/sync-framework.ps1                 框架快照同步脚本
└─ AGENTS.md                                协作者（含 AI）开工前必读的硬约定
```

> **技术栈**：Unity **2022.3.15f1c1** · 2D（未启用 URP）· uGUI + TextMeshPro · DOTween（仅表现层）· Revolution 框架 · Sprite Atlas V2 + AssetBundle。
> `Library/`、`Temp/`、`Logs/`、生成 `.csproj/.sln`、打包导出目录一律不入库（见 `.gitignore`）；美术资源、`Assets/`、`策划案/` 是**要**入库的。

## 📊 配置表

**策划数值一律进 Excel 走导表，不写死在 C#** —— 血攻、倍率、冷却、概率、奖励、章节参数、玩家可见文案里的数字，都在表里。

| Excel（`RevGame_Client/配置表/`） | 管什么 |
|---|---|
| `棋子数值配置.xlsx` | 9 种棋子的职业 / 走法 / 射程 / 星级成长 |
| `关卡与奖励配置.xlsx` | 6 章参数、回合结构、章节奖励、解锁节奏 |
| `增益与事件配置.xlsx` | 准备增益、战斗事件（随机"帮手"）、奖励强化 |
| `战斗弹幕配置.xlsx` | Boss 9 技能：权重 / 冷却 / 蓄力 / 段数 / 倍率 / 半径 |
| `战斗数值参数.xlsx` | 战斗节奏与平衡参数 |

导表后落在 `Assets/GameRes/Data/*.txt`（14 张表），运行时由 `RevDataTableManager` 按类型取；
启动时会做结构与引用校验（例如 `ChapterConfig` 行数必须等于 `Consts.ChapterCount`，不一致直接报出原因）。

## 📚 文档导航

| 想了解 | 去哪看 |
|---|---|
| 🎯 玩法设计（权威） | [玩法拓展策划案 v18](RevGame_Client/策划案/玩法拓展策划案.html) |
| 🏛 项目架构与框架讲解 | [RevGame 项目架构与 Revolution 框架讲解](RevGame_Client/文档/架构/RevGame项目架构与Revolution框架讲解.html) |
| 📏 战斗性能规则 | [战斗性能优化规则](RevGame_Client/文档/战斗性能优化规则/) |
| 🐞 踩过的坑 | [Bug 踩坑记录](RevGame_Client/文档/Bug踩坑记录/) |
| ✅ 实测报告 | [进度同步](RevGame_Client/文档/进度同步/)（含 Boss AI、第一章流程、性能） |
| 🤖 自动化怎么用 | [BossAI 自动化测试说明](RevGame_Client/文档/进度同步/BossAI自动化测试说明.md) · [业务流程自动化](RevGame_Client/Tests/BusinessFlowAutomation/README.md) |
| 🎨 美术需求 | [美术资源需求清单](RevGame_Client/策划案/美术资源需求清单.html) · [AI 生图提示词对照表](RevGame_Client/策划案/美术资源补全清单与AI生图提示词对照表.html) |
| ⚖️ 素材授权 | [第三方素材授权](RevGame_Client/文档/第三方素材授权/) |
| 🤝 开工约定 | [AGENTS.md](AGENTS.md) |
| 🧩 框架本体文档 | [Revolution 文档站](https://yokino337088.github.io/Revolution/) |

## 🛠 常用命令

```powershell
# 1) 离线断言（不开 Unity，秒级）
cd RevGame_Client/Tests/CombatAttackRegression; dotnet run -c Release

# 2) PlayMode 场景（真实 Unity，可连拍截图取证）
cd RevGame_Client/UnityProject/Revolution.Automation
.\rev.cmd --mode live --project "..\RevGame.Unity" --timeout 600 scenario run <场景.json> --report-dir <输出目录>

# 3) 业务流程自动化（首页 → 通章 → 回首页）
python RevGame_Client/Tests/BusinessFlowAutomation/run_flow.py RevGame_Client/Tests/BusinessFlowAutomation/plans/first-chapter.json
```

> 命令 2、3 都会生成 HTML 报告；`results/` 不入库，正式报告与必要原始证据归档到 `RevGame_Client/文档/进度同步/`。

## 📌 当前进度

- [x] 工程初始化（Unity 2022.3 + 2D 模板）
- [x] 框架本体装入 `Assets/Revolution`，编译 0 error
- [x] 玩法策划案 v18 定稿（傻瓜式：局内一个动作、局外一条线）
- [x] 美术素材入库 + Sprite Atlas / AB 分包验收
- [x] 第一版可玩闭环：首页 → 选关 → 准备 → 战斗 → 结算
- [x] 6 章 × 11 回合 + 章节奖励 + 强化选择
- [x] Boss 回合与 9 个技能（含全屏弹幕、蓄力预警、狂暴二阶段）
- [x] 离线断言体系（182 场景 / 4 171 条断言）
- [x] PlayMode 自动化 + 业务流程自动化（第一章已跑通 64/64 步）
- [ ] 手机端 / 小游戏 AB 冷启动与真机帧率回归
- [ ] 框架问题清单持续回灌 Revolution

## 🤝 贡献约定

欢迎提交 [Issue](https://github.com/Yokino337088/RevGame/issues) 和 Pull Request。动手前请先看 [AGENTS.md](AGENTS.md)，几条硬约定拎出来：

- 🧩 **业务只碰门面**（`RevUI` / `RevTimer` / `RevEvent` / `RevResManager` / `RevPool` / `RevScope` / `RevTask`），不碰框架 `Core/`、`Implementation/`；
- 🚫 **不在本仓库改框架**：`Assets/Revolution` 是快照，要改请回 Revolution 仓库；
- 🗂 **数值进 Excel**，不写死在 C#；文案里的数字也一样；
- 🧵 **异步只用 `RevTask`**，不引 `System.Threading.Tasks` 与第三方异步库；
- 📦 **资源只用逻辑路径**（`RevResPath.*` 或两段式 `Load`），不拼 `StreamingAssets` / `dataPath`；
- ✍️ **注释中文、方法体必带大括号**、方法签名的 `(` 与参数不换行；
- 📎 **`.meta` 必须一起提交**（存的是 GUID 与导入设置，缺了会引用断裂、设置回默认值）。

---

> 这是**验证性项目**：它的价值一半在游戏，一半在"框架在真实业务里到底行不行"的结论。
> 过程中发现的框架缺口、API 不好用的地方、文档没写清的地方，都会被记下来回灌到 [Revolution](https://github.com/Yokino337088/Revolution)。

**Made with ❤️ by RevGame**
