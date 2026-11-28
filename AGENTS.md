# AGENTS.md · RevGame（围棋大作战）

RevGame 用真实业务验证 Revolution 框架。Unity 工程为 `RevGame_Client/UnityProject/RevGame.Unity`。业务使用框架公开门面，异步用 `RevTask`，资源用逻辑路径。`Assets/Revolution` 是导出快照，框架修改回 Revolution 源仓库。

- 开工查相关源码和实际注册能力。按 [项目 Skill](.codex/skills/revgame-development/SKILL.md) 选当前专题，保留已有工作区及暂存改动。
- 新增场景、页面命令和启动依赖优先沿用 [业务自动交付契约](.codex/skills/revolution-framework/references/automatic-delivery.md)。项目提供实际玩法许可、首屏、提交点、快照和恢复策略。第一章交接与存档回归可复用 [真实 AB 交付计划](tools/automation/first-chapter-delivery.json)。
- 新增或修改手写代码时，按 [新手注释守则](.codex/skills/revgame-development/references/code-and-comments.md) 在类型和接口、字段和属性、方法和回调旁逐项写中文注释。关键实现写清原因、顺序、边界和收尾。简单行为一句话，复杂契约分段说明。交付前核对实际 diff 和新文件，方法体有改动时也检查声明注释。
- 类、方法、属性和 `if` / `else if` / `else` 的大括号各占一行，方法参数与左括号同一行。移动 Unity 资产保留原 `.meta` 和 GUID。
- 修改 Shader、材质、纹理和渲染特效时读 [Revolution-TA](.codex/skills/revolution-ta/SKILL.md)。Shader 位于 `Assets/GameShader`，材质位于 `Assets/GameRes/Material/<模块>`。按实际管线、纹理用途与目标 AB 验证画面和资源归属。
- 编写、修改或审查战斗、逐帧更新、密集 UI/特效及其它性能敏感代码时，核对 CPU 逻辑与提交、GPU、内存与 GC、缓存失效与所有权。按相同负载验证受影响的规则、画面与收益。 [性能 Skill](.codex/skills/revolution-performance/SKILL.md) · [RevGame 新手指南](RevGame_Client/文档/战斗性能优化规则/RevGame性能优化实践_新手指南.html)
- 攻坚和高技术含量任务交付时，新建或实质更新 [RevGame Wiki](https://github.com/Yokino337088/RevGame/wiki) 的对应案例。使用 [Revolution Wiki Skill](https://github.com/Yokino337088/Revolution/blob/main/Skills/revolution-wiki/SKILL.md)，写清问题、定位过程、取舍、实测和边界，按任务已有授权发布。
- 导入前先在 AssetDatabase 核对已有资产、逻辑路径和包归属，只补项目缺少的资源。相同内容复用一份正式资产，打包时同一目标平台只归一个 AB，在用 Sprite 只归一个 Sprite Atlas V2。跨包通过依赖复用，发布前核对实际依赖、清单和包体。见 [Unity 与资源迭代](.codex/skills/revgame-development/references/unity-and-assets.md)。
- 最后一次影响 Unity 编译的改动后，在目标在线编辑器确认编辑态，依次刷新导入、`editor.recompile`、`editor wait`、`editor.compilation`。确认 `requestId` 对应、`finished=true`、无 Error 后再进 Play 或交付。用户对局先交接，验收后改脚本须重跑。
- 改代码或资产运行 `python tools/RevPolicy/check.py`，提交前用 `--staged`。业务验收按需求走正常入口，核对实际状态、错误和清理。资产变更走项目资源验证及真实 AB 冷启动。
- PR 使用目标分支的 RevPolicy 规则。业务编辑器代码、运行时代码、Shader 分别放入 `Assets/Editor`、`Assets/GameScripts`、`Assets/GameShader`。原生决斗库按已登记平台路径导入，`StreamingAssets` 按各自平台的构建清单发布。未经主分支纳入基线的外部插件、Unity 包及包来源不得随 PR 引入。
- 开发验收默认在 Unity Editor Play 完成。需要交付独立测试包或正式包时，优先使用 [GitHub GameCI 出包流程](.codex/skills/revgame-development/references/gameci-build-delivery.md)。本机手动打包主要用于排查特定工具链问题。确需在本机验证独立 Player 时走 RevCLI，新测试包统一输出到仓库根目录 `打包测试(不入库)/` 下的任务子目录。
- 云端 Unity 构建按用户当次要求手动触发。Windows、Android 和 WebGL 分别使用对应的测试包或正式包入口。WebGL 构建先交付 Actions 产物，网页发布走现有发布流程。普通 push 和 PR 照常跑 RevGame 规范检查。目标为 `main` 的 PR 还要通过云端 `Unity 脚本编译`。出包使用所选提交里已生成的目标平台 `StreamingAssets` AB，构建前核对清单与 SHA-256，上传前核对同一批包内字节。资源更新后先生成并提交目标 AB。交付时核对远端提交、Build ID、包内资源和可下载产物。
- 安卓普通远征以真实选关、手牌、战斗回合和 Boss 为主验收。低端机平均至少 45 FPS，整帧 P95 严格低于 40 ms，优先争取低于 35 ms。81 棋和其他固定压力用于容量、峰值和回归。
- 游戏测试优先常驻项目 RevMCP。首次连接和域重载后执行 `rev_discover`，用真实 `rev_call` 或带断言和清理的 `rev_run`，报告实际动作及结果。Editor 已在线时用 live。详见 [验收与证据细则](.codex/skills/revgame-development/references/project-acceptance.md)。
- `.codex/skills` 和项目协作说明入库。确需另存的机器证据留 `.codex/work`，禁止 ZIP 与 Profiler 原始录制。Unity 缓存、生成工程文件、外部 AB 和安装包按 `.gitignore` 排除。
- 自动化默认在对话汇报。计划和响应优先走内存，确需临时工单时放系统临时目录或项目忽略目录，消费后立即永久删除。`RevGame_Client/UnityProject/Revolution.RevMCP/results/` 禁止运行输出。
- 用户明确要求才生成自动化测试报告。用 `reportDirectory` 写入用户指定的项目文档路径。只要求出报告时设 `report: true`，优先沿用 `RevGame_Client/文档/AI自动化测试报告/`。正式报告只保留 HTML 和必要截图/GIF。
