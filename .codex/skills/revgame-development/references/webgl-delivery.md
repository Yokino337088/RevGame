# RevGame 网页版交付

## 出包与恢复编辑器

1. 用工程相邻的 `Revolution.RevCLI/rev.py`，显式指定 `RevGame_Client/UnityProject/RevGame.Unity` 的绝对路径。查 `editor status`、`build targets` 与 `build settings`，记下开始的目标。切到 WebGL 后等编辑器稳定。
   切平台的内存工单在域重载后若回读 `JobNotFound`，先查实际活动目标和编译状态。目标已正确时继续原流程。
2. 同一目标执行 `ab build-incremental --target WebGL`、`ab check-map --target WebGL`、`ab publish-incremental --target WebGL`。核对 `Assets/StreamingAssets/WebGL/WebGL`、`BuildManifest.json` 及目标 AB。项目早期的 `game.background.build` 主要用于 PC 和 Android，网页采用框架注册的 WebGL AB 入口。
3. 用 `build player --target WebGL --backend il2cpp --path <暂存目录>/Web --detach` 构建正式包，记录工单 ID 后跟踪 `job <id> --wait`。客户端超时后查询原工单与 `build-result.json`。中文 Windows 上若 Emscripten 日志先报 GBK 解码错误，核对框架的 `PYTHONUTF8` 补丁和运行 Editor 的实际进程环境。内存工单若报 `target-started` 路径缺失，查已安装框架版本。见 [Revolution WebGL 参考](../../revolution-framework/references/webgl-delivery.md)。
4. 将完整的 `Build`、模板和 WebGL 资源发布到 `RevGame_Client/Publish/Web`。先在暂存区核对产物与项目依赖，再切换交付目录。2026-11-21 的原包同时复制了 PC 和 Android 资源，最终交付只取 WebGL 资源。发布文件与源包按清单和摘要核验。构建结束后按本次约定恢复 `StandaloneWindows64`，以 `build settings` 回读为准。

## 网页入口、全屏和声音

- 本机从交付目录的 `启动网页游戏.cmd` 启动。它拉起只监听回环地址的 `WebPreview.ps1`，并打开 `http://127.0.0.1:17890/`。直接双击 HTML 时，模板在 Unity 下载前给出启动提示。线上部署把整个交付目录一起上传，按 [Unity 2022.3 WebGL 部署](https://docs.unity3d.com/2022.3/Documentation/Manual/webgl-deploying.html) 核对 Brotli 响应头和 Wasm MIME。
- 模板为 `Assets/WebGLTemplates/RevGameFullscreen/index.html`。公开链接直接启动《围棋大作战》，本机和公开版都使用普通浏览器窗口。画布铺满当前视口，只由玩家点击「全屏」请求原生全屏。沿 [Fullscreen 标准](https://fullscreen.spec.whatwg.org/#dom-element-requestfullscreen) 核对退出和拒绝后的行为。
- 浏览器验收启用时，从序章或大厅按真实入口走完输入、BGM、操作音和战斗。Unity 2022.3 的 `PrintScreen` 键位由框架映射到 `KeyCode.Print`。音频先看真实用户交互后的播放状态，再查 AB 导入。避免只凭 Console 没有错误判断有声。存档跨刷新验证需要同一地址与页面目录。

## 正式包内容

- 六章、解锁棋子、篇章回放、奖励和鸦鸦小屋采用 `Consts.WebContentOpenByDefault` 的 Web Player 策略。正常入口用 `GameFlow.ReplayOpeningStory` 和 `ReplayTutorial`，开发专用宏只保护诊断注册和预览控制。
- 内容可访问、序章与教学重看、实际通关与领取状态分别核对。持续使用真实通关记录和奖励条件。浏览器原生全屏与 Unity Player 运行状态分别记录。
- 证据按本次范围记录。构建、HTTP 预检、编译通过和实际浏览器对局各有自己的结论。历史实测、工单和定位过程见 [WebGL 构建踩坑复盘](../../../../RevGame_Client/文档/安卓构建踩坑报告/WebGL构建踩坑复盘_2026-11-21.html)。

## GitHub Pages 发布

- 发布仓库为 `Yokino337088/RevGame`，公开入口为 [GitHub Pages](https://yokino337088.github.io/RevGame/)。`main` 维护 README 入口、Unity 模板和转换脚本。Pages 发布来源为 `gh-pages` 根目录。
- 从已验证的 `Publish/Web` 运行 `node tools/WebGLPreview/prepare-github-pages.mjs <Web 源目录> <新的暂存目录>`。附带的 WebGL AB 先校验内容再按摘要落到 `StreamingAssets/<版号>/WebGL`。入口使用同一版本目录，Unity 对这部分内容采用 `immutable` 缓存。初次进入一版时低优先级预下载 7 个必要包，分批预热使用已有交接和资源租约。用相同网络条件核对首屏请求是否与引擎启动重叠，分别量冷启动和 HTTP 缓存清理后的 AB 传输量。见 [GitHub Pages 部署实战](../../../../RevGame_Client/文档/安卓构建踩坑报告/GitHubPages部署WebGL实战指南_2026-11-21.html)。
- 上传中断时回读远端引用，保留原有发布提交。经代理的 Git HTTP 上传无法收尾时，使用发布指南中对当前命令生效的参数重试同一提交。确认 GitHub Pages 实际返回新版 HTML 及资源后再报告生效。

## 决斗内核与退出

- `RevGame_Client/RevGameCore/BuildWebGL.py` 使用当前 Unity 的 Windows WebGL 工具链，从同一 C++17 真源产出 `Assets/Plugins/RevGameCore/WebGL/libRevGameCore.a`，用 Node 执行内核现有 FIFO 与检查点测试。WebGL 的 `DuelCorePipe` 经 `__Internal` 解析六个 `GameCore_*` 符号。Editor、桌面与服务端仍各取自己的库。核对归档、IL2CPP 跨语言调用与真实对局入口。
- 大厅的「我不玩了」沿 `GameFlow.RequestQuit`。WebGL 把退出请求从 `RevGameBrowser.jslib` 交给模板，通过当前实例 `Quit()` 清理，再请求关闭标签页。浏览器拒绝关闭普通标签页时导航到只显示「已退出游戏」的页面状态，卸载旧 Player。验证直接加载、退出、受限回退和重新进入。
- 编译命令、跨语言接口和退出链详见 [WebGL 原生插件与 P/Invoke 复盘](../../../../RevGame_Client/文档/安卓构建踩坑报告/WebGL原生插件与PInvoke踩坑复盘_2026-11-21.html)。

## 网页 Agent Key

- `PvpAgentSettingsPanel` 在 Unity 输入框对应位置提供原生 HTML 密码框。打开时暂时交还全页键盘，关页或退域时移除 DOM 控件并恢复 Unity 键盘捕获。用实际粘贴事件验证，输入仅在授权时跨 Wasm 边界进入业务。
- 网页授权归常驻 `PvpService` 内存持有，清除 Key 或载入新页面便关闭云端。浏览器缓存用于资源，Key 只提交给配置的 DeepSeek 接口。用假 Key 验证保存、重开面板、清除和刷新，支付型真实调用留给玩家自己的授权。必要时用无凭据 OPTIONS 核对当前站点的 CORS。
