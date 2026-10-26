# 六章魔法棋盘验收

先通过 RevMCP `rev_discover` 确认当前项目，再在编辑态调用 `game.board-magic.configure` 和 `game.background.build`（PC / Android）。Play 使用 StartScene，真实AB模式，`game.story.test-arm flags=3` 隔离首次剧情和积分；启动后必须等待 `story.state.screen=Home`、`transitioning=false`、`blocked=false`。

执行 `RevGame/解锁六章关卡` 后依序运行 chapters.json 的六份 rev_run 计划。每轮走真实选关和剧情按钮，检查81格、单一共享章节材质、正确Shader、其他五章材质引用为0、合法落点无误亮/漏亮，再截图；finally回大厅。

`board.magic.state` 为开发态只读状态；眼睛的frame、name与packed来自真实SpriteRenderer。`board.magic.eye-fixture`仅编辑器隔离战斗界面可用，通过已有真实Place/RefreshGoState规则创建双方眼位，不能直接写眼位标记。此夹具回到第一章，测试后必须返回大厅。

连拍眼位至少4秒，确认同一格确实出现四帧；截图转小尺寸GIF进入HTML，原始JSON保存在.codex工作目录，禁止生成ZIP。结束后检查runtime.logs、退出Play并test-disarm。

章节材质随同章背景AB按需加载；BoardCell预制体不硬引用六章材质。新增常驻效果复用原有格面Renderer，CPU不逐帧上传材质；眼睛由BoardView单一20Hz调度，帧改变才赋值。
