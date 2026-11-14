# RevCLI 增量构建与发布 AB

使用 Python 3.10+。下例把 `<工程>` 替换为目标 Unity 工程路径，把平台替换为实际 BuildTarget。Windows Editor 的 AB 验收使用 `StandaloneWindows64`，Android 发布使用 `Android`。每个平台独立执行整条流程。

```powershell
py -3 Revolution.RevCLI/rev.py --project "<工程>" --mode live commands --live --query incremental
py -3 Revolution.RevCLI/rev.py --project "<工程>" --mode live editor wait
py -3 Revolution.RevCLI/rev.py --project "<工程>" --mode live --timeout 600 ab build-incremental --target StandaloneWindows64
py -3 Revolution.RevCLI/rev.py --project "<工程>" --mode live ab check-map --target StandaloneWindows64
py -3 Revolution.RevCLI/rev.py --project "<工程>" --mode live --timeout 180 ab publish-incremental --target StandaloneWindows64
```

- 编辑态执行，按顺序核对每条回执的 `success`；有错误即停。项目有分包配置、图集或 Shader 门禁时，先走项目入口。RevGame 涉及标记、图集或依赖布局改动时调用 `game.resources.configure`，仅改已有包里的内容时调用 `game.resources.validate`。
- `ab.build-incremental` 为本次调用保留输出目录并关闭强制全打，不保存共享设置。Unity 仍检查所有依赖并生成完整主清单。首建、切换实际平台、缓存缺失或从中断恢复时可能重建更多包。`data.totalBundles` 是清单中的总数。
- `ab.publish-incremental` 只读已完成的同平台构建，增量同步到 `Assets/StreamingAssets/<平台目录>`。回执包含 `published`、`destination`、`updatedBundles`、`removedBundles` 和 `updatedMetadata`。未变化文件保留，前次发布清单声明的过时包会由工具移除。回执的更新数是实际替换数，和 Unity 内部重建计数口径不同。
- 发布失败时检查工单和回滚结果。构建已完成且源文件齐全时，可针对同一平台单独重试发布。构建超时或传输中断先查原工单及编辑器状态，避免重复提交。不要手工拼接主清单或往运行中的 Player 覆盖包。

## 读构建回执并处理全量回退

先判断构建回执的 `success`，再读 `data.rebuildReason`。构建成功且原因非空，说明这张工单已经执行了强制全量。记录原因、平台和工单 ID，继续同平台的映射检查，检查通过后再发布。成功回退无需再提交一次构建。原因空串表示允许 Unity 复用缓存，实际替换数量以发布回执为准。旧桥缺少该字段时，先核对框架版本和实时注册。

| `data.rebuildReason` | AI 下一步 |
| --- | --- |
| 缺少当前版本的已验证构建基线 | 首次打包、升级或更换输出目录时预期出现。用本次成功产物继续检查和发布，保留输出供下次使用。 |
| 上一次构建未完成 | 核对上一张工单、Unity Console 和当前工单结果。当前全量成功后可继续。反复出现则查取消、文件占用或导入错误。 |
| 构建环境、包归属或非文本资源依赖有变化 | 核对平台、Unity 版本、构建选项、材质、Shader、图集和分包的预期改动。确认属于本次范围后使用全量结果。若每次都出现，定位持续变化的输入。 |
| 缓存产物校验失败… | 保留报错中的文件路径。若当前构建已成功，按新产物继续。若仍失败，查缺失、内容不一致、路径误用及文件占用。 |
| 启用了强制全部重打 | 表示构建配置要求全量。记录实际构建方式，按当前发布流程继续。 |

构建失败、超时或返回结果未知时，先核对原工单与 Unity 状态，确认完成状态后决定下一步。发布因未完成、平台不符、缺少完整性信息或 SHA-256 不匹配而被拒绝时，针对正确平台重建完整产物，再检查映射、发布。已有成功产物且只是目标文件暂时被占用时，确认回滚结果，释放占用后单独重试发布。

正式发版、修改依赖外部环境的自定义构建回调，或出现异常画面时，设置 `forceRebuild=true` 并走 `ab.build`，再检查映射、发布及在目标平台验收。比较增量耗时时，单独记录已触发全量回退的样本。

## Codex 插件调用

用 `rev_bind` 绑定工程绝对路径，再用 `rev_discover(target="editor", query="incremental")` 查注册情况。依次使用 `rev_call`，`target` 为 `editor`，`action` 依次为 `ab.build-incremental`、`ab.check-map`、`ab.publish-incremental`，三步的 `arguments` 都传 `{"target":"StandaloneWindows64"}`。也可用 `rev_run` 顺序执行，对发布结果的 `/data/published` 和 `/data/target` 写断言。旧 Unity 桥缺少注册时先同步框架再发现。
