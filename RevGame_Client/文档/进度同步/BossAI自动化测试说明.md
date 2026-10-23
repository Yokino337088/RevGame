# Boss AI 自动化测试说明（交给 Codex / MCP 执行）

> 目标：用 Revolution.Automation 在已打开的 Unity 编辑器里，自动验收 Boss 主将的 9 个技能状态、
> 三段节奏（蓄力 → 命中 → 收招）、目标规则、瞬移落点与状态机回收，并给出可复现的通过/失败结论。
>
> 本文只描述**怎么测**。规则实现与代码位置见 `文档/架构/RevGame项目架构与Revolution框架讲解.html` 第 11 节。

---

## 1. 被测对象

| 项目 | 内容 |
|---|---|
| Boss 状态机 | `Assets/GameScripts/Battle/AI/BossAI.cs`、`BossStates.cs`、`BossTactics.cs` |
| 服务宿主 | `Assets/GameScripts/Services/Battle/AI/BattleService.BossAI.cs` |
| 表现 | `View/Combat/Effects/BossSkillFxView.cs`、`View/Board/BoardView.BossSkills.cs`、`GameShader/Combat/BossSkill.shader` |
| 配置 | Excel `BossSkillConfig`（9 行）、`BalanceRule` 的 `Boss.*`（7 行） |
| 离线回归 | `Tests/CombatAttackRegression`（含 `BossRegression.cs`，当前 181 场景 / 4167 断言） |

---

## 2. 必须先完成的准备

1. **Unity 编辑器已打开本工程**：`RevGame_Client\UnityProject\RevGame.Unity`
   （桥接文件在 `Library/RevolutionAutomation/`，编辑器进程存活才有 `endpoint.json`）。
2. **等脚本编译完成**：本轮新增了 GM 指令，必须让 Unity 重新编译后再进 Play，否则会提示“没有这条命令”。
3. 所有命令从 `UnityProject` 目录执行：

```powershell
cd RevGame_Client\UnityProject

# 自检：工程路径 / 会话 / 桥接
python Revolution.Automation\rev.py --project RevGame.Unity doctor

# 状态：playing / compiling
python Revolution.Automation\rev.py --project RevGame.Unity --mode live editor status
```

> `rev.cmd` 与 `rev.py` 等价；`rev.cmd` 依赖 `py -3`，无 Python Launcher 时用 `python rev.py`。

---

## 3. 新增的自动化 GM 指令（本次为测试补充）

这些指令只在 `UNITY_EDITOR || DEVELOPMENT_BUILD` 注册，正式包不存在。

| 指令 | 参数 | 返回（精确字符串） | 作用 |
|---|---|---|---|
| `RevGame/Boss准备` | 章节，默认 1 | `loading` / `ready` | 幂等推进：主界面→选关→进入战斗域→加载 boss 夹具→发准备增益→开战。过场未结束时返回 `loading`。**注意：夹具固定为第 1 章第 11 回合；章节参数只决定进入哪个章节的战斗屏，且该章节必须已解锁** |
| `RevGame/Boss查询` | 无 | `idle` / `casting` / `dead` / `stopped` / `none` | Boss 当前状态，供 `until` / `assert` 精确比较 |
| `RevGame/Boss技能` | 技能编号 0~8 | `ok` | 让 Boss 立刻施放指定技能；无合法目标时抛错（场景判失败） |
| `RevGame/Boss清卫` | 无 | `ok:<数量>` | 移除 Boss 以外的敌棋，给贴身技能腾落点 |
| `RevGame/Boss靠近` | 距离，默认 1 | `ok` | 把我方最小的一颗存活棋子移到与 Boss 指定间隔的空格（1=贴身，2=隔一格） |
| `RevGame/Boss续航` | 无 | `ok` | 双方回满血，保证连续验收 9 个技能不因阵亡中断 |
| `RevGame/返回主界面` | 无 | 流程快照 | 收尾 |

技能编号：

| 0 | 1 | 2 | 3 | 4 | 5 | 6 | 7 | 8 |
|---|---|---|---|---|---|---|---|---|
| 横扫 | 狙击 | 轰炸 | 弹幕 | 瞬移 | 激光 | 汲取 | 召唤 | 狂暴 |

> ⚠️ **不要用 PowerShell 直接把中文指令名传给 `gm exec`**：会变成乱码（如 `Boss鏌ヨ`）。
> 正确做法：把命令写进场景 JSON（UTF-8），或让 Codex 用 Python 客户端/进程参数数组调用。

---

## 4. 一键场景脚本

文件：`Revolution.Automation/examples/boss-ai.scenario.json`

结构：

