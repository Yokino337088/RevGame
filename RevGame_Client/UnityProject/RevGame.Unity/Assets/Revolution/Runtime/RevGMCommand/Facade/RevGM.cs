// RevGM.cs —— ★ 小白从这里开始（一行注册、一行执行）
//
// ┌─ 一行注册 ───────────────────────────────────────────────────────────────────────────┐
// │ // 放在你自己的静态类里，整局只在一处注册（别散落各处 —— 装配要能一眼看全）              │
// │ RevGM.Register("经济/加金币", "给当前玩家加金币", args => AddGold(args.Int(0, 1000)),     │
// │               RevGMArg.Int("数量", 1000));                                             │
// │ RevGM.Register("战斗/清空敌人", "把所有敌人血量清零", args => ClearEnemy(),               │
// │               RevGMFlags.HighRisk);            // ← 高危：面板执行前会二次确认           │
// │                                                                                        │
// │ // 面板：菜单 Revolution.Tools/GM 指令面板（Ctrl+Shift+G）—— 边打边联想、回车执行         │
// │ // 代码里也能执行：RevGM.Execute("经济/加金币 500")                                       │
// └────────────────────────────────────────────────────────────────────────────────────────┘
//
// 【接下来看哪】（只有前两个是你必须看的）
//   ① 本文件                     注册 + 执行 + 联想（唯一入口）
//   ② RevGMArgs.cs               取参数（args.Int / Float / Bool / Str / Enum，都带默认值）
//   ③ RevGMArg.cs                参数说明（面板据此显示帮助、执行前校验参数）
//   ④ RevGMFlags.cs              标记：HighRisk（高危二次确认）/ Hidden（不进联想）
//   ⑤ Implementation\ 与 Core\RevGMEntryAttribute.cs          引擎内部与可选入口标记，不用读
//
// 【三条铁律】
//   ① 注册只写在一处（组合根），命令实现体里不要注册别的命令（那会让"有哪些命令"变得不可预测）
//   ② 参数不要自己解析字符串：用 args.Int(0, 默认值) —— 解析失败会给出一句人话，而不是抛英文异常
//   ③ 业务拒绝执行时抛 RevGMUsageException("为什么不行")：这句话会原样显示在面板上（别用静默 return）
//
// 【正式包怎么办】本框架**没有靠编译宏裁剪**：没人在启动期调 Register，就等于不存在（零开销、零泄漏）；
//   需要临时关闭时把 Enabled 置 false 即可。这也是它比"整包宏 + 反射扫描"更安全的地方。

