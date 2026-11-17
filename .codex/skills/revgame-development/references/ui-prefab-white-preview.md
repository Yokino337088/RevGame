# UI 预制体白块排查

## 已复现案例

2026-11-17，Prefab Mode 的 `StoryPanel` 满屏白色，右上跳过按钮和对白框仍可见。`storyBackdrop` 是空纹理、默认材质、不透明且铺满画布的 `RawImage`。Unity 用内置白纹理绘制这个空图槽。Prefab Mode 没有执行 `StoryBackdrop.PrepareAsync` 和剧情帧绑定，没有背景、立绘和台词注入。独立手牌、布阵手牌和奖励三选一也暴露了同类空图槽。

本次全量扫描覆盖 49 个预制体、32 个 UI 预制体和 295 个图像控件。4 个预制体的 12 个槽位有编辑态白块，修后 `unresolved` 和 `missingTexture` 均为空，并有同尺寸离屏对比。运行时 Play 属于单独的验收范围。

## 诊断和复测

1. 用 `editor.selection` 确认实际编辑的 Prefab。发现并运行 `game.ui.prefab-audit`，查看 `unresolved`、`missingTexture` 和 `previewed`。扫描覆盖 `Assets` 下所有预制体。透明点击区、隐藏层、纯色底板和自行采样的 Shader 各自承担原有用途。
2. 默认材质且图槽为空时，查业务何时赋图。用 `game.ui.preview-capture` 传 `prefab`、`output` 和 `plain=true` 取证，默认参数使用编辑态示例。同一尺寸比较修前修后画面。有运行时数据源的槽位可用 [GameUiPrefabPreview](../../../../RevGame_Client/UnityProject/RevGame.Unity/Assets/Editor/RevGame/Rendering/GameUiPrefabPreview.cs) 从现有配置取示例。用 `game.ui.prefab-preview-state` 核对实际 Prefab Stage 的临时内容与脏标记。保存、离开预制体和进入 Play 时清理预览，核对原 Prefab 没有写入示例。
3. 已有 Sprite 但取不到像素时，先查 `mainTexture`、图集归属和当前 Build Target。确认是编辑器缓存后再用 `game.ui.preview-recover` 或 `game.atlas.preview-repack`。编辑态图集绑定沿用 [GameEditorAtlasPreview](../../../../RevGame_Client/UnityProject/RevGame.Unity/Assets/Editor/RevGame/Rendering/GameEditorAtlasPreview.cs)。
4. Play 也白时，从正常入口做真实目标 AB 冷启动，查首帧加载屏障、图集回调和材质纹理。序章可用 `game.story.test-arm` 进入隔离首启，通过 `story.yaya-state` 核对背景与立绘像素，跳过剧情后退出 Play 并解除隔离。正式资源有变动时沿用 [资源验证流程](unity-and-assets.md)。

## 新增或改动 UI 时的检查

交付前核对受影响的 `Image` 和 `RawImage`，尤其是全屏背景、常驻图标、池化卡片。为运行时图槽确定加载前、成功、失败和回池的显示状态。需编辑动态排版时用真实配置做编辑预览，不把样例塞进正式 Prefab。发现新白块后在同一环境复测。

审计直接读取资产时沿层级检查 `activeSelf` 及组件 `enabled`，并考虑 `CanvasGroup` 和颜色透明度。用 `activeInHierarchy` 会把未实例化的资产误判为隐藏。图集缺像素的检测必须检查实际材质采样所用的贴图。
