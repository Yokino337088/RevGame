---
name: revolution-gal
description: 为安装 Revolution.Gal 的 Unity 项目创建、改写和验收视觉小说剧情。适用于对白、旁白、分支、选项条件、立绘、语音、图鉴、存读档、RevGal Panel 与 View 适配，以及用 RevCLI 生成剧情 SO。普通过场或项目原有 StoryBook 仍按该项目的现有入口维护。
---

# Revolution.Gal

先定位宿主工程的 `Assets/Revolution.Gal` 或 UPM 安装，以及真正的 RevAB 资源根。Gal 是单独的扩展，核心和现有项目剧情可独立运行。查 [扩展使用文档](https://github.com/Yokino337088/Revolution/tree/gal) 和目标工程的实际能力。

## 创作与导表

- 先明确入口、稳定节点 ID、玩家可见选项与结局。需要按已有内容生成时，先导出当前 SO 作模板并保留 ID。
- 编辑器走 **Revolution.Tools → Gal → 剧情工作台**。右侧写正文、舞台和条件，中间连接出口。出版前检查底部问题列表，从目标节点试读至少一条正路和拒绝或保底路。
- 自动化先发现 `gal.story.generate/create/validate/import/export/simulate` 和 `gal.ui.create`。用 `gal story.generate --document <文件>` 校验并在资源根下按 ID 创建 SO，读回逻辑路径与包名。`--check-only` 预检，`--path` 和 `--bundle` 可以覆盖默认值。已有资产只有实际需要更新时才传 `--overwrite`，它保留 GUID。不要用 YAML 文本拼 SO。
- 舞台和音频填 Revolution 逻辑路径。舞台空值沿用，单减号清除。实际 AB 模式需要真实图集、ResMap 和对应平台资源包；验证阶段从正常入口加载。

## 接运行时

- 默认走 `RevGal.OpenAsync(逻辑路径, options)`。`gal ui.create` 生成默认 Prefab 和共享主题，`--theme` 绑定已有主题。工作台的 **编辑面板主题** 可以设置锚点、偏移、字体、图集切片与独立 Sprite。另做界面时用 `RevGal.LoadAsync` 获取会话，交给自己的 `RevUIPanel<RevGalSession>` 并负责释放。
- **Session** 推进、验条件、写变量、维护档案。**Panel** 接 RevUI 生命周期、持有会话与媒体租约、协调信号和输入。**View** 消费帧和发出玩家意图，保持 UGUI / 表现逻辑可替换。编辑器和 CLI 共用同一 SO 模型。
- 信号用稳定名和参数绑定宿主，异步完成前后检查会话与 Token。信号结束会建立回退边界。需要改存档兼容合同时调整剧情 Revision。
- 真实验收先 `rev_discover`。Editor 的 simulate 是规则预览。Play 使用 `gal.open/state/close` 和实际 UGUI 控件核对主题、分支、存读档、设置弹层的暂停及退出。调试档案只在本次 Play 内。功能与修复回归按 `revolution-ai-automation` 工作流取证。
