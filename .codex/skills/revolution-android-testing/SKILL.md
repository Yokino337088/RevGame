---
name: revolution-android-testing
description: 用 USB ADB 连接 Unity Android 测试 APK 与 Revolution RevMCP，执行真实设备业务、性能与清理验证，并合并 Unity Test Framework 的独立结果。适用于安卓实包、真机热态、ADB 身份错配及测试接线排查。
---

# Revolution Android 真机测试

以 [Revolution.Android README](https://github.com/Yokino337088/Revolution/blob/main/Revolution.Android/README.md) 为使用契约。源仓库运行 `Revolution.Android/rev_android.py`。Codex 插件提供并排的 `tools/Revolution.Android`、`Revolution.RevCLI` 与 `Revolution.RevMCP`。

## 构建和设备

原生手机测试台支持无 USB 真机压测。Windows 双击 `Revolution.Android/Android_APP/start-test-service.cmd`，使用屏幕上显示的电脑 IP 和端口。手机选择游戏并连接，首次核对指纹和配对码并授权，以后可直接连接同一台电脑。需要在游戏上操作时，从测试台启用悬浮窗并允许系统许可。Agent 以真实工程和电脑输出的 `runtime.json` 执行 `rev_bind` → `rev_discover` → `rev_run`。通过游戏的 `runtime.android.device` 读取进程 PSS、热态和电池温度，用 `runtime.performance.begin/state` 取得对应 runId 的 FPS 和帧时间分位数，最后取消并关闭采样。使用项目真实压力负载和隔离策略。 [手机测试台与性能计划](https://github.com/Yokino337088/Revolution/tree/main/Revolution.Android/Android_APP)

1. 查项目规则与真实注册表。出测试包使用当前打开的目标工程，通过 RevCLI `--mode live` 执行 `build.player` 并核对 BuildReport、包名和 Unity 版本。按需启用 `automation`、`frameTiming`，只随包交付 Android AB。切目标、隔离其他平台 AB 和签名沿 [Android 构建配方](../revolution-unity-editor/references/android-build-and-verification.md)。
2. 用 `devices` 读真实 ADB 状态，再锁定已授权的串号。复用用户已配置的 ADB。先确认设备与包身份，按用户授权安装、替换或重开。已有正式进程应保留，测试优先独立包和隔离存档。RevGame 使用 `--isolate` 并在计划中断言隔离。
3. `mcp --project <工程> --package <包名>` 可同时接已打开的 Editor 和 USB 真机。先 `rev_discover`，分别核对 Editor 与游戏的 action。手机使用 `game`／`gm`。`run` 可直接执行同一份 RevMCP 计划，支持 stdin JSON。连接的 `/health` 只证明到了正确运行时。
4. Windows 用户手动操作时可构建 [WPF 测试工作台](https://github.com/Yokino337088/Revolution/tree/main/Revolution.Android/Desktop)。先扫描授权与连接方式，再核对 APK 包名和编辑器。选择「连接并启动」后确认手机回执，运行计划及取消沿用 RevMCP。若用户表示接线后另行通知，先准备工具和 APK，等其通知再启动手机测试。

## 真机性能验收

- 先用正式入口让游戏进入测量状态，确认真实棋子、攻击或触发路径。用 `runtime.performance.begin/state` 验实际 runId、采样秒数、错误、失焦、P95/P99 和反馈等待。性能验收要适用 [Android 平台测量与取舍](../revolution-performance/references/android-performance.md)。
- 计划用 `finally` 停采样、收拾游戏业务和资源。工具归还本次认领的转发及私有凭据。发现设备重启或断线时保留首错，先重新确认身份再发新任务。
- `run --device-samples` 在计时窗外记录 PSS（进程占用口径）、系统热状态与电池温度。热态看连续同负载、同画质的实际时间窗，记录缺测项。
- Unity Test Framework 的 Android Player 测试与业务计划各有结论。可以用 `unity-results` 读 NUnit XML，或用 `run --unity-results` 汇总到同一回执。Profiler 的定位轮和正式性能对照分开。 [官方工具互补与实包步骤](https://github.com/Yokino337088/Revolution/blob/main/Revolution.Android/README.md#3-unity-官方工具放在哪一步)

向用户交代实际 APK、设备、动作、结果与清理。设备清单为空时，可以验证 SDK、构建和桌面协议，真机项按待设备接入记录。
