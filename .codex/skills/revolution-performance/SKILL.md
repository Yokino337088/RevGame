---
name: revolution-performance
description: 编写、修改、审查 Revolution Unity 项目的战斗、AI、密集 UI/特效或其他性能敏感代码时使用，也用于瓶颈诊断和收益验收。检查 CPU、GPU、内存、GC、缓存和资源寿命。按 Windows Editor 与独立 Player、安卓真机或 WebGL 实包选择测法，复用 RevGame 的固定负载、分段计时和实测案例。
---

# Revolution 性能优化

先按 [RevGame 全链路索引](../revolution-framework/references/revgame-practice-map.md) 找对应阶段，再按 [性能与设备证据](../revolution-framework/references/revgame-delivery.md#性能与目标设备) 区分逻辑、呈现、加载/清理、Editor 与 Player。按目标项目的实际工作量、设备、采样与质量形成结论。

将需求落实为**定位瓶颈 → 减少重复工作 → 保持规则与画面 → 同输入复测 → 明确收益与剩余问题**。目标是当前项目与设备约束下可验证的最优实现；技能不提供未经测量的“极致”、零GC、固定加速倍数或帧率保证。

以 [RevGame 实践与来源](references/revgame-practice.md) 为例，从性能 Wiki 的通用、设备和平台索引找到可复用做法，读取对应实测条件。实际步长、容量、入口和通过标准读取目标项目。

固定负载可以通过 [RevMCP 性能批次](../revolution-framework/references/automatic-delivery.md#自动验收与性能对比) 预热、重复并比较回执。指标从每轮真实结果提取，按路径、工作量与设备口径核对。

## 写性能敏感代码时

先按真实调用入口估算工作量，明确每呈现帧、每逻辑步、每次变更或加载时分别做什么，最多处理多少实体、顶点、UI 项或资源。按下面四项检查本次路径，计量与改动直接相关的成本。

| 维度 | 实现前核对 | 有效指标 |
| --- | --- | --- |
| CPU | 按规则消除重复遍历、试算与脏写，分别检查模拟、事件处理、UI 重建和渲染提交。 | 局部耗时与次数、主线程与渲染线程时间、整帧 P95/P99 |
| GPU | 保持画质与透明顺序，检查实际绘制范围、覆盖面积、过绘、采样、批次和数据传输。 | 有效 GPU 帧时、批次与 SetPass、三角形、提交字节 |
| 内存与 GC | 分清包体、加载峰值、托管分配、原生资源和常驻容量。预热或复用有界的容器与对象，明确取消、回池和切场释放。 | 分配量、GC 慢帧、同资源根的驻留字节、冷启动与重入峰值 |
| 缓存 | 按用途确定键、输入版本、失效入口、所有者和上限，核对冷路径和版本失效后的成本。 | 命中与重算次数、冷热耗时、驻留大小、逐字段一致性 |

写战斗或密集运动表现时，先确认现有逻辑步和显示帧的职责。把伤害、占格、索敌、随机与结算留在现有业务入口，显示层根据提交后的只读样本更新外观与插值。按 [逻辑帧与表现帧](references/frame-presentation.md) 设计断点、显示批次及回归。普通 UI 和资源热点同样可按数据变化更新，并沿已有生命周期回收。

## 开工

1. 读目标项目AGENTS、性能规则和用户报告；核对实际Unity/渲染管线/框架快照/场景入口/资源模式/测试能力。实现遵循已安装 `revolution-framework` 的门面与架构；缺少技能时核对公开门面和项目同类源码，不假称已读。
2. 明确目标设备/平台/API/分辨率/场景/种子/对象数/画质/反馈预算/模型步长/预热/采样/目标P95。未连接目标机仍可做局部工作，**目标明确未验收**；不以开发机替代。
3. 先取得完整基线与业务/视觉/资源约束，再选热点。隔离测试存档/积分，保护用户原Play/场景/配置。没计时先做可复测夹具，不按“对象多”直接迁移ECS或关特效。
4. 先发现真实工具再调用；连续操作复用常驻RevMCP，少量临时编辑器操作可用安装的live CLI，Unity资产/构建/Play串行。实际测试使用可用的 `revolution-ai-automation` 契约，不能推定动作已注册；有界等待真正完成，不把accepted当通过。

## 按瓶颈读取

先读测量，按实际运行平台与症状选专题。已有瓶颈证据时，继续读相应实现与回归。

| 工作 | 读取 |
|---|---|
| 帧率波动、基线/CPU-GPU归因/目标机验收 | [测量](references/measurement.md) |
| 跨平台定位重复计算、透明过绘、加载峰值或退场增长 | [通用 CPU、GPU 与内存优化](references/common-performance.md)。沿同帧证据和资源所有权选实现 |
| Windows Editor 与独立包、核显与独显、主线程或送屏等待 | [Windows 性能优化](references/windows-performance.md)。固定同步、显示和图形条件，分开解释 Editor 与目标包 |
| 安卓冷启动、热态帧节奏、画质档或重复进出内存 | [安卓性能优化](references/android-performance.md)。分开核对验包、预览和目标真机证据 |
| WebGL 面板冷开、画布、首次特效、浏览器回调或战斗反馈 | [WebGL 定制性能优化](references/webgl-performance.md)。选首开、首次效果或持续战斗的窗口，按实际症状读取对应小节 |
| 射界/路径/棋群/AI试算、成员顺序、数据布局与并行交叉点 | [CPU与缓存](references/cpu-cache.md) |
| Boss 控制期限、锁定区域和批量格面预警 | [持续技能规则](references/cpu-cache.md#boss-持续技能用到期时间和静态覆盖分开减负) · [共享格面参数](references/gpu-rendering.md#同一次格面提示共用动画参数) |
| 固定步、位移跳格、快照插值、CPU 与 GPU 显示路径 | [逻辑帧与表现帧](references/frame-presentation.md) |
| HUD/Layout/TextMesh、飘字/弹道队列、Tween/闭包/回池 | [UI与反馈](references/ui-feedback.md) |
| GPU/透明过绘/合批、动态网格有效范围与提交、局部 Shader | [GPU与绘制](references/gpu-rendering.md) |
| 制作或修改 Shader、材质、纹理与视觉表现 | [Revolution-TA](../revolution-ta/SKILL.md) |
| 图集V2/AB/音频/状态按需、首屏白图/加载/内存增长 | [资源与生命周期](references/resources-lifecycle.md) |
| 回归/报告/百分比/失败/取消/安装与交付 | [验证](references/verification.md) |
| 找生产代码与历史来源、正负实验 | [RevGame实践](references/revgame-practice.md) |

## 实施边界

- **唯一真源**：不靠减少棋子/画质/预算、隐藏背景、暂停攻击或跳规则证明优化。占格、索引、版本、死亡/补子/换位/复活/气眼及同分事件顺序一起验证；预测和提交同公式，表现不二次结算。
- **缓存有完整契约**：键、全部影响输出的输入/版本、所有者、上限、重入、失效和退出清理。几何可缓存，HP威胁等评分实时；保容量不保无限历史对象。业务缓存命中率不冒充L1/L2计数。
- **独立工作区**：调用方缓冲/源盘私有深试盘，全可变字段覆盖，保持候选顺序；不共享全局可变草稿，不移动实棋试算，不将复用名单交异步持有。
- **公开门面与寿命**：RevPool/RevResManager/RevScope/RevTimer/RevEvent/RevTask，回池Kill(false)，先旧回调失效再回收，临时/池租约成对。业务项目不改框架快照内部；源框架改动属独立范围。
- **真实绘制成本**：共享材质/图集，避免renderer.material克隆；不破坏透明排序。instancing关键字、子Canvas、贴图少/Draw Call少都不能单独当性能成功。
- **新手可维护**：改代码按 [新手注释规范](../revolution-framework/references/code-style.md#3-面向新手的中文注释是交付门禁) 逐项注释并核对 diff。注明字段单位、所有者、上限和寿命，解释算法次序、缓存失效、深复制、字形重建、闭包与引用计数。

## 推进与完成

选择可归因的单项或紧密关联修改，记录热点/原浪费/假设；同输入复测并查规则/资源/视觉，再保留、修正或撤回。负实验保留完整轮次，不覆盖成最好样本；重大架构原型须证据支持且属于用户范围。

“尽可能优化”意味着持续处理仍有证据且可行动的热点。完成条件：授权改动完成、相关验证通过、剩余耗时/分配/未知及目标机未测明确。设备/有效计数缺失不编造，也不无限扩大重构；收益不成立不放宽验收。

## 汇总与交付

报告热点/具体文件与方法/实现/为何/验证/代价/剩余项，分开局部CPU、整帧、GPU、GC、纹理/进程内存和冷加载。耗时减少 `(前-后)/前`、速度倍数 `前/后`、FPS变化 `(后/前-1)` 只对有限且可比工作量计算，不能加局部百分比或跨设备推断。

可用离线辅助：

```text
python scripts/analyze-results.py local/result.json
python scripts/analyze-results.py before.json --after after.json
```

只读已完成 **RevGame game.perf JSON**，检查身份/时长/段数/负载/错误/清理，输出摘要、缓存率与观测差异；不运行Unity、不证明运行状态可比、不自动宣告优化成功。其它项目映射自己的真实契约，不能伪填字段。

默认在对话汇报。文件版测试报告按用户明确要求及指定路径生成，确需保留的机器证据放项目忽略目录。临时工单消费后立即永久删除。RevGame 的证据格式和清理要求见 [验证](references/verification.md)。提交、推送和安装遵循用户授权。

## 独立Player、IL2CPP与真机性能面板

涉及CLI打包到指定路径、Player启动/连接/退出、运行时GM/MCP、非开发自动化包或性能面板时，先读 [独立Player测试](references/player-testing.md)。复用现有RevRuntimeAutomation与RevMCP，项目负责隔离/业务负载，框架提供通用采样与构建；不要另造通信系统。