```jsonc
{
  "name": "Boss AI 九技能自动化验收",
  "repeat": 1,
  "warmup": 0,
  "stopOnFailure": true,
  "failOnUnityError": true,
  "setup": [
    { "kind": "until", "command": "RevGame/Boss准备 1", "expected": "ready", "timeoutSeconds": 120, "intervalSeconds": 0.5 }
  ],
  "steps": [
    { "kind": "until", "command": "RevGame/Boss查询", "expected": "idle", "timeoutSeconds": 20 },
    { "kind": "gm", "command": "RevGame/Boss清卫" },
    // 技能 0：贴身
    { "kind": "gm", "command": "RevGame/Boss靠近 1" },
    { "kind": "gm", "command": "RevGame/Boss续航" },
    { "kind": "gm", "command": "RevGame/Boss技能 0" },
    { "kind": "until", "command": "RevGame/Boss查询", "expected": "idle", "timeoutSeconds": 20 },
    // 技能 1~8 重复同样的 4 步（靠近距离见下表）
    { "kind": "assert", "command": "RevGame/Boss查询", "expected": "idle" }
  ],
  "teardown": [{ "kind": "gm", "command": "RevGame/返回主界面" }]
}
```

### 每个技能该用哪个距离

| 技能 | 靠近距离 | 原因 |
|---|---|---|
| 0 横扫 | **1** | 范围是 Boss 身体外一圈，必须贴脸 |
| 1 狙击 | **2** | 目标必须与 Boss 间隔 ≥2，贴脸会被视为“交给横扫” |
| 2 轰炸 | **2** | 落点必须离 Boss ≥2 格 |
| 3 弹幕 | 1 | 圆形范围半径 3，贴身也命中 |
| 4 瞬移 | 2 | 需要附近有玩家棋子作为落点参考 |
| 5 激光 | 1 | 半径 3 |
| 6 汲取 | 1 | 锁定残血，任意距离 |
| 7 召唤 | 1 | 只需要空格 |
| 8 狂暴 | 1 | 无目标要求 |

### 运行

```powershell
# 1) 退出 Play（改过脚本必须先退出）
python Revolution.Automation\rev.py --project RevGame.Unity --mode live --timeout 120 editor stop

# 2) 刷新并等待编译完成（compiling=false 连续两次再继续）
python Revolution.Automation\rev.py --project RevGame.Unity --mode live --timeout 180 editor refresh
python Revolution.Automation\rev.py --project RevGame.Unity --mode live --timeout 60 editor status

# 3) 进入 Play
python Revolution.Automation\rev.py --project RevGame.Unity --mode live --timeout 300 editor play --scene "Assets/Scenes/StartScene.unity"

# 4) 确认指令已注册（应看到 Boss准备/查询/技能/清卫/靠近/续航）
python Revolution.Automation\rev.py --project RevGame.Unity --mode live --timeout 120 gm list

# 5) 跑场景
python Revolution.Automation\rev.py --project RevGame.Unity --mode live --timeout 600 `
  scenario run Revolution.Automation/examples/boss-ai.scenario.json `
  --report-dir Revolution.Automation/results/boss-ai
```

报告：`Revolution.Automation/results/boss-ai/report.html`（另有 `summary.json`、`iterations.csv`）。

---

## 5. 逐技能验收清单

每一步都按“预警 → 等待 → 命中 → 回到 idle”验收，除 GM 断言外还需人工/截图确认表现。

| # | 技能 | 预警期应看到 | 命中时应看到 | 关键判定 |
|---|---|---|---|---|
| 0 | 横扫震地 | Boss 周围方形红色危险区，刀光缓转蓄力 | 刀光转一圈、冲击波扩散、星芒 | 返回 `idle`；贴身棋子掉血 |
| 1 | 致命狙击 | 目标格出现准星并由大收紧，Boss→目标为红色激光瞄准线，较原版加粗 50% | 红色光束闪过，较原版加粗 50%，目标处星芒 | 预警后把目标移开应打空（见第 7 节扩展用例） |
| 2 | 三连轰炸 | 3×3 危险区 + 三个准星依次亮起 | 三发炸弹依次落下、火球、地面焦痕 | 命中事件 3 段；返回 `idle` |
| 3 | 弹幕风暴 | 以 Boss 为圆心的圆形危险区、中心聚光 | 16 发子弹等角扫射、冲击波 | 伤害只结算 1 次（不会因子弹多而重复扣血） |
| 4 | 瞬影爆破 | 落点出现传送门，原地冒烟 | Boss 一帧瞬移到落点并爆炸 | 棋盘占格与事件落点一致（离线测试已覆盖） |
| 5 | 天光回旋 | 圆形危险区 + 4 根黄色预瞄线旋转，线宽为原版 2 倍 | 6 道黄色激光环绕旋转，线宽为原版 2 倍，共 3 段伤害 | 命中事件 3 段；狂暴仍保留黄色识别色 |
| 6 | 灵魂汲取 | 两个目标脚下绿色漩涡 | 绿色光束连线、Boss 漩涡收缩回血 | Boss 血量上升 |
| 7 | 召唤影卫 | Boss 脚下法阵放大 | 传送门 + 星芒，附近出现小兵 | 敌方数量增加，小兵各自有状态机 |
| 8 | 狂暴怒吼 | Boss 身上血色漩涡加速 | 两道血色冲击波 | 命中事件 `Enraged=true`；此后冷却缩短 |

---

## 6. 通过标准

`scenario-result.json` 必须同时满足：

