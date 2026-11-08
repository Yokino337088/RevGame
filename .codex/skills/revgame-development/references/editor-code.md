# RevGame Editor 代码规范

适用于 `Assets/Editor/RevGame` 下的生成、构建、迁移、预览、Inspector、编辑器动作和验收工具。项目以 [AGENTS.md](../../../../AGENTS.md) 为准。命令与操作见 [编辑器与资源流程](unity-and-assets.md)，架构依据见 [RevCLI 与项目 Editor 的职责审查](../../../../RevGame_Client/文档/架构/RevCLI与项目Editor职责审查_2026-11-08.html)。

## 什么时候写 Editor 代码

| 需求 | 选型 | 在 RevGame 中的例子 |
|---|---|---|
| 创建普通 Prefab，局部改节点、布局、材质、组件或导入字段 | 先发现并使用已有 RevCLI 动作，写入后读回 | `prefab.inspect` → `prefab.modify` → `prefab.inspect`。新对象树可用 `scene.compose` 再保存为 Prefab |
| 固定树和参数需要重复制作 | 将 JSON 配方和重建入口入库，沿用现有 CLI 动作 | 固定的父子结构、锚点和序列化字段用数据表达 |
| 要根据依赖计算、组装多种资源、保留迁移策略或运行项目门禁 | 扩展已有项目生成器、导入器或检查类 | `GameEndlessBuild` 装配场景、棋盘、HUD 与入口。`GameSpriteAtlasBuild` 按使用范围分组，`GameResourceBuild.Configure` 串联门禁和框架构建 |
| 要检查项目专属配置、画面或运行结果 | 复用 Editor Inspector、预览和检查入口，清楚区分编辑态与 Play | `StoryBookInspector` 检查剧情资产字段，`GameSceneTransitionChecks` 验证真实流程完成和资源就绪 |
| 缺少可复用的 Unity 基础操作 | 核对实时注册、版本与扩展安装。确认为通用缺口时回 Revolution `main` 修改、验证、导出再同步 | `atlas.inspect/create/set-packables` 处理给定图集，项目规则计算成员与包归属 |
| 需求改变玩家操作、规则、状态、存档或流程 | 实现在运行期对应服务、Logic 或面板。Editor 可以制作输入或调用开发态测试入口 | 编排战斗、奖励结算、UI 交互仍走玩家共用路径 |

若目标已经由生成器维护，永久规则要改源数据、配方或生成器，同时更新必要输出。一次局部编辑只触及目标。整套重建前列出会覆盖的资源，确认范围和对手工调整的保留策略。

## 目录、命名与依赖

- 按功能归入已有 `Assets/Editor/RevGame` 子目录。场景装配在 `Scenes`，无尽配方在 `Endless`，剧情工具在 `Story`，渲染和预览在 `Rendering`，分包与动作接线沿用 `ResourceBuild` 的现有分工。新类型沿用同目录命名与命名空间。可按职责拆现有 partial，保留类型身份和移动资产的 `.meta`。
- 名称表达职责，例如 `*Build` 负责产物，`*Automation` 提供动作，`*Checks` 做断言，`*Preview` 或 `*Inspector` 提供编辑器观察。优先扩展同一产物的已有所有者，避免为每一次制作另建脚本和菜单。
- `UnityEditor`、AssetDatabase、Importer、菜单和生成器只放 Editor 侧。核对 `RevGame.ResourceBuild.Editor.asmdef` 的引用和平台边界。跨程序集复用已有固定目标的接线，禁止将方法名、类型名或脚本执行权交给命令参数。运行期使用生成资产和 Rev 门面。
- 注释用中文，说明用途、输入约束、产物所有者、为什么要按这个顺序、失败如何收尾。方法签名的 `(` 与参数同行。方法体带大括号，所有 `if / else if / else` 分支逐行展开，类、方法和属性的大括号独占一行。生成 C# 时也检查输出模板。

## 配方和动作如何写

