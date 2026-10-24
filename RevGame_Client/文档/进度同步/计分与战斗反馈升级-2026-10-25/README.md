# 计分与战斗反馈升级 · 2026-10-25

[打开图文验收报告](report.html) · [积分滚筒动图](Score-Reels.gif) · [完整战斗反馈动图](Score-Feedback.gif)

## 交付与框架可行性结论

Revolution现有门面支持本轮真实业务：BattleService统一伤害与计分，RevEvent传值快照，RevPool控制表现预算，RevResManager加载共享Shader预制体，RevTimer合并存档写入，RevScope退出时清理。项目侧补充积分规则/存储/HUD与幂等图集配置，未修改框架实现快照。

- 普攻暴击18%概率、1.8倍伤害，橙色“暴击！”；区域波动、能量巨大、炮火/断链和18种Boss技能分别读Excel颜色。文字用DOTween弹出、振动、上浮和淡出。
- 积分按用户选择采用“每回合独立 + 永久累计”。击杀基础100、暴击15，致死暴击相加后乘倍率。左侧显示本回合分、倍率、永久总分；得分飞星弧线汇聚、数字滚筒和面板脉冲。准备阶段隐藏避免覆盖手牌。
- 倍率=min(上限,(1+连续章节进度×0.12)×(1+额外存活玩家棋数×0.05))，默认上限20。棋子死亡会降低棋数部分，挑战重开不会继承上次进度倍率。
- 每章9个普通回合+第10回合Boss；6章60组初始敌阵验证，每章十轮布局不同。近战前排、远程后排，普通敌棋基础9、上限21、每5轮递增；Boss额外加入。
- 增援目标间隔依AI档位为2/1.75/1.5/1.25秒，窗口最多2子，逐颗验证预算/空位/气眼。执行对齐固定模拟步，不保证毫秒精度。
- 永久总分使用64位饱和相加，每2秒合并落盘，结算/离场/后台/退出立即刷盘。突然断电可能丢失最后一个保存周期。视觉测试有隔离区，不向真实玩家存档写入测试分数。

## 配置入口

`RevGame_Client/配置表/战斗数值参数.xlsx`：

| 参数 | 当前值 |
|---|---:|
| Round.NormalPerChapter | 9 |
| Enemy.CountBase / Enemy.CountMax / Enemy.CountRoundsPerExtra | 9 / 21 / 5 |
| Deploy.StartSeconds / Deploy.IntervalMinSeconds | 1.5 / 1 |
| Deploy.IntervalBaseSeconds / Deploy.IntervalPerLevelSeconds / Deploy.BatchCount | 2.25 / 0.25 / 2 |
| Damage.CriticalChance / Damage.CriticalMultiplier | 0.18 / 1.8 |
| Score.KillPoints / Score.CriticalPoints | 100 / 15 |
| Score.RoundGrowth / Score.PieceGrowth / Score.MultiplierCap | 0.12 / 0.05 / 20 |

`RevGame_Client/配置表/战斗弹幕配置.xlsx`：新增24行`Damage_*`，字段包括颜色HEX、文案、字号、持续时间和峰值缩放。保持项目现有“能量巨大”文案。修改Excel后执行真实动作`excel.export`导表，再以`game.background.build`构建对应PC/Android资源，不能只改生成txt。启动会校验缺行与范围。

工作簿保留检查：原有工作表和样式差异0；仅指定8处既有单元格变化（7数值+1说明），新增8数值行、24弹幕行。[原始核对](evidence/workbook-preservation.txt)

## 验证结果

| 范围 | 实际结果 | 原始证据 |
|---|---|---|
| 积分归属、去重、永久存档、10回合、60敌阵、节奏 | 1,089条断言通过 | [规则及冷启动](evidence/rules-cold-ui.json) |
| 真实暴击/击杀、隔离原生存储、可见数字、飞星、回收 | 222条Play断言通过 | [积分与反馈](evidence/score-feedback.json) |
| 120次合并、100目标控频、35条五职业连续弹道 | 2,897条Play断言通过 | [积分与反馈](evidence/score-feedback.json) |
| 冷启动真实AB、3次入场/返回、图像/六手牌/81格/过场 | 3,579条采样断言通过 | [冷启动记录](evidence/cold-ui.txt) |
| 原有规则与战术 | 675 + 747条通过，两组固定种子真实胜利10轮及失败重开 | [回归](evidence/regression.txt) |
| PC / Android | 各29包；302映射，6图集唯一覆盖186图；重复/循环/漏标/Android Shader错误0 | [PC](evidence/pc.json) / [Android](evidence/android.json) |
| 连续两次资源配置 | 全部门禁通过 | [幂等检查](evidence/atlas-repeat.json) |
| 最终状态 | 编译结束，Console无错误，已退出Play，StartScene clean | [状态](evidence/editor-final.json) |

断言数含逐帧重复采样，不等于独立测试案例。回归所在原流水线在成功完成回归后遇到桥接短暂离线，顶层false保留；只对实际完成的动作报告通过，后续配置构建有独立成功证据。

## 性能测量

环境：Windows，Unity 2022.3.15f1c1，i7-12700H / Intel Iris Xe，1920×1080，真实PC AB。

- 固定10棋初始布阵：1次预热+5次采样中位数1.447 → 0.102 ms。
- 同棋盘AI4补子：双方各5棋，每样本10次查询，中位数1.334 → 0.914 ms/次。
- 数字滚筒从23个逐列裁剪窗口合为3个整行窗口；本轮同压力夹具中间测量57批次，最终36批次。不是全项目GPU吞吐量结论。
- 最终180帧（30帧预热，不含截图）：帧间隔中位数10.394 ms、P95 14.190 ms，GPU中位数1.291 ms。
- 活动峰值35弹道/20文字/12积分飞星；压力结束活动反馈归零。内存采样前/末/回收后884.73/885.38/885.53 MiB，含Unity编辑器与保留池，不是GC.Alloc或泄漏判据。
- 中间CPU采样曾为0.246/1.540 ms，反映桌面负载波动；先前帧间隔样本6.745 ms，最终更长，不能将批次下降等同整帧提速。
- 颜色解析缓存、待播伤害struct、单Tween飘字、10Hz HUD刷新、动画合并和有界池均已落地。未启用未经测量的实例化开关来宣称优化成功。

[CPU基线](evidence/performance-before.txt) / [CPU最终](evidence/performance-after.txt) / [渲染最终](evidence/render-performance.txt)

**尚未验证：** 低端手机实机、独立Player、10分钟热稳定/降频、可靠GC.Alloc。当前Mono分配计数探针无效，不能宣称零GC或手机稳定帧率。报告完成离线链接/媒体检查，不将浏览器渲染计为通过。

## 发现与修复

1. CJK字体行高使早期滚筒Text生成0顶点：允许生成完整字形再裁剪，并检查真实可见数字，随后优化成整行裁剪。
2. 图集重复配置丢失包标记：保留成员不变的V2源文件，Importer保存完成后重新取引用写最终包标记。门禁未关闭，连续配置和两平台构建通过。早前meta写入失败的外部占用根因未确证。
3. 原有测试的11回合与确定伤害假设已更新为新配置/同暴击类别比较，并验证实际命中数和HP损失；没有使用强制胜利来绕过规则。

早期失败截图被后续同名采集覆盖，报告不补造。动态GIF取真实Unity24帧，100ms/帧用于观察；性能在不截图的单独阶段采样。
