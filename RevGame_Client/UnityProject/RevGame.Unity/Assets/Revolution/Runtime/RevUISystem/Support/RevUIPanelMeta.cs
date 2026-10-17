// ============================================================
// RevUIPanelMeta.cs —— 面板 / Part 的"声明式元数据"解析（纯 C#，不引用 UnityEngine）
//
// 位置：Runtime\RevUISystem\Support\
//
// 【它解决什么】
//   把"面板类上写的 [RevUIPanel(root, layer, name...)]"变成框架能直接用的数据：
//   规范化后的根目录段、资源名（默认 = 类名）、层级、缓存方式、遮罩、互斥组、是否进返回栈。
//   业务侧因此**一行路径都不用写**，打开面板时也不存在"调用处把路径拼错"的可能。
//
// 【为什么解析结果要缓存】
//   同一个面板会被反复打开。反射读特性 + 字符串规范化只该做一次 ——
//   缓存之后，打开面板的路径是"字典命中 + 直接读字段"，没有任何反射开销。
//
// 【两种元数据放一个文件】
//   面板与 Part 用的是同一套规范化规则（根目录怎么写都行）、同一个缓存策略，
//   拆两个文件就得把这两段复制两遍。
//
// 【本文件不引用 UnityEngine】所以能被工程外的断言直接链接编译 ——
//   "根目录怎么写才规范 / 资源名默认取什么 / Auto 遮罩在该层到底挡不挡"都能在工程外逐条验。
// ============================================================
using System;
using System.Collections.Generic;

namespace Revolution
{
    /// <summary>面板元数据（由 <see cref="RevUIPanelAttribute"/> 解析而来，解析结果被缓存）</summary>
    public sealed class RevUIPanelMeta
    {
        /// <summary>面板的 C# 类型</summary>
        public readonly Type PanelType;

        /// <summary>规范化后的资源根目录段（不带结尾 '/'），如 "UI/Panel"</summary>
        public readonly string Root;

        /// <summary>资源名（不带扩展名），如 "BagPanel" —— 未声明时就是类名</summary>
        public readonly string Name;

        /// <summary>层级</summary>
        public readonly RevUILayer Layer;

        /// <summary>缓存方式（可能是 Unspecified，由管理器套用 RevUISetting 的默认值）</summary>
        public readonly RevUICacheMode CacheMode;

        /// <summary>遮罩策略（可能是 Auto，请用 <see cref="MaskResolved"/>）</summary>
        public readonly RevUIMaskMode Mask;

        /// <summary>声明的画布类型（最终进哪个画布还要看架构与层级，见 <see cref="RevUILayerUtil.ResolveCanvasType"/>）</summary>
        public readonly RevUICanvasType CanvasType;

        /// <summary>互斥组名（空 = 不互斥）</summary>
        public readonly string ExclusiveGroup;

        /// <summary>是否参与返回栈</summary>
        public readonly bool InBackStack;

        /// <summary>把 Auto 遮罩按层级解析后的最终结果（Popup / Guide / Top 默认挡点击）</summary>
        public RevUIMaskMode MaskResolved => RevUILayerUtil.ResolveMask(Mask, Layer);

        /// <summary>
        /// 面板身份键（唯一）：形如 "UI/Panel/BagPanel"。
        /// ★ 复用资源系统同一套拼接规则（<see cref="RevResPathUtil.Join"/>），
        ///   所以面板键与资源键天然一致，排查时可以互相印证。
        /// </summary>
        public string Key => RevResPathUtil.Join(Root, Name);

        private RevUIPanelMeta(Type panelType, RevUIPanelAttribute attr)
        {
            PanelType = panelType;
            Root = NormalizeRoot(attr.Root);
            Name = string.IsNullOrEmpty(attr.Name) ? panelType.Name : attr.Name.Trim();
            Layer = attr.Layer;
            CacheMode = attr.CacheMode;
            Mask = attr.Mask;
            CanvasType = attr.CanvasType;
            ExclusiveGroup = string.IsNullOrEmpty(attr.ExclusiveGroup) ? null : attr.ExclusiveGroup.Trim();
            InBackStack = attr.InBackStack;
        }

        // ---------------- 解析与缓存 ----------------

        private static readonly Dictionary<Type, RevUIPanelMeta> Cache = new Dictionary<Type, RevUIPanelMeta>();

        /// <summary>
        /// 取面板元数据（带缓存）。解析失败时抛 <see cref="InvalidOperationException"/> ——
        /// 这是"代码写错了"（忘了写特性 / root 空），必须在开发期就大声报出来，不能静默用一个猜的路径。
        /// </summary>
        public static RevUIPanelMeta Resolve(Type panelType)
        {
            if (TryResolve(panelType, out RevUIPanelMeta meta, out string error)) return meta;
            throw new InvalidOperationException(error);
        }

