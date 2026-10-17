// ============================================================
// RevLogicAttribute.cs —— 给业务单元（RevLogic）声明"我属于哪个域 / 只在哪个变体下生效"
//
// 位置：Runtime\RevScope\Core\
//
// 【怎么写】
//     [RevLogic(RevDomain.Battle)]                      // 属于战斗域：战斗域关闭时它会被自动 Stop
//     public sealed class DamageBoardLogic : RevLogicBase { }
//
//     [RevLogic(RevDomain.Battle, Variant = "Season")]  // 只在变体 "Season" 下才允许 Start（A/B 用）
//     public sealed class SeasonBoardLogic : RevLogicBase { }
//
//     public sealed class FooLogic : RevLogicBase { }   // 不写特性：属于"Start 那一刻的当前域"
//
// 【为什么要声明域，而不是调用方传】
//   谁启动这个 Logic 是不确定的（可能是流程脚本、可能是另一个 Logic、可能是 GM 命令），
//   但"它该跟着谁生灭"是 Logic 自己的属性。写在类上，调用方就不可能传错域 ——
//   这是王者 CBusinessLogic 用 BusinessLogicPropertiesAttribute 声明 Scope 的同一个思路。
//
// 【声明了域但域没开会怎样】Start 会明确报错（"它声明属于域「Battle」，但这个域现在没有打开"）并返回 null，
//   而不是悄悄挂到别的域上 —— 挂错了域，要么早早被误关，要么永远不被关。
// ============================================================
using System;

namespace Revolution
{
    /// <summary>声明业务单元的归属域与变体。不写本特性 = 属于启动那一刻的当前域、不限变体。</summary>
    [AttributeUsage(AttributeTargets.Class, Inherited = false)]
    public sealed class RevLogicAttribute : Attribute
    {
        /// <summary>归属的域名（建议用 <see cref="RevDomain"/> 常量）。null = 取启动时的当前域。</summary>
        public string Domain { get; }

        /// <summary>
        /// 变体名（A/B 用）：只有 <c>RevLogic.Variant</c> 与它相同时才允许 Start。null = 任何变体都行。
        /// </summary>
        public string Variant { get; set; }

        public RevLogicAttribute(string domain = null)
        {
            Domain = domain;
        }
    }
}
