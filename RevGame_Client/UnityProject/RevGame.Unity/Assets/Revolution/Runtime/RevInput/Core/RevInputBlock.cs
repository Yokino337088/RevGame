// ============================================================
// RevInputBlock.cs —— "屏蔽输入"的句柄（纯 C#，不引用 UnityEngine）
//
// 位置：Runtime\RevInput\Core\
//
// 【要解决的问题】
//   弹窗期间可用 World 屏蔽本输入框架的所有动作和手势（包括 ESC / 返回键；当前没有“系统按键例外”）；
//   教程遮罩可以只屏蔽被遮住的那根手指，让另一根手指仍能拖动镜头。
//   也就是说，可以按“世界动作 / 某根手指 / 全部输入”选择范围，不必为了挡一个来源而关闭整个输入系统。
//
// 【三条铁律】
//   ① **可精确到指针**：<see cref="RevInputBlockKind.World"/> 挡世界、<see cref="RevInputBlockKind.Pointer"/> 挡单指、
//      <see cref="RevInputBlockKind.All"/> 全挡（指针位置也读不到）。
//   ② **句柄可失效**：<see cref="IsEmpty"/> 为真时所有操作都是安全空操作（重复关闭不会出错）。
//   ③ **带 owner**：`RevInput.UnblockAllOf(owner)` 一行清掉某个对象申请的全部屏蔽（
//      和框架里 计时器 / 音效 / 公共 Mono 的作用域写法一致）。
// ============================================================

namespace Revolution
{
    /// <summary>
    /// 一次屏蔽申请的句柄。<b>不要自己 new</b>：用 <c>RevInput.Block(...)</c> 拿，
    /// 或 <c>using (var scope = RevInput.OpenScope()) { scope.Block(...) }</c>。
    /// </summary>
    public readonly struct RevInputBlock
    {
        /// <summary>无效句柄（所有操作都是空操作）。</summary>
        public static readonly RevInputBlock Empty = new RevInputBlock(0);

        internal readonly int Id;

        internal RevInputBlock(int id)
        {
            Id = id;
        }

        /// <summary>是否是无效句柄。</summary>
        public bool IsEmpty => Id <= 0;

        /// <summary>屏蔽种类（无效句柄返回 <see cref="RevInputBlockKind.None"/>）。</summary>
        public RevInputBlockKind Kind => Id <= 0 ? RevInputBlockKind.None : RevInput.GetBlockKind(Id);

        /// <summary>解除这次屏蔽（重复调用安全）。</summary>
        public bool Close() => Id > 0 && RevInput.Unblock(this);

        public override string ToString() => IsEmpty ? "RevInputBlock(Empty)" : "RevInputBlock#" + Id + "(" + Kind + ")";
    }
}
