# RevGame 玩家业务流程自动化

执行器当前放在 `Assets/GameScripts/Automation`。这里是命令行入口和可改的业务计划，复用现有 `UnityProject/Revolution.Automation` 客户端、作业队列、Scenario、GM 和报告，不另建通信服务器。

## 如何跑第一章

1. 用 Unity 2022.3.15f1c1 打开 `UnityProject/RevGame.Unity`，等待编译完成；本轮采用编辑器资源直读，不需要重新打 AB。
2. 确保项目现有 Revolution.Automation 编辑器桥已开启。
3. 从仓库根目录执行：

```powershell
python RevGame_Client/Tests/BusinessFlowAutomation/run_flow.py RevGame_Client/Tests/BusinessFlowAutomation/plans/first-chapter.json
```

默认只接管未运行的编辑器，从 `StartScene` 开始 Play，结束后只停止本命令启动的 Play，Unity 窗口保持打开。如果已由电脑插件/自己手动启动 Play，明确加 `--attach`；这种模式结束后保留原来的 Play 状态，避免工具擅自打断你的操作。

加 `--validate-only` 仅生成并校验框架 Scenario，不启动 Unity，不能当作通章通过。结果默认放 `results/日期-时间/`，也可用 `--output 目录` 指定。`results/` 忽略入库，正式报告及必要原始证据归档到项目文档中。

## 当前第一章计划

- 首页点击开始 → 选第一章 → 等待场景交割、准备增益发放与六张手牌就绪。
- 每轮点击六张真实手牌，再点击合法空格；逐张核对手牌和额度各减一、落点和棋种正确。
- 点击开战 → 等待真实 `BattleEnded` → 核对胜方 → 点击下一轮；包含普通第1～10轮与Boss第11轮。
- 选择章节奖励 → 验证第二章准备与六张牌 → 点击返回首页 → 验证场景、战斗域、Logic、棋盘视图和战斗面板已收回。

测试强化是用户指定的**额外+500%**：我方攻击伤害和攻击进度积累为原来6倍；当前没有独立防御字段，防御采用承受正伤害÷6，最少1，盾挡后的0保持0。强化仅编译在 Editor/Development Build 中，不改Excel、存档、棋子定义、Time.timeScale或敌方数值。完成、失败、取消、退出Play都归还强化租约，报告 `boostReleased=true` 才能判最终通过。

这份计划用于快速验证流程，不能代表普通难度、Boss九技能、AB冷启动或真机性能。若计划仅靠boost配置强化，删除 `boost` 即可走普通数值；新倍率计划还含属性设置GM，测普通数值时也必须移除该步骤；只改JSON不需要重编译C#。普通数值整章还会要求观察到敌人实际出手。

## 如何描述自己的业务

### 2026-10-22：攻击150%、末四轮不下牌

本次专用计划为 `plans/first-chapter-2026-10-22.json`：攻击额外+50%（普通攻击最终伤害×1.5，沿用整数取整），攻速和防御额外加成为0，生命不添加测试强化。前7轮各下6张，共42张；第8～11轮不执行落牌操作，`roundWonNoHand`逐轮核对实际落牌0张、开战手牌6张、结算清牌后0张。`EndBattle`原本就统一清牌，不能错误要求结算仍有6张。原有轻松模式、准备增益、援军、回血等正常游戏规则保留；不能将其误说成无辅助基础难度。

```powershell
python -X utf8 RevGame_Client/Tests/BusinessFlowAutomation/run_flow.py RevGame_Client/Tests/BusinessFlowAutomation/plans/first-chapter-2026-10-22.json
```

开发态报告新增每轮落牌数、开战/结算手牌数、双方存活名单和生命合计、实际承伤（另列有攻击者来源的部分）、Boss技能数及实际准备增益。人数差不等于死亡数，血量差不等于伤害量，援军、复活、治疗都可能改变总量。

计划可设 `captureScreenshots: true`，仅在Editor中用 `ScreenCapture.CaptureScreenshot` 请求正常帧末PNG，输出到每次独立的 `Library/RevolutionAutomation/BusinessFlowScreenshots/` 子目录。截图请求及当时阶段写入逐步证据，CLI报告后应核对文件与画面。它不暂停或手工推进PlayerLoop；本工程Unity MCP同步截图路径曾在作业中触发 `EditorApplication.Step` 递归错误，不应与正式作业并用。失败证据保留后再重跑，不能关闭错误门禁掩盖问题。

复制 `plans/first-chapter.json` 后修改名字和步骤。每份计划1～256步，总超时与单步超时均1～3600真实秒。

| 操作 | 用途 | 常用字段 |
| --- | --- | --- |
| `wait` | 条件未成立就等，超时明确失败 | screen、phase、chapter、round、hand、condition |
| `assert` | 立即核对状态，不满足就停 | 同wait |
| `click` | 模拟面板按钮的按下、隔帧松开/点击 | target，如 `HomePanel/btnPlay` |
| `deployHand` | 从第一个手牌槽开始逐张选牌、落子和验证 | count，1～6 |
| `gm` | 执行受控玩家业务指令，单独计数 | command、可选expected |

