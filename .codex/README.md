# RevGame 的 Codex 插件与规范检查

根 `AGENTS.md` 是本项目约定。项目技能在 `.codex/skills/revgame-development`，通用技能从 Revolution 源仓库的 `Skills` 同步。原始证据留在忽略目录 `.codex/work`，测试报告按项目约定保留 HTML 和必要的截图或 GIF。

## 安装

在 Codex 桌面端打开并信任 RevGame 仓库根目录。插件目录的 **RevGame** 来源会列出 **Revolution** 和 **RevGame**，项目配置默认启用这两个插件。首次打开时，若插件仍显示为未安装，可在该来源安装，或按[换机部署文档](../RevGame_Client/文档/规范/RevGame换机部署与合并检查.md)执行 CLI 安装。本机从源码开发插件时可使用 Revolution 仓库的 **Revolution** 来源。安装后打开新会话，在 Codex 的 **Review hooks** 中审核会话钩子。更新钩子后按 Codex 提示重新审核。

插件的 Unity MCP 需要 PATH 上的 Python 3.10+，例如 Windows 上通过 `python --version` 确认。先用 `rev_bind` 绑定 `RevGame_Client/UnityProject/RevGame.Unity` 的绝对路径，再用 `rev_discover` 查询实时注册。已有同工程的常驻 RevMCP 可直接复用。做场景、Prefab、导表或构建时使用项目 RevCLI。只有实际调用并核对断言与清理，才能记录业务通过。

## 检查

从仓库根运行以下命令。检查器依赖 Python 3.10+、Git。涉及手写 C# 变更时还需要 .NET 8 SDK。

```powershell
python tools/RevPolicy/check.py
python tools/RevPolicy/check.py --staged
python tools/RevPolicy/check.py --base origin/main
```

默认检查工作区，含新建文件。`--staged` 读取 Git 索引。`--base` 比较与指定提交的共同祖先至当前 HEAD。退出码 0 通过，1 发现违规，2 检查无法完成。

GitHub Actions 使用 `--format github` 生成原生注解。PR 的 **Checks → revgame-policy** 可查看违规文件与修复提示，落在 PR 差异内的代码行也会在 **Files changed** 中标记。插件市场清单的 JSON 语法错误也会标出文件、行和列。代码诊断使用提交中从 1 起算的行和列，GUID 问题尽量标到 `.meta` 的 `guid:` 行，目录和缺失文件问题按资产路径显示。重复 GUID 会标在变更的 `.meta` 上，并列出冲突路径。全部诊断同时保留在作业日志中。

启用随仓库分发的 Git hook 前，用 `git config --get core.hooksPath` 查看已有设置。没有现有 hook 时可运行 `git config --local core.hooksPath .githooks`，否则在现有 pre-commit 中接入同一条 `--staged` 命令。GitHub Actions 的 `revgame-policy` 检查推送和 PR。要让失败的检查阻止合并，由仓库管理员将其设为 `main` 的必需状态检查。

| 规则 | 检查范围 |
|---|---|
| REV100–103 | 资源目录，文件和目录的 `.meta` 配对，原 GUID，新增或修改的 GUID 是否重复 |
| REV110 | 变更框架快照时是否同步有效的上游包提交锚点 |
| REV200 | 手写业务和项目 Editor 代码的 `if`、`else if`、`else` 格式 |
| REV201–206 | 业务异步 API、已登记框架内部类型、直接加载资源、初始化入口、切场景和面板销毁自身 |

规范检查只报本次涉及的资产和新增代码诊断。Unity 继续负责 `GameResourceUsage.Validate`、实际引用、图集与 AB，业务验收继续核对 Play 中的状态。注释的解释质量、FSM/Scope 归属、参数是否应入表等需要结合代码审查。

## 同步来源

`tools/RevPolicy` 是 Revolution 插件检查器的源码副本。`tools/RevPolicy/SOURCE.txt` 记录来源提交。更新时从同一个 Revolution 提交同步检查器及框架技能，保留项目自己的 `.codex/rev-policy.json`。插件目录的 Git 来源也固定到已验证的提交。
