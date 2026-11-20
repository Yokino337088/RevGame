# 业务自动交付契约

把目标、依赖、许可、提交点和恢复路径留在游戏代码。场景、域、界面、工具和存档沿现有框架门面执行。以下入口共用现有实际资源系统和自动化回执。

## 场景交接

用 `RevSceneHandoffExecution.Start(tracker, plan)`。计划声明目标场景、源和目标业务状态、实际状态查询，以及需要的遮挡、预热、准备、提交、就绪、清理和揭幕回调。使用同一个 `RevSceneHandoffTracker` 保留请求 ID，取回的句柄使用 `WaitAsync()` 等待终态。接单返回空表示已有交接在途，原请求继续拥有输入门禁。

标准加载使用 `RevScene`，也可适配项目现有加载步骤。候选放行可传 `CanActivate`，用有限等待后再切换活动场景。交接令牌须归页面流程或常驻宿主，寿命覆盖新旧业务域。已发给 Unity 的加载需按原契约正常收尾。首屏检查使用实际目标场景和目标业务组件。回调内部存在跨帧提交时，在业务提交点继续检查令牌。

成功依序准备、提交、就绪、清理和揭幕。失败仍会尝试回收临时业务对象和预热租约，并把 `CommitStarted`、阶段和原始错误交恢复适配器。可恢复的旧场景与服务端已确认离开的房间分别按真实状态处理。结案前通过 `BeforePublish` 清除业务门禁；等待者被唤醒时可以读取唯一终态及实际业务状态。

## 作用域请求和 UI

`RevScopedOperations<T>` 由实际域句柄持有，可以在关窗时提前释放。以稳定键发出工作，只有双方显式共享的同键读取复用等待结果。使用有限的非缩放超时时供主线程驱动，取消先回收在途名额并唤醒所有等待方。迟到资源交 `discard` 归还。使用 `canDeliver` 比对房间或业务版本。工作函数处理调用端的令牌，规则修改以明确的业务提交点为准。服务器已经受理的写入继续按权威状态处理。

在 `RevUIPanel.OnOpen` 调用 `CreateStateBinding`。用 `Bind` 表达快照字段到控件的映射，用 `Observe` 指定状态变更事件，已有定时刷新也可调用 `Refresh`。同一轮刷新读取一次快照，按字段比较。用 `BindCommand` 将实际按钮、许可、结果提交与错误展示接在一起；关窗、取消交割或销毁自动解绑本轮。新增接线的按钮由一个入口提交，迁移时移除相同动作的旧监听。Editor 的 `ui.binding-check` 按运行时规则核查预制体字段、节点和组件，查看 `errors` 数组。

## 启动、配置与本机保存

`RevStartupPlan.Add` 声明依赖键、异步工作与失败补偿。用 `AddService<TService,TImplementation>` 接现有服务门面，用 `AddTable<T>` 接现有资源表加载。缺失和循环依赖在执行前报告。失败逆序调用已开始项的补偿，预先加载的表保留既有引用。项目提供跨表、资源键和业务数值校验的最后一步。注册清单可由项目生成或在启动接线处维护。

文件平台用 `RevFileSave.Read/Write` 统一主档与备份、同槽串行、安全临时文件与读回校验。项目继续持有既有编码、迁移和规则校验，隔离测试可给独立路径或内存持有。恢复后首次提交可选保留可用备份。`RevSaveCheckpoint` 负责合并设置写入及应用生命周期检查点，宿主释放前显式提交需要保留的改动。在失败后沿项目状态处理保留的脏标记。

## 资源、契约与校验交付

- 在 Editor 用 `asset.import-plan` 提供已导入候选的逻辑路径和当前显式 BuildTarget。读取文件和导入设置、候选 GUID、包名、图集打包项和依赖哈希。只有唯一同用途候选时建议复用；歧义由项目确认归属。正式配置遵循 RevAB 与项目资源校验入口。
- 游戏动作可用 `RevRuntimeAutomation.Register<TInput,TOutput>`，Editor 扩展动作可在 `RevAutomationAction` 声明 `InputType`、`OutputType` 和 `ReadOnly`。DTO 由可序列化公有字段构成，可省略字段使用 `RevAutomationOptional`。读取类型化合同后仍要通过实际环境、confirm 和业务规则检查。旧动作继续兼容已发布的清单。
- `rev.py contract` 校验离线命令键及类型，`contract --live` 核对实际编辑器注册并返回项目身份、清单与在线契约哈希。核对插件与项目工具主版本，编译重载后重新发现动作。
- `ship --verified --target StandaloneWindows64` 串行导表、刷新、完整重编、同工单诊断核对、增量 AB 构建、映射校验与发布。可用 `--verify-plan` 在 Player 构建前运行显式 RevMCP 业务计划。每步有构建 ID 和实际 Unity 工单。失败后按其工单状态选择继续节点，变更动作按显式授权逐步恢复。测试包输出遵循项目地址规则。

## 自动验收与性能对比

使用正常入口、同一份数据和同一设备，计划的 `finally` 负责复位。向 `rev_run` 计划添加 `benchmark` 对象，提供 `identity` 中的 `case/revision/device/workload`，以及 `warmup/repeat/metrics`。每个指标用 JSON Pointer 指向实际步骤回执，例如 `/steps/1/response/data/p95Ms`。保留各样本和中位数。可将上一版的 `benchmark` 回执作为 `baseline`，核对工作量、设备与指标路径后计算差值。需要的设备或指标由项目验证；回执身份的场景信息是计划声明。只有用户要求时导出报告，已有计划的断言与清理仍为通过依据。

## 可选热更新

`RevHotConfig.RequiredAssets` 接收候选 ResMap 中的代表逻辑键。完整候选校验后，通过候选包、依赖和资源路径实际读取并卸载探针持有，保持当前版本指针。可用 `CanActivate` 复核业务状态，提交前再次确认资源使用情况。继续使用既有回退日志和旧版本快照。
