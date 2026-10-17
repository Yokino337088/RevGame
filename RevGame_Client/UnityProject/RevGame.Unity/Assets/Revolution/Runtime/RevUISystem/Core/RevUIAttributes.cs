// ============================================================
// RevUIAttributes.cs —— 面板 / Part / 控件绑定 的声明式特性（纯 C#，不引用 UnityEngine）
//
// 位置：Runtime\RevUISystem\Core\
//
// 【为什么要用特性声明资源路径，而不是在代码里手填】
//   原框架（唐老师框架）靠"预制体名 = 面板类名"这一条约定去加载，很方便，但路径只有约定、没地方写清楚：
//   换个目录、改个名字就只能去改 UIMgr 或调用处。这里把"这个面板的预制体在哪"**写在面板类自己身上**：
//
//       [RevUIPanel(RevResPath.UI_Panel, RevUILayer.Normal)]   // 资源名默认 = 类名（BagPanel）
//       public sealed class BagPanel : RevUIPanel<BagData> { ... }
//
//   → 打开时框架直接从特性里读路径，业务侧一行路径都不用写（也就没有"调用处拼错路径"这种问题）。
//
// 【本文件不引用 UnityEngine】所以能被工程外的断言直接链接编译 ——
//   "根目录怎么写才规范 / 资源名默认取什么"这些规则才能在工程外逐条验。
// ============================================================
using System;

namespace Revolution
{
    /// <summary>
    /// 面板声明：写在面板类上，告诉框架"我的预制体在哪、挂哪一层、怎么缓存"。
    ///
    /// 【root 是什么】就是资源系统里的"根目录段"（和 <c>RevResManager.LoadAsync(rootPath, resName, ...)</c> 的 rootPath 同义）：
    ///   支持多级嵌套，如 "UI/Panel"、"UI/UIPanel/Lobby"；
    ///   结尾带不带 '/' 都行（框架会规范化），也可以用生成的 <c>RevResPath</c> 常量，享受编译期保护。
    ///
    /// 【name 省略时】用**面板类的类名**（原框架就是这么约定的：预制体名 = 面板类名），
    ///   所以绝大多数面板只写一个 root 就够了。
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, Inherited = false)]
    public sealed class RevUIPanelAttribute : Attribute
    {
        /// <summary>预制体所在的资源根目录段（必填），如 "UI/Panel"</summary>
        public string Root;

        /// <summary>预制体资源名（不带扩展名）；留空 = 用面板类名</summary>
        public string Name;

        /// <summary>挂在哪个层级（默认 Normal）</summary>
        public RevUILayer Layer = RevUILayer.Normal;

        /// <summary>关闭后的处置方式（默认 Unspecified → 用 RevUISetting.DefaultCacheMode）</summary>
        public RevUICacheMode CacheMode = RevUICacheMode.Unspecified;

        /// <summary>遮罩策略（默认 Auto → 按层级推断：Popup/Guide/Top 挡点击）</summary>
        public RevUIMaskMode Mask = RevUIMaskMode.Auto;

        /// <summary>
        /// 放进哪个画布（默认 Common）。只在 <c>RevUISetting.CanvasArchitecture = Split</c> 时生效；
        /// Static / Dynamic 只用于 Scene 层（主界面、HUD），别的层声明了会自动放回常用画布并告警。
        /// </summary>
        public RevUICanvasType CanvasType = RevUICanvasType.Common;

        /// <summary>
        /// 互斥组名：同组面板"打开新的就自动关掉旧的"（如所有背包页签共用一个组）。
        /// 留空 = 不互斥。
        /// </summary>
        public string ExclusiveGroup;

        /// <summary>是否参与返回栈（默认 true）；飘字/常驻界面设 false</summary>
        public bool InBackStack = true;

        public RevUIPanelAttribute(string root, RevUILayer layer = RevUILayer.Normal, string name = null)
        {
            Root = root;
            Layer = layer;
            Name = name;
        }
    }

    /// <summary>
    /// Part 声明。
    ///
    /// 【两种 Part，用不用这个特性来区分】
    ///   · **节点级 Part**（不加特性）：就摆在宿主面板的预制体里（如顶部栏、页签组），
    ///     靠 <c>[RevBind]</c> 字段或 <c>GetComponent</c> 拿到 —— 零加载成本。
    ///   · **预制体 Part**（加特性并写 root）：自己是一个独立预制体，**可以跨面板复用**
    ///     （同一个商店页签单元给背包、商城、活动三个面板用）—— 由 <c>RevUIPart.Create&lt;T&gt;</c> 挂到宿主里。
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, Inherited = false)]
    public sealed class RevUIPartAttribute : Attribute
    {
        /// <summary>Part 预制体所在的资源根目录段（必填），如 "UI/Part"</summary>
        public string Root;

        /// <summary>Part 预制体资源名（不带扩展名）；留空 = 用 Part 类名</summary>
        public string Name;

        public RevUIPartAttribute(string root, string name = null)
        {
            Root = root;
            Name = name;
        }
    }

    /// <summary>
    /// 控件绑定：写在面板/Part 的字段上，框架按"**字段名 ↔ 节点名**"自动找到并赋值。
    ///
    ///     [RevBind]                private Button _btnClose;      // 找名字叫 "btnClose" 的节点（前导下划线自动忽略）
    ///     [RevBind]                private Text   _title;         // 同上，类型自动适配（TMP 也一样，只要写对类型）
    ///     [RevBind("Top/Title")]   private Text   _title2;        // 节点重名 / 层次深时，用显式路径
    ///
    /// 【为什么不学王者做"代码生成"】
    ///   王者的绑定代码是生成的（字段名 = 节点名，UI_BINDING 占位）。生成物的好处是编译期就报错，
    ///   代价是多一道生成流程、美术改节点要重新生成。这里用**特性 + 反射（每类型只做一次，结果缓存）**
    ///   换掉生成流程：写法一样短，且改节点名后第一次运行就会明确报"找不到节点 xxx"（不会静默变 null）。
    /// </summary>
    [AttributeUsage(AttributeTargets.Field)]
    public sealed class RevBindAttribute : Attribute
    {
        /// <summary>显式节点路径（相对面板根节点，支持 "A/B/C" 多级）；为空 = 按字段名匹配节点名</summary>
        public string Path;

        public RevBindAttribute(string path = null)
        {
            Path = path;
        }
    }
}
