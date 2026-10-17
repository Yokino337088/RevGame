// RevGMCommand.cs —— 一条命令的完整记录（框架内部：注册表与面板都读它）
//
// 【名字即层级】完整名用 `/` 分段：`经济/货币/加金币` → 组 = "经济/货币"，基础名 = "加金币"。
//   这是王者那套最省事的一条设计：不用额外配置分类，人也能一眼看懂归属；面板据此自动长出分组树。

using System;
using System.Text;

namespace Revolution
{
    /// <summary>一条已注册的 GM 命令。</summary>
    public sealed class RevGMCommand
    {
        /// <summary>完整名（含分组，例如 <c>经济/货币/加金币</c>）</summary>
        public string Name { get; }

        /// <summary>分组路径（例如 <c>经济/货币</c>；没有写 `/` 时为空串）</summary>
        public string Group { get; }

        /// <summary>末段名（例如 <c>加金币</c>）—— 只写末段也能执行（同名时框架会提醒你写完整名）</summary>
        public string BaseName { get; }

        /// <summary>说明（显示在面板上；写清楚"干什么、要注意什么"）</summary>
        public string Description { get; }

        /// <summary>参数说明（可以为空数组：表示"没有参数说明"，框架就不做执行前校验）</summary>
        public RevGMArg[] Args { get; }

        /// <summary>标记（高危 / 隐藏……）</summary>
        public RevGMFlags Flags { get; }

        /// <summary>高危命令（面板执行前会二次确认）</summary>
        public bool IsHighRisk => (Flags & RevGMFlags.HighRisk) != 0;

        /// <summary>隐藏命令（不进联想列表，但直接写完整名仍可执行）</summary>
        public bool IsHidden => (Flags & RevGMFlags.Hidden) != 0;

        /// <summary>命令体（统一收成"给参数、回一句给用户看的话"）</summary>
        internal Func<RevGMArgs, string> Handler { get; }

        internal RevGMCommand(string name, string description, RevGMArg[] args, RevGMFlags flags, Func<RevGMArgs, string> handler)
        {
            Name = name;

            int split = name.LastIndexOf('/');
            Group = split > 0 ? name.Substring(0, split) : string.Empty;
            BaseName = split >= 0 ? name.Substring(split + 1) : name;

            Description = description ?? string.Empty;
            Args = args ?? Array.Empty<RevGMArg>();
            Flags = flags;
            Handler = handler;
        }

        /// <summary>
        /// 帮助文本：<c>经济/货币/加金币 &lt;数量|整数(默认 1000)&gt;</c>
        /// （王者的 `2:256-285` 就是这个套路：参数说明自动拼成帮助）
        /// </summary>
        public string Signature()
        {
            if (Args.Length == 0) return Name;

            StringBuilder builder = new StringBuilder(Name);
            builder.Append(" <");
            for (int i = 0; i < Args.Length; i++)
            {
                if (i > 0) builder.Append(' ');
                builder.Append(Args[i].Describe());
            }

            builder.Append('>');
            return builder.ToString();
        }

        /// <summary>调试显示</summary>
        public override string ToString() => Signature();
    }
}
