# Shader 与材质制作

## 先确定画面的契约

从同类场景截取一张基线图，记下镜头、目标分辨率、当前帧或效果进度、参与画面的 Renderer 或 UI 节点。说明想改哪一处轮廓、色彩、材质运动或层级。临时 Shader Preview 用于挑参数，验收在玩家实际进入的场景和光照下做。

贴图的角色先定清。颜色贴图通常按 sRGB 颜色语义处理。距离场、噪声、法线或 RGB 遮罩按数据语义导入，决定能否有损压缩。明确取哪个通道、有无 Alpha、是否留 Mipmap、寻址模式和边缘采样。不透明全屏 UV 输入、Sprite Atlas 的 UV 及用于局部图案的 0～1 UV 分别写明。

| 画面要做什么 | 先决定 |
|---|---|
| 不透明棋盘、环境底面 | 是否写深度，棋盘格与场景使用世界坐标还是局部坐标，安全区和镜头缩放下棋格是否对齐 |
| 透明棋子、立绘、序列帧 | Sprite Atlas 的采样矩形、色调和 Alpha 约定。统一 Sprite 边框与枢轴，检查透明排序、反转、完整全身和周围图片串色 |
| 弹道、拖尾、冲击、治疗 | 每种形状和进度由表现层参数驱动。确定美术寿命、遮挡与叠加顺序，复用纹理、材质和池，预算按有效入屏面积测量 |
| UI、书页、对话、全屏过场 | 保留 Graphic 传入的色彩、透明度、Stencil、ClipRect、UI Alpha Clip 和现有层级。调整发光或扭曲时验 Mask、RectMask2D、CanvasGroup、九宫格和不同宽高比 |
| 动态背景、区域遮罩 | 明确背景全 UV 与局部区域。将只需变化的区域用遮罩或数据场标出，固定轮廓与构图，按目标设备预算控制采样和片元计算 |
| 水面、后处理、GPU 粒子或模拟 | 查代码以外的深度、颜色缓存、相机事件、Renderer Feature、GPU 缓冲和平台能力。先做可撤回的单独样例，确认实现成本再接生产 |

## 选管线、Pass 和属性

- 以 Graphics Settings、当前 Quality 档的 Render Pipeline Asset、相机设置、包版本和实拍为准。Built-in 示例常用 `UnityCG.cginc` 和 `CGPROGRAM`。URP 的 `Core.hlsl`、`RenderPipeline`、`LightMode` 和 `UnityPerMaterial` 按当前 URP 版本与 Renderer 查证。HDRP 按它的 Pass 和缓冲要求单独配置。迁移 Shader Graph 时核对生成 Target、材质 Inspector 和各目标的兼容节点。
- 先确定能表达画面的最少 Pass。根据角色选择 Queue、Cull、ZTest、ZWrite、ColorMask 和混合。直通 Alpha 常用 `Blend SrcAlpha OneMinusSrcAlpha`，预乘颜色匹配 `Blend One OneMinusSrcAlpha` 与预乘后的 RGB。纯加色光、遮罩剪裁和深度写入分别按需要处理。对同一 PNG 在黑、白和复杂背景上看软边。
- 对每项材质参数记录精确 Shader 名和键名、类型、范围和单位、设置者、更新时机、默认值、空纹理或零值含义。约定 `_Progress` 的语义是一次表现的归一化时间时，持续秒数由表现拥有者管理。影响玩法的冷却、伤害与判定沿配置和业务规则处理。场景暂停、跳过、低帧和清零后都要能回到合法画面。
- 纹理采样 UV 和图案 UV 各自使用。写明矩形打包是 `(origin, size)` 还是 `(min, max)`，由实际 Sprite、Mesh 或调用者提供。全幅 UV 效果遇到 Atlas 的旋转、紧密打包、留边或子帧缩放时，先核对输入契约再设计采样。含多次纹理读取的效果先数实际采样和复用机会。
- 用所需的 Render Type、Pass、平台分支和关键词组合建立检查范围。只添加会实际使用的变体。区分 `shader_feature` 与 `multi_compile` 的保留条件，特别检查运行时才开启的关键词是否被裁剪。Shader Variant Collection 或预热适用于已确认需要命中的组合，核对目标 Player 和 AB 产物。 [Unity Shader 变体裁剪](https://docs.unity3d.com/2022.3/Documentation/Manual/shader-variant-stripping.html)

## 材质装配与所有权

1. 先读现有 Shader、材质与实际消费者。项目已有同用途的共享材质时，确认属性和模式能否复用，随后复制必要的美术预设，保持旧 GUID、引用和着色器属性契约。Editor 写入使用项目已有配方或真实发现的动作，写后读回 Shader、Render Queue、关键词和属性值。
2. 不变参数保存在共享材质。每对象的颜色、进度、UV 和短期纹理在合适的 Renderer 路径使用复用的 `MaterialPropertyBlock`。同一 Renderer 的多来源属性由同一写入点合并，回池清理自己的覆盖。uGUI 也保留 Mask 产生派生材质的行为，需要独立材质时由面板持有并释放。
3. 避免逐帧获取 `renderer.material` 触发实例化，也避免直接改共享材质影响别处。确需实例化 Material、Mesh、RenderTexture 或 GPU 缓冲时，在进入或重建时分配，配对保管、归还或销毁。尺寸变化和晚到回调不能占用已经退出的场景或新一轮的资源。
4. 根据实际渲染管线决定批处理。Built-in 的绘制合批、URP 的 SRP Batcher、GPU Instancing、uGUI CanvasBatch 和手工 Mesh 合批有不同条件。URP 的 SRP Batcher 需检查材质 CBUFFER 和 Renderer 的兼容条件，MPB 会影响该路径。用 Frame Debugger 找出实际的分批原因。 [Unity SRP Batcher](https://docs.unity3d.com/2022.3/Documentation/Manual/SRPBatcher.html)

## 文件与注释

Shader 的属性应一眼看出用途。解释会影响兼容的坐标空间、Alpha、纹理通道与导入要求，方法内部在 UV 还原、遮罩、深度测试、降级或合成步骤旁标明为什么需要该顺序。共享 include 只承接真正共同的计算，并标明由哪些 Pass 和材质调用。按 [Revolution 代码与注释](../../revolution-framework/references/code-style.md#3-面向新手的中文注释是交付门禁) 检查源码和生成器模板。

制作完成后沿 [诊断和画面验收](diagnosis-and-verification.md) 核对运行效果、依赖和退出，再决定是否扩展效果范围。
