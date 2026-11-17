# 验收与排错

## 按改动选择证据

1. 查完整节点和入边。用同一份文档预检与保存。复查 `valid/changed/written/issues/hash`，再读回目标节点和相关入边。只改一句时与原结构比对。图更新至少从能到达目标的入口走一条新的路径和一条受影响的保底、锁定或拒绝路径。存档兼容需求要用代表性旧档。
2. 改脚本或程序集后，在目标在线 Unity 完成项目要求的编辑态刷新、`editor recompile`、`editor wait` 和 `editor compilation`。确认工单匹配且无 Error，再进入 Play。编译后重新发现。资产改动用宿主已有资源/图集校验和目标模式冷启动。
3. 从项目正常启动和资源模式进入。试读仍适合验证变量、图、回退和存档语义。UI、图集、输入、音频和宿主信号用真实运行期验证。

## 真实运行时入口

在 Editor Play、Development Build 或定义 `REV_AUTOMATION` 的测试环境，RevMCP 先 `rev_discover(target="game", query="gal.")`。Editor 用 live，独立 Player 按项目流程绑定其开发态 endpoint。连接出现 capability unavailable 时先确认实际工程、Play 是否稳定、桥接和请求注册。

| 动作 | 参数和可观察结果 |
| --- | --- |
| `gal.open` | `path` 为真实逻辑路径，可传 `slot/instant`。`accepted` 只确认接单。轮询 `gal.state` 检查 `pending=false/open=true/error=null` 和预期首节点 |
| `gal.state` | `open/pending/error/storyId/node/kind/text/revealed/backgroundVisible/settingsOpen/historyOpen/mode/theme/choices/variables/progress/history` |
| `gal.close` | 关闭默认面板并使待加载请求失效。再读 `gal.state` 确认 `open/pending/settingsOpen/historyOpen` 均为 false |

默认开发态动作使用内存存档和试验信号回调。验证真实宿主副作用时走该项目玩家入口或显式业务接口。普通用户界面通过实际 `ui.dump` 与 `ui.click` 操作，用当次控件 ID；项目有自定义 UI 时核对实际名字和射线。更改脚本、切域或重进 Play 后重新查询控件。`rev_run` 用 `expect` 核对业务字段，以有界 `until` 等待只读状态，`finally` 关闭本次剧情并核对清理。检查运行日志和新增 Error。视觉要求保留真实截图，有动作变化时按项目约定记录动图。

### 推荐断言

- 节点细改从 `node.find` 读出原文、ID 和分支，确认新文案及未要求修改的字段。加节点并改线后核对前驱、插入点、后继；删节点后核对起点、Next 和选项入边。以 `story.simulate` 走所有被修改的关键分支。
- 打开剧情时验证首节点、主题、文字、舞台及首个互动。选择某项前看 enabled，点击后查节点与变量。拒绝或无选项走保底时查最终可交互节点。
- 主题、设置或输入有改动时，查主面板、设置等层级的按钮位置和射线。设置内暂停自动推进，旧控件无法穿透。检查回看、回退、快存快读、已读跳过与回到剧情。把真实 Sprite/布局观测与 SO 值比对。
- 资源改动要覆盖实际模式下首次打开及退出重开。宿主信号检验成功、错误、重复和发生关闭后的迟到回调。看见失败要保留首轮响应，退出并清理测试资产后再结案。

## 按症状定位

| 症状 | 核对点 |
| --- | --- |
| CLI 没有 `node.*` | 从对应工程旁执行 `gal --help` 并读版本。用 `commands --live --query gal.node.` 区分 Python 副本与 Unity Gal Editor 程序集是否落后 |
| 查询为空或有多条 | 核对资产、`--id` 大小写、类型过滤、分页和上下游关系。变更前从查询结果拿精确 ID |
| 修改拒绝或图没更新 | 查 hash 冲突、资产 dirty、节点重复、未知字段、悬空 Start/Next/Target。合法的预检和 no-op 也可能返回 `written=false` |
| 文案写回后显示旧文案 | 确认实际是当前资产与当前会话，重开加载新快照。检查 TextKey 翻译覆盖、运行资源模式和 AB 是否已发布 |
| `gal.open` 等不到 | 核对 `RevInitialize`、逻辑路径、默认面板键、`pending/error`、平台主包与 ResMap，以及是否换了工程实例 |
| 图集或媒体白图、音频空 | 核对实际资源类型、图集切片名、主题引用、字体、包名、依赖和运行时 AB 来源。直读成功仍要验证实际包 |
| 弹层按钮不可点 | 读 `settingsOpen/historyOpen/mode`，再看是否被其他 RevUI 层覆盖、是否可交互、实际射线命中和 RectTransform |
| 自动分支兜圈或旧档打不开 | 从真实起点复跑变量效果与条件，检查无条件出口顺序。用当前修订、ID 和 History 引用判断档案兼容 |
| 再进 Play 才出现缓存或已销毁对象错误 | 记录 Domain/Scene Reload 配置与核心异常栈。先在标准域重载从冷态确认。宿主无域重载支持以实际回归结果为准 |

检查结束时，关闭本次启动的 Play，恢复临时修改的资源模式和测试现场，按宿主约定保留报告。汇报已验证的入口、平台、断言、实际错误及清理。Editor Play、独立 Player 和目标真机分别记实。
