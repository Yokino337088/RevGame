# RevGame 玩家业务流程自动化

执行器按项目要求放在 `Assets/GameScripts/Animation`。这里是命令行入口和可改的业务计划，复用现有 `UnityProject/Revolution.Automation` 客户端、作业队列、Scenario、GM 和报告，不另建通信服务器。

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

这份计划用于快速验证流程，不能代表普通难度、Boss九技能、AB冷启动或真机性能。将计划中的 `boost` 整段删掉即可走普通数值；只改JSON不需要重编译C#。普通数值整章还会要求观察到敌人实际出手。

## 如何描述自己的业务

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

计划内仅允许打开选关、进入战斗、返回主界面、开始战斗、下一回合、刷新手牌、选奖励等既有玩家动作。强制胜利、跳关、清敌、任意脚本和自由改属性均不在白名单。第一章默认计划全部走指针输入，GM只负责启动、观察和收尾。

## 生命周期和证据

`GameAutomationRunner`由Bootstrap注册为开发服务，跨StartScene/GameScene保留；用Unscaled `RevTimer`分帧推进，帧号屏障避免低帧率补发时把按下、释放和验证挤在同帧。owner统一为执行器；终止时只回收自己的计时器、订阅、手势及强化，不清整个框架。

先看 `response.json` 和 `scenario-result.json` 的 success/错误，再看 `business-report.json` 的 passed、完整步骤、零Unity错误和强化回收，不能只看Python退出或1/1迭代完成。报告还包含实际攻击、正伤害、Boss施法、敌人初始数量、擒王证据及逐次落牌记录。

本轮HTML人工验收报告及截图放在 `RevGame_Client/文档/进度同步/第一章流程自动化测试结果-2026-10-21.html`。
