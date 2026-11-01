# 关机重启后重新连接

电脑重启后 Unity、常驻服务都没了，`instance:` 对象 id 也全部作废。**开工第一步，或任何连接类报错时，都跑一次 `connect`**。它是幂等的，已经连好时只做核对，重复执行没有副作用。

## 三步恢复

1. **找解释器。** 不要直接用 `python`，它可能指向旧版本。

   ```powershell
   py -3 -c "import sys;print(sys.executable)"
   ```

   拿到路径后，后续都用这个路径调 `rev.py`（`py` 启动器每次多花上百毫秒）。没有 3.10+ 时告诉用户需要安装，CLI 不依赖任何第三方包，不需要 pip。

2. **连接。** 显式传工程，不猜。

   ```powershell
   <python> Revolution.RevCLI/rev.py --project <Unity工程> connect
   ```

   它会依次：检查 Python 和工程 → 定位 Unity → 判断残留状态 → 打开 Unity 并等编译 → 起常驻服务 → 比对 CLI 与 Unity 的动作。默认最多等 15 分钟；工程没有 Library、要全量导入时加 `--timeout 3600`。

3. **读结果，不要只看成功。**

   | 字段 | 含义 |
   |---|---|
   | `state: connected` | 可以开工 |
   | `capabilityGap.missingInUnity` | 工程里的 `Assets/Revolution` 比 CLI 旧，这些动作用不了，需要更新后才有 |
   | `playing: true` | Unity 正在 Play，编辑类动作要先 `editor stop` |
   | `steps[].fix` | 失败时的下一步做法 |

只想看现状、不想启动任何东西，用 `doctor`（完全只读）。

## 哪些东西重启后还在

| 东西 | 重启后 | 处理 |
|---|---|---|
| Unity 编辑器 | 没了 | `connect` 自动打开 |
| CLI 常驻服务 | 没了，端点文件可能残留 | `connect` 清理残留并重启 |
| RevMCP | 由 AI 客户端自动启动 | 不用管；工具不见了就重开客户端会话 |
| `Temp/UnityLockfile` | 断电时残留 | `connect` 能识别出没人持有，**不要手动删** |
| `endpoint.json` | 残留，里面的 PID 可能被别的进程复用 | 不要据此判断 Unity 在运行 |
| `instance:` 对象 id、未完成的工单 | 全部失效 | 重新查询，不重放未知结果的写操作 |
| Unity 路径 | 记在工程 `Library/RevolutionAutomation/editor-path.json` | 自动复用 |
| Excel 来源 | 存在工程 `UserSettings/RevExcelTool.asset` | 保留；更早的框架版本重启会丢，来源为空时重新 `excel.configure` |
| 未保存的场景 | 丢失 | 这正是 AI 改完要 `scene.save` 的原因 |
| Windows 的 VS 工具链联接 | 保留 | `doctor` 的 `il2cpp-toolchain` 会核对 |

## 连接失败怎么办

| 现象 | 原因 | 做法 |
|---|---|---|
| `PythonTooOld` | 用了旧解释器 | 回到第 1 步 |
| `unity-editor` 失败 | Hub 记录里没有这个版本 | 用 `--unity <Unity.exe>` 或环境变量 `UNITY_EDITOR` 指定；成功一次后会记住 |
| `bridge-code` 失败 | 工程没装 Revolution 编辑器代码 | 只复制 Python 不会生效，要把 `Assets/Revolution` 装进工程 |
| `connect` 超时 | 首次导入太慢、Unity 弹窗（如安全模式、登录）或编译错误 | Unity 不会被关掉。看窗口，读 `Library/RevolutionAutomation/launch-*.log`，处理后重跑 `connect` |
| `connect` 一直在等，Unity 却能正常操作 | 上次会话留下的长工单（烘焙、构建、测试）还在跑。客户端断开不会取消 Unity 里的工作 | 看 `Library/RevolutionAutomation/endpoint.json` 的 `activeRequest`，用 `job <id>` 查看；确认不需要后 `job <id> --cancel`，`connect` 会随即完成 |
| `常驻服务正在执行另一条命令…本次没有执行` | 上一条命令还没结束 | 这条没执行，等一会儿重试即可 |
| `state: busy` | Unity 开着但没有心跳 | 等导入完成；编译错误用 `editor compilation` 读，修好后重跑 |
| Unity 标题栏“未响应”，长时间没有心跳和日志 | Unity 卡死；实测在空闲内存不足 1GB 时退出 Play 出现过一次 | `connect` 不会替你结束 Unity。告诉用户哪个工程卡住了，让用户决定关不关；关掉后再 `connect`，未保存内容会丢 |
| 动作报 `未知参数` 或 `动作不存在` | Unity 里的桥比 CLI 旧 | 看 `capabilityGap`，告诉用户需要更新工程里的框架 |
| `常驻服务已收到请求但连接中断` | 请求可能已执行 | **不要重放**，用 `job`、`editor.console` 或读回确认副作用 |
| IL2CPP 打包报找不到 C++ 组件 | VS 装在中文路径，Unity 解码失败 | 看 `doctor` 的 `il2cpp-toolchain`；Mono 打包不受影响 |

## 不要做

- 不要手动删 `Temp/UnityLockfile` 或 `Library`。
- 不要为了连接去关用户已经打开的 Unity，也不要杀进程。
- 已有 Unity 打开工程时不要用 `--mode batch`，会抢工程锁。
- 不要跳过 `connect` 直接重放上次会话里的 id。
