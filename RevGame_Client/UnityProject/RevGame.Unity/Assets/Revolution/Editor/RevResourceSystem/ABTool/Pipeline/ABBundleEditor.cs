// ============================================================
// ABBundleEditor.cs —— 改分包标记的全部操作（新建 / 加入 / 移出 / 重命名 / 删除 / 换层级 / 清理）
//
// 位置：Editor\RevResourceSystem\ABTool\Pipeline\
//
// 【为什么单独一个类】
//   以前这些操作写在分包浏览视图里，别的页签（依赖 / 检查 里的"移到共享包"）想用只能复制一份。
//   现在：界面只管"用户想做什么"，怎么改 .meta 里的标记全在这里 —— 一处实现，处处一致。
//
// 【★ 前提：Unity 里"包"不是实体】
//   分包 = 资源（或文件夹）身上写着的 assetBundleName（存在 .meta 里）。所以：
//     · 新建包 = 一个还没人用的名字（资源拖进去才真正存在）；
//     · 删除包 = 清掉所有写着它的标记（资源本身不动）；
//     · 重命名 = 把所有显式写着旧名的标记改成新名 —— **连文件夹一起改**，否则"删了包、资源却还在包里"。
//
// 【三条和 AssetBundle Browser 一致的规则】
//   ① 包名一律小写：Unity 的 assetBundleName 会被强制转小写，不在这里先转，界面上的选中 / 待建名单就对不上；
//   ② 改名 / 删除后调 AssetDatabase.RemoveUnusedAssetBundleNames()：
//      否则旧名字会以"未使用的包名"留在工程里，下次校验被当成"空包"报错；
//   ③ 给文件夹下的某个资源单独写标记 = 它脱离文件夹那个包（显式标记优先于继承）——
//      "把继承来的资源移到别的包"就是这么实现的。
//
// 【批量写】大量改标记时用 StartAssetEditing / StopAssetEditing 包住：合并成一次导入，快得多。
// ============================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;

namespace Revolution.Editor
{
    /// <summary>分包标记的编辑操作（只改 .meta 里的 assetBundleName / Variant，不动资源文件本身）</summary>
    internal static class ABBundleEditor
    {
        // ============================================================
        // 名字规则
        // ============================================================

        /// <summary>规范化包名：去首尾空白、'\\' → '/'、转小写（Unity 会强制小写，这里先转，前后一致）</summary>
        public static string Normalize(string name)
            => (name ?? string.Empty).Trim().Replace('\\', '/').Trim('/').ToLowerInvariant();

        /// <summary>包名合法性：'.' 是变体分隔符、空格与 '\\' 各平台容易出问题、不允许空段</summary>
        public static bool IsLegalBundleName(string name, out string why)
        {
            if (string.IsNullOrEmpty(name)) { why = "名字不能为空"; return false; }
            if (name.Contains("\\")) { why = "不要包含 '\\'（用 '/' 分层）"; return false; }
            if (name.Contains(".")) { why = "不要包含 '.'（Unity 用它分隔变体）"; return false; }
            if (name.Contains(" ")) { why = "不要包含空格"; return false; }
            if (name.EndsWith("/") || name.StartsWith("/")) { why = "首尾不要带 '/'"; return false; }

            foreach (string seg in name.Split('/'))
                if (seg.Length == 0) { why = "不要出现连续的 '/'"; return false; }

            why = null;
            return true;
        }

