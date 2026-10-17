// ============================================================
// RevResPathUtil.cs —— 逻辑路径的拼接与键计算（纯 C#）
//
// 位置：Runtime\RevResourceSystem\Core\
//
// 【背景：为什么 API 是两个参数，内部却是一条字符串】
//   对外（业务）写法是"根目录 + 资源名"两段：
//       RevResManager.Load<Sprite>(RevResPath.UI_Icon, "Hero_1001", RevResGroup.UI);
//   对内（映射表 ResMap、AB 包名、RevResHandle.StandardPath）认的是一条完整逻辑路径
//   "UI/Icon/Hero_1001"。这个文件负责把两段合成一条，以及——更重要的——
//   **不合成字符串也能算出它的键**。
//
// 【两个东西必须严格等价】
//       ComputeKey(rootPath, resName)  ==  ComputeKey(Join(rootPath, resName))
//   意义在于：缓存命中的路径（绝大多数请求）可以**零字符串分配**地查表 ——
//   两个字符串增量喂给 FNV，得到和"先拼好再哈希"完全一样的结果。
//   一旦不等价，就会出现"用两个参数加载的资源，用完整路径查不到"这类鬼故事。
//   ★ 工程外断言专门验了这条等价性，含空根目录、"Data"、"Data/"、"/" 等边界。
//
// 【另外提供】NormalizeResName：统一资源名的写法（Windows 反斜杠 / 多余斜杠 / 首尾空格 / 首尾斜杠）。
//   调用方（如音效系统）先过一遍它，就能挡掉最常见的手写失误；它不改语义、不猜资源。
//
// 【本文件不引用 UnityEngine】所以能被工程外的断言直接链接编译。
// ============================================================
using System;

namespace Revolution
{
    /// <summary>逻辑路径工具：拼接 + 键计算（FNV-1a，与资源系统的缓存键同源）。</summary>
    public static class RevResPathUtil
    {
        /// <summary>逻辑路径的分隔符（统一用正斜杠，跨平台一致）。</summary>
        public const char Separator = '/';

        private const ulong FnvOffset = 14695981039346656037UL;
        private const ulong FnvPrime = 1099511628211UL;

        /// <summary>
        /// 资源名是否合法。
        /// ★ 只校验"资源名"：根目录允许为空 —— 那表示资源就在资源根目录下（没有子目录）。
        /// </summary>
        public static bool IsValidResName(string resName) => !string.IsNullOrEmpty(resName);

        /// <summary>
        /// 把"根目录 + 资源名"拼成完整逻辑路径。
        /// 根目录可以是 "UI/Icon/"（生成的 RevResPath 常量就是这种带结尾斜杠的形式），
        /// 也可以是 "UI/Icon"（自动补斜杠）；为空或 "/" 时结果就是资源名本身。
        /// </summary>
        public static string Join(string rootPath, string resName)
        {
            if (string.IsNullOrEmpty(rootPath) || rootPath == "/") return resName;
            if (rootPath[rootPath.Length - 1] == Separator) return rootPath + resName;

            return rootPath + Separator + resName;
        }

        // ============================================================
        // 资源名规范化（挡掉"手写习惯"，不改语义）
        // ============================================================

        /// <summary>
        /// 规范化"资源名"—— 它同时是"相对根目录的资源路径"，可以带任意层子目录：
        /// <code>
        /// "UI/click"                  → 原样（规范写法）
        /// "UI\\click"（Windows 反斜杠） → "UI/click"
        /// "/UI//click/"（多余斜杠）     → "UI/click"
        /// "  "                        → ""（= 非法，调用方按"空名字"处理）
        /// </code>
        /// ★ 已经是规范写法的名字**不产生任何分配**（先扫一遍，脏了才重建字符串）——
        ///   热路径（每帧播放）用得起。
        /// ★ 这不是"纠错"：它只统一写法，不猜你想指哪个资源；真正写错的路径依旧会在加载时失败。
        /// ★ 用法见音效系统（RevSound 的 Play/PlayBgm 都先过这一层）。
        /// </summary>
        public static string NormalizeResName(string resName)
        {
            if (string.IsNullOrEmpty(resName)) return string.Empty;

            int start = 0;
            int end = resName.Length - 1;

            // 首尾：空格与两种斜杠都裁掉（"UI/click/"、"\\UI\\click" 都当同一种手写习惯）
            while (start <= end && IsTrimChar(resName[start])) start++;
            while (end >= start && IsTrimChar(resName[end])) end--;

            if (start > end) return string.Empty;

            bool dirty = false;
            for (int i = start; i <= end; i++)
            {
                char c = resName[i];
                if (c == '\\' || (c == Separator && i > start && resName[i - 1] == Separator)) { dirty = true; break; }
            }

            string trimmed = start == 0 && end == resName.Length - 1
                ? resName
                : resName.Substring(start, end - start + 1);

            if (!dirty) return trimmed;

            var sb = new System.Text.StringBuilder(trimmed.Length);
            for (int i = 0; i < trimmed.Length; i++)
            {
                char c = trimmed[i];
                if (c == '\\') c = Separator;
                if (c == Separator && (sb.Length == 0 || sb[sb.Length - 1] == Separator)) continue;   // 首斜杠 / 重复斜杠
                sb.Append(c);
            }

            // 末尾斜杠（转换后才出现的，例如 "UI\click\" 里最后那个反斜杠）
            if (sb.Length > 0 && sb[sb.Length - 1] == Separator) sb.Length--;

            return sb.ToString();
        }

        /// <summary>首尾允许被裁掉的字符：空格与两种斜杠。</summary>
        private static bool IsTrimChar(char c) => c == ' ' || c == Separator || c == '\\';

        /// <summary>完整逻辑路径的 FNV-1a 键（与 <see cref="ComputeKey(string, string)"/> 等价）。</summary>
        public static ulong ComputeKey(string standardPath)
        {
            if (string.IsNullOrEmpty(standardPath)) return FnvOffset;

            ulong hash = FnvOffset;
            for (int i = 0; i < standardPath.Length; i++)
            {
                hash ^= standardPath[i];
                hash *= FnvPrime;
            }

            return hash;
        }

        /// <summary>
        /// "根目录 + 资源名"的键：**不拼字符串**，把两段（必要时补一个 '/'）依次喂给同一个 FNV。
        /// 结果与 <c>ComputeKey(Join(rootPath, resName))</c> 完全一致 —— 这是热路径零分配的前提。
        /// </summary>
        public static ulong ComputeKey(string rootPath, string resName)
        {
            ulong hash = FnvOffset;

            if (!string.IsNullOrEmpty(rootPath) && rootPath != "/")
            {
                for (int i = 0; i < rootPath.Length; i++)
                {
                    hash ^= rootPath[i];
                    hash *= FnvPrime;
                }

                // 根目录没带结尾斜杠 → 补一个（等价于 Join 里补的那个字符）
                if (rootPath[rootPath.Length - 1] != Separator)
                {
                    hash ^= Separator;
                    hash *= FnvPrime;
                }
            }

            if (!string.IsNullOrEmpty(resName))
            {
                for (int i = 0; i < resName.Length; i++)
                {
                    hash ^= resName[i];
                    hash *= FnvPrime;
                }
            }

            return hash;
        }
    }
}
