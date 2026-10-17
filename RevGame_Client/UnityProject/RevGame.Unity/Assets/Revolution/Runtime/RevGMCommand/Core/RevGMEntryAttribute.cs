// RevGMEntryAttribute.cs —— 可选：标记"注册入口方法"，让 GM 面板**不进入 Play 也能看到命令清单**
//
// 【为什么需要它】运行期的命令来自 `RevGM.Register(...)` 这些调用 —— 只有代码真的跑起来才有清单。
//   但人往往是"先打开面板查有哪些命令，再决定要不要跑游戏"。标记一个注册入口，面板就能在编辑期
//   （用 Unity 的 TypeCache 找方法，零反射扫描成本）**调用它一遍**，从而拿到命令清单做联想。
//
// 【唯一的纪律】入口方法里**只做注册**（只调 RevGM.Register，不要初始化业务、不要碰运行时对象）——
//   因为面板在编辑期也会调用它。这也是"组合根"该有的样子。
//
// 【用法】
//   public static class MyGameCommands
//   {
//       [RevGMEntry("战斗 / 经济 / 背包 相关")]
//       public static void Register()
//       {
//           RevGM.Register("经济/加金币", "给当前玩家加金币", args => AddGold(args.Int(0)), RevGMArg.Int("数量", 1000));
//       }
//   }

using System;

namespace Revolution
{
    /// <summary>标记"GM 命令注册入口"方法（面板编辑期靠它发现命令）。</summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    public sealed class RevGMEntryAttribute : Attribute
    {
        /// <summary>这个入口里都是些什么样的命令（显示在面板上，便于分堆看）</summary>
        public string Description { get; }

        /// <summary>标记一个注册入口方法</summary>
        /// <param name="description">这批命令的说明（可空）</param>
        public RevGMEntryAttribute(string description = null) => Description = description ?? string.Empty;

        /// <summary>调试显示</summary>
        public override string ToString() => string.IsNullOrEmpty(Description) ? "RevGMEntry" : "RevGMEntry(" + Description + ")";
    }
}