`screen`/`phase`使用项目枚举名；chapter/round为0表示忽略；hand为-1表示忽略，0表示确实没牌。`ready`检查准备增益与面板；`roundWon`检查真实开战/结算和交战证据；`chapterWon`逐轮检查1～11轮及章节解锁；`homeClean`检查回首页后的回收。

**擒王规则不能漏掉：**Boss主将气为0、锁死时允许未出手就获胜。执行器保存开战时的敌人数和主将编号，再在结算时核对同一主将死亡或锁死；不能把没有生成Boss、仅解锁下一章或进入下一轮当作通章。

## 输入与GM边界

输入适配器走 `EventSystem.RaycastAll` 的**最上层命中**，发送 pointerDown → 隔帧 pointerUp/click。它不会调用 `Button.onClick.Invoke`、私有回调或落子服务来绕过遮罩。棋盘通过真实 `Physics2DRaycaster` 命中，位置由实际控件和相机换算，支持项目1920×1080横屏布局。

这是 **Unity指针事件层模拟**，不是操作系统鼠标或真机触屏。电脑插件负责实际编辑器启动、画面检查等操作；不将两者混为一种覆盖。

工具GM：`RevGame/自动化开始`（Base64 UTF-8 JSON计划）、`自动化状态`、`自动化检查`、`自动化报告`、`自动化取消`、`自动化收尾`。后五条也使用 `RevGame/` 分组前缀。`自动化检查`会在失败时立即抛原因，避免把明确失败拖成半小时超时。

计划内允许打开选关、进入战斗、返回主界面、开始战斗、下一回合、刷新手牌、选奖励等既有玩家动作，以及下节精确列出的七条开发属性GM。强制胜利、跳关、清敌和任意脚本不在白名单。第一章默认计划走指针输入；2026-10-22新倍率计划另含一次明确的属性设置GM，单独计数。

## 开发属性GM（人工与AI共用）

进入Play后，在框架GM面板（Unity菜单 `Revolution.Tools/GM 指令面板`，Ctrl+Shift+G）执行：

```text
RevGame/属性设置 250 100 0 0
RevGame/加攻击 25
RevGame/加攻速 10
RevGame/加防御 50
RevGame/加血量 100
RevGame/属性查询
RevGame/属性清除
```

`属性设置`四个参数依次为攻击、攻速、等效防御、最大生命的**额外整数百分比**，全部必填；每项范围0～10000。0为不额外加成，250为原值×3.5。设置覆盖四项，重复执行结果相同，适合AI重试；`加…`累计到当前值，允许负增量下调，但最终仍须在范围内。输入错误整条拒绝，不会部分修改。这个上限用于防溢出和过量动作，属于开发工具边界。

攻击作用于真实普通攻击最终伤害，沿用整数截断；攻速作用于攻击进度，不改变移动冷却或Time.timeScale。游戏尚无独立防御字段，因此“等效防御+100%”表示正伤害÷2，最少1，护盾挡完的0仍为0。生命+100%表示最大生命×2，保持当前生命比例、不复活死棋，后落手牌、援军、换轮及升星均继承。原准备增益仍正常组合；清除只撤销开发强化。

AI可用官方Automation的 `gm.list` 查询注册，再通过 `gm.exec`（payload中的 `command` 字段）调用同一命令；桥层和GM层的成功值都要检查。也可在本业务计划中写：

```json
{"op":"gm","name":"设置并核对四项","command":"RevGame/属性设置 250 100 0 0","expected":"{\"attackBonusPercent\":250,\"attackSpeedBonusPercent\":100,\"defenseBonusPercent\":0,\"healthBonusPercent\":0}"}
```

七条命令成功时均返回以上四字段JSON（清除后全0）。手动GM在当前Play内保留；自动化开始时接管手动租约并采用计划参数，运行中的GM沿用执行器租约，完成、失败、取消与退出Play均清理本次自动化强化。不会在自动化结束后恢复旧手动值。报告保留最终配置，逐次变更另记在evidence。

本次重跑计划 `plans/first-chapter-2026-10-22-attack250-speed100.json`：攻击额外+250%（×3.5）、攻速额外+100%（×2）、防御/生命0；前7轮共42张，末4轮不出牌。新 `healthBonusPercent` 可选，旧计划未填写时默认0。Editor专项入口 `RevGame/验收/属性GM（Play）` 验证注册、原子校验、真实属性效果与生命周期。

## 生命周期和证据

`GameAutomationRunner`由Bootstrap注册为开发服务，跨StartScene/GameScene保留；用Unscaled `RevTimer`分帧推进，帧号屏障避免低帧率补发时把按下、释放和验证挤在同帧。owner统一为执行器；终止时只回收自己的计时器、订阅、手势及强化，不清整个框架。

先看 `response.json` 和 `scenario-result.json` 的 success/错误，再看 `business-report.json` 的 passed、完整步骤、零Unity错误和强化回收，不能只看Python退出或1/1迭代完成。报告还包含实际攻击、正伤害、Boss施法、敌人初始数量、擒王证据及逐次落牌记录。

本轮HTML人工验收报告及截图放在 `RevGame_Client/文档/进度同步/第一章流程自动化测试结果-2026-10-21.html`。