- `success = true`
- `completed = requested`（1/1）
- `errors` 为空
- `unityErrors = 0`
- 每个技能步骤后 `RevGame/Boss查询` 都回到 `idle`（`until` 未超时）
- 人工/截图确认：9 套预警与命中特效都出现，且不遮挡伤害数字与棋子血条

任一条不满足即失败，不要用重试掩盖；先看 `errors[0]` 与 `Library/RevolutionAutomation/jobs/<id>/unity-errors.jsonl`。

---

## 7. 建议补充的专项用例

在 `boss-ai.scenario.json` 的 `steps` 里追加（每条独立成场景更清晰）：

1. **躲避狙击**：`Boss靠近 2` → `Boss技能 1` → 立刻 `Boss靠近 1` 或换位 → 等 `idle` → 断言目标未掉血。
   需要新增一条查询 GM 返回目标血量，例如 `RevGame/Boss血量`（当前未实现，可由 Codex 按现有 GM 风格补充）。
2. **瞬移被堵**：先把我方棋子移到落点再施放瞬移，期望 Boss 原地爆炸而不是穿模。
3. **狂暴只触发一次**：连续两次把 Boss 打到 50% 以下（需要回血/扣血 GM），断言只出现一次 `Enrage`。
4. **章节难度**：当前 `LoadDevelopmentScenario("boss")` 固定第 1 章第 11 回合，直接传 `Boss准备 6` 只会尝试进入第 6 章并因未解锁而失败。
   要测第六章，需要先扩展夹具（例如给 `LoadDevelopmentScenario` 增加章节参数，或新增 `boss-6` 场景），再验证可用技能数 = 9、冷却更短、伤害更高。
5. **高压性能**：`repeat: 20`，观察 `frameP95Ms` 与 `unityErrors`，确认每轮回收 Boss 特效无泄漏。

---

## 8. 已知坑（按这个顺序排查）

| 现象 | 原因 | 处理 |
|---|---|---|
| `没有这条命令：「RevGame/Boss清卫」` | 改了脚本但 Unity 没重新编译就进了 Play | 退出 Play → `editor refresh` → 等 `compiling=false` → 再 Play → `gm list` 确认 |
| `画面装配超时：Battle` | GM 在过场未结束时开战，准备面板提前关闭，就绪屏障等不到 | `Boss准备` 已加 `IsTransitioning` 门槛；不要绕过它直接调 `开始战斗` |
| `Boss 周围 1 格外没有可放置的空格` | 敌方护卫围满 Boss，或我方只有 2×2 大棋 | 先 `Boss清卫`；`Boss靠近` 会优先选 1×1 小棋 |
| `Snipe/Bombard 当前没有合法目标` | 两者都要求与 Boss 间隔 ≥2 | 用 `Boss靠近 2` |
| `IOException ... being used by another process` | 场景运行中 `progress.json` 被 CLI 轮询与 Unity 写入撞车 | 重试一次；仍失败则用 `job <id>` 看最终 response，或改用 `pipeline` 减少外部轮询 |
| `gm exec` 中文变乱码 | PowerShell 编码 | 用场景 JSON 传参，或 Python 进程参数数组调用 |
| `InterruptedByReload` | 执行中改了脚本，域重载 | 不会自动重放，重新跑一次场景 |

---

## 9. 当前进展（避免重复踩坑）

已确认：

- `doctor` 通过，桥接存活，编辑器 2022.3.15f1c1，未在 Play。
- 新增 GM 指令已编译进运行期，`gm list` 能看到 `Boss准备 / 查询 / 技能 / 靠近 / 续航`。
- 场景能跑：`setup` 返回 `ready`，技能 0（横扫）执行并回到 `idle`。

尚未完成（需要 Codex 接着跑）：

- `Boss清卫` 是最后一次改动，**还没有在重新编译后的 Play 里确认注册**。
- 技能 1~8 尚未在真实 Play 里逐个跑通（此前因“贴脸放不下/距离不足”中断）。
- 所有特效的视觉尺寸、层级、颜色与性能都**没有**做过人工确认。

---

## 10. 离线回归（不依赖 Unity，可先跑）

```powershell
dotnet run --project RevGame_Client\Tests\CombatAttackRegression\CombatAttackRegression.csproj
```

期望输出：`CombatAttackRegression: 181 scenarios, 4167 assertions passed.`

它覆盖：章节解锁与数值换算、三段节奏、狙击可被躲开、9 技能逐个施放与段数、瞬移落点、狂暴一次、死亡终态与回收。
**它不能替代 Play 验收**：特效、动画时序、界面弹幕、实际手感只能进 Play 看。

---

## 11. 需要回报的内容

给 Codex 的交付格式：

1. 场景执行结果（`success`、`completed`、`errors`、`unityErrors`、`frameP95Ms`）；
2. 报告路径（HTML）；
3. 9 个技能各自的截图或录屏（至少预警与命中各一张）；
4. 若失败：失败步骤、`errors[0]` 原文、对应 `unity-errors.jsonl` 摘录；
5. 结论只写两类：**通过** / **未通过 + 根因**。不要含糊表述。
