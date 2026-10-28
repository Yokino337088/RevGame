# 代码与注释规范（本项目硬规范）

> 这份规范**优先级高于个人习惯**。生成/修改 Revolution 代码时逐条对齐；审查时逐条检查。
> 现有代码就是范例：写新文件前先看一个同类文件（同目录、同层）怎么起头。

## 1. 方法括号与参数：默认不换行

- **方法签名 / 调用的 `(` 与参数默认写一行**；只有**太长**（显示宽度约 > 160 列，中日韩字符算 2 列）才允许换行。
- 换行时**参数对齐缩进**，不要"每个参数独占一行"。
- 太长时的首选做法**不是**硬拉一行，而是**先提局部变量**，让调用/抛出本身保持单行：

```csharp
// ✓ 好：消息先拼好，抛出语句是单行
string reason = item.Name + " 下载源不支持断点续传，已回退为重新下载";
throw new RevHotException(RevHotError.Of(RevHotErrorCode.DownloadFailed, reason, url), true);

// ✗ 差：参数换行
throw new RevHotException(RevHotError.Of(
    RevHotErrorCode.DownloadFailed,
    item.Name + " 下载源不支持断点续传，已回退为重新下载", url), true);
```

- 自检（PowerShell，在要检查的目录里跑）：

```powershell
# 找出"以 ( 结尾"的行（= 括号后换行）；结果应为 0
Get-ChildItem -Recurse -Filter *.cs | Select-String -Pattern '\(\s*$'
# 顺带查"( 参数"这种多余空格
Get-ChildItem -Recurse -Filter *.cs | Select-String -Pattern '\(\s+[A-Za-z"]'
```

## 2. 方法体必须带大括号

```csharp
// ✓
if (handle.IsLoaded == false) { return; }
// ✗
if (handle.IsLoaded == false) return;
```

- 单行 `if` 也要大括号；`for` / `foreach` / `while` 同理。`=>` 表达式体成员（一行方法、属性）是允许的。

## 3. 注释：核心 / 易错 / 易忽略处要写"为什么"

- **必须写**的位置：① 并发 / 时序 / 生命周期相关的判断；② 反直觉的写法（"为什么不能这样"）；③ 曾经踩过的坑（把现象写出来）；④ 对外承诺（性能契约、清理时机）。
- **面向小白**：先讲"这么做会怎样"，再讲"所以怎么防"，最后才是代码本身。
- 用 `★` 标记要点；反例/现象用引号写出"用户会看到的现象"。

```csharp
// ★ 必须先取快照再调用：属性可能在别的线程被清空（直接读两次会拿到不同值）
Func<string, string> resolver = BundlePathResolver;
string custom = resolver == null ? null : resolver(abName);

// ★ 失败消息为空时兜底成人话：否则面板上只显示一个空红框，
//   使用者看不到任何原因 —— 等于把"永不静默失败"这条目标漏掉。
```

- 反面例子（不要这样）：复述代码（`// 设置 i 为 0`）、把注释当日志、整段注释掉的死代码。

## 4. 文件头：第一段必须是"为什么"

每个 `.cs` 文件开头是一段分块注释，写清：

```csharp
// ============================================================
// RevXxx.cs —— 一句话是什么
//
// 位置：Assets\Revolution\Runtime\RevXxx\Facade\
//
// 【它解决什么】/【怎么用】/【本示例演示什么】/【铁律与坑】
//   照同类文件写：讲清"为什么这么设计、不这么做会怎样"。
// ============================================================
```

## 5. 命名与结构

| 项 | 规则 |
|---|---|
| 类名 | 新框架类型用 `Rev` 前缀，FSM 缩写统一大写；业务状态/域常量遵循项目约定。既有 FSM 文件仍含 `Fsm` / `StateMachine`，不要为匹配类型名顺手改资产路径 |
| 命名空间 | 框架内：`Revolution`（门面/模块内可 `Revolution.Editor` 等）；扩展包：`Revolution.HotUpdate`；示例：`Revolution.Demo.<模块>` |
| 目录分层 | `Core`（数据与契约）/ `Facade`（对外入口）/ `Implementation`（实现）/ `Interfaces`（接口）/ `Support`（宿主适配：Unity 钩子、宿主组件） |
| 私有字段 | `_camelCase`；静态只读 `PascalCase` 或 `_camelCase` 与同文件保持一致 |
| 常量 | `PascalCase`；对外承诺的常量放 `public const` |
| 注释文档 | 对外成员用 `/// <summary>`，参数用 `<paramref>`；要点用 `★` |
| 资产 | `Assets/` 下新增资产带 `.meta`，文件夹 meta 是同级文件；`Tests/`、Skills 和 CI 文本不属于 Unity 资产，不额外生成 meta |

## 6. 其他约定（与框架现状一致）

- **零第三方依赖**：只用 Unity 官方模块（不引 Newtonsoft / UniTask / 第三方 HTTP 库）。
- **不新增分层**：不要为"某个功能"新增一层抽象（`System` / `BusinessLogic` / `Manager` 套 Manager）——先看 `references/ui-and-layering.md` 的结论。
- **失败必带原因**：返回 `bool` 的方法要有别的出口说明原因（句柄字段 / 枚举 / 日志），不要用 `bool` 吞掉原因。
- **纯 C# 优先**：凡是不需要 UnityEngine 的逻辑（路径、槽位表、解析、规则），写成不引用 UnityEngine 的类 —— 这样能进工程外断言（见 `references/verify-and-test.md`）。
- **性能契约要写明**：框架里"零 GC / 零分配 / 上限多少"这类承诺都写在注释里；你写同类代码时也照做，并且**改完要复验**。
- **文档同步**：改行为 → 该模块《使用说明》；改设计 → 《架构解析》；md 与 html 都要改。

## 7. 提交前自检清单

- [ ] 没有一行以 `(` 结尾（括号后换行）；没有 `( 参数` 的多余空格。
- [ ] 所有 `if / for / foreach / while` 都带大括号。
- [ ] 新文件有文件头"为什么"段；关键/易错处有 `★` 注释。
- [ ] 新框架类型用 Rev 前缀，FSM 缩写大写；命名空间正确，不顺手改既有旧名 FSM 文件路径。
- [ ] Assets 新增资产带 `.meta`，文件夹 meta 是同级命名；Tests、Skills 和 CI 文件不生成 Unity meta。
- [ ] 门面之外没有新增 public API（除非确实要给业务用，且已在文档里写清）。
- [ ] 大括号 / 圆括号配平（防止文件被截断或漏闭合）。
- [ ] 相关文档（md + html）已同步。
