using System;

namespace Revolution
{
    /// <summary>
    /// 资源策略接口
    /// <para>【它是整个架构的"路由层"】</para>
    /// <para>门面 RevResManager 不知道资源从哪来，只把标准路径交给策略族，按注册顺序依次询问。</para>
    /// <para>【它是"责任链 + 兜底"，不是"单选"】</para>
    /// <para>第一条匹配的策略如果加载失败，只要它 AllowFallback = true，门面就会继续问下一条策略
    /// —— 这正是"AB 里没有就落回 Resources"的实现方式。</para>
    /// </summary>
    public interface IRevResPolicy
    {
        /// <summary>这个路径归不归我管（注册顺序 = 优先级，先匹配的先上）</summary>
        bool Match(string standardPath);

        /// <summary>
        /// 标准路径 → 真实路径（给 Loader 用）。
        /// 返回 null 或空串表示"匹配了但映射失败"：
        ///   AllowFallback = true  → 换下一条策略继续试
        ///   AllowFallback = false → 直接返回空句柄
        /// </summary>
        string MapPath(string standardPath, Type contentType);

        /// <summary>创建本策略对应的加载器</summary>
        IRevResLoader CreateLoader();

        /// <summary>
        /// 加载失败后是否允许继续尝试后面的策略（兜底开关）。
        /// 例：RevABResPolicy = true（AB 里找不到 → 让 Resources 再试一次）。
        /// </summary>
        bool AllowFallback { get; }
    }
}