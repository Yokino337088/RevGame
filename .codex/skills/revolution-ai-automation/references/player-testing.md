# 独立Player测试

先核对目标项目当前能力，不能用Editor成功代替IL2CPP实包。已有编辑器走live；框架源码改main再发布package，业务项目只增量同步并保护资源映射和生成常量。

- 构建：`rev.py --project <工程> --mode live --timeout 3600 build player --target StandaloneWindows64 --path <专用空目录/Game.exe> --backend il2cpp --automation --frame-timing`。诊断包加`--development`；发布包不加automation。成功需BuildReport Succeeded/errors=0；临时backend/FrameTiming/编译配置finally还原。AB由项目管线先构建，不能把Player构建当AB门禁。
- 启动：`rev.py player launch --executable <exe> --directory <新的会话目录>`；返回endpointPath，不输出token。MCP用`--runtime-endpoint <runtime.json> serve`。端口、会话目录、launchId/pid/sessionId/buildId一致才操作，不能拿旧endpoint或任意PID强杀。
- 向用户展示真实rev_discover、rev_call、rev_run顺序、关键参数和结果。先发现后调用；进入独立Player后只用其注册表，Editor专用动作不可照搬。
- 项目GM和调试面板若只允许UNITY_EDITOR/DEVELOPMENT_BUILD，非开发自动化包必须补REV_AUTOMATION条件。框架已有宏不代表项目已接通。测试隔离必须在业务初始化/存档写入前完成。
- 采样：runtime.performance.begin(seconds=1..600,warmup=0..60) → state（readOnly） → completed/success/errors/seconds/runId/负载断言。finally先cancel再disable，再项目业务收尾。采样本身不改变画质/帧率/玩法，也不证明达到预算。
- RevRuntimeAutomation.EnablePerformance/PerformanceSnapshot/DisablePerformance供项目面板共用。显示FPS、帧时分位、Draw Calls/Batches/SetPass与有效计数。Release缺少详细动态/静态/实例化计数时显示N/A；GPU无新有效样本=-1，不能解释为0ms。面板低频刷新，基准窗口隐藏，截图/汇总在窗口外。
- 首版launcher限Windows。Android端口转发和WebGL传输不是已验证能力。脚本测试、编译、Editor、IL2CPP开发包/非开发包分别报告；温度、后台/最小化、VSync/分辨率和负载不可混作前后收益。
- 不导出Profiler录制、不生成ZIP（RevGame约定）；HTML与必要截图/GIF交付，原始JSON和凭据只进忽略目录。

完整手册：源仓库 `Revolution.Document/自动化工具/独立Player构建与性能自动化.md`（同名HTML）。

构建发布检查：ab.build受copyToStreamingAssets配置控制，必须核对Player首包实际文件；项目剧情等待复用正式AdvanceStory等入口，不直接设置完成标记。旧Unity是否支持本机VS版本由IL2CPP实际BuildReport确认，不凭vswhere发现组件就宣布可用。
