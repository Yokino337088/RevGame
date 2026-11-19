# GitHub 技术美术项目参考

2026-11-19 核对 GitHub 仓库与具体源码。星数是当日快照，用来说明样本的社区关注度。设计和兼容性依据实现、版本和同条件测试。

| 项目 | 阅读到的做法 | 如何用在 Revolution |
|---|---|---|
| Unity Graphics · 3,029 ★ | URP Lit 分开定义 Forward、Depth、Shadow 等用途。Production Ready Lit 的 Shader Graph 样例把美术参数与 Target 绑定，说明样例与代码 Shader 的覆盖差异。 [URP Lit 源码](https://github.com/Unity-Technologies/Graphics/blob/0efc28ea742b1966b9dd9cc4489e58b986a13faf/Packages/com.unity.render-pipelines.universal/Shaders/Lit.shader) · [Shader Graph 样例](https://github.com/Unity-Technologies/Graphics/blob/0efc28ea742b1966b9dd9cc4489e58b986a13faf/Packages/com.unity.shadergraph/Documentation~/Shader-Graph-Sample-Production-Ready-Lit.md) | 用现行管线先列 Pass、属性和样例场景。URP 可参考材质参数组织与验证方式。RevGame 的 Built-in Shader 继续按自己的 include、Pass 和 Renderer 实现 |
| Unity Boat Attack · 2,808 ★ | `WaterSystemFeature` 分别入队水面效果和焦散 Pass，设置绘制阶段，提供观察中间结果的调试分支。 [WaterSystemFeature](https://github.com/Unity-Technologies/BoatAttack/blob/6acf5f773a440030489cb5e2b3c009fb5fe1e8ce/Packages/com.verasl.water-system/Scripts/Rendering/WaterSystemFeature.cs) | 把水面的形状、扰动、反射和焦散分项观察，并记录各自缓存、Pass 和图形 API 需求。RevGame 的二维湖泊可以先验区域遮罩和局部扰动，不必启用相机空间水面所需的资源 |
| keijiro/Skinner · 3,594 ★ | 预制 Mesh 模板承载顶点与索引，Source 负责将蒙皮变形数据送入 GPU，Source Viewer 可以单独检查，再让粒子、轨迹、Glitch 消费。 [Skinner 设计](https://github.com/keijiro/Skinner/tree/4f1b8656985d89c92f31d20e93ca1b1446c4640e#readme) | 把可检查的输入、固定容量的绘制拓扑和材质表现分开。大规模效果先在同负载对照顶点数、纹理或缓冲与画质。采用旧项目的 GPU 路径前核对当前硬件和管线 |
| keijiro/KinoGlitch · 2,829 ★ | DigitalGlitch 的组件保有历史帧纹理，按节奏更新采样帧，写参数后 Blit。Shader 处理画面，组件处理状态与相机时序。 [渲染入口](https://github.com/keijiro/KinoGlitch/blob/e102950361c260aba8967d55f14212b5a70f4cce/Assets/Kino/Glitch/DigitalGlitch.cs) · [像素处理](https://github.com/keijiro/KinoGlitch/blob/e102950361c260aba8967d55f14212b5a70f4cce/Assets/Kino/Glitch/Shader/DigitalGlitch.shader) | 动态材质保留明确的效果状态与输入缓存，优化时测更新频率、分辨率、全屏面积及释放。原例使用 Built-in 相机回调，SRP 要重新确定 Pass 插入点 |
| lilToon · 1,611 ★ | Editor 预设与输入优化分开，`lilOptimizer` 检查实际材质及 AnimationClip 的材质引用和属性动画。 [优化入口](https://github.com/lilxyzw/lilToon/blob/e698a2f791bd28dd1aabf227c74f14bedeca3587/Assets/lilToon/Editor/lilOptimizer.cs) · [材质预设](https://github.com/lilxyzw/lilToon/tree/e698a2f791bd28dd1aabf227c74f14bedeca3587/Assets/lilToon/Presets) | 归纳可维护的材质预设。移除纹理、属性或变体前，查 Prefab、动画、逻辑路径、C# 运行期写入、关键词和平台选项。项目静态资产之外还要覆盖实际动态入口 |

还可以用 [Cyanilux URP 代码模板](https://github.com/Cyanilux/URP_ShaderCodeTemplates/blob/457b34127e0f4431275ebb0926513da049ec294b/URP_DiffuseLitTemplate.shader) 对照最小 URP 光照 Pass、`UnityPerMaterial`、贴图和 AlphaClip 的关联。这份额外模板面向 URP v10，写入新项目之前先用该项目当前 URP 版本做最小画面和构建验证。

## 从参考落到项目

1. 先找到需要解决的表现问题与版本匹配的源码。写下要借鉴的是数据输入、参数面板、预览样例、批处理、Pass 边界还是验证方法。
2. 保留项目现用的管线、素材风格、资源所有权与玩家路径，写一个同资产、同场景的小改动。分别对照图集 UV、颜色、混合、兼容和资源释放。外部代码或素材确实需要引入时，先核对该项许可和依赖。
3. 用 [同条件验收](diagnosis-and-verification.md) 比较前后画面、目标帧成本和包体。外部项目的结果只支持它自己的测试条件。RevGame 的已测结果集中在 [实践案例](revgame-cases.md)。
