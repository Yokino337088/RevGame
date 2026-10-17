// RevResDefine.cs —— 资源系统枚举定义

using System;

namespace Revolution
{
    /// <summary>
    /// 资源加载状态机。
    ///
    /// 【为什么需要状态机？】
    ///   加载是异步的，业务可能在"还没加载完"时就访问资源。
    ///   有了 State，调用方就能明确区分：
    ///     · Unload  → 还没开始加载
    ///     · Loading → 正在加载（Content 一定是 null，别用）
    ///     · Loaded  → 可以安全使用 Content
    ///     · LoadErr → 加载失败（原因见 handle.ErrorReason）
    /// </summary>
    public enum RevResState
    {
        Unload,     // 未加载
        Loading,    // 加载中：Content 暂为 null
        Loaded,     // 加载完成：Content 可用
        LoadErr     // 加载失败：原因见 handle.ErrorReason
    }

    /// <summary>
    /// 资源分组：资源的"归属标签"，**只用于按业务域批量卸载**。
    ///
    /// 【它不参与加载】
    ///   从哪读（编辑器 / AB / Resources）、读成什么类型，由
    ///   「策略链 + ResMap 映射表 + ContentType」决定，与本枚举毫无关系。
    ///   分组只在"卸载"这一步被用到：
    ///     RevResManager.UnloadGroup(group) / RevResBootstrap.Shutdown(group) / RevResPreloader.Release(group)
    ///
    /// 【归属规则：首次确定，之后不再变更（一个资源只属于一个分组）】
    ///   同一资源被多个域加载时，归属算"第一个给它定组的域"；
    ///   其它域照样能用，靠引用计数保护（RefCount > 0 时不会被安全卸载摘掉）。
    ///   若加载时传 Unknown，则归属保持 Unknown（后续可被首次的非 Unknown 补齐，
    ///   但一旦定了就不再变）——见 RevResManager.AssignGroup。
    ///
    /// 【共享资源怎么办？】
    ///   跨域共享（如公共图集被 UI 和 Battle 同时引用）的资源，打
    ///   RevResInstanceFlag.Resident 标志 —— 它**永不参与分组卸载**，连 force 也不会误伤。
    ///
    /// 【用法】加载时传入分组；切场景时 RevResBootstrap.Instance.Shutdown(RevResGroup.Scene)。
    /// </summary>
    public enum RevResGroup
    {
        Unknown,    // 未指定（默认；不会被任何分组连带卸载，只能靠 UnloadAll / 自动卸载回收）
        Battle,     // 战斗：技能特效、子弹、英雄模型
        UI,         // 界面：窗体、图标、字体
        Sound,      // 音效：BGM、技能音、语音
        Config,     // 配置：策划表（全程要用，建议改用 Resident 标志）
        Scene       // 场景：地图、光照贴图（过场景时整体释放）
    }

    /// <summary>
    /// 资源实例标志位。
    ///
    /// 【为什么用位标志（[Flags]）而不是一堆 bool？】
    ///   一个资源可能同时具备多种属性（既要缓存、又是常驻）。
    ///   用 uint 的每一位表示一种属性，一个字段就能装下全部，
    ///   判断/开关都是一次位运算，比多个 bool 更省内存、更快。
    /// </summary>
    [Flags]
    public enum RevResInstanceFlag
    {
        None           = 0,
        NeedCache      = 1 << 0,   // 需要进缓存（重复加载直接命中）
        Resident       = 1 << 1,   // 常驻：即使引用为 0 也不被分组卸载
        InAsyncLoading = 1 << 2,   // 正在异步加载（用于合并重复请求）
        UsedAccurate   = 1 << 3,   // 曾执行过精确加载（排查用）
        MarkedUnused   = 1 << 4,   // 已放入"未使用表"，等待延迟释放
        LoadFromEditor = 1 << 5,   // 由编辑器策略加载（便于排查来源）
        Preloaded      = 1 << 6    // 由"预加载系统"持有引用（可被批量释放；自动卸载默认跳过）
    }

    /// <summary>
    /// 加载优先级（数字越大越先加载，作用于异步加载泵的排队顺序）。
    ///
    /// 【为什么需要它？】
    ///   预加载往往是"后台慢慢铺"，不该和玩家当前操作的资源抢 IO。
    ///   给预加载一个低优先级、给关键资源一个高优先级，加载泵排队时就能自动让路。
    /// </summary>
    public enum RevResLoadPriority
    {
        Background = 0,     // 后台预加载（不抢前台）
        Normal     = 100,   // 普通请求（默认）
        Urgent     = 300    // 紧急（如进战斗前的关键资源）
    }

    /// <summary>
    /// 加载失败原因。
    /// 【为什么要细分？】本方案不打日志，失败后只剩 handle.ErrorReason 这一条线索。
    /// </summary>
    public enum RevResLoadErrorReason
    {
        None,
        PolicyNotFound,     // 没有任何策略能处理该路径（通常是前缀写错）
        PathNotMapped,      // AB 映射表里查不到（资源没打进包 / 忘了打包）
        BundleLoadFail,     // AB 包本身加载失败（文件不存在 / 平台不匹配）
        AssetLoadFail,      // 包打开了，但里面没有这个资源（资源名写错）
        FileNotExist,       // 文件不存在（Resources / 编辑器路径）
        TypeMismatch,       // 资源类型不匹配（如按 Texture 加载了一个 Prefab）
        Cancelled           // 被取消（切场景中断在途加载）
    }

    /// <summary>
    /// 资源分组的扩展工具。
    ///
    /// 【内置分组够不够用？】
    ///   Unknown / Battle / UI / Sound / Config / Scene 这六个覆盖了绝大多数项目。
    ///   不够时有两种扩展方式：
    ///
    ///   ① 【推荐】直接在 RevResGroup 枚举里加值（Bag、Shop、Activity …）
    ///      —— 类型安全、IDE 有补全、switch/比较都正常，且不影响已有代码。
    ///
    ///   ② 分组名由配置/运行时决定时（例如"每个活动一个分组"），用 Custom(n)：
    ///         RevResGroup g = RevResGroupUtil.Custom(activityId);
    ///         RevResManager.LoadAsync&lt;Sprite&gt;(path, s =&gt; { }, g);
    ///         RevResBootstrap.Instance.Shutdown(RevResGroupUtil.Custom(activityId));   // 活动结束整组卸
    ///      —— 它把编号映射到 CustomBase 之后的区间，不会和内置值撞车。
    ///
    /// 【为什么不用字符串做分组？】字符串比较要分配、要比内容，还会让"卸载时点名不到"变成
    ///   运行时才能发现的错误（拼错一个字母就白卸了）。用数值 ID 则零成本、可断言。
    /// </summary>
    public static class RevResGroupUtil
    {
        /// <summary>业务自定义分组的编号起点（内置枚举值到 5，从 1000 起足够安全）</summary>
        public const int CustomBase = 1000;

        /// <summary>取第 index 个自定义分组（index 从 0 开始；建议用你的业务 ID）</summary>
        public static RevResGroup Custom(int index) => (RevResGroup)(CustomBase + index);

        /// <summary>是否是自定义分组（排查 / 日志用）</summary>
        public static bool IsCustom(RevResGroup group) => (int)group >= CustomBase;
    }
}
