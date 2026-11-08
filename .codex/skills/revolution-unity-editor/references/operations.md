# 按任务选择 Unity 操作

先发现当次 action 的 keys/inputSchema。以下是框架当前命名，不保证旧桥已安装；检查→修改→读回，不猜属性路径或实例 ID。

| 任务 | 动作 | 关键纪律 |
|---|---|---|
| 场景管理 | scene.list/open/new/save/save-all/close/active/hierarchy | 先查 dirty，替换/关闭不能丢未保存内容；新场景保存需明确相对资产路径 |
| 一次搭一片 | scene.compose、scene.capture、sceneview.frame | 一份 JSON 建树，失败全回滚；`@相对路径` 引用同批节点；截图不含 Overlay UI，不证明玩法。见 [全链路开发](fullchain.md) |
| 对象 | gameobject.find/create/primitive/configure/inspect/set/parent/duplicate/delete | ID 来自当次结果；primitive 是真实网格与 Collider；configure 改名称/激活/Layer/Tag/Static，Layer/Tag 须先存在 |
| 脚本与文本 | script.create/attach、editor.recompile、asset.read-text/write-text/import-file | 写完 editor wait 再挂载；类名与文件名一致；Editor 程序集的类不能挂；序列化资产（按扩展名和 `%YAML` 内容判断）禁止文本写入；路径段不能以点或空格结尾、不能含冒号；覆盖需 confirm |
| 三维变换 | transform.get/set | space=local/world；position/eulerAngles 三项数字；localScale 始终局部，不伪造“世界缩放” |
| 组件 | component.types/list/get/add/set/remove | types 发现真实类型，get 返回可写属性；set 保留序列化/Undo/Prefab override；引用字段自动换算组件与子资产，类型不符明确报错 |
| 资产与导入 | asset.find/inspect/create/set/copy/move/delete/import/importer/importer-set | Assets/Packages 只读，写入限定 Assets；GUID 保留，禁止越界与目标覆盖 |
| Sprite Atlas V2 | atlas.inspect/create/set-packables | 读取或写入纹理成员，分组、AB 标记和平台策略由项目配方决定 |
| 材质 / Shader | shader.list、material.inspect/create/clone/set | 先查 Shader/属性/关键字真实声明；材质所有参数预检后提交，不接受任意属性猜测 |
| UI 面板 | ui.create、rect.get/set | kind=Canvas/Panel/Image/Text/Button；非 Canvas 必须在 Canvas 下；rect.set 的偏移与 anchoredPosition/sizeDelta 二选一，单独改锚点会移动矩形 |
| Prefab | prefab.create/instantiate/inspect/modify/apply/revert/unpack/variant | 加载内容操作后卸载；创建不覆盖；apply 只限 Assets 下 Prefab，apply/revert/unpack 均不可 Undo，需用户授权；unpackMode=root/completely |
| 动画 | clip.create/inspect/curve、animator.create/inspect/parameter/state/transition | 时间严格递增；无条件且无 exitTime 的过渡被拒绝；不做 BlendTree、IK、Avatar Mask |
| 烘焙 | navmesh.bake/clear、lighting.bake/clear、bake.status | 场景须已保存，地面须标静态；清除需 confirm；NavMesh 为内置 Builder，不含 AI Navigation 包的 Surface；lighting.bake 只有真正烘完才算成功，停下但没完成会报失败 |
| 包 | package.list/add/remove | 增删改 manifest 并触发编译，需 confirm；不替用户换包源；超时或取消后请求仍可能生效，以 package.list 为准 |
| Revolution 全链路 | ab.*、excel.*、hot.*、platform.*、ship | 先 `commands --action` 看契约；构建类等真实结果，不并发；整包用 `ship`，失败即停不重试 |
| 选择与回退 | editor.selection/select/undo/redo | 记录原选择，只回退本次有清晰账本的动作；磁盘资产创建不属于 Undo |
| 配置/构建/测试 | settings.get/set/tag-add/layer-add、build.settings/configure/scene-add/scene-remove/targets/target/player、editor.compilation/console、test.list/run/result | 不换用户包源/版本；构建真实 BuildReport，Test Runner 等完成，零测试失败 |
| 游戏 UI/GM | ui.dump/click/input/back/close、runtime.capabilities/call/screenshot、gm.list/exec | 使用 revolution-ai-automation，本局注册与玩家共用入口；不以直接改状态替代玩家行为 |

## 三维对象配方

“在指定场景创建 Cube 放到世界坐标，读回验证；仅保留用户要求的资产，其余收尾。”

