// ============================================================
// ResPathNaming.cs —— 资源目录常量的「扫描 + 命名 + 生成」（纯逻辑）
//
// 位置：Editor\RevResourceSystem\ABTool\CodeGen\
//
// 【它负责什么？】
//   把"资源根目录下的文件夹"变成一段可以直接编译的 C# 源码：
//       Assets/GameRes/UI/Icon   →   public const string UI_Icon = "UI/Icon/";
//   业务侧就写：   RevResManager.Load<GameObject>(RevResPath.UI_Icon, "Hero_1001", RevResGroup.UI);
//   （框架内部把"根目录 + 资源名"两段拼成 "UI/Icon/Hero_1001"）
//
// 【为什么单独一个文件，不写在 ABResPathGenerator 里？】
//   本文件**不引用 UnityEditor / UnityEngine**，只有 System.IO 和字符串处理：
//     ① 命名规则是"业务规则"，和"写文件 / 触发重导入"这类编辑器 IO 分开更清楚；
//     ② 纯逻辑可以被工程外的测试程序直接编译运行 —— 生成的代码能不能通过编译，
//        不开 Unity 也能验。
//
// 【★ 为什么必须生成 const，而不是 static readonly？】
//   const 是"编译期常量"：常量名写错编译就报错，IDE 也能补全、能查引用；
//   换成 static readonly 就退化成"只有运行时才知道拼错了"。
//
// 【为什么常量值带结尾斜杠？】
//   让它天生就是"前缀"，看着就知道后面还要接资源名 —— 少一处可能写错的地方。
//   （RevResPathUtil.Join 对"带不带结尾斜杠"都兼容：写 "UI/Icon" 也照样拼得对。）
// ============================================================
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Revolution.Editor
{
    /// <summary>
    /// 资源路径常量的命名与代码生成（纯逻辑，可脱离 Unity 单独编译验证）。
    /// 真正落盘由 ABResPathGenerator 负责。
    /// </summary>
    public static class ResPathNaming
    {
        /// <summary>生成出来的类名（业务写 RevResPath.UI_Icon；Rev 前缀与框架其他类型保持一致）</summary>
        public const string ClassName = "RevResPath";

        /// <summary>生成出来的命名空间</summary>
        public const string NamespaceName = "Revolution";

        // ============================================================
        // ① 扫描：资源根目录下的所有文件夹
        // ============================================================

        /// <summary>
        /// 扫描资源根目录下的所有文件夹，返回**相对路径**并按字典序排序。
        /// 例：resRoot = "Assets/GameRes" → ["Effect", "UI", "UI/Icon"]
        ///
        /// 【为什么用文件系统扫描，而不是 AssetDatabase】
        ///   这样**空文件夹也算**：可以先建好目录结构、再往里放资源，
        ///   常量列表和目录结构始终对得上，不会出现"目录建了、常量却还没生成"。
        /// </summary>
        public static List<string> CollectFolders(string resRoot)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(resRoot)) return result;

            string root = resRoot.Replace('\\', '/').TrimEnd('/');
            if (!Directory.Exists(root)) return result;

            foreach (string full in Directory.GetDirectories(root, "*", SearchOption.AllDirectories))
            {
                string rel = full.Replace('\\', '/').Substring(root.Length + 1);
                if (IsUnityIgnored(rel)) continue;      // Unity 根本不导入的目录，给它生成常量只会误导人
                result.Add(rel);
            }

            // 排序：保证输出稳定 —— 目录没变时源码就不会变，配合生成器的"防抖"避免无意义写盘
            result.Sort(string.CompareOrdinal);
            return result;
        }

        /// <summary>
        /// Unity 会忽略的目录：路径里任意一段以 "." 开头或以 "~" 结尾（如 .git、Temp~）。
        /// 这类目录 Unity 不导入，自然也不该出现在资源路径常量里。
        /// </summary>
        private static bool IsUnityIgnored(string relDir)
        {
            foreach (string seg in relDir.Split('/'))
            {
                if (seg.Length == 0) continue;
                if (seg[0] == '.' || seg[seg.Length - 1] == '~') return true;
            }
            return false;
        }

        // ============================================================
        // ② 命名：相对目录 → 合法 C# 标识符
        // ============================================================

        /// <summary>
        /// "UI/Icon" → "UI_Icon"：分隔符和非字母数字字符都换成下划线。
        /// 实在无法生成合法标识符时返回 null（调用方跳过该目录）。
        /// </summary>
        public static string ToConstName(string relDir)
        {
            if (string.IsNullOrEmpty(relDir)) return null;

            var sb = new StringBuilder(relDir.Length);
            foreach (char c in relDir)
            {
                // 中文目录名也是合法标识符（C# 允许 Unicode 字母），
                // 所以只替换"既不是字母、也不是数字、也不是下划线"的字符
                bool ok = c != '/' && (char.IsLetterOrDigit(c) || c == '_');
                sb.Append(ok ? c : '_');
            }

            string name = sb.ToString();
            if (name.Length == 0) return null;

            // ① 不能以数字开头：目录 "2D" → "_2D"
            if (char.IsDigit(name[0])) name = "_" + name;

            // ② 不能是 C# 保留字：目录 "class" / "event" → "_class" / "_event"
            if (Keywords.Contains(name)) name = "_" + name;

            return name;
        }

        /// <summary>相对目录 → 常量值："UI/Icon" → "UI/Icon/"（带结尾斜杠，天生就是前缀）</summary>
        public static string ToConstValue(string relDir) => relDir + "/";

        /// <summary>C# 保留字（拿它们当字段名会编译不过，所以前面补下划线）</summary>
        private static readonly HashSet<string> Keywords = new HashSet<string>(
            // ★ 括号不能省：".Split()" 只作用于紧邻它的那一个字面量，
            //   不括起来会变成 "a" + "b" + string[]（第三个参数被 ToString 成 "System.String[]"）
            ("abstract as base bool break byte case catch char checked class const continue decimal " +
             "default delegate do double else enum event explicit extern false finally fixed float for " +
             "foreach goto if implicit in int interface internal is lock long namespace new null object " +
             "operator out override params private protected public readonly ref return sbyte sealed " +
             "short sizeof stackalloc static string struct switch this throw true try typeof uint ulong " +
             "unchecked unsafe ushort using virtual void volatile while").Split(' '));

        // ============================================================
        // ③ 生成：拼出完整源码
        // ============================================================

        /// <summary>
        /// 生成 RevResPath.cs 的完整源码。
        /// </summary>
        /// <param name="resRoot">资源根目录（只写进文件头注释，说明常量来自哪里）</param>
        /// <param name="relDirs">CollectFolders 的结果（相对目录，已排序）</param>
        /// <param name="conflicts">输出的命名冲突列表 —— 不同目录算出了同一个常量名，供调用方打警告</param>
        public static string BuildCode(string resRoot, List<string> relDirs, out List<string> conflicts)
        {
            conflicts = new List<string>();

            var lines = new List<string>
            {
                "// ============================================================",
                "// 本文件由 ABResPathGenerator 自动生成，请勿手改（重新生成会整份覆盖）。",
                "//",
                "// 内容：资源根目录 \"" + resRoot + "\" 下所有文件夹的「目录前缀」常量。",
                "// 用法：目录前缀与资源名一起交给资源系统（框架内部拼成 \"UI/Icon/Hero_1001\"）",
                "//       RevResManager.Load<GameObject>(RevResPath.UI_Icon, \"Hero_1001\", RevResGroup.UI);",
                "//",
                "// 为什么这样设计：",
                "//   · 目录前缀很少变 —— 新增资源不用重新生成，随手写上资源名即可；",
                "//   · const 是编译期常量：目录这一段有编译期保护，写错编译不过；",
                "//   · 两段直接传参即可：框架按「两段增量哈希」算缓存键，",
                "//     命中缓存时不拼字符串、零分配（见 RevResourceSystem\\Core\\RevResPathUtil.cs）；",
                "//   · 注意：\"Hero_1001\" 那一段仍是普通字符串 ——",
                "//     写错不会编译报错，只会在运行时加载失败（建议从 Project 窗口复制名字）。",
                "// ============================================================",
                "namespace " + NamespaceName,
                "{",
                "    /// <summary>资源目录前缀常量（值均以 \"/\" 结尾；与资源名一起传给 RevResManager）</summary>",
                "    public static class " + ClassName,
                "    {"
            };

            var used = new Dictionary<string, string>();     // 常量名 → 目录，用于查重
            foreach (string dir in relDirs)
            {
                string name = ToConstName(dir);
                if (name == null) continue;

                if (used.TryGetValue(name, out string owner))
                {
                    // 例：目录 "UI/Icon" 与 "UI_Icon" 会算成同一个 CLI 标识符 UI_Icon
                    conflicts.Add($"\"{owner}\" 与 \"{dir}\" 都算成了 {name}");
                    continue;
                }
                used[name] = dir;

                lines.Add($"        public const string {name} = \"{ToConstValue(dir)}\";");
            }

            lines.Add("    }");
            lines.Add("}");
            return string.Join("\n", lines) + "\n";
        }
    }
}
