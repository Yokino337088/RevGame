# RevGame 换机部署与合并检查

拉取 RevGame 后，仓库里的规范、插件来源、Git 钩子脚本和 CI 工作流都已到位。在新电脑用 Codex 打开并信任仓库后，项目配置会自动安装两个插件。仍需审核并信任会话钩子，以及启用当前克隆的 Git 提交钩子。GitHub 的必需状态检查由管理员对同一个远端仓库配置一次，所有电脑共用。

| 组件 | 配置位置 | 作用 |
|---|---|---|
| Revolution 和 RevGame 会话钩子 | 每台电脑的 Codex | 会话启动及上下文压缩后补充项目约定 |
| Git `pre-commit` | 每份 Git 克隆 | 检查暂存内容，失败时终止本次提交 |
| GitHub Actions `revgame-policy` | 仓库内的工作流 | 检查面向 `main` 的 PR 和推送到 `main` 的提交 |
| GitHub Actions `revgame-unity-compile` | 仓库内的工作流，使用远端 CI 授权 | 用 Unity Editor 编译 PR 或 `main` 的脚本 |
| GitHub 必需状态检查 | 远端仓库的 Rulesets | 要求检查通过后才能合并到 `main` |

## 新电脑

安装 Git、支持本地插件和钩子的 Codex，以及加入 PATH 的 Python 3.10 或更新版本。检查手写 C# 变更时还需要 .NET 8 SDK，可用 `dotnet --list-sdks` 确认。

首次克隆后进入 RevGame 仓库根目录。已有克隆时，在本地 `main` 上执行 `git pull --ff-only` 更新。

```powershell
git clone https://github.com/Yokino337088/RevGame.git
Set-Location RevGame
python --version
```

### 安装和信任会话钩子

在 Codex 桌面端打开 RevGame 仓库根目录，信任项目后，Codex 会从 **RevGame** 来源自动安装并启用 **Revolution** 和 **RevGame**。两款插件由仓库的 [marketplace.json](../../../.agents/plugins/marketplace.json) 固定版本，安装时从 Revolution 的远端获取。新电脑可以只克隆 RevGame。

若客户端未自动安装，可在仓库根目录用 Codex CLI 注册并安装：

```powershell
codex plugin marketplace add .
codex plugin add revolution@revgame-project
codex plugin add revgame@revgame-project
```

打开新的本地 Codex 会话，在 **Review hooks** 或 **Settings → Hooks** 中审核并信任两款插件各自的 `SessionStart`。CLI 可通过 `/hooks` 查看和审核。插件更新后如显示钩子已更改，复核后重新信任。 [Codex 插件安装](https://developers.openai.com/plugins/build/plugins) · [Codex 钩子信任](https://learn.chatgpt.com/docs/hooks#review-and-trust-hooks)

验收时确认两条钩子都显示已启用、已信任。在 RevGame 的测试会话完成一轮对话，再用 Codex CLI 的 `/compact` 压缩，继续一轮并核对两条钩子的运行结果。Revolution 应补充 `RevScope`、`RevTask` 等框架约定，RevGame 应补充材质目录和 `tools/RevPolicy/check.py` 检查入口。 [Codex SessionStart](https://learn.chatgpt.com/docs/hooks#sessionstart)

### 启用 Git 提交钩子

先用 `git config --show-origin --get core.hooksPath` 查看当前配置，并检查默认的 `.git/hooks/pre-commit`。如果已有提交钩子，在现有入口接入 `python tools/RevPolicy/check.py --staged` 并传递失败退出码。如果没有现有钩子，在仓库根执行：

```powershell
git config --local core.hooksPath .githooks
git config --local --get core.hooksPath
python tools/RevPolicy/check.py --staged
```

第二条命令应输出 `.githooks`。检查器退出码 `0` 表示通过，`1` 表示发现违规，`2` 表示检查无法完成。 [项目检查器说明](../../../.codex/README.md)

## GitHub 管理员配置一次

1. 在 **Settings → Secrets and variables → Actions** 新建仓库 Secret `UNITY_LICENSE`，填入适用于 CI Windows runner 的合法 Unity `.ulf` 授权文件全文。开发机现有授权可能绑定机器，需以 CI 激活实测为准。 [Unity 命令行授权说明](https://docs.unity3d.com/cn/2022.3/Manual/ManualActivationCmdWin.html)
2. 在 **Actions → RevGame Unity 编译 → Run workflow** 选择 `main` 运行一次。等待作业 `revgame-unity-compile` 通过后，再把它设为必需检查。缺少 Secret、激活失败或 Editor 未正常完成都会导致检查失败。
3. 在 **Settings → Rules → Rulesets** 新建或编辑现有的 `main` 分支规则。将规则设为 **Active**，目标分支选 `main`，绕过名单留空。
4. 启用 **Require status checks to pass before merging**，添加 **`revgame-policy`** 和 **`revgame-unity-compile`**，两项来源都选 **GitHub Actions**。如果搜索不到规范检查，可从 **Actions → RevGame 规范检查** 对 `main` 运行一次。
5. 保存后，用临时 PR 验证门禁。可加入一份错放目录的测试材质，以及一处指向不存在类型的 C# 声明。应分别看到 `REV100` 和带源码坐标的 C# 错误，合并区域应明确显示必需检查失败并阻止合并。验收后关闭 PR 并删除临时分支。

[RevGame Rulesets](https://github.com/Yokino337088/RevGame/settings/rules) · [GitHub 官方设置说明](https://docs.github.com/en/repositories/configuring-branches-and-merges-in-your-repository/managing-rulesets/creating-rulesets-for-a-repository)

规范作业 `revgame-policy` 检查资源目录、`.meta`、GUID 和部分 C# 约定。编译作业 `revgame-unity-compile` 在 GitHub 托管 Windows runner 上使用 Unity `2022.3.15f1c1`，按 Win64 目标导入并编译 Editor 脚本。Player、Android、AB 和 Play 验收仍分别执行。 [规范工作流](../../../.github/workflows/revgame-policy.yml) · [Unity 编译工作流](../../../.github/workflows/revgame-unity-compile.yml)

Fork PR 无法读取仓库授权 Secret，Unity 检查会报告授权缺失。维护者可在审阅后将要编译的提交放到 RevGame 仓库内的分支，再提交面向 `main` 的 PR。 [GitHub fork 工作流说明](https://docs.github.com/en/actions/reference/workflows-and-actions/events-that-trigger-workflows#workflows-in-forked-repositories)

检查失败时，在 PR 的 **Checks → revgame-policy** 查看带规则编号的注解。代码显示违规行列，资源显示具体路径、冲突的 `.meta` 或应补交的文件。位于差异内的代码行也可在 **Files changed** 查看，完整结果在作业日志中。 [项目检查器说明](../../../.codex/README.md)

Unity 编译失败时，在 **Checks → revgame-unity-compile** 查看 Unity 原始错误编号和文件位置。仓库内的源码会标注实际行号和 Unity 提供的列号，落在 PR 差异内的行可在 **Files changed** 中查看。依赖包缓存、授权及启动错误会显示诊断信息，有源码坐标时保留原始坐标。 [GitHub 注解说明](https://docs.github.com/en/actions/reference/workflows-and-actions/workflow-commands#setting-an-error-message)
