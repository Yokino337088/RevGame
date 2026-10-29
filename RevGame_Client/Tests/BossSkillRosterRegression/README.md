# 六章Boss技能池回归

2026-10-29：每章六个主动通用、四个专属；狂暴是额外血线机制。技能配置来自 `配置表/关卡与奖励配置.xlsx / BossSkillConfig`，新增伤害文案来自 `战斗弹幕配置.xlsx / BattleText`。

## 执行链

先从正式 StartScene 冷启动隔离进度，必须读取本次构建并发布的PC AB。通过 RevMCP 实时发现 `game.boss-impact.begin/state/cancel` 后，调用 `begin`，指定 `first=1,last=6,fromSkill=0,toSkill=60,extended=true`。

这里一次请求启动 Unity 内部的检查器。检查器逐章逐招重建高生命固定靶场，通过真实 `RevGame/Boss技能` GM 施放，验证段数、真实伤害事件、AudioSource实际播放、镜头反馈、格面Shader与双跳、根节点不动、最多8个特效组；逐招截图/连拍，章末经 GameFlow.ReturnHome 等待首页与离场回收。返回 started/running 不等于通过，必须持续读取 state，核对 status=passed、cases=60、errors=0、audioErrors=0、cleaned=true。

`start.plan.json` 只负责启动，因此工具成功只能证明启动接受。长测试的状态与业务断言由 Unity 检查器记录；取消必须调用 `game.boss-impact.cancel` 并等待其清理完成。测试结束后停止本次启动的Play、解除隔离进度。不要关闭进程后宣称清理完成。

## 离线规则

在仓库根目录运行：

```powershell
dotnet run --project RevGame_Client/Tests/CombatAttackRegression/CombatAttackRegression.csproj -- RevGame_Client/UnityProject/RevGame.Unity/Assets/GameRes/Data boss-expansion
dotnet run --project RevGame_Client/Tests/CombatAttackRegression/CombatAttackRegression.csproj -- RevGame_Client/UnityProject/RevGame.Unity/Assets/GameRes/Data boss-support
dotnet run --project RevGame_Client/Tests/CombatAttackRegression/CombatAttackRegression.csproj -- RevGame_Client/UnityProject/RevGame.Unity/Assets/GameRes/Data boss
dotnet run --project RevGame_Client/Tests/CombatAttackRegression/CombatAttackRegression.csproj -- RevGame_Client/UnityProject/RevGame.Unity/Assets/GameRes/Data boss-patterns
```

前两项分别覆盖31新增技能的计划/FSM/伤害/到期/隔离及六章自然轮换，和300秒配额/独立冷却/单棋保护。后两项保留原Boss生命周期、狙击躲避、全屏弹幕与原九种格形检查。

高生命靶场只验证技能实现，不证明正常胜率或性能。正常配置平衡使用已有 `helper-balance` 服务回放，图形长程、Android与手机性能单独报告。

原始证据在 `.codex/work/boss-roster-2026-10-29/` 和检查器返回的 `.codex/work/boss-support-2026-10-29/impact-play/`；HTML、必要截图与GIF在 `文档/数值平衡/2026年10月29日Boss六通用四专属扩展/`。不生成ZIP。
