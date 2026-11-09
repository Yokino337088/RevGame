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
if (handle.IsLoaded == false)
{
    return;
}
// ✗
if (handle.IsLoaded == false) return;
```

- 所有 `if` / `else if` / `else` 必须按上例展开，包括Lambda、事件回调、宏分支与生成器输出。禁止条件、左大括号、语句或右大括号挤在同一行。类、方法、属性和访问器的大括号必须独占一行。
- `.editorconfig` 使用 `csharp_new_line_before_open_brace = all`、`csharp_new_line_before_else = true`、`csharp_preserve_single_line_blocks = false`、`csharp_preserve_single_line_statements = false`；只配置 `control_blocks` 会令其它大括号合到行尾，禁止这样配置。
- 批量格式化必须逐token确认语句和注释不变，核对宏分支与Lambda内if；发现生成器输出缺少大括号时同步修正生成模板。不得绕过目标项目对框架快照/生成目录的边界。

## 3. 面向新手的中文注释是交付门禁

框架 Runtime、Editor、工具、测试和本次编写的项目接线采用同一覆盖要求。每个手写的新声明及本次修改的声明都要有中文说明。新文件逐项检查，旧文件补齐本次触及的契约和相关实现。生成代码从模板或生成器维护永久注释，第三方代码遵循其维护边界。

| 位置 | 必须让读者理解的内容 |
|---|---|
| 每个类、接口、结构体、枚举等类型 | 职责、状态所有者、谁创建或调用、生命周期和边界。接口说明调用方可以依赖的不变量。 |
| 每个字段、属性、常量和有特殊含义的枚举值 | 语义、默认值、单位和范围。适用时说明哨兵、缓存版本、可写方、更新时机、失效和清理。禁止一行声明多个成员再共用一条含糊注释。 |
| 每个方法、构造函数、重载、回调和本地函数 | 入口、参数、返回结果、副作用和失败语义。私有和内部成员同样覆盖。异步与生命周期入口说明谁拥有资源、何时完成和如何收尾。 |
| 方法内部各职责段 | 按预检、状态提交、外部调用、跨帧等待、失败回滚和清理解释意图与次序。短方法也要让读者从注释理解实现意图。 |
| 重要算法与容易误改的分支 | 解释输入假设、单位、下标、边界、计算步骤、不变量及为什么采用当前策略。说明缓存失效、随机序列、浮点或定点确定性，以及并发和取消会怎样影响结果。 |

- 声明用就近的 `/// <summary>` 或适合该语言的文档注释。C# 参数与返回契约必要时用 `<param>` 与 `<returns>`。实现段用紧贴代码的中文注释，`★` 可标记容易踩坑的步骤。
- 只解释对理解有用的理由、结果与失败边界。修改逻辑的同时修正注释，不留下过期解释或被注释掉的旧实现。

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
| 注释文档 | 所有手写声明和实现段按 §3 覆盖。C# 声明优先用 `/// <summary>`，正文引用参数用 `<paramref>` |
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
- [ ] 新文件有文件头。逐个核对本次新增或修改的类型、字段、属性、常量、方法、构造函数和回调均有面向新手的中文注释。
- [ ] 方法内部各职责段和重要算法已写明顺序、原因、边界与失败处理。单位、生命周期和注释与实现一致。
- [ ] 新框架类型用 Rev 前缀，FSM 缩写大写；命名空间正确，不顺手改既有旧名 FSM 文件路径。
- [ ] Assets 新增资产带 `.meta`，文件夹 meta 是同级命名；Tests、Skills 和 CI 文件不生成 Unity meta。
- [ ] 门面之外没有新增 public API（除非确实要给业务用，且已在文档里写清）。
- [ ] 大括号 / 圆括号配平（防止文件被截断或漏闭合）。
- [ ] 相关文档（md + html）已同步。
