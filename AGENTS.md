# AGENTS.md · RevGame（围棋史莱姆）

RevGame 用真实业务验证 Revolution 框架。Unity 工程为 `RevGame_Client/UnityProject/RevGame.Unity`。业务使用框架公开门面，异步用 `RevTask`，资源用逻辑路径。`Assets/Revolution` 是导出快照，框架修改回 Revolution 源仓库。

- 开工查相关源码和实际注册能力。按 [项目 Skill](.codex/skills/revgame-development/SKILL.md) 选当前专题，保留已有工作区及暂存改动。
- 业务代码写中文契约注释。类、方法、属性和 `if` / `else if` / `else` 的大括号各占一行，方法参数与左括号同一行。移动 Unity 资产保留原 `.meta` 和 GUID。
- 最后一次影响 Unity 编译的改动后，在目标在线编辑器确认编辑态，依次刷新导入、`editor.recompile`、`editor wait`、`editor.compilation`。确认 `requestId` 对应、`finished=true`、无 Error 后再进 Play 或交付。用户对局先交接，验收后改脚本须重跑。
- 改代码或资产运行 `python tools/RevPolicy/check.py`，提交前用 `--staged`。业务验收按需求走正常入口，核对实际状态、错误和清理。资产变更走项目资源验证及真实 AB 冷启动。
- 开发验收默认在 Unity Editor Play 完成。仅在用户明确要求交付测试包，或验收项确实需要独立 Player 时打包。新测试包统一输出到仓库根目录 `打包测试(不入库)/` 下的任务子目录。
- 游戏测试优先常驻项目 RevMCP。首次连接和域重载后执行 `rev_discover`，用真实 `rev_call` 或带断言和清理的 `rev_run`，报告实际动作及结果。Editor 已在线时用 live。详见 [验收与证据细则](.codex/skills/revgame-development/references/project-acceptance.md)。
- `.codex/skills` 和项目协作说明入库。机器证据留 `.codex/work`，测试报告保留 HTML 和必要截图/GIF，禁止 ZIP 与 Profiler 原始录制。Unity 缓存、生成工程文件、外部 AB 和安装包按 `.gitignore` 排除。
