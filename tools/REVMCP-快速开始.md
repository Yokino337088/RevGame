# RevMCP 快速开始

给第一次接手这个仓库的人：**一条命令**让 AI 客户端直接操作这台机器上的 Unity 工程。

---

## 1. 一键初始化

Windows 直接双击 `tools/init-revmcp.cmd`，或者在仓库根目录执行：

```powershell
.\tools\init-revmcp.ps1
```

跑完看到 `[OK] 初始化完成，环境可用。` 就成了。

### 为什么是 PowerShell 而不是 Python 脚本

这个脚本的核心任务是**确认本机有没有可用的 Python 3.10+**。如果脚本自己就要求装 Python，
那"机器上没有 Python"这种最需要脚本的情况反而跑不起来——死循环。
PowerShell 是 Windows 自带的，`tools/` 里也已有 `sync-framework.ps1`，保持一致。

> 注意：**RevMCP 本身仍然需要 Python**（3.10+，纯标准库，不需要 pip 装任何东西）。
> 这个脚本负责找到并验证它，而不是装一个。

### 它到底做了什么

| 步骤 | 检查内容 | 失败说明 |
|---|---|---|
| 1 | 找 `py -3` / `python` / `python3`，取 `sys.executable` 绝对路径，版本 ≥ 3.10 | 版本太低或只在 Microsoft Store 里（那个 `python.exe` 是占位程序，会弹商店） |
| 2 | `Revolution.RevMCP`、`Revolution.Automation`、Unity 工程目录是否齐全 | 目录结构不对，或 `-Project` 指错了 |
| 3 | **真的拉起一次 MCP 服务**，发 `initialize` + `tools/list`，拿到工具列表 | 配置能连上的唯一硬证据 |
| 4 | 生成 3 份客户端配置到 `tools/revmcp-configs/` | — |
| 5 | 探测 Unity 是否已连上（不通过不算失败） | 编辑器没开或还在编译，属正常状态 |

> 第 3 步是关键。只检查"文件存在"和"JSON 能解析"是不够的——配置里少一个反斜杠，
> 客户端只会静默显示"连不上"，脚本替你把这一步提前做完。

---

## 2. 配置你的 MCP 客户端

脚本会在 `tools/revmcp-configs/` 生成三份现成片段，内容里的 Python 路径和工程路径已是本机绝对路径：

| 文件 | 用在哪 |
|---|---|
| `mcp-generic.json` | 绝大多数客户端：CodeBuddy、Cursor、Claude Desktop、Windsurf 等（`mcpServers` 结构） |
| `vscode-mcp.json` | VS Code 的 `.vscode/mcp.json`（键名是 `servers`，需要 `"type": "stdio"`） |
| `codex-config.toml` | OpenAI Codex 的 `~/.codex/config.toml` |

对应的配置文件位置：

| 客户端 | 配置文件 | 用哪份片段 |
|---|---|---|
| CodeBuddy | `~/.codebuddy/mcp.json` | `mcp-generic.json` |
| Cursor | `~/.cursor/mcp.json` | `mcp-generic.json` |
| Claude Desktop | `%APPDATA%\Claude\claude_desktop_config.json` | `mcp-generic.json` |
| VS Code | `<工程>/.vscode/mcp.json` | `vscode-mcp.json` |
| Codex | `~/.codex/config.toml` | `codex-config.toml` |
| 其它 stdio 客户端 | 按其文档放进 `servers` / `mcpServers` | `mcp-generic.json` |

### 懒得手贴：让脚本自己合进去

```powershell
.\tools\init-revmcp.ps1 -Write
```

它只会动**本机确实存在**的客户端配置文件，并且：

- 改之前把原文件备份成 `xxx.json.bak-20261023-153000`；
- 已存在同名 `revolution` 条目就覆盖，不会塞出两个；
- TOML 只做最小追加，不重排你已有的内容；
- 没装的客户端直接跳过并打印提示。

### 手贴也行：核心就是这条命令

所有客户端填的都是同一条（脚本会把真实路径填好，别手打）：

```
<python.exe 绝对路径>  <仓库>/RevGame_Client/UnityProject/Revolution.RevMCP/rev_mcp.py  --project  <仓库>/RevGame_Client/UnityProject/RevGame.Unity  serve
```

JSON 里用正斜杠（`C:/Program Files/...`）最省事；TOML 用单引号字面量（`'C:\Program Files\...'`）保留反斜杠原样。

---

## 3. 确认真的通了

1. 打开 Unity 工程，等右下角编译转圈结束；
2. 命令行直接验证，不经过任何客户端：

```powershell
python Revolution.RevMCP\rev_mcp.py --project RevGame_Client\UnityProject\RevGame.Unity discover
```

返回 `success: true` 且 `actions` 里有条目 → 桥是通的。
（这里用 `python` 是因为要验证的正是客户端的执行路径；路径以脚本打印的为准。）

3. 回到客户端重启 / Reload MCP，先让助手调 **`rev_discover`**，再按返回的真实 action 名调用。
   **不要让助手凭想象编能力名**——真实注册表是什么，就只能用什么。

---

## 4. 常见故障

| 现象 | 原因 | 处理 |
|---|---|---|
| 脚本报"没有找到可用的 Python 3.10+" | 没装、版本低，或只装了 Microsoft Store 版 | 装 3.10+ 并勾选 "Add python.exe to PATH"，重开终端 |
| 客户端显示已连接但没有工具 | Python 路径写错，或指向了另一个 Python | 用脚本生成的值，别手打 |
| `discover` 连接超时 / 找不到工程 | Unity 没开，或工程还在编译 | 编辑器打开并等编译完成；这不是 RevMCP 坏了 |
| 改了配置没生效 | 客户端要重启或手动 Reload | CodeBuddy / Cursor 在 MCP 设置里点刷新 |
| 提示 `Revolution.Automation not found` | 两个目录被拆开或改名了 | 保持 `Revolution.RevMCP` 与 `Revolution.Automation` 同级 |
| 中文乱码 | 脚本用了 UTF-8；`cmd` 按 OEM 码页读批处理 | 直接跑 `.ps1`；`.cmd` 只作双击入口且保持纯 ASCII |

---

## 5. 边界（别踩）

- **写操作要显式确认**：改资产、跑 GM 改数值这类动作，只有带 `confirm` 才会真的执行。
- **正式发行包没有测试接口**：运行时桥只在显式开启的独立开发版 Player 里存在。
- **不执行任意 shell**：外部脚本必须用 manifest 显式登记，RevMCP 不会执行模型随手生成的命令。
- **Unity 必须是"当前打开的那个工程"**：`-Project` 指错目录时，`discover` 会连到一个不该动的工程。

---

## 6. 想了解更细的

- `RevGame_Client/UnityProject/Revolution.RevMCP/README.md` —— 目录职责与命令全集
- `RevGame_Client/UnityProject/Revolution.Document/自动化工具/RevMCP使用说明.md` —— 完整教程、接口注册、计划格式
- Unity 菜单 `Revolution.Tools/AI/RevMCP 控制台` —— 在编辑器里直接看到连接状态和可复制配置
