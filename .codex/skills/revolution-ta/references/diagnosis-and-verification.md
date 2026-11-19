# 诊断与画面验收

先用现有场景复现，最少记下 Unity 版本、管线和质量档、Build Target、Editor 或设备图形 API、资源模式、主包来源、场景与触发步骤。保留首次错误与实拍，再选择只改一处能证伪的假设。

| 症状 | 优先区分哪两层 | 用什么证据 |
|---|---|---|
| 粉屏、材质 Shader 不支持 | 源 Shader 或 Pass 无法在目标环境编译，或包来源与当前环境不符 | 查真实 Shader 名、`shader.isSupported`、编译错误、实际 Pass、目标 API 与主包名称。直读和 AB 做同条件对照，检查 Shader 变体与共享包依赖 |
| 白图、UI 按钮只有底色 | 引用缺失，或 Sprite 有引用但图集像素未绑定 | 查 Sprite GUID 和名字、`packed`、`sprite.texture`、`Image.mainTexture`、Atlas 标签与订阅时序。查看首帧后的真实像素 |
| 串图、边缘黑白线、帧乱或倒置 | 纹理或 Atlas 矩形错，或混合、翻转、留边和导入与采样约定不一致 | 查材质纹理、Sprite `rect` 与 `textureRect`、旋转和紧密打包，核对 `_SpriteUV`、`_FrameUV` 等具体键及 C# 写入。黑白背景对照 |
| UI 过遮罩、层级错误或透明叠加过亮 | uGUI 的 Stencil、Clip 与 Canvas 顺序，或世界 Renderer 的队列、深度和混合 | 用 Frame Debugger 和实际遮挡场景查材质、Stencil 值、Render Queue、Sorting Layer、Order、ZTest、ZWrite 与描边或拖尾的相邻顺序 |
| 首次正常、第二次旧纹理或效果没清 | 池状态、临时材质、异步回调与资源引用 | 同条件退出再进。查 owner、属性块、事件、Tween、Mesh、RT、池和租约。等待实际完成，核对失败或晚到结果的去向 |
| 高负载掉帧、包和内存持续上升 | CPU 提交和重建、GPU 片元量、纹理或资源驻留 | 先固定负载对照，查渲染线程、GPU 时间、透明覆盖、SetPass、Batches、贴图页和资产租约。分别报告帧率、AB 磁盘字节、纹理数据量和内存 |

## 粉屏或丢失图像的检查顺序

1. 在实际渲染器上读回材质、Shader、已启用 Pass 和关键词、纹理、Sprite 图集与必要的属性值。源资源正确仍可能在运行时被旧 Prefab、自动生成器、属性块或图集绑定覆盖。
2. 确认 API、活动管线、点击进场时真正用的目标 AB 路径。Windows Editor 切到 Android Build Target 后仍运行桌面图形 API。在 RevGame Editor 真 AB 冷启动检查 PC 主包、`shared_shaders` 与对应章节包，Android Player 则检查设备上的 Android 主包和依赖。
3. 如果直读能画而 AB 失败，沿真实 `ResMap`、主包 Manifest、依赖 AssetBundle、Shader 所有者和运行时关键词组合追踪。构建前检查用于定位，实际设备或宿主第一次加载用于确认。给出明确定向的变体或缺包修复，合入原构建链。
4. 如果两种模式都不出图，依据首个 Shader 编译错误、管线标签、Pass 和图形能力定位。修复后用同一输入和平台复测。全量加入 Always Included Shaders、追加一个兜底材质或关闭画面就绪检查，都可能隐藏原本的归属或兼容问题。

## 性能与验收样本

- 固定分辨率、场景、镜头、安全区、构图、读条状态、同一次效果的归一化进度、可见数量、战斗种子及时间域。条件可控时先做同机对照。暂停或固定时钟抓静态帧，时序类截真实连拍。截图证明画面，玩法用状态断言。
- 先从目标分辨率的透明覆盖和实际 Pass 数判断 GPU 工作。片元着色优化可缩小作用区域、复用采样、将数据场离线制备或降低过密的全屏动画，维持构图、遮挡与视觉辨识。CPU 密集时查 Canvas 重建、对象和 Mesh 或 Material 实例数、绑定与批次提交。参考 [Revolution 性能测量](../../revolution-performance/references/measurement.md)。
- 未绑定前的占位、纹理颜色与 Alpha、低帧跳过表现帧、回池进度清零、屏幕边缘、亮暗背景、切换章节、关闭重入和异步取消都按实际需求覆盖。跨端差异标明 Editor 或 Player、API、AB 和设备。复验保留失败基线与同一断言，输出首个错误和收尾状态。
- Shader 修改验目标编译和 Console。带 C# 或程序集变更按项目的最后一次编译门禁。资源变更核对 AssetDatabase 保存的 Shader、材质和纹理引用、目标 Manifest 和 AB 冷启动。正式验收走项目真实入口并保留复现用的参数、图像和阶段数据。

### RevGame 可用的观测入口

先从实时注册表发现入口和当前参数。基线有 `material.inspect`、`game.background.audit`、`game.background.check`、`story.yaya-state`、`runtime.screenshot` 和 `runtime.logs`。填写 `material.inspect` 的 `path` 为工程内的真实材质路径。用 `story.yaya-state` 核对 Shader 支持性、背景纹理和绑定，再结合实拍。性能用项目已有的 `game.perf.*`，等待本次测量真正结束。互相依赖的测试步骤用 RevMCP `rev_run` 写断言与 `finally`，按 [验收工作流](../../revolution-ai-automation/SKILL.md) 清理。