using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Revolution
{
    /// <summary>
    /// GM 指令框架入口：一行注册、统一执行、模糊联想。
    /// <para>纯 C#（不引用 UnityEngine），所以命令的注册/解析/执行逻辑可以直接在工程外跑单元测试。</para>
    /// </summary>
    public static class RevGM
    {
        /// <summary>约定的"成功"返回值（命令返回它或返回别的文本都算成功）—— 保留王者的这个约定，因为它够直白</summary>
        public const string Done = "done";

        private static readonly RevGMRegistry Registry = new RevGMRegistry();

        /// <summary>总开关：置 false 后 <see cref="Execute"/> 一律拒绝（注册仍保留，便于再打开）</summary>
        public static bool Enabled { get; set; } = true;

        /// <summary>已注册的命令数量</summary>
        public static int Count => Registry.Count;

        /// <summary>全部已注册命令（按注册顺序）—— 面板据此长分组树</summary>
        public static IReadOnlyList<RevGMCommand> Commands => Registry.All;

        // ============================================================
        // 注册（一行一条）
        // ============================================================

        /// <summary>注册一条没有参数说明的命令（框架不做参数预校验，参数自己用 <see cref="RevGMArgs"/> 取）</summary>
        public static void Register(string name, string description, Action<RevGMArgs> handler)
            => Add(name, description, Wrap(handler), RevGMFlags.None, Array.Empty<RevGMArg>());

        /// <summary>注册一条没有参数说明、且会返回一句结果文本的命令</summary>
        public static void Register(string name, string description, Func<RevGMArgs, string> handler)
            => Add(name, description, handler, RevGMFlags.None, Array.Empty<RevGMArg>());

        /// <summary>
        /// 注册一条带参数说明的命令（推荐写法）：参数说明会被面板拿去显示帮助 + 执行前校验。
        /// <code>RevGM.Register("经济/加金币", "给当前玩家加金币", args =&gt; AddGold(args.Int(0)), RevGMArg.Int("数量", 1000));</code>
        /// </summary>
        public static void Register(string name, string description, Action<RevGMArgs> handler, params RevGMArg[] args)
            => Add(name, description, Wrap(handler), RevGMFlags.None, args);

        /// <summary>注册一条带参数说明、且会返回一句结果文本的命令</summary>
        public static void Register(string name, string description, Func<RevGMArgs, string> handler, params RevGMArg[] args)
            => Add(name, description, handler, RevGMFlags.None, args);

        /// <summary>注册一条带标记的命令（例如 <c>RevGMFlags.HighRisk</c>：面板执行前二次确认）</summary>
        public static void Register(string name, string description, Action<RevGMArgs> handler, RevGMFlags flags, params RevGMArg[] args)
            => Add(name, description, Wrap(handler), flags, args);

        /// <summary>注册一条带标记、且会返回一句结果文本的命令</summary>
        public static void Register(string name, string description, Func<RevGMArgs, string> handler, RevGMFlags flags, params RevGMArg[] args)
            => Add(name, description, handler, flags, args);

        /// <summary>注销一条命令（动态命令 / 测试收尾用；返回是否确实删掉了）</summary>
        public static bool Unregister(string name) => Registry.Remove(name);

        /// <summary>清空所有命令（编辑器在"退出 Play / 脚本重编译"时会用它，避免残留上一次的注册）</summary>
        public static void Clear() => Registry.Clear();

        // ============================================================
        // 执行
        // ============================================================

        /// <summary>
        /// 执行一行 GM 文本（面板、控制台、测试都走这里）。
        /// <para>流程固定：解析 → 查命令 → 按参数说明预校验 → 执行（统一兜异常）→ 返回结果 + 耗时。
        /// 所以"命令没找到""参数不对""业务抛异常"都会变成一句人能看懂的话，不会静默失败。</para>
        /// </summary>
        public static RevGMResult Execute(string commandLine)
        {
            if (!Enabled) return RevGMResult.Fail("GM 命令已关闭（RevGM.Enabled = false）");

            RevGMParser.Split(commandLine, out string name, out string[] args);

            if (string.IsNullOrEmpty(name))
                return RevGMResult.Fail("没有输入命令。格式：完整命令名 + 参数，例如「经济/加金币 1000」");

            RevGMResolve resolve = Registry.Resolve(name, out RevGMCommand command, out List<RevGMCommand> candidates);

            if (resolve == RevGMResolve.NotFound) return RevGMResult.Fail(NotFoundMessage(name));
            if (resolve == RevGMResolve.Ambiguous) return RevGMResult.Fail(AmbiguousMessage(name, candidates));

            string invalid = RevGMArgs.Validate(args, command.Args);
            if (invalid != null) return RevGMResult.Fail($"{command.Name} → {invalid}");

            long startTicks = Stopwatch.GetTimestamp();

            try
            {
                string message = command.Handler(new RevGMArgs(args, command.Args));
                return RevGMResult.Ok(message, ElapsedMs(startTicks));
            }
            catch (RevGMUsageException usage)
            {
                // 用法错误：这句话是写给人看的，原样回显
                return RevGMResult.Fail(usage.Message, ElapsedMs(startTicks));
            }
            catch (Exception e)
            {
                // 真 bug：把类型 + 消息 + 首个堆栈行给出来（不做静默失败 —— 王者那套的老毛病是"点了没反应"）
                return RevGMResult.Fail(
                    $"执行「{command.Name}」时抛异常：{e.GetType().Name}: {e.Message}{FirstStackLine(e)}",
                    ElapsedMs(startTicks));
            }
        }

        // ============================================================
        // 联想（面板的自动补全用它；也可在游戏内控制台里用）
        // ============================================================

        /// <summary>
        /// 模糊联想：支持 前缀 / 连续子串 / 子序列（相当于缩写）三种命中，越像越靠前。
        /// <para>空输入返回前 <paramref name="max"/> 条（便于"打开面板先看看有什么"）。</para>
        /// </summary>
        public static IReadOnlyList<RevGMCommand> Suggest(string input, int max = 8)
        {
            List<RevGMCommand> result = new List<RevGMCommand>(max > 0 ? max : 8);
            if (max <= 0) return result;

            string query = input?.Trim();

            if (string.IsNullOrEmpty(query))
            {
                for (int i = 0; i < Registry.All.Count && result.Count < max; i++)
                    if (!Registry.All[i].IsHidden) result.Add(Registry.All[i]);

                return result;
            }

            List<Scored> scored = new List<Scored>(16);

            for (int i = 0; i < Registry.All.Count; i++)
            {
                RevGMCommand command = Registry.All[i];
                if (command.IsHidden) continue;

                int score = RevGMMatcher.Score(command, query);
                if (score > 0) scored.Add(new Scored { Score = score, Command = command });
            }

            // 分高的在前；同分时名字短的在前（更像"用户想打的那条"）
            scored.Sort((a, b) =>
            {
                int byScore = b.Score.CompareTo(a.Score);
                if (byScore != 0) return byScore;
                return a.Command.Name.Length.CompareTo(b.Command.Name.Length);
            });

            for (int i = 0; i < scored.Count && result.Count < max; i++) result.Add(scored[i].Command);

            return result;
        }

        /// <summary>按完整名（或唯一的末段名）取一条命令</summary>
        public static bool TryGet(string name, out RevGMCommand command)
        {
            RevGMResolve resolve = Registry.Resolve(name, out command, out _);
            return resolve == RevGMResolve.FoundFull || resolve == RevGMResolve.FoundBase;
        }

        // ============================================================
        // 内部
        // ============================================================

        private static void Add(string name, string description, Func<RevGMArgs, string> handler, RevGMFlags flags, RevGMArg[] args)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler), "GM 命令必须有一个「干什么」的方法");
            if (string.IsNullOrEmpty(name) || name.Trim().Length == 0)
                throw new ArgumentException("GM 命令名不能为空（建议写成 分组/子分组/名字，例如 经济/货币/加金币）", nameof(name));

            string normalized = name.Trim().Trim('/');

            // ★ 归一化之后必须再判一次空：
            //   "/"、"///"、" / " 这类"只有分隔符"的名字会被 Trim('/') 削成空串，放过去就会注册出一条
            //   空名命令（联想里看不到、也没法按名字注销）；更糟的是第二次再注册只会报"已经注册过了"，
            //   把真正的原因（名字本身不合法）盖掉。
            if (normalized.Length == 0)
                throw new ArgumentException(
                    $"GM 命令名不能只有分隔符：「{name}」—— 请写成 分组/名字，例如 经济/加金币", nameof(name));

            for (int i = 0; i < normalized.Length; i++)
                if (char.IsWhiteSpace(normalized[i]))
                    throw new ArgumentException($"GM 命令名里不能有空格：「{name}」—— 命令名与参数是用空格分开的（分组请用 / ）", nameof(name));

            Registry.Add(new RevGMCommand(normalized, description, args, flags, handler));
        }

        private static Func<RevGMArgs, string> Wrap(Action<RevGMArgs> handler)
            => handler == null
                ? null
                : args => { handler(args); return Done; };

        private static double ElapsedMs(long startTicks)
            => (Stopwatch.GetTimestamp() - startTicks) * 1000d / Stopwatch.Frequency;

        private static string NotFoundMessage(string name)
        {
            IReadOnlyList<RevGMCommand> hints = Suggest(name, 5);

            if (hints.Count == 0)
                return $"没有这条命令：「{name}」（当前共注册了 {Registry.Count} 条；面板里可以边打边看联想）";

            string list = string.Empty;
            for (int i = 0; i < hints.Count; i++)
            {
                if (i > 0) list += " / ";
                list += hints[i].Name;
            }

            return $"没有这条命令：「{name}」。你是不是想输入：{list}";
        }

        private static string AmbiguousMessage(string name, List<RevGMCommand> candidates)
        {
            string list = string.Empty;
            for (int i = 0; i < candidates.Count; i++)
            {
                if (i > 0) list += " / ";
                list += candidates[i].Name;
            }

            return $"「{name}」匹配到 {candidates.Count} 条命令（{list}）—— 请写完整名（含分组）";
        }

        private static string FirstStackLine(Exception e)
        {
            string stack = e.StackTrace;
            if (string.IsNullOrEmpty(stack)) return string.Empty;

            int end = stack.IndexOf('\n');
            return "\n   " + (end > 0 ? stack.Substring(0, end) : stack);
        }

        private struct Scored
        {
            public int Score;
            public RevGMCommand Command;
        }
    }
}
