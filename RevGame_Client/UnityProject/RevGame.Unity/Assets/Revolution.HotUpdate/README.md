# RevHotUpdate —— 资源热更新（轻量 · 可热插拔）

> 基于 Revolution 资源系统的资源热更：**清单 → 版本比对（大版本锚定）→ 差量下载 → 校验落地 → 加载路径重定向**。
> 独立包：`Assets/Revolution.HotUpdate/` 不导入 = 框架零影响；导入后业务加载代码一行不用改。
> 完整设计见 `Revolution.Document/热更新/RevHotUpdate技术方案.md`（md + html）。

## 安装（与框架配对）

```text
# 两条命令各拉一个包（热更包是"扩展包"，框架本体是前提）
git clone -b package   https://github.com/Yokino337088/Revolution.git Assets/Revolution
git clone -b hotupdate https://github.com/Yokino337088/Revolution.git Assets/Revolution.HotUpdate
```

> ★ `hotupdate` 是**生成分支**（CI 从 `main` 的 `Assets/Revolution.HotUpdate/` 整份重建），不要往它上面提交；
> 改代码请改 `main`。热更包用到的两个钩子（`RevABLoader.BundlePathResolver` / `RevResBootstrap.ResMapOverride`）
> 是框架侧后加的 —— **框架必须不早于引入钩子的那一版**，否则编译不过。
>
> ⚡ **想先看跑起来的效果**：`Assets/Revolution.Demo/RevHotUpdate.Demo/`（演示面板 + 编辑器一键"打包 + 清单 +
> 装配本地 CDN" + `起本地CDN.cmd` 假 CDN；3 步跑通，全程不需要真云账号）。

## 最省事用法（放在"加载任何业务资源"之前）

```csharp
RevHotUpdateResult result = await RevHotUpdate.InitializeAsync(
    new RevHotConfig { RemoteRoot = "https://cdn.example.com/gameA" },
    p => Debug.Log(p.Percent + "% " + p.Text));

if (result.Success == false)
{
    Debug.LogWarning(result.Error.Message);   // 热更失败：旧版本照常可用（current.txt 没动）
}
// 之后照旧：RevResManager.LoadAsync<Sprite>(路径, 回调, 分组) —— 加载代码不变
```

## 组成

| 目录 | 内容 |
|---|---|
| `Runtime/Facade` | `RevHotUpdate`：Check / Update / Initialize / Install / Dump |
| `Runtime/Core` | 配置 / 清单（解析与序列化）/ 版本规则 / 差量计划 / 进度 / 错误 / 平台能力表 |
| `Runtime/Download` | URL 拼装 / 下载源 / 下载引擎（并发 · 重试退避 · 多源 · Range 续传 · 磁盘预检） |
| `Runtime/Pipeline` | 存储管家（版本目录 / 原子落盘 / 首包落地）/ 校验（尺寸 + SHA-256 分帧）/ 版本比对 |
| `Runtime/Integration` | 框架桥：装载 `RevABLoader.BundlePathResolver` 与 `RevResBootstrap.ResMapOverride` 两个钩子 |
| `Runtime/Support` | 进 Play 复位内存状态（不动磁盘数据） |
| `Editor` | 打完 AB 后生成 `RevHotManifest.txt` / `RevHotBuiltin.txt` + 自检（菜单：Revolution.Tools/热更新） |

## 对框架的改动（共 2 处、默认 null、零影响）

- `RevABLoader.BundlePathResolver`：包从哪读（持久化版本目录 → 首包落地目录 → 交还 StreamingAssets 默认）；
- `RevResBootstrap.ResMapOverride`：映射表覆盖来源（热更表覆盖内置表，"新增资源"才能下发）。

## 出包与发布流程

1. 框架打包工具打 AB（AssetBundles/<平台>/）；
2. `Revolution.Tools/热更新/热更清单窗口` → 生成清单 + 自检；
3. 上传：**先传所有内容，最后传 RevHotManifest.txt**（清单是原子切换开关）；
4. 客户端 `InitializeAsync` → 只下变化的包；跨大版本 → 重新出包（大版本锚定）。

## 边界（本期明确不做）

代码热更、加密、二进制差量、灰度、按需下载（tag）—— 见技术方案 1.2 与第十五章。
