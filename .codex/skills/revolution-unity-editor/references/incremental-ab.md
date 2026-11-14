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

## Codex 插件调用

用 `rev_bind` 绑定工程绝对路径，再用 `rev_discover(target="editor", query="incremental")` 查注册情况。依次使用 `rev_call`，`target` 为 `editor`，`action` 依次为 `ab.build-incremental`、`ab.check-map`、`ab.publish-incremental`，三步的 `arguments` 都传 `{"target":"StandaloneWindows64"}`。也可用 `rev_run` 顺序执行，对发布结果的 `/data/published` 和 `/data/target` 写断言。旧 Unity 桥缺少注册时先同步框架再发现。
