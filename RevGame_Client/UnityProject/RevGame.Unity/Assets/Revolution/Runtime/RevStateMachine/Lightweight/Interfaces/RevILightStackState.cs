// ============================================================
// RevILightStackState.cs —— 轻量级栈状态接口
//
// 位置：Runtime\RevStateMachine\Lightweight\Interfaces\
//
// 【设计来源：王者四套状态机的"灵魂"—— 栈语义】
//   王者《01-四套状态机全景》核心结论第 1 条：状态不是简单"切换"，而是"压栈 / 弹栈"。
//   OnSuspend（被盖住）与 OnResume（重新露出）这两个回调，
//   让"UI 弹窗返回""输入模式切换"这类场景有了精确的表达力：
//
//     大厅输入 →（进战斗 Push 摇杆）→ 大厅输入收到 OnSuspend：禁用大厅点击，但不销毁
//     战斗结束 →（Pop）→ 大厅输入收到 OnResume：恢复点击
//
//   如果用普通状态机（只有 Enter/Exit），"进战斗"就得销毁大厅输入、回来再重建 —— 浪费且易错。
//
// 【命名对照】
//   王者①栈式版叫 OnStateOverride / OnStateResume；
//   王者③泛型栈版叫 OnSuspend / OnResume —— 这里取更通用的后者。
// ============================================================
namespace Revolution
{
    /// <summary>
    /// 轻量级栈状态接口：在 <see cref="RevILightState"/> 之上，增加"被压住 / 重新露出"的栈语义。
    /// <para>被 <see cref="RevLightStackFSM{T}"/> 驱动的状态实现它。</para>
    /// </summary>
    public interface RevILightStackState : RevILightState
    {
        /// <summary>
        /// 被新压入的状态盖住（自己还在栈里、不销毁）。
        /// 典型用途：禁用输入、暂停表现、UI 置灰 —— 回来还要用，所以只"暂停"不"销毁"。
        /// </summary>
        void OnSuspend();

        /// <summary>
        /// 上层状态弹出，自己重新露出成栈顶。
        /// 典型用途：恢复输入、恢复表现 —— 与 <see cref="OnSuspend"/> 严格配对。
        /// </summary>
        void OnResume();
    }
}
