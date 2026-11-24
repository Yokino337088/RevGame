---
name: revolution-revasset
description: 为 Revolution Unity 项目选择、配置和验收可选 RevAsset 后端。用于地址采集、原生 AB 分包与变体、共享依赖、首包按标签、RevCLI 打包、签名热更新、缓存修复、版本激活与回退。
---

# RevAsset

先确认项目同时安装 Revolution 框架和 `Revolution.RevAsset` 扩展。用 `revasset.status` 核对工程选择、模拟模式和目标平台。业务沿用 `RevResManager`、RevUI、RevPool、RevSound、RevScope 与预加载入口。资源逻辑地址由项目规则和编译时生成的目录决定。

## 采集与分包

打开 `Revolution.Tools → 资源 → RevAsset`，选择项目资源后端。编辑器快速迭代打开模拟，真实回归关闭模拟并准备本机平台首包。

- 规则可以指向 `Assets` 内任意文件或目录。最具体的路径胜出。单文件可指定精确逻辑地址，目录可指定地址前缀。默认地址取资源根下相对路径，去文件扩展名。
- `NativeOrFolder` 优先读取原生 importer 包名和变体。用 `Together` 精确合包，`ByFolder` 按目录，`ByAsset` 一项一包，`Exclude` 隐藏公开地址。标签用于选择分发阶段。
- 预览核对地址唯一、变体、包归属和共享依赖。在多包间复用的未归属资产由采集器归并，正式依赖以 Unity 原生构建结果为准。遇到循环先调整实际资产引用或包归属。
- 首包标签留空带齐全部资源。设标签时将其完整依赖闭包一起装入。Player 构建只注入目标平台的实际文件。

## RevCLI

在项目已编译的 Unity 上先运行 `commands --live --query revasset`。以下命令省略 `--project`，运行时使用真实工程绝对路径。

```powershell
python rev.py revasset status
python rev.py revasset select --set 'enabled=true' --set 'simulation=false'
python rev.py revasset rule --path Assets/GameRes/UI --packing Together --bundle ui/lobby --tag core
python rev.py revasset plan --limit 20
python rev.py revasset build --target StandaloneWindows64 --version 1.0.0 --revision 1 --builtin
```

修改规则用 `revasset.rule`，修改逻辑根、包名、首包标签和公开频道用 `revasset.configure`。CI 点名实际平台并检查结果的 `target`、`bundles`、`assets`、`builtin`。一次只在对应构建进程消耗私钥。加载不到时核对选择的后端、实际目标、逻辑地址和包含的标签。

`ship` 使用在线编辑器实际选择的资源后端。RevAsset 首包版本可通过 `--revasset-version` 与 `--revasset-revision` 传入。

## 发布与恢复

用面板生成项目发布密钥，把私钥放到项目目录外。客户端保存公钥。使用 HTTPS 发布目录并保持内容包名、平台、应用兼容代号一致。正式频道按递增序号发布。运行时校验 RSA-SHA256 发布签名、目录摘要、资源包长度和内容 SHA-256。

```powershell
python rev.py revasset build --target StandaloneWindows64 --version 1.0.1 --revision 2 --no-builtin --signing-key-path C:/secure/revasset-private.xml
python rev.py game actions --query revasset
python rev.py game call revasset.download
python rev.py game call revasset.status
```

先部署 `objects/` 和 `catalogs/`，再更新 `channel.json`。不可变对象按摘要缓存，频道指针走短缓存或重验证。支持 Range 的主机允许从部分文件的字节位置继续下载。按标签预下载可供下一次整版安装复用。业务需要显示进度时读取更新回执，或在启动时直接等待 `RevAsset.UpdateAsync()`。

资源关闭域并归还租约后，用 `RevAsset.TryActivateInstalled()` 或 `game call revasset.activate` 装配完整版本。拒绝时等到下一安全边界。回滚用 `RevAsset.RollbackInstalled()`，随后按相同方式装配。清缓存保留生效、已安装、前一版和首包。

## 验收

在目标 Unity 平台走模拟、真实首包、相同目录的两个签名版本。用真实业务 API 读取新旧内容，并记录复用、下载、拒绝和回退结果。至少包含一份稳定资源、一份变更资源、一个共享依赖、实际关域、损坏对象和不正确签名。浏览器与移动端按目标制品验证缓存、网络、存储和恢复。

- [框架资源门面](../revolution-framework/references/resource-and-ab.md)
- [框架域与租约](../revolution-framework/references/domain-lifetime.md)
- [转接与发布细节](https://github.com/Yokino337088/Revolution/tree/main/RevolutionFrameWork_Unity/Assets/Revolution.RevAsset)
