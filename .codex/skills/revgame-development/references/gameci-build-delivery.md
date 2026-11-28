# 用 GitHub GameCI 交付 Unity 包

日常在在线 Editor 完成功能、画面和规则验收。需要交付独立测试包或正式包时，优先从选定提交使用现有 GitHub GameCI 工作流。本机手动打包主要用于排查特定工具链问题。确需在本机验证时，复用 RevCLI 的自动化入口并记录环境差异。

## 选择入口

| 平台 | 测试包 | 正式包 |
| --- | --- | --- |
| Android | [安卓测试包](../../../../.github/workflows/revgame-gameci-test-android.yml) | [安卓正式包](../../../../.github/workflows/revgame-gameci-personal-android.yml) |
| Windows | [Windows 测试包](../../../../.github/workflows/revgame-gameci-test-windows.yml) | [Windows 正式包](../../../../.github/workflows/revgame-gameci-personal-windows.yml) |
| WebGL | [WebGL 测试包](../../../../.github/workflows/revgame-gameci-test-webgl.yml) | [WebGL 正式包](../../../../.github/workflows/revgame-gameci-personal-webgl.yml) |

云端出包沿一次明确的任务授权手动触发，使用实际已推送的目标提交。规则检查和 PR 脚本编译走原 CI。测试包构建启用项目的自动化和帧时间能力，正式包用各自的发布配置。WebGL 产物含浏览器入口、Wasm、构建报告及 WebGL AB，网页发布及 HTTP 响应配置见 [WebGL 交付](webgl-delivery.md)。

## 从提交验到最终下载包

1. 推送已验的源码与目标平台资源。资源有更新时，先按实际平台生成并提交新的 AB 和清单。出包使用所选提交已有的目标 AB，核对平台、文件集合及 SHA-256。
2. 从目标分支触发对应工作流，确认 GitHub 运行记录的 `head_sha` 与选择的提交一致。等构建、验包和上传进入终态。上传前检查 APK 或 Player 内同一批目标 AB 的字节，并排除其他平台的包。
3. 发布下载链接前核对产物名、构建报告、Build ID、Unity、脚本后端、目标架构和测试能力。Android 同时检查实际应用 ID 与签名。WebGL 核对 `index.html` 引用的构建文件、Wasm 和网页启动配置里的产品名。核对下载产物摘要。跨 Windows 本地检出和云端时，清单 JSON 可能出现 CRLF/LF 差异，对照选定提交的 Git 文件，再逐项比较 AB 文件的实际字节。
4. 把测试运行时的 Build ID 与构建报告对应，再进入隔离真机或独立 Player 测试。Editor、桌面独立包、安卓和浏览器各自报告。需要长期保存的证据遵循项目验收目录约定。

2026-11-28 的安卓测试包先核对提交中的 241 个目标清单文件。下载后由构建报告、APK 的 ARM64 IL2CPP、包内 AB 与 SHA-256 再次闭环。Windows 本地清单文本与云端文件的换行差异，通过已提交的原始内容得到确认。 [实际 GameCI 验包](https://github.com/Yokino337088/RevGame/actions/runs/46143115278) · [安卓测试与布局](android-build-and-mobile-ui.md)
