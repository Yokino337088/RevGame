# RevGame 换机部署与合并检查

拉取 RevGame 后，仓库里的规范、插件来源、Git 钩子脚本和 CI 工作流都已到位。新电脑还需要安装并信任两个 Codex 插件的会话钩子，以及启用当前克隆的 Git 提交钩子。GitHub 的必需状态检查由管理员对同一个远端仓库配置一次，所有电脑共用。

| 组件 | 配置位置 | 作用 |
|---|---|---|
| Revolution 和 RevGame 会话钩子 | 每台电脑的 Codex | 会话启动及上下文压缩后补充项目约定 |
| Git `pre-commit` | 每份 Git 克隆 | 检查暂存内容，失败时终止本次提交 |
| GitHub Actions `revgame-policy` | 仓库内的工作流 | 检查面向 `main` 的 PR 和推送到 `main` 的提交 |
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

在 Codex 桌面端打开 RevGame 仓库根目录，从插件目录的 **RevGame** 来源安装 **Revolution** 和 **RevGame**。两款插件由仓库的 [marketplace.json](../../../.agents/plugins/marketplace.json) 固定版本，安装时从 Revolution 的远端获取。新电脑可以只克隆 RevGame。

若插件目录里未显示 RevGame 来源，可在仓库根目录用 Codex CLI 注册并安装：

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

1. 用有仓库管理权限的账号打开 RevGame 的 **Settings → Rules → Rulesets**，新建分支规则。
2. 将规则设为 **Active**，目标分支选 `main`，绕过名单留空。
3. 启用 **Require status checks to pass before merging**，添加 `revgame-policy`，来源选 **GitHub Actions**。如果搜索不到，先在 **Actions → RevGame 规范检查** 中对 `main` 运行一次，等作业完成再添加。
4. 保存后，用临时分支开一个面向 `main` 的 PR，加入一份故意放错目录的测试材质。检查应报 `REV100`，GitHub 的合并区域应明确显示必需检查失败并阻止合并。验收后关闭 PR 并删除临时分支。

[RevGame Rulesets](https://github.com/Yokino337088/RevGame/settings/rules) · [GitHub 官方设置说明](https://docs.github.com/en/repositories/configuring-branches-and-merges-in-your-repository/managing-rulesets/creating-rulesets-for-a-repository)

`revgame-policy` 检查资源目录、`.meta`、GUID 和部分 C# 约定。若还要让 Unity 编译失败拦住合并，需要另接 Unity 编译作业，并把该作业也设为必需检查。 [RevGame 工作流](../../../.github/workflows/revgame-policy.yml)