1. scene.list 确认场景身份与 dirty。
2. 调 gameobject.primitive：`type` 必须为 `Cube/Sphere/Capsule/Cylinder/Plane/Quad`，`name` 必填，可选 parent（当次父对象 ID）、scene。
3. 保存返回的对象 ID，调用 transform.get 读取 local/world 与父节点。
4. 调 transform.set 后读回相同 ID；任一参数非法应全部拒绝，不先改好字段。

```json
{
  "target": "editor",
  "action": "transform.set",
  "arguments": {
    "id": "<当次对象ID>",
    "space": "world",
    "position": [1, 2, 3],
    "eulerAngles": [0, 45, 0],
    "localScale": [1, 1, 1]
  }
}
```

不同时发送局部和世界坐标。零缩放父节点无法逆变换世界位置，明确拒绝。非有限数字、向量长度/类型错误和 Single 范围溢出必须在修改前拒绝。根 Prefab Transform 的位置属于 Unity 的默认 override；验证时不能用排除 default override 的查询断言它不存在。

## 材质配方

“查出材质实际 Shader 参数，复制一份再调整颜色，原件不变。”

1. shader.list 的 query/offset/limit 只列资产与当前已加载 Shader。创建/切换材质先选 `findable=true`（Shader.Find 可解析）；可见的编辑器私有名称可能不可创建。不能宣称未加载内置 Shader 全覆盖，也不能用 supported 证明目标设备画面正确。
2. material.inspect 的 `path` 是现存 `.mat`，返回 shader、properties、keywords、renderQueue。
3. material.clone 的 `path/destination` 不覆盖；material.create 的 `path/shader` 使用真实 Shader 名。父文件夹必须存在，创建磁盘资产不保证 Undo，临时件由 asset.delete 明确清理。
4. material.set 的 `properties` 是属性名到类型化值的对象：Float/Range 有限数字、Int 为 Int32 整数、Color/Vector 四项数字、Texture 为资产路径或 null。纹理维度须匹配。
5. 可选 shader、renderQueue（-1..5000，-1 继承）、enableKeywords/disableKeywords（Shader 已声明的局部关键字）。同一关键字不能同时启用和禁用，空修改拒绝。
6. 全部修改先在候选副本预检，再修改源并保存；坏属性或坏纹理不能先改其他字段。读回值、检查源 GUID 不变；必要时真实场景截图验证，不把写入成功等同视觉正确。

```json
{
  "target": "editor",
  "action": "material.set",
  "arguments": {
    "path": "Assets/Materials/Preview.mat",
    "properties": {"_Color": [0.2, 0.6, 1.0, 1.0]},
    "renderQueue": -1
  }
}
```

`_Color` 仅为具有该声明的 Shader 示例；实际名字从 inspect 读取，不保证任何渲染管线都有此属性。

## Sprite Atlas V2 配方

- 用 `atlas.inspect` 的 `path` 读取现有图集的 GUID、源成员、Variant 和 Include In Build。返回的是成员列表与导入设置，可另用 `asset.importer` 查看更多设置。
- 用 `atlas.create` 创建新主图集。参数为 `path` 和 `assets`，其中 `assets` 是已按 Sprite 导入的纹理资产路径数组。写入目标的父目录须已存在，已有资产会被拒绝。
- 用 `atlas.set-packables` 替换现有主图集的完整成员列表。按需传空数组清空成员。全局选项 `--confirm` 写在 `call` 前。先校验全部路径，列表相同直接返回，成员修改保留原 GUID 和 Importer 设置。动作写盘，不能用场景 Undo 撤销。
- 写动作要求项目已启用 V2 模式。读回可以显示文件夹、单独 Sprite 和 Variant，写入接受纹理资产路径，暂不提供 V1 转换、Variant 编辑、切片级成员或按项目规则自动分组。

```json
{
  "path": "Assets/UI/Inventory.spriteatlasv2",
  "assets": ["Assets/UI/Bag.png", "Assets/UI/Slot.png"]
}
```

将参数保存为 `atlas.json` 后执行：

```powershell
python Revolution.RevCLI/rev.py --project $project --mode live call atlas.create --json atlas.json
python Revolution.RevCLI/rev.py --project $project --mode live --confirm call atlas.set-packables --json atlas.json
```

## 覆盖边界

通用序列化入口对任何组件都生效：装了 Cinemachine、Input System、URP、TMP、Timeline 后，它们的组件和资产也能用 component.* 与 asset.* 操作，只是没有专用的语义化动作。没有的能力：eval、Terrain/Tilemap 专用绘制、BlendTree、Android 设备控制和未注册业务能力。`script.create` 写的是普通工程文件，走 Unity 正常编译并能被 git 看到，不等于 eval。缺接口明确记录，新增扩展应采用可验证的注册动作和受控 schema，不照搬参考工具的任意代码后门。
