# WebGL 构建与交付

## 先定位故障所在的进程

| 证据 | 处理 |
| --- | --- |
| 浏览器报 `Build/*.data.br` 下载失败，页面地址为 `file://` | Unity 网页加载器在引擎启动前就被浏览器拦下。用项目的本地 HTTP 启动器、Unity Build And Run 或 HTTPS 静态站提供完整构建目录。项目模板可在加载器启动前提示正确入口 |
| HTTP 请求成功，仍报 Brotli、Wasm 解码或缺文件 | 查 Network 的实际响应。压缩文件带 `Content-Encoding: br`，`*.wasm.br` 带 `Content-Type: application/wasm`，`*.js.br` 带 JavaScript 类型。核对 404 响应体、跨域 CORS、重定向和缓存是否混用了两版产物 |
| Windows 的 Emscripten 报 `UnicodeDecodeError: 'gbk'`，随后 `llvm-nm` 相关 `IndexError` | 查 Unity 自带 Python、子进程环境和编译日志中的第一条异常。框架 `build.player` 对 Windows WebGL 构建设置 `PYTHONUTF8=1`，在 `finally` 恢复。直用其它构建入口时按进程传递，不改机器全局环境 |
| `build.target` 在内存工单报 `jobs/<id>/target-started` | 老版本把磁盘标记当成内存工单前提。同步用 `SessionState` 持有目标切换状态的版本，等重载后查同一工单和 `build.settings` |
| RevCLI 等待超时，Unity Editor 仍在构建 | 保留原工单。长构建用 `build player … --detach`，再以足够的全局 `--timeout` 调用 `job <id> --wait`。以 Unity 的最终 BuildReport、目标、输出和错误数为准 |
| Release 编译报 Editor、预览或 GM 专用方法缺失 | Player 需要的逻辑放入正式程序集和正式门面；工具注册与开发控制面保持宏保护。WebGL Release、Development 和 Editor 各用自己的编译参数 |

