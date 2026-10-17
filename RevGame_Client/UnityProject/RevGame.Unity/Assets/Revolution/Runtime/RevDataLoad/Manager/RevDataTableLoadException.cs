// ============================================================
// RevDataTableLoadException.cs —— 数据表加载失败
//
// 位置：Runtime\RevDataLoad\Manager\
//
// 【为什么要有专门的异常类型？】
//   加载失败的原因在框架里是枚举 RevResLoadErrorReason（不打日志、不抛异常是资源系统的约定），
//   但"数据表读不到"对业务是致命的：必须让调用方没法忽略它。
//   await 一个 RevTask 时异常会自然沿调用栈抛出 —— 这正是我们想要的"响亮地失败"。
//
// 【为什么不直接抛 RevResLoadErrorReason？】
//   枚举没有异常语义，也带不上表名。出错时"哪张表 + 什么原因"必须一起出现在堆栈里，
//   否则从日志反查是哪张表要花掉大量时间。
// ============================================================
using System;

namespace Revolution
{
    /// <summary>数据表加载失败（表名 / 资源位置 / 失败原因一并带出）</summary>
    public class RevDataTableLoadException : Exception
    {
        public RevDataTableLoadException(string tableName, string rootPath, string resName, RevResLoadErrorReason reason)
            : base($"[RevDataTable] 加载失败：{tableName}（{RevResPathUtil.Join(rootPath, resName)}），原因：{reason}")
        {
            TableName = tableName;
            RootPath = rootPath;
            ResName = resName;
            Reason = reason;
        }

        /// <summary>表名（如 HeroSkin）</summary>
        public string TableName { get; }

        /// <summary>数据文件所在根目录（如 "Data/"）</summary>
        public string RootPath { get; }

        /// <summary>数据文件名（如 "HeroSkin"）</summary>
        public string ResName { get; }

        /// <summary>失败原因（资源系统给出的细分原因）</summary>
        public RevResLoadErrorReason Reason { get; }

        /// <summary>拼好的完整逻辑路径（如 "Data/HeroSkin"，打日志用）</summary>
        public string ResourcePath => RevResPathUtil.Join(RootPath, ResName);
    }
}
