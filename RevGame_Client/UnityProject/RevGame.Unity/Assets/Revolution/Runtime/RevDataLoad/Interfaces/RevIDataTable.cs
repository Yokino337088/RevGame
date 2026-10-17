// ============================================================
// RevIDataTable.cs —— 数据表容器的统一契约
//
// 位置：Runtime\RevDataLoad\Interfaces\
//
// 【为什么需要这个接口？】
//   RevDataTableManager 要能"动态加载 / 卸载任意一张表"，就必须能把所有表
//   一视同仁地存进同一本字典（键是表名，值是容器）。
//   若没有统一契约，管理器就只能针对每张表写一份代码 —— 表是生成出来的，
//   数量不定，这条路走不通。
//
//   有了它，管理器只认 6 个成员：叫什么、资源在哪个目录、资源叫什么、有几行、装没装、怎么清。
// ============================================================
namespace Revolution
{
    /// <summary>数据表容器的统一契约（由生成器生成的 XxxTable 实现）</summary>
    public interface RevIDataTable
    {
        /// <summary>表名（= 容器类去掉 Table 后缀，如 HeroSkinTable → HeroSkin）</summary>
        string TableName { get; }

        /// <summary>
        /// 数据文件所在的根目录（带结尾斜杠），如 "Data/"。
        /// 与 <see cref="ResourceName"/> 一起交给资源系统（RevResManager 的参数就是这两段）。
        /// </summary>
        string ResourceRoot { get; }

        /// <summary>数据文件名（不带扩展名），默认等于表名，如 "HeroSkin"。</summary>
        string ResourceName { get; }

        /// <summary>当前已装载的行数</summary>
        int Count { get; }

        /// <summary>是否已装载数据（装载失败或已卸载为 false）</summary>
        bool IsLoaded { get; }

        /// <summary>
        /// 用一段数据文本装载本表（由 RevDataTableManager 拿到 TextAsset 后调用）。
        /// 返回成功解析的行数；errorCount 是解析失败/主键重复的行数。
        /// </summary>
        int LoadText(string text, out int errorCount);

        /// <summary>清空已装载的数据（容器实例保留，可再次装载）</summary>
        void Clear();
    }
}
