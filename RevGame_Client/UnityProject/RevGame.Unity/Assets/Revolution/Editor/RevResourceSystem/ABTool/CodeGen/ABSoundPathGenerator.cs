// ============================================================
// ABSoundPathGenerator.cs —— 生成 RevSoundPath.cs（音效系统的资源目录常量）
//
// 位置：Editor\RevResourceSystem\ABTool\CodeGen\
//
// 【解决什么问题？】
//   音效 / BGM 放在资源根目录的哪个子目录，属于"项目结构"而不是代码常量：
//   使用者在打包窗口（菜单 Revolution.Tools/资源/RevAB 打包工具 →「打包」页签 → 资源目录 → 音效目录）里选一次，
//   工具把结果写成 RevSoundPath 常量 → 音效系统读常量（目录写错编译不过、运行期零 IO）。
//
// 【生成到哪？】
//   ABBuildSetting.SoundPathCodePath = Assets/Revolution/Runtime/RevSoundSystem/Generated/RevSoundPath.cs
//   ★ 必须落在 Runtime 程序集里：Revolution.Runtime.asmdef 的 references 是空的，
//     反向引用 Generation（RevResPath 所在程序集）会成环 —— 所以音效路径常量只能自己生成一份。
//
// 【为什么要"防抖"？】
//   与 ABResPathGenerator 同理：本文件会在编辑器加载 / 改配置时被自动调用，
//   如果每次无脑写文件 → 触发脚本重编译 → 又回调 → 再写……会死循环。所以：内容没变就不写。
// ============================================================
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Revolution.Editor
{
    public static class ABSoundPathGenerator
    {
        private const string Eol = "\n";

        /// <summary>按当前配置生成（会通过 Instance 确保配置资产存在）</summary>
        public static void Generate() => Generate(ABBuildConfig.Instance);

        /// <summary>
        /// 只读取**已存在**的配置并生成，不创建配置资产（供编辑器加载时自动同步用）。
        /// ★ 配置不存在也照样生成（用默认值）：RevSoundPath.cs 是音效系统的编译依赖，缺了会编译不过。
        /// </summary>
        public static void GenerateSafe()
            => Generate(AssetDatabase.LoadAssetAtPath<ABBuildConfig>(ABBuildSetting.ConfigAssetPath));

        /// <summary>按指定配置生成（cfg 为 null 时用默认值）</summary>
        public static void Generate(ABBuildConfig cfg)
        {
            // ★ 框架被当作只读包安装（UPM）时跳过：包目录不可写，硬写只会留下"假生成"
            if (!ABBuildSetting.EnsureWritableForGeneratedCode("RevSoundPath", ABBuildSetting.SoundPathCodePath)) return;

            string sfx = cfg != null ? cfg.GetSfxRoot() : "";
            string bgm = cfg != null ? cfg.GetBgmRoot() : "";

            if (sfx.Length == 0) sfx = ABBuildConfig.DefaultSfxRoot;
            if (bgm.Length == 0) bgm = ABBuildConfig.DefaultBgmRoot;

            // 目录不存在只提醒、不阻断生成：否则文件不写出来，音效系统直接编译不过
            WarnIfFolderMissing(cfg, "音效", sfx);
            WarnIfFolderMissing(cfg, "BGM", bgm);

            Write(BuildCode(sfx, bgm));
        }

        /// <summary>菜单入口：不想开打包窗口时也能单独刷新</summary>
        [MenuItem("Revolution.Tools/资源/生成音效目录常量 (RevSoundPath)", false, 4)]
        private static void MenuGenerate() => Generate();

        // ==================== 内部 ====================

        private static void WarnIfFolderMissing(ABBuildConfig cfg, string label, string segment)
        {
            if (cfg == null || !cfg.HasResRoot) return;                 // 还没配资源根目录 → 无从校验

            string folder = cfg.GetResRoot() + "/" + segment;
            if (!AssetDatabase.IsValidFolder(folder))
                RevABLog.Warn($"[RevSoundPath] {label}目录还不存在：{folder}\n" +
                                 "（把音频放进去就行；或回打包窗口的「音效目录」重新选一个。）");
        }

        private static void Write(string code)
        {
            // ★ 防抖：内容没变直接返回（否则 写文件 → 重编译 → 回调 → 再写 会成死循环）
            if (File.Exists(ABBuildSetting.SoundPathCodePath) &&
                File.ReadAllText(ABBuildSetting.SoundPathCodePath) == code)
                return;

            string dir = Path.GetDirectoryName(ABBuildSetting.SoundPathCodePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

            File.WriteAllText(ABBuildSetting.SoundPathCodePath, code, Encoding.UTF8);
            AssetDatabase.ImportAsset(ABBuildSetting.SoundPathCodePath);

            RevABLog.Info($"[RevSoundPath] 已生成音效目录常量 → {ABBuildSetting.SoundPathCodePath}");
        }

        /// <summary>拼源码：内容与 Generated\RevSoundPath.cs 完全一致（防抖就是靠这个字符串比较）</summary>
        private static string BuildCode(string sfxSegment, string bgmSegment)
        {
            StringBuilder sb = new StringBuilder(1024);

            sb.Append("// ============================================================").Append(Eol);
            sb.Append("// RevSoundPath.cs —— 音效 / BGM 的资源目录常量").Append(Eol);
            sb.Append("//").Append(Eol);
            sb.Append("// ★ 本文件由 ABSoundPathGenerator 自动生成，请勿手改（重新生成会整份覆盖）。").Append(Eol);
            sb.Append("//   改目录：菜单 Revolution.Tools/资源/RevAB 打包工具 →「打包」页签 → 资源目录 → 音效目录").Append(Eol);
            sb.Append("//   （值存在 Assets/Editor/ABBuildConfig.asset 里，团队成员共享同一份）").Append(Eol);
            sb.Append("//").Append(Eol);
            sb.Append("// 【值是「相对资源根目录」的逻辑目录段（带结尾 /）】").Append(Eol);
            sb.Append("//   与 RevResManager.LoadAsync(rootPath, resName) 的 rootPath 同义；").Append(Eol);
            sb.Append("//   资源根目录本身不写在这里 —— 编辑器直读的前缀由 RevEditorResPolicy.ResRoot 负责。").Append(Eol);
            sb.Append("//").Append(Eol);
            sb.Append("// 【音效名可以带子目录（任意层）】").Append(Eol);
            sb.Append("//   RevSound.Play(\"UI/ui_click\") → 加载 <资源根目录>/Audio/Sfx/UI/ui_click").Append(Eol);
            sb.Append("//   子目录随时可建，不用改这里的配置：这两个常量只表示「根」这一层。").Append(Eol);
            sb.Append("//").Append(Eol);
            sb.Append("// 【为什么生成到 Runtime 程序集里（而不是 Generation 的 RevResPath）】").Append(Eol);
            sb.Append("//   Revolution.Runtime.asmdef 不引用任何程序集，而 Generation 反过来引用了 Runtime ——").Append(Eol);
            sb.Append("//   音效系统在 Runtime 里，反向引用 Generation 会成环，所以路径常量必须生成在 Runtime 内部。").Append(Eol);
            sb.Append("// ============================================================").Append(Eol);
            sb.Append("namespace Revolution").Append(Eol);
            sb.Append("{").Append(Eol);
            sb.Append("    /// <summary>音效系统用的资源目录常量（自动生成；改目录请走打包工具窗口）</summary>").Append(Eol);
            sb.Append("    public static class RevSoundPath").Append(Eol);
            sb.Append("    {").Append(Eol);

            AppendField(sb, "Sfx", "音效根目录", sfxSegment);
            sb.Append(Eol);
            AppendField(sb, "Bgm", "背景音乐根目录", bgmSegment);

            sb.Append("    }").Append(Eol);
            sb.Append("}").Append(Eol);

            return sb.ToString();
        }

        private static void AppendField(StringBuilder sb, string name, string summary, string segment)
        {
            string value = ToValue(segment);
            sb.Append("        /// <summary>").Append(summary).Append("：").Append(value).Append("</summary>").Append(Eol);
            sb.Append("        public const string ").Append(name).Append(" = \"").Append(value).Append("\";").Append(Eol);
        }

        /// <summary>逻辑段 → 常量值（统一带结尾 "/"，与 RevResPath 常量同风格）</summary>
        private static string ToValue(string segment)
            => string.IsNullOrEmpty(segment) ? "" : segment.Trim('/') + "/";
    }
}
