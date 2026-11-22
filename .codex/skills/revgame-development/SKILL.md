---
name: revgame-development
description: RevGame 玩法、UI、资源和验收的项目入口。按改动范围选择专题。
---

# RevGame 项目开发

以仓库根 [AGENTS.md](../../../AGENTS.md) 为准。需要找源码或同类入口时查 [项目地图](references/project-map.md)。只加载当前命中的专题。纯 Skill 或文档修改用链接、导出和相关工具测试验收。

| 任务 | 读取入口 |
| --- | --- |
| Revolution.Gal 章节背景、面板布局、剧情编辑、路线回归和自定义界面 | [Gal Skill](../revolution-gal/SKILL.md)。用户自有界面走 Gal 会话和宿主现有流程。项目既有 StoryBook 沿原入口。 |
| 玩法、UI、流程或战斗 | [框架 Skill](../revolution-framework/SKILL.md) 的命中专题及 [项目契约](references/project-contracts.md)。玩法意图查 [六章策划案](../../../RevGame_Client/策划案/玩法拓展策划案.html) 或 [无尽策划案](../../../RevGame_Client/策划案/无尽模式策划案_棋阵幸存者_2026-11-01.html)。 |
| 切场、域内异步、UI 命令、配置或存档的自动交付 | [框架契约](../revolution-framework/references/automatic-delivery.md) · [RevGame 实际接入](references/automatic-delivery.md) |
| Shader、材质、纹理、特效或渲染排错 | [Revolution-TA](../revolution-ta/SKILL.md)。按场景选 [Shader 索引](../revolution-ta/references/revgame-catalog.md)、现有制作配方及目标画面验收。 |
| 配置表、场景、Prefab、资源或 AB | [Unity 与资源迭代](references/unity-and-assets.md)。按需查 [编辑器 Skill](../revolution-unity-editor/SKILL.md) 和 [增量 AB 配方](../revolution-unity-editor/references/incremental-ab.md)。 |
| Play 验收、修 Bug、交付测试包 | [验收范围与交付](references/validation-and-delivery.md) 和命中的 [项目验收细则](references/project-acceptance.md)。缺陷闭环走 [自动化 Skill](../revolution-ai-automation/SKILL.md)。 |
| WebGL 面板首开、画布、首次特效、慢帧、飘字或背景遮挡 | [官方 WebGL 性能专题](../revolution-performance/references/webgl-performance.md) · [RevGame 入口与验收](references/web-and-gpu-performance.md)。按目标设备核对真实 AB、画面、帧间隔和反馈等待。 |
| WebGL 打包、网页打开、默认开放与浏览器排错 | [RevGame 网页版交付](references/webgl-delivery.md) · [框架 WebGL 参考](../revolution-framework/references/webgl-delivery.md) |
| 点 Play 后等待域重载、同一编辑器重复测试 | [编辑器快速 Play](references/validation-and-delivery.md#编辑器快速-play)。检查项目模式、首屏耗时和跨 Play 的资源及测试状态。 |
| 战斗、逐帧更新、密集 UI/特效、资源热路径或性能诊断 | [性能 Skill](../revolution-performance/SKILL.md) 的命中专题。从设计起核对 CPU、GPU、内存与缓存，再按 [RevGame 新手指南](../../../RevGame_Client/文档/战斗性能优化规则/RevGame性能优化实践_新手指南.html) 选同类案例。 |
| 攻坚、高技术含量任务的复盘、Wiki 目录与发布 | 使用 Revolution 或 RevGame 插件中的 [Revolution Wiki Skill](https://github.com/Yokino337088/Revolution/blob/main/Skills/revolution-wiki/SKILL.md)。按通用或目标平台归档，零基础释词，保留实测条件和原始出处。 |

导入或打包资源前先查已有资产、逻辑路径和包归属，只导入缺少的内容。各模块复用同一份正式资产和已有 AB。按 [Unity 与资源迭代](references/unity-and-assets.md) 核对 Sprite Atlas V2 归属和目标平台的实际产物。

写、改或审查代码先读 [代码与新手注释守则](references/code-and-comments.md)。类型与接口、字段、属性、方法和回调逐项写中文契约，关键实现解释原因、先后、边界和清理。交付前检查实际 diff 和完整的新文件，补齐缺失或过期的注释。

复杂改动按需查 [实现与交付细则](references/implementation-workflow.md)。交付说明实际效果、验证证据和框架边界。
