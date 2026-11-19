# AGENTS.md · RevGame（围棋史莱姆）

RevGame 用真实业务验证 Revolution 框架。Unity 工程为 `RevGame_Client/UnityProject/RevGame.Unity`。业务使用框架公开门面，异步用 `RevTask`，资源用逻辑路径。`Assets/Revolution` 是导出快照，框架修改回 Revolution 源仓库。

- 开工查相关源码和实际注册能力。按 [项目 Skill](.codex/skills/revgame-development/SKILL.md) 选当前专题，保留已有工作区及暂存改动。
- 新增或修改手写代码时，按 [新手注释守则](.codex/skills/revgame-development/references/code-and-comments.md) 在类型和接口、字段和属性、方法和回调旁逐项写中文注释。关键实现写清原因、顺序、边界和收尾。简单行为一句话，复杂契约分段说明。交付前核对实际 diff 和新文件，方法体有改动时也检查声明注释。
- 类、方法、属性和 `if` / `else if` / `else` 的大括号各占一行，方法参数与左括号同一行。移动 Unity 资产保留原 `.meta` 和 GUID。
- 修改 Shader、材质、纹理和渲染特效时读 [Revolution-TA](.codex/skills/revolution-ta/SKILL.md)。Shader 位于 `Assets/GameShader`，材质位于 `Assets/GameRes/Material/<模块>`。按实际管线、纹理用途与目标 AB 验证画面和资源归属。
- 导入前先在 AssetDatabase 核对已有资产、逻辑路径和包归属，只补项目缺少的资源。相同内容复用一份正式资产，打包时同一目标平台只归一个 AB，在用 Sprite 只归一个 Sprite Atlas V2。跨包通过依赖复用，发布前核对实际依赖、清单和包体。见 [Unity 与资源迭代](.codex/skills/revgame-development/references/unity-and-assets.md)。
- 最后一次影响 Unity 编译的改动后，在目标在线编辑器确认编辑态，依次刷新导入、`editor.recompile`、`editor wait`、`editor.compilation`。确认 `requestId` 对应、`finished=true`、无 Error 后再进 Play 或交付。用户对局先交接，验收后改脚本须重跑。
- 改代码或资产运行 `python tools/RevPolicy/check.py`，提交前用 `--staged`。业务验收按需求走正常入口，核对实际状态、错误和清理。资产变更走项目资源验证及真实 AB 冷启动。
- 开发验收默认在 Unity Editor Play 完成。仅在用户明确要求交付测试包，或验收项确实需要独立 Player 时打包。新测试包统一输出到仓库根目录 `打包测试(不入库)/` 下的任务子目录。
- 游戏测试优先常驻项目 RevMCP。首次连接和域重载后执行 `rev_discover`，用真实 `rev_call` 或带断言和清理的 `rev_run`，报告实际动作及结果。Editor 已在线时用 live。详见 [验收与证据细则](.codex/skills/revgame-development/references/project-acceptance.md)。
- `.codex/skills` 和项目协作说明入库。机器证据留 `.codex/work`，测试报告保留 HTML 和必要截图/GIF，禁止 ZIP 与 Profiler 原始录制。Unity 缓存、生成工程文件、外部 AB 和安装包按 `.gitignore` 排除。
- `RevGame_Client/UnityProject/Revolution.RevMCP/results/` 禁止运行输出。RevMCP 工单默认存 `.codex/work/revmcp/`。HTML 与必要附件优先放用户指定的项目文档目录。未指定时查项目既有文档，本项目使用 `RevGame_Client/文档/AI自动化测试报告/`。计划可用 `reportDirectory` 指定本轮目录。
