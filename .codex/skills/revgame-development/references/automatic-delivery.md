# RevGame 自动交付接入

- 场景由 GameFlow 提供当前玩法、目标场景、首屏就绪条件和业务提交回调，框架执行遮挡、预热租约、场景与就绪阶段、回收及终态发布。过场留在流程宿主，第一章返回时保留此前入场请求的资源退域快照。
- GameBootstrap 声明常驻服务依赖，GameFlow 声明冷启动配置表和规则校验。配置快照就绪才进入实际玩法，启动失败按本次加载归属释放。
- PvP 的房间快照用于标题、棋手显示和停一手、确认数子、重连的共同许可。回包仍由 PvpService 核对实际连接。关窗后组件不再写原面板。
- 无尽存档与纪录使用 RevFileSave，继续读取现有格式和备份。音量设置用 RevSaveCheckpoint 合并写入，在关面板、退后台及正常退出保存。
- Editor 在相同 BuildTarget 下运行资源预检、实际资源归属检查和 `ui.binding-check`。复用包使用已存在的正式资源键，玩家可用性以真实 AB 冷启动检查为准。
- `tools/automation/first-chapter-delivery.json` 是已有真实断言与复位入口组成的项目计划。可在 Play 的空闲大厅传给 `rev_run`，或由 `ship --verified --target StandaloneWindows64 --no-ab --verify-plan tools/automation/first-chapter-delivery.json` 管理编译、Play 与收尾。使用实际 Windows 发布包并核对加载模式。需要发布变更 AB 时执行项目资产校验和同平台增量发布。
- 可选热更新包随框架版本同步，项目按需求决定通道、候选代表资源及激活时点。
