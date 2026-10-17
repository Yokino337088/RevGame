# RevGame · 围棋史莱姆

> **这个项目是被"造"出来验证 [Revolution](https://github.com/Yokino337088/Revolution) 框架可行性的** ——
> 用一款完整可玩的 2D 小游戏当"压力测试用例"，把框架从"Demo 能跑"推到"真实业务能跑"，再把暴露的问题回灌给框架。
> 游戏本身好玩是加分项，**框架的可行性结论才是这个仓库的第一交付物**。

📄 [玩法拓展策划案](RevGame_Client/策划案/玩法拓展策划案.html) · 🧩 [框架仓库 Revolution](https://github.com/Yokino337088/Revolution)

---

## 一、为什么要有这个项目

Revolution 是一个从零手写、模块化、内核尽量不依赖引擎的 Unity 框架，已有 20 个运行时模块（`RevUI` / `RevResManager` / `RevTimer` / `RevEvent` / `RevPool` / `RevScope` / `RevTask` / `RevSound` / `RevSequence` / `RevLightFSM` …）与配套的编辑器工具链。

但"每个模块都能单独跑 Demo"不等于"这套框架能撑起一个真实游戏"。真实项目会同时施加这些压力：

- **资源要真的加载、真的释放**（不是 Demo 里直读一个预制体）；
- **UI 要真的开开关关、传数据、做转场**（不是点一下弹个提示）；
- **流程要真的分阶段**（登录 / 大厅 / 战斗 / 结算 / 退出），而且要能被中断与回退；
- **时间、事件、对象池、异步**同时在一个场景里高频工作；
- **2D 玩法逻辑**本身就复杂（围棋的气 / 眼 / 连断），业务代码量会迅速逼近框架的边界。

所以需要一个**真项目**来回答一句话：**这套框架能不能在真实业务里撑住？**

`RevGame` 就是这个答案的载体。

## 二、验证目标：每项框架能力都落到具体玩法上

| 框架能力 | 在本项目里被怎么验证 |
|---|---|
| `RevUI` + `RevUIPanel` | 局外整条线：关卡选择 / 编队 / 结算 / 强化选择，全部走"一个面板 = 一个脚本 + 职责钩子" |
| `RevScope` + `RevLogic` + `RevLightFSM` | 一局游戏的 准备 → 战斗 → 结算 → 退出 四段流程，域内业务与状态机联动 |
| `RevTimer`（四个时间域） | 战斗 15–20 秒计时、Boss 回合不限时、UI 倒计时（unscaled） |
| `RevEvent` | 棋子死亡、气归零、连/断标记、战斗事件（随机"帮手"）等广播 |
| `RevPool` / `RevRefPool` | 棋子、血条、飘字、格子的高频创建/回收 |
| `RevResManager` | 美术资源、棋子数据、配置表的逻辑路径加载与引用计数释放 |
| `RevTask` | 回合推进、战斗曲线、结算动画的异步编排（不引第三方异步库） |
| `RevSequence` | 回合内的表现序列：落子 → 移动 → 攻击 → 结算 |
| `RevSound` | 落子、锁死、击杀、Boss 出场音效与 BGM |
| `RevDataTableManager` | 65 个棋子、4 职业、3 档的数值表 |
| `RevGM`（受控业务指令） | 关键业务留 GM 查询/动作接口，供后续 MCP / AI 自动验收 |
| 编辑器工具链 | 打包、导表、UI 生成器、GM 面板在真实项目里跑一遍 |

> 结论会以"哪些能力直接可用 / 哪些需要补 / 哪些设计需要改"的形式反馈回框架仓库。

## 三、游戏是什么：围棋史莱姆

一句话：**9×9 的围棋盘上打"史莱姆军团"战争 —— 你只负责落子，棋子自己会走、自己会打。**

| 项 | 设定 |
|---|---|
| 棋盘 | 9×9 / 81 格（沿用围棋） |
| 棋子 | 65 个 · 4 职业 · 3 档（★ → ★★ → ★★★） |
| 一局时长 | 约 40 分钟（6 章 × 每章 11 回合） |
| 一个回合 | **准备**（先看敌方布阵 → 落 2–4 张牌）→ **战斗**（15–20 秒全自动）→ **结算**（布局与状态全部保留） |
| Boss 回合 | 每章第 11 回合：战斗不限时，打死 / 围死 Boss 即通关本章 |
| 局内要记的事 | 2 件（围棋：围住就锁死 / 做两只眼锁不死；成长：同名三合一升星） |
| 已砍掉 | 行动力条、商店、装备 / 遗物 / 天赋树、天梯、打劫、多页签选人 |

**围棋内核被压成了四件套**：`连` / `断` / `围` / `眼` —— 气被填满就锁死（不能动、不能打、挨双倍伤害），做出两只眼就围不死；玩家不需要学规则，颜色与脚下小点会自动提示。

**结构参考《小丑牌》**：只取"一局之内分章推进 + 章节末 Boss 改规则"的骨架，不取商店与构筑内脏；另有战斗事件（随机天降的"帮手"）与"跳过关卡换一档强化"的取舍。

完整数值、回合结构、难度阶梯、Boss 设计与平衡取舍见 📄 **[玩法拓展策划案（精简版 v18）](RevGame_Client/策划案/玩法拓展策划案.html)**。

## 四、工程结构

```text
RevGame/
├─ RevGame_Client/                     # 客户端
│  ├─ UnityProject/RevGame.Unity/      # Unity 工程（2022.3.15f1c1）
│  │  ├─ Assets/
│  │  │  ├─ Revolution/               # ★ 框架本体（从 Revolution 的 package 分支装入，244 个 .cs）
│  │  │  └─ Scenes/                   # 场景（当前：SampleScene）
│  │  ├─ Packages/                    # 包清单（含 MCP for Unity 的 git 依赖）
│  │  └─ ProjectSettings/
│  ├─ 策划案/                          # 玩法策划案（html）
│  └─ 美术/                            # 美术资源
│     ├─ 原版美术资源/                  # 参考素材（≈190 MB）
│     └─ 替代/                         # 可替换 / 自制素材
└─ RevGame_Server/                     # 服务端（预留，暂空）
```

**技术栈**：Unity **2022.3.15f1c1** · 2D（URP 未启用）· uGUI + TextMeshPro · Revolution 框架 · `com.coplaydev.unity-mcp`（AI 助手 ↔ Unity 编辑器桥接，MCP 可选）。

## 五、怎么跑起来

1. 克隆仓库：
   ```bash
   git clone https://github.com/Yokino337088/RevGame.git
   ```
2. 用 **Unity Hub** 以 **2022.3.15f1c1** 打开 `RevGame_Client/UnityProject/RevGame.Unity`（版本不一致会触发重导入，尽量对齐）。
3. 首次打开会解析 `Packages/manifest.json`：
   - `com.coplaydev.unity-mcp` 是 **git 依赖**，需要本机有可用的 `git`；
   - 若网络受限导致解析失败，可在 Package Manager 里重试，或临时从清单移除该行（不影响游戏本体）。
4. 打开 `Assets/Scenes/SampleScene` 直接 Play；框架入口由启动脚本调用 `RevInitialize.Initialize()`（只调一次）。

> 框架代码是**从 `package` 分支装入 `Assets/Revolution` 的快照**：改框架请回 [Revolution](https://github.com/Yokino337088/Revolution) 仓库改 main，再重新导出；本仓库不反向同步框架。

## 六、美术资源说明

- `RevGame_Client/美术/原版美术资源/` —— 项目参考素材（字体、图标、UI 切片等，≈190 MB）。**需要随仓库分发**，以便直接跑起来；
- `RevGame_Client/美术/替代/` —— 可替换 / 自制素材，用于规避版权或风格统一；
- 单文件均 < 50 MB，满足 GitHub 单文件 100 MB 限制；
- Unity 的 `Library/`、`Temp/`、`Logs/`、生成 `.csproj/.sln`、打包产物等**一律不入库**（见 `.gitignore`）。

## 七、当前进度

- [x] 工程初始化（Unity 2022.3 + 2D 模板）
- [x] 框架本体装入 `Assets/Revolution`，编译 0 error
- [x] 玩法策划案 v18 定稿（傻瓜式：局内一个动作、局外一条线）
- [x] 美术素材入库
- [ ] 第一版可玩闭环：准备 → 战斗 → 结算（单章可通）
- [ ] 6 章 + Boss 回合 + 强化选择
- [ ] 框架问题清单回灌 Revolution

---

> 这是**验证性项目**：它的价值一半在游戏，一半在"框架在真实业务里到底行不行"的结论。
> 过程中发现的框架缺口、API 不好用的地方、文档没写清的地方，都会被记下来回灌到 [Revolution](https://github.com/Yokino337088/Revolution)。
