# RevGame 工程实践索引

RevGame 是框架迭代的常驻业务样本。做 Revolution 功能、排障或自动化时，先定位职责相近的实际玩家路径。目标工程的版本、规则、平台、资源归属和用户授权决定最终做法。

## 用证据选择实践

1. 先读目标项目约定和已安装的包版本。嵌入 Unity 的框架快照可通过 `Assets/Revolution/PACKAGE_VERSION.txt` 定位来源。记录目标的渲染管线、资源模式和当前 Editor/Player 状态。
2. 优先打开本机已知的 RevGame checkout，读相应的 `AGENTS.md` 和下面的最小示例组。本机没有副本时读 [官方 RevGame 源码](https://github.com/Yokino337088/RevGame)。例子应能串出玩家入口、业务提交、状态观察和退出。表中资产路径均相对 `RevGame_Client/UnityProject/RevGame.Unity/Assets/`，其余路径明确标记仓库相对位置。
3. 提炼生命周期、数据所有权、失败语义与验证口径。联机协议、棋盘规则、服务端权威来源、UI 用途和资源分组按目标项目适配。框架 API 以目标源码、包版本和在线能力为准。历史报告用于理解固定条件和已见故障。
4. 记录“RevGame 来源及版本 → 采用契约 → 项目差异及原因 → 本轮有效证据”。从当前登记能力构造测试，旧样例中没出现的路径单独确认。找不到对应用例时用目标项目相近实现补足证据。

## 按工作入口选参考

| 任务 | 最小追踪范围 |
| --- | --- |
| 新工程启动、常驻服务、导表、冷首屏、场景失败恢复 | [启动、流程与长期状态](revgame-lifecycle.md#启动和配置) |
| 大厅入口、章节解锁、场景域、输入门禁、首屏进度、退场重入 | [启动、流程与长期状态](revgame-lifecycle.md#页面与场景交接) |
| 章节剧情、首次教程、Gal、鸦鸦、收藏书 | [故事、Gal 与内容生产](revgame-content.md#剧情与教学) |
| 六章战斗、准备、落牌、成长、结算、棋规、Boss 和帮手 | [战斗与无尽](revgame-combat.md#章节战斗) |
| 随机性、预判、AI、固定步、缓存稳定性 | [战斗与无尽](revgame-combat.md#权威逻辑和表现) |
| 无尽模式、ECS、施法、上限、插值和断点 | [战斗与无尽](revgame-combat.md#无尽模式) |
| 九路围棋本地、人机、局域网、服务器、重连、数子 | [PvP、原生决斗和 Agent](revgame-online.md#九路围棋) |
| 双席输入、C++ Core、快照、修正、显示与回放 | [PvP、原生决斗和 Agent](revgame-online.md#原生决斗) |
| AI 模型、建议、托管、快照版本和拒绝 | [PvP、原生决斗和 Agent](revgame-online.md#外部-agent) |
| Prefab、Part、HUD、动作许可、窗口安全区与跨屏 | [启动、流程与长期状态](revgame-lifecycle.md#ui-和显示设置) · [PvP、原生决斗和 Agent](revgame-online.md#九路围棋) |
| 存档、备份、音量和隔离设置 | [启动、流程与长期状态](revgame-lifecycle.md#存档设置与复位) |
| 编辑器制作、资源归属、图集、材质、Shader、声音 | [故事、Gal 与内容生产](revgame-content.md#资源与包怎么交付) |
| 自动化、编译、Play、设备、性能、产物和发布 | [交付、回归和流程证据](revgame-delivery.md) |

业务入口的框架选择和失败语义先读 [业务自动交付契约](automatic-delivery.md)。历史演进与已有对照样本继续查 [早期工程参考](revgame-engineering.md)。专题实践也从 Editor、性能、TA、Gal、自动化 Skill 的各自入口进入。