        /// <summary>取面板元数据（不抛异常），失败时通过 error 返回**可操作的**原因说明</summary>
        public static bool TryResolve(Type panelType, out RevUIPanelMeta meta, out string error)
        {
            if (panelType == null) { meta = null; error = "面板类型为 null。"; return false; }

            if (Cache.TryGetValue(panelType, out meta)) { error = null; return true; }

            RevUIPanelAttribute attr = GetAttribute<RevUIPanelAttribute>(panelType);
            if (attr == null)
            {
                error = $"面板 {panelType.Name} 没有 [RevUIPanel] 特性 —— 框架不知道该加载哪个预制体。\n" +
                        $"请在类上写一行，例如：\n" +
                        $"    [RevUIPanel(RevResPath.UI_Panel, RevUILayer.Normal)]\n" +
                        $"    public sealed class {panelType.Name} : RevUIPanel {{ ... }}\n" +
                        $"（资源名默认就是类名 {panelType.Name}，要换就加第三个参数。）";
                return false;
            }

            if (string.IsNullOrEmpty(attr.Root) || string.IsNullOrEmpty(attr.Root.Trim()))
            {
                error = $"面板 {panelType.Name} 的 [RevUIPanel] 没写资源根目录段（第一个参数 root）。\n" +
                        $"root 是预制体所在的目录段，和 RevResManager 的 rootPath 同义，例如 \"UI/Panel\" 或 RevResPath.UI_Panel。";
                return false;
            }

            meta = new RevUIPanelMeta(panelType, attr);
            Cache[panelType] = meta;
            error = null;
            return true;
        }

        /// <summary>清空缓存（域重载 / 单元测试用）</summary>
        public static void ClearCache() => Cache.Clear();

        /// <summary>
        /// 根目录段的规范化：去首尾空白、'\\' 统一成 '/'、去掉首尾多余的 '/'。
        /// "UI/Panel/"、"UI/Panel"、"\\UI\\Panel\\" 三种写法等价 —— 都得到 "UI/Panel"。
        /// </summary>
        public static string NormalizeRoot(string root)
        {
            if (string.IsNullOrEmpty(root)) return "";

            return root.Trim().Replace('\\', '/').Trim('/');
        }

        private static T GetAttribute<T>(Type type) where T : Attribute
        {
            object[] attrs = type.GetCustomAttributes(typeof(T), false);
            return attrs.Length > 0 ? (T)attrs[0] : null;
        }

        public override string ToString() => $"{PanelType.Name} → {Key}（{Layer}）";
    }

    /// <summary>Part 元数据（由 <see cref="RevUIPartAttribute"/> 解析而来）</summary>
    public sealed class RevUIPartMeta
    {
        /// <summary>Part 的 C# 类型</summary>
        public readonly Type PartType;

        /// <summary>规范化后的资源根目录段（为空 = 这个 Part 是"节点级"，不需要加载）</summary>
        public readonly string Root;

        /// <summary>资源名（未声明时 = 类名）</summary>
        public readonly string Name;

        /// <summary>真 = 独立预制体（可跨面板复用，用 RevUIPart.Create 加载）</summary>
        public bool IsPrefab => Root.Length > 0;

        /// <summary>Part 身份键（形如 "UI/Part/ShopTab"）</summary>
        public string Key => RevResPathUtil.Join(Root, Name);

        private RevUIPartMeta(Type partType, RevUIPartAttribute attr)
        {
            PartType = partType;
            Root = attr == null ? "" : RevUIPanelMeta.NormalizeRoot(attr.Root);
            Name = attr == null || string.IsNullOrEmpty(attr.Name) ? partType.Name : attr.Name.Trim();
        }

        private static readonly Dictionary<Type, RevUIPartMeta> Cache = new Dictionary<Type, RevUIPartMeta>();

        /// <summary>
        /// 取 Part 元数据（带缓存）。
        /// ★ 与面板不同：**没有 [RevUIPart] 特性不是错误** —— 那表示它是"节点级 Part"，
        ///   就摆在宿主面板的预制体里，根本不需要加载。
        /// </summary>
        public static RevUIPartMeta Resolve(Type partType)
        {
            if (partType == null) throw new ArgumentNullException(nameof(partType));

            if (Cache.TryGetValue(partType, out RevUIPartMeta meta)) return meta;

            object[] attrs = partType.GetCustomAttributes(typeof(RevUIPartAttribute), false);
            meta = new RevUIPartMeta(partType, attrs.Length > 0 ? (RevUIPartAttribute)attrs[0] : null);
            Cache[partType] = meta;
            return meta;
        }

        /// <summary>清空缓存（域重载 / 单元测试用）</summary>
        public static void ClearCache() => Cache.Clear();

        public override string ToString()
            => IsPrefab ? $"{PartType.Name} → {Key}（预制体 Part）" : $"{PartType.Name}（节点级 Part）";
    }
}
