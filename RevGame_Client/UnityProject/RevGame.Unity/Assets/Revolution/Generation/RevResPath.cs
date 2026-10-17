// ============================================================
// 本文件由 ABResPathGenerator 自动生成，请勿手改（重新生成会整份覆盖）。
//
// 内容：资源根目录 "Assets/GameRes" 下所有文件夹的「目录前缀」常量。
// 用法：目录前缀与资源名一起交给资源系统（框架内部拼成 "UI/Icon/Hero_1001"）
//       RevResManager.Load<GameObject>(RevResPath.UI_Icon, "Hero_1001", RevResGroup.UI);
//
// 为什么这样设计：
//   · 目录前缀很少变 —— 新增资源不用重新生成，随手写上资源名即可；
//   · const 是编译期常量：目录这一段有编译期保护，写错编译不过；
//   · 两段直接传参即可：框架按「两段增量哈希」算缓存键，
//     命中缓存时不拼字符串、零分配（见 RevResourceSystem\Core\RevResPathUtil.cs）；
//   · 注意："Hero_1001" 那一段仍是普通字符串 ——
//     写错不会编译报错，只会在运行时加载失败（建议从 Project 窗口复制名字）。
// ============================================================
namespace Revolution
{
    /// <summary>资源目录前缀常量（值均以 "/" 结尾；与资源名一起传给 RevResManager）</summary>
    public static class RevResPath
    {
        public const string Data = "Data/";
        public const string RevHotDemo = "RevHotDemo/";
        public const string RevUIDemo = "RevUIDemo/";
        public const string Sound = "Sound/";
        public const string Sound_BGM = "Sound/BGM/";
        public const string Sound_SFX = "Sound/SFX/";
        public const string UI = "UI/";
    }
}