资料见 [Unity 2022.3 WebGL 部署](https://docs.unity3d.com/2022.3/Documentation/Manual/webgl-deploying.html)。

## 资源、下载和宿主隔离

- 用同一目标构建 Player 和 AB，核对 `StreamingAssets/WebGL/WebGL` 主包、ResMap、依赖及构建清单。Unity 会复制 StreamingAssets 的全部内容，发布目录需要明确保留该产品用到的平台目录与共享文件。
- 浏览器和小游戏使用异步 AB 加载。整个加载器 `ReleaseAll` 后，旧请求停止重试并在下一次帧检查时中止。已完成的旧包由原请求归还，单个消费者取消由其作用域清理。下载时限使用实时时钟，错误记录包名、传输结果和 HTTP 状态码。
- 可选 `RevHotUpdate` 沿相同的 WebGL 平台名与小游戏宏。网页清单、映射表和候选 AB 以真实帧等待，有取消、实时时限和 `Abort`，候选验证失败时保留生效版本。CDN 另核对 HTTPS、CORS、版本化 URL 与缓存策略。
- 使用 `UNITY_EDITOR` 区分运行宿主与活动构建目标。WebGL 与已接入小游戏的 Player 跳过后台文件日志与本机 `HttpListener`，在线桌面 Editor 仍可用 RevCLI。普通浏览器开发测试用下节页内桥。异步用 RevTask，线程、Socket 和文件 I/O 按平台实际能力分支。 [Unity 2022.3 WebGL 技术限制](https://docs.unity3d.com/2022.3/Documentation/Manual/webgl-technical-overview.html)

## 浏览器页内开发桥

对普通 WebGL Player，`DEVELOPMENT_BUILD` 或 `REV_AUTOMATION` 编译进页内桥。只对本地或获准的测试页面，在 `createUnityInstance` 前把目标 canvas 的 `data-rev-automation` 设为 `enabled`。正式包与小游戏沿各自的配置和入口。项目如需隔离存档，仍按它自己的启动配置设置，并用业务状态验证。 [Unity 与浏览器交互接口](https://docs.unity3d.com/2022.3/Documentation/Manual/webgl-interactingwithbrowserscripting.html)

```javascript
canvas.setAttribute("data-rev-automation", "enabled");
// 等 Unity 完成启动、canvas.revAutomation 出现，再发现本次的真实能力。
const receipt = await canvas.revAutomation.request("runtime.capabilities");
```

调用形式为 `await canvas.revAutomation.request(action, args, confirm)`。一次处理一个请求，参数契约、GM 完整名和确认均沿用 `RevRuntimeAutomation`。先查 `receipt.success`，再使用 `receipt.data`、实际业务状态和清理回执判定通过。跨帧操作可用 `canvas.revAutomation.cancel()` 取消当前等待。网页关闭时归还运行时操作与采样钩子。浏览器自动化通过本页上下文调用，桌面独立包继续用它自己的运行时凭据。

归因用 `canvas.revAutomation.frames.start(capacity, countTextures)`，稳定窗口结束后用 `frames.stop()`，中止走 `frames.cancel()`。当前采样依赖普通浏览器中实际使用浏览器动画帧的 Unity 主循环与目标画布的 WebGL 上下文。页面被隐藏、画布尺寸变化、上下文丢失或容量溢出时保留诊断字段，`valid` 为假。 [WebGL 帧时测量](../../revolution-performance/references/webgl-performance.md#计算帧间隔与回调耗时)

## 浏览器 AB 的冷开与依赖调度

- 把首开拆成下载依赖、解包/取出资产、实例装配、可见动画。用固定网络和新的浏览器配置，比对实际请求起止与面板可见的区间。带哈希的 Unity 缓存与 HTTP 缓存分开核对，同源版本化 URL 可以由浏览器复用。
- 普通 WebGL Player 的 AB 依赖按清单小批发出。每个资源一批最多 3 个，加载器实际 HTTP 合计最多 4 个，含清单和重试。取目标资产前仍等依赖齐全。小游戏与文件宿主沿用各自的调度。
- 验证跨资源同名包仅发出一次。在唤醒等候者前登记全部引用，某个调用方先取消也要给其余请求留住包。批内失败先等已发的请求收尾，再回滚全部实领引用。退代时核对旧请求中止及新代同名包的独立所有权。
- 场景首屏已有交接屏障时，候选预热放在页面可交互后。业务按域声明少量高频入口，使用同一个逻辑路径和具名租约，串行准备并按域释放。前台沿既有异步开页，保持图集可画门禁。评估命中、省时、额外传输、驻留与离场。RevGame 的无尽入口结果和成本见 [Web 性能复盘](https://github.com/Yokino337088/RevGame/wiki/%E8%AE%A9%E7%BD%91%E9%A1%B5%E7%89%88%E7%82%B9%E5%BE%97%E5%BF%AB%E3%80%81%E6%89%93%E5%BE%97%E9%A1%BA)。

## 网页战斗的绘制预算

- CSS 视口、实际绘制缓冲和设备 DPR 是三个量。像素预算改变绘制缓冲时，核对全屏、普通窗口、Unity 实际读到的尺寸以及真实点击坐标。尽可能在视口变化时调整，避免把频繁重建缓冲算进稳态。
- 同一 Wasm 比较两种尺寸，先确定尺寸再从相同种子开局。固定棋数、阶段、采样时长，记录攻击数、P95/P99 和长帧。平台表现预算可限同屏反馈与每帧生成量，维持规则结算和队列所有权。单看平均帧率无法判断偶发停顿。 [Unity WebGL Canvas](https://docs.unity3d.com/2022.3/Documentation/Manual/webgl-canvas-size.html)

## 输入、音频和持久化

- 键位先核对实际目标版本的 Unity 枚举。Unity 2022.3 的 `RevKey.PrintScreen` 映射到 `KeyCode.Print`，公开枚举及已有存档的值继续沿用。检查 canvas 获焦后键盘输入，以及文本框拥有焦点时的输入归属。
- 网页默认铺满视口。浏览器原生全屏要在真实用户手势内请求，处理拒绝和退出，并保留入口。音频也有手势门槛，首个点击同时核对解锁。WebGL 长 BGM 可用 `CompressedInMemory`，短音效按时序、内存和 AB 实际解码选导入方式。见 [Fullscreen 标准](https://fullscreen.spec.whatwg.org/#dom-element-requestfullscreen)、[Unity 2022.3 WebGL 音频](https://docs.unity3d.com/2022.3/Documentation/Manual/webgl-audio.html)。
- 浏览器 `Application.persistentDataPath` 由 IDBFS 支持。使用 `RevFileSave` 写主档与备份成功后，网页插件请求 IndexedDB 异步写回。记录存档时区分虚拟文件提交与浏览器事务完成。验证重开用同一 origin 和页面目录，存档即时刷新与配额拒绝另做针对性检查。见 [Unity persistentDataPath](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/Application-persistentDataPath.html)、[Emscripten FS.syncfs](https://emscripten.org/docs/api_reference/Filesystem-API.html#FS.syncfs)。

## GitHub Pages

- 只有静态托管能力时，按可实际返回的 HTTP 响应头选格式。Unity 的预压缩 `.br` 需要 `Content-Encoding: br`。可在发布阶段解开 `.br`，同步改入口文件的 URL，或者在 Unity 构建时选择 Decompression Fallback。核对公开 HTTPS 实际返回的 Wasm MIME 和真实 Player 启动。见 [Unity WebGL 部署](https://docs.unity3d.com/2022.3/Documentation/Manual/webgl-deploying.html)。
- 分支发布可用独立 `gh-pages` 根目录和 `.nojekyll`。保留页面、Build、目标 StreamingAssets 及模板依赖。入口与资源使用相对 URL，确保项目建在 `/<仓库名>/` 时可加载。首次启用后回读 Pages 构建状态与匿名访客页面。见 [GitHub Pages 发布来源](https://docs.github.com/en/pages/getting-started-with-github-pages/configuring-a-publishing-source-for-your-github-pages-site)。

## C++、P/Invoke 与退出

- Windows DLL 供 Windows 原生宿主调用。Unity WebGL 使用同一版 Editor 自带的 Emscripten，将 C++ 构建为 wasm32 `.o` 的 `.a` 归档并只给 WebGL Player 导入。C# 用 `UNITY_WEBGL && !UNITY_EDITOR` 选择 `DllImport("__Internal")`。Editor 和其它 Player 继续选自己的动态库。先核对 `extern "C"` 与 `llvm-nm` 的实际符号，再看 IL2CPP 链接和网页执行证据。见 [Unity WebGL 原生插件](https://docs.unity3d.com/2022.3/Documentation/Manual/webgl-native-plugins-with-emscripten.html)。
- C ABI 使用定宽整数、指针或缓冲和显式长度。托管层接管 `IntPtr`，按每局原生实例的所有权释放。跨平台继续复用同一消息序列与定点规则。实体句柄、输出缓冲、检查点与回放分别验证，避免单凭静态归档存在或标题页打开判断 P/Invoke 成功。
- `.jslib` 同样由 `__Internal` 绑定。调用宿主页时，将请求交给当前 `Module.canvas`，让原生调用栈返回后由模板调用实例 `Quit()`。网页负责界面反馈、清理时限和关闭。用真实点击创建可关闭的同源游戏标签页。浏览器保留手动标签页时，退出后返回启动入口。见 [Unity 网页模板](https://docs.unity3d.com/2022.3/Documentation/Manual/webgl-templates.html)、[HTML Window.close](https://html.spec.whatwg.org/multipage/nav-history-apis.html#dom-window-close-dev)。

## 交付记录

按任务范围分别记录 WebGL Player 构建、AB 清单、HTTP 预检、编译与浏览器玩法的证据。框架改动在源仓库通过对应流程导出后，以正式 `package` 的 SHA 同步项目，逐项核对快照。可选扩展以其独立导出为准。构建结束后恢复本次约定的开发目标。
