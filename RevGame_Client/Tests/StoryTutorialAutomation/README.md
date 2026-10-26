# 真实棋盘剧情与教学回归（2026-10-26修订）

新手教学位于GameScene，使用同一BattleService、BoardModel、81个真实BoardCellView和原有PreparePanel/BattleHudPanel。旧TutorialScene已移除，旧UI棋盘计划已替换。

## 执行
1. 编辑器停止Play，等待编译无错，调用game.story.configure。
2. 资源变动时，通过game.background.build分别构建StandaloneWindows64、Android；game.background.mode(enabled=true)启用PC真实AB。
3. game.story.test-arm(flags=0)使用首次玩家内存进度；测试注册同时租用BeginScorePreview保护永久积分。
4. editor.play(scene=Assets/Scenes/StartScene.unity)，再runtime.start（不传port=0），取得endpointFile。
5. 使用RevMCP同时传--project与--runtime-endpoint。先rev_discover，再rev_run执行ab.plan.json。运行态请求走HTTP，编辑器用于开始/停止和收尾。
6. finally返回大厅、等待交付、停止Play并game.story.test-disarm。预检失败或提前中断，也须执行停止与disarm。

完整计划包含：透明剧情框、3帧向导、真实AB绑定、EventSystem手牌与开战按钮、Physics2DRaycaster点格子、错序拒绝、真实扣牌/封气/换位、同场景战斗自然结算、返回清理。截图用于画面证据，状态断言决定业务结果。

flags=1验证开场已完成但教程未完成的续接及中途退出；flags=3验证已完成存档跳过首次教学。story.preview仅在隔离会话允许，不能用它宣称通关。tutorial.card/cell仅为同一BattleLogic的受控快捷入口；指针链路请用ui.click和RevGame/点格子。

## 本轮结果与边界
报告：文档/进度同步/真实棋盘新手教学_2026-10-26/真实棋盘新手教学测试_2026-10-26.html。
最终HTTP回归无运行错误，PC/Android资源构建成功。第一次文件桥AB测试出现2次inbox共享冲突，失败证据保留；切换既有HTTP后重新通过，未更改框架快照或降低断言。
未执行Android真机、多分辨率、全六章战役。教学第一局预置同伴，其耗时不作为常规关卡平衡结论。

## 柔和高亮与GM重播

`RevGame/新手教学`（无参数）通过真实GameFlow重播，替换当前战役，保留已有教学进度。开发版/Editor可调用；当前教程、剧情、过场中重复调用会返回明确拒绝。

使用flags=3启动后执行`soft-highlight.plan.json`，验证五步真实操作、Shader、文本高度、提示框与实际控件无重叠、从战斗再次重播、返回清理。结束后停止Play并解除test-arm。测试报告仅HTML与画面/GIF，不再生成ZIP；机器证据放忽略的`.codex/work/tutorial-soft/`。

## 加强魔法教学引导

strong-guidance.plan.json使用flags=3，覆盖五个真实教学步骤、提示框与目标双重高亮、落子和换位目标闪烁、文字放大后的无裁切与无重叠、原始手牌/开战按钮点击和返回清理。四组各12帧截图需检查实际动态效果，不能只检查Shader是否支持。报告见文档/进度同步/加强新手引导_2026-10-26/加强新手引导测试_2026-10-26.html。