1. **先声明输入、产物和写入边界。** 明确由谁重建，每个输出是新建、局部修改还是全量重建。写盘前验证目标身份、路径、必需的素材和组件引用。对同样输入跳过无变化的保存或重导入。失败信息说明阶段、目标和已发生的副作用，部分失败时优先清理本次临时产物。
2. **用 Unity API 保存和读回。** 原资源移动用 AssetDatabase 或 `asset.move`，保留 GUID。局部修改 Prefab 使用加载内容、按节点修改、保存、`finally` 卸载的顺序。显式校验绑定字段、按钮路由、Sprite 和变更后的 GUID。场景改动记录目标及 dirty 状态，禁止文本替换序列化资产和覆盖其它未保存场景。
3. **让菜单和命令共用同一配方。** 已有公开入口则复用。需要新入口时，用 `RevAutomationAction` 注册稳定的 `game.<功能>.<动作>`，按当前契约限定参数键、环境和确认条件。动作层只校验参数、调用固定入口并返回可断言结果。只读审计、配置写入、实际构建与发布分开，参照 `game.resources.audit/configure/validate`。
4. **按项目资源规则制作。** UI 复用原图、正确裁切和九宫格，适用底板用 `Image.Type.Sliced`。活动 Sprite 按项目规则恰好归属一个 Sprite Atlas V2。保留大厅、章节共享、章节专属和无尽的加载边界。图集归属、源图标记、依赖、映射与门禁走 `GameResourceBuild.Configure`，运行时图集绑定走既有 `GameSpriteAtlasService`。
5. **自动触发控制写入范围。** Inspector 重绘和只读查询不触发制作、迁移或发布。Inspector 的显式用户编辑走 `SerializedObject`，保留 Unity 的撤销和脏标记。导入回调只作用于约定资源，防止触发自身重导入。预览和检查结束时清理本次临时对象、场景和设置。

现有 `game.resources.audit` 展示了动作层的写法。注册只读动作时声明运行环境和参数键，把扫描与审计放在 `GameResourceUsage` 内。新增动作使用自己的唯一 ID。

```csharp
/// <summary>只读返回资源使用者和包名，供配置前后对照。</summary>
[RevAutomationAction("game.resources.audit", Environment = "edit", Keys = new string[0], Description = "只读审计真实业务引用与 AB 标记")]
public static object Audit(RevAutomationRequest request)
{
    return GameResourceUsage.Audit();
}
```

## 验收与提交

- 变更 Editor C# 后等编译稳定，查新增 Console 错误，再用真实 RevCLI 重新发现动作和参数。核对实际落盘的引用、GUID、生成清单和产物范围。对写入入口检查合法请求、典型拒绝和收尾，重建配方用相同输入再执行一次并核对差异。
- 影响正式资源时，从真实入口检查对应平台 AB 的冷加载、首次显示与重入。涉及玩法、流程或 UI 运行期断言时，按本项目约定使用 RevMCP 验收。编辑态写盘成功、Play 业务完成与目标平台表现分别记录。
- 同一编辑器的资产写入、导表、构建、Play/Stop 串行。已有实例使用 live，保留原场景和选择。超时后先查工单、Console 与落盘结果，再决定是否重试。进一步的测试及交付见 [验收与交付](validation-and-delivery.md)。
- 提交前检查源配方、必要的正式输出和 `.meta` 是否配套。临时资产、机器回执、Unity 缓存和本地测试包按项目忽略规则处理。

同类源码可从 [无尽生成](../../../../RevGame_Client/UnityProject/RevGame.Unity/Assets/Editor/RevGame/Endless/GameEndlessBuild.cs)、[资源构建](../../../../RevGame_Client/UnityProject/RevGame.Unity/Assets/Editor/RevGame/ResourceBuild/GameResourceBuild.cs)、[图集分组](../../../../RevGame_Client/UnityProject/RevGame.Unity/Assets/Editor/RevGame/ResourceBuild/GameSpriteAtlasBuild.cs)、[资源动作接线](../../../../RevGame_Client/UnityProject/RevGame.Unity/Assets/Editor/RevGame/ResourceBuild/GameResourceUsageAutomation.cs) 和 [切场景回归](../../../../RevGame_Client/UnityProject/RevGame.Unity/Assets/Editor/RevGame/Scenes/GameSceneTransitionChecks.cs) 起读。