        /// <summary>
        /// 从资源 / 文件夹名推一个安全的包名（拖资源到空白处新建包时用）：
        /// 小写，非 [a-z0-9_-] 的字符换成 '_'，拿不到就叫 "bundle"。
        /// </summary>
        public static string SafeNameFrom(string assetPath)
        {
            string raw = AssetDatabase.IsValidFolder(assetPath)
                ? Path.GetFileName(assetPath.TrimEnd('/'))
                : Path.GetFileNameWithoutExtension(assetPath);

            var sb = new StringBuilder(raw.Length);
            foreach (char c in raw.ToLowerInvariant())
                sb.Append((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_' || c == '-' ? c : '_');

            string name = sb.ToString().Trim('_');
            return name.Length == 0 ? "bundle" : name;
        }

        /// <summary>在 existing 里挑一个不重名的：name、name_1、name_2 …</summary>
        public static string MakeUnique(string name, ICollection<string> existing)
        {
            if (!existing.Contains(name)) return name;

            for (int i = 1; ; i++)
            {
                string candidate = name + "_" + i;
                if (!existing.Contains(candidate)) return candidate;
            }
        }

        /// <summary>path 是否等于 prefix，或在 prefix 这个"文件夹"之下（"ui" 匹配 "ui"、"ui/main"，不匹配 "uix"）</summary>
        public static bool IsUnder(string name, string prefix)
            => name == prefix || name.StartsWith(prefix + "/", StringComparison.Ordinal);

        /// <summary>包名的上一级（"ui/main/top" → "ui/main"；没有上一级返回 ""）</summary>
        public static string ParentOf(string name)
        {
            int slash = name.LastIndexOf('/');
            return slash < 0 ? string.Empty : name.Substring(0, slash);
        }

        /// <summary>拼 "上一级/名字"（上一级为空时就是名字本身）</summary>
        public static string Combine(string parent, string leaf)
            => string.IsNullOrEmpty(parent) ? leaf : parent + "/" + leaf;

        // ============================================================
        // 加入 / 移出
        // ============================================================

        /// <summary>
        /// 把资源（或文件夹）加入包；文件夹会被整个标记（子文件继承）。
        /// 返回实际改了几个；moved = 其中从别的包挪过来的；skipped = 没改的（本来就在 / 脚本 / 取不到导入器）。
        /// </summary>
        public static int AddAssets(string bundle, IEnumerable<string> assetPaths, out int moved, out int skipped)
        {
            // out 参数不能在 lambda 里用：先用局部变量计数，结束后再赋给 out
            int movedCount = 0, skippedCount = 0, added = 0;

            Batch(() =>
            {
                foreach (string path in assetPaths)
                {
                    if (string.IsNullOrEmpty(path) || path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                    {
                        skippedCount++;                                        // 脚本不进包
                        continue;
                    }

                    AssetImporter importer = AssetImporter.GetAtPath(path);
                    if (importer == null) { skippedCount++; continue; }

                    string current = importer.assetBundleName;
                    if (current == bundle && string.IsNullOrEmpty(importer.assetBundleVariant)) { skippedCount++; continue; }

                    // 显式标记在别的包 → 挪过来；继承自文件夹 → 显式标记会覆盖继承（这就是"移到别的包"）
                    string effective = AssetDatabase.GetImplicitAssetBundleName(path);
                    if (!string.IsNullOrEmpty(effective) && effective != bundle) movedCount++;

                    importer.assetBundleName = bundle;
                    importer.assetBundleVariant = string.Empty;                // 顺带清变体，免得出现"包名.变体"这种难管的名字
                    added++;
                }
            });

            moved = movedCount;
            skipped = skippedCount;

            // 标记写在 .meta 里，显式落盘；挪走的资源可能让原来的包变成"没人用"，顺手清掉
            if (added > 0) Finish();
            return added;
        }

        /// <summary>清掉这些资源自己身上的标记（继承来的清不掉 —— 调用方先用 <see cref="FindMarkSource"/> 判断）</summary>
        public static int ClearMarks(IEnumerable<string> assetPaths)
        {
            int changed = 0;

            Batch(() =>
            {
                foreach (string path in assetPaths)
                {
                    AssetImporter importer = AssetImporter.GetAtPath(path);
                    if (importer == null || string.IsNullOrEmpty(importer.assetBundleName)) continue;

                    importer.assetBundleName = string.Empty;
                    importer.assetBundleVariant = string.Empty;
                    changed++;
                }
            });

            if (changed > 0) Finish();
            return changed;
        }

        // ============================================================
        // 重命名 / 删除（按"包名或包文件夹"整体处理）
        // ============================================================

        /// <summary>
        /// 把 oldPath（一个包，或一个包"文件夹"）整体改名为 newPath：
        ///   "ui" → "hud" 会同时把 "ui"、"ui/main"、"ui/top" 改成 "hud"、"hud/main"、"hud/top"。
        /// 返回改了几处标记（含文件夹上的标记）。
        /// </summary>
        public static int RenamePrefix(string oldPath, string newPath)
        {
            if (string.IsNullOrEmpty(oldPath) || oldPath == newPath) return 0;

            return Rewrite(name =>
            {
                if (name == oldPath) return newPath;
                if (name.StartsWith(oldPath + "/", StringComparison.Ordinal))
                    return newPath + name.Substring(oldPath.Length);
                return name;
            });
        }

        /// <summary>删除 path 这个包（或包文件夹下的全部包）：清掉所有写着它们的标记。返回清了几处。</summary>
        public static int DeletePrefix(string path)
        {
            if (string.IsNullOrEmpty(path)) return 0;
            return Rewrite(name => IsUnder(name, path) ? string.Empty : name);
        }

        /// <summary>清理"已经没有任何资源使用"的包名（改名 / 删除后的残留），返回清掉几个</summary>
        public static int RemoveUnusedNames()
        {
            int before = AssetDatabase.GetAllAssetBundleNames().Length;
            AssetDatabase.RemoveUnusedAssetBundleNames();
            return before - AssetDatabase.GetAllAssetBundleNames().Length;
        }

        // ============================================================
        // 查询
        // ============================================================

        /// <summary>
        /// 这个资源的包标记"写在谁身上"：它自己，或最近的那个带标记的上级文件夹；都没有返回 null。
        /// （回答"为什么这个资源在这个包里"：继承来的标记单独移不掉，得去改那个文件夹。）
        /// </summary>
        public static string FindMarkSource(string assetPath)
        {
            string path = assetPath;
            while (!string.IsNullOrEmpty(path) && path.StartsWith("Assets", StringComparison.Ordinal))
            {
                AssetImporter importer = AssetImporter.GetAtPath(path);
                if (importer != null && !string.IsNullOrEmpty(importer.assetBundleName)) return path;

                int slash = path.LastIndexOf('/');
                if (slash < 0) break;
                path = path.Substring(0, slash);
            }
            return null;
        }

        // ============================================================
        // 内部
        // ============================================================

        /// <summary>
        /// 对工程里每一个"带显式标记"的资源 / 文件夹，把它的生效包名（含变体）交给 map 算新名字；
        /// 新名字不同就改写（空串 = 清掉）。一趟扫完，批量写入。
        /// </summary>
        private static int Rewrite(Func<string, string> map)
        {
            int changed = 0;

            Batch(() =>
            {
                foreach (string path in AssetDatabase.GetAllAssetPaths())
                {
                    if (!path.StartsWith("Assets/", StringComparison.Ordinal)) continue;

                    AssetImporter importer = AssetImporter.GetAtPath(path);
                    if (importer == null || string.IsNullOrEmpty(importer.assetBundleName)) continue;

                    string variant = importer.assetBundleVariant;
                    string effective = string.IsNullOrEmpty(variant)
                        ? importer.assetBundleName
                        : importer.assetBundleName + "." + variant;

                    string mapped = map(effective);
                    if (mapped == effective) continue;

                    importer.assetBundleName = mapped;
                    importer.assetBundleVariant = string.Empty;        // 改名后不再保留变体（工具内不允许带 '.' 的包名）
                    changed++;
                }
            });

            if (changed > 0) Finish();
            return changed;
        }

        /// <summary>落盘 + 清掉因此变成"没人用"的旧包名</summary>
        private static void Finish()
        {
            AssetDatabase.SaveAssets();
            AssetDatabase.RemoveUnusedAssetBundleNames();
        }

        /// <summary>批量改标记：合并成一次导入（出异常也一定配对 StopAssetEditing，否则编辑器会一直不导入）</summary>
        private static void Batch(Action action)
        {
            AssetDatabase.StartAssetEditing();
            try
            {
                action();
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }
        }
    }
}
