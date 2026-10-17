// ============================================================
// RevSoundPath.cs —— 音效 / BGM 的资源目录常量
//
// ★ 本文件由 ABSoundPathGenerator 自动生成，请勿手改（重新生成会整份覆盖）。
//   改目录：菜单 Revolution.Tools/资源/RevAB 打包工具 →「打包」页签 → 资源目录 → 音效目录
//   （值存在 Assets/Editor/ABBuildConfig.asset 里，团队成员共享同一份）
//
// 【值是「相对资源根目录」的逻辑目录段（带结尾 /）】
//   与 RevResManager.LoadAsync(rootPath, resName) 的 rootPath 同义；
//   资源根目录本身不写在这里 —— 编辑器直读的前缀由 RevEditorResPolicy.ResRoot 负责。
//
// 【音效名可以带子目录（任意层）】
//   RevSound.Play("UI/ui_click") → 加载 <资源根目录>/Audio/Sfx/UI/ui_click
//   子目录随时可建，不用改这里的配置：这两个常量只表示「根」这一层。
//
// 【为什么生成到 Runtime 程序集里（而不是 Generation 的 RevResPath）】
//   Revolution.Runtime.asmdef 不引用任何程序集，而 Generation 反过来引用了 Runtime ——
//   音效系统在 Runtime 里，反向引用 Generation 会成环，所以路径常量必须生成在 Runtime 内部。
// ============================================================
namespace Revolution
{
    /// <summary>音效系统用的资源目录常量（自动生成；改目录请走打包工具窗口）</summary>
    public static class RevSoundPath
    {
        /// <summary>音效根目录：Audio/Sfx/</summary>
        public const string Sfx = "Audio/Sfx/";

        /// <summary>背景音乐根目录：Audio/Bgm/</summary>
        public const string Bgm = "Audio/Bgm/";
    }
}
