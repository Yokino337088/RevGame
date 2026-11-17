---
name: revolution-gal
description: 用自然语言、RevCLI 和 Unity 工作台制作、编辑与验收 Revolution.Gal 视觉小说。适用于节点增删改查、分支和存档、宿主接入、面板主题及舞台资源。项目原有 StoryBook 和普通游戏过场沿其现有入口。
---

# Revolution.Gal

1. 定位目标 Unity 工程、项目规则、已安装的 Revolution 核心和 Gal 扩展。读取目标工程的 RevAB 资源根及实际版本。RevGame 使用工程相邻的 RevCLI 和 RevMCP，项目本身的剧情由其业务入口管理。
2. 对在线编辑器显式指定 `--project <Unity 工程> --mode live`。先发现 `commands --live --query gal.` 或 RevMCP 的 `rev_discover(target="editor", query="gal.")`。用实际注册动作区分客户端过旧、Gal 未安装、编译失败和编辑器处于 Play。
3. 按请求选择参考。需要改生成器、公开契约或框架实现时，回到 Revolution 源仓库，使用该项目的编译和发布流程。

| 要做的事 | 先读 |
| --- | --- |
| 把口述要求落实为节点改动，编写 CLI 批处理或读工具回执 | [自然语言与 RevCLI](references/revcli.md) |
| 写新故事或调整图、分支、变量、台词、稳定 ID 和兼容修订 | [剧情数据与控制流](references/story-model.md) |
| 将 Gal 接入游戏、独立会话、RevUI、宿主信号、存档、热更新 | [宿主与 Revolution 核心](references/integration.md) |
| 生成和定制面板，配置主题、字体、图集、媒体、AB | [UI 与资源](references/ui-resources.md) |
| 验证编辑写回、真实 Play、资源冷启动，定位失败 | [验收与排错](references/verification.md) |

## 自然语言编辑

用户指明剧情时，先定位对应 `RevGalStory` 资产路径，用 `gal node.find` 通过关键词或精确 ID 读取节点和入边。标题可重复。以实际关系辨认所指节点，例如“开场后一句”要读取开场的 `Next`。当查询和上下文仍不足以区分多个候选时，列出候选和差异，让用户选定。

把用户要改的字段写入局部 JSON。单节点用 `node.add/update/remove`，插入对白并改线、调整多条出口等相互依赖改动用一次 `node.patch`。同一份文档可先 `--check-only`，再以查询得到的 `--expected-hash` 保存。摘要不同就重新查询并按当前资产重算目标。保存后按 ID 读回，结构改动从受影响入口试读。

- 剧情 ID、节点 ID、选项 ID、变量 ID 和收藏 ID 可能被档案、宿主代码或翻译使用。普通文案改动保留这些 ID 和其他字段。删除节点先看入边并确定用户想要的后续走向。
- 通过工作台或 Gal 导入、节点编辑动作写 SO。Unity 的未保存编辑由资产所有者处理，查询回执的 `dirty` 可辅助定位。
- 制作验收使用目标项目的真实加载模式。`story.simulate` 验证图与会话。面板、输入、资源和真实宿主信号通过 Editor Play 或任务要求的目标 Player 验证。
