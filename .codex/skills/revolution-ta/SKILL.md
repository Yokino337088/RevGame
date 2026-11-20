---
name: revolution-ta
description: 在 Unity 与 Revolution 项目中设计、实现、调试和验收 Shader、材质、纹理和渲染特效。用于 ShaderLab/HLSL/CG、Shader Graph、Sprite 图集 UV、UI 遮罩、材质与 AB 依赖、变体和粉屏、移动端渲染及 GPU 性能。RevGame 提供完整 Shader 实践与实际缺陷案例。
---

# Revolution-TA · Unity 技术美术

先读 [RevGame 资源与画面实践](../revolution-framework/references/revgame-content.md#资源与包怎么交付) 和命中的 [RevGame 具体案例](references/revgame-cases.md)。用目标工程真实 Shader、管线、导入设置、图集和实际输出选择方案。核对材质、帧、输入与池回收的边界。

从玩家看到的画面出发，把美术目标落实到 Shader、材质、纹理、调用者和目标平台。先做出可验证的最小改动，再用正式入口确认画面、资源加载与退出收尾。

1. 读目标项目的 `AGENTS.md` 和相关代码。确认 Unity 版本、实际渲染管线、活动质量档和目标设备图形 API。涉及 AB 时记录 Editor/Player、资源模式及实际主包来源。
2. 顺着 `Renderer`、`Graphic` 或自定义绘制入口查材质，核对 Shader 声明、纹理类型、Pass、混合方式、UV 空间、属性设置者和释放方。先找可以复用的正式资产和制作配方。
3. 按需求选下表的一到两个参考。写代码时遵循 [新手注释规范](../revolution-framework/references/code-style.md#3-面向新手的中文注释是交付门禁)，Shader 属性和方法旁写清单位、通道、坐标系、顺序与兼容边界。交付前逐项核对 diff。
4. 使用目标工程真实注册的工具。只读检查先发现 `material.inspect`、`asset.importer`、`atlas.inspect` 等动作。编辑器制作、分包构建和真实 Play 分别沿 [编辑器工作流](../revolution-unity-editor/SKILL.md) 和 [验收工作流](../revolution-ai-automation/SKILL.md) 执行。

| 当前工作 | 读取入口 |
|---|---|
| 新增 Shader、调整材质或复用一个视觉效果 | [Shader 与材质制作](references/shader-and-material.md) |
| RevGame 开发，查资产目录、真实消费者、图集与 AB 归属 | [RevGame 接入与交付](references/revgame-integration.md) |
| 找 RevGame 现有 Shader、关键参数和制作位置 | [RevGame Shader 全量索引](references/revgame-catalog.md) |
| 粉屏、白图、UV 串图、透明排序、漏变体或掉帧 | [诊断和画面验收](references/diagnosis-and-verification.md) |
| 复用已验证的解决办法或核对历史数据的适用范围 | [RevGame 案例](references/revgame-cases.md) |
| 查 Unity 开源项目的实现结构或跨管线迁移线索 | [GitHub 项目实践](references/open-source-patterns.md) |

- **材质是可交付资产。** 通过项目制作入口保存正式材质、Shader 引用和纹理设置。运行时只在当前渲染器或面板拥有的对象上改需要变化的参数。保存、移动和生成时遵循项目资产与 GUID 规则。
- **按实际用途选渲染路径。** Shader Graph 或手写 ShaderLab 都要对上实际管线与 Pass。临时遮罩、预览材质、画面渲染与玩法状态各有所有者。变体、排序与性能以目标环境的实测为准。
- **保住画面和资源寿命。** 对照同一姿势、镜头、分辨率和负载。检查首次落屏、半透明边缘、真实图集或材质绑定、暂停或切场回收。在 Editor 看到画面后，按需求复测目标 AB 或设备。

RevGame 的 Shader 名、材质目录、分包和预览入口是已核对的项目实践。其它项目沿用本 Skill 的判断步骤，通过所在仓库规则确定资源根、图形管线和可用命令。
