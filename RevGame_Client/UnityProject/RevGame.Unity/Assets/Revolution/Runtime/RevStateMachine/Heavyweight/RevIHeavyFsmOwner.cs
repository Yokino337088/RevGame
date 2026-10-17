// ============================================================
// RevIHeavyFSMOwner.cs —— 重量级状态机的宿主行为接口
//
// 位置：Runtime\RevStateMachine\Heavyweight\
//
// 【设计来源：保留原框架（唐老师框架）的 IFSMObj 泛型设计】
//   原框架的 IFSMObj 是空标记接口，业务按需要定义自己的行为接口继承它：
//     interface IBossObj : IFSMObj { float Health { get; } void MoveTowardsPlayer(); ... }
//   状态类只面向"行为接口"编程，不依赖具体的 MonoBehaviour ——
//   这样状态逻辑可以被普通单元测试构造（传一个假实现），
//   是原框架泛型设计里最值得保留的一点。
//
// 【为什么本身没有成员】
//   每种 AI 的能力完全不同（Boss 有血量和愤怒阈值，小怪有巡逻路径），
//   框架不知道、也不该规定宿主该有什么 —— 行为契约由业务自己的接口定义。
// ============================================================
namespace Revolution
{
    /// <summary>
    /// 重量级状态机的宿主行为接口（标记接口，本身无成员）。
    /// <para>用法：业务定义自己的行为接口继承它，然后以它为 <c>RevHeavyFSM</c> 的第二个泛型参数：</para>
    /// <code>
    /// public interface IBossObj : RevIHeavyFSMOwner
    /// {
    ///     float Health { get; }
    ///     bool  IsPlayerInAttackRange();
    ///     void  MoveTowardsPlayer();
    /// }
    ///
    /// var fsm = new RevHeavyFSM&lt;BossStateType, IBossObj&gt;(boss);
    /// </code>
    /// <para>状态类里通过 <c>Owner</c> 属性访问这些行为（强类型，不用拆箱/转型）。</para>
    /// </summary>
    public interface RevIHeavyFSMOwner
    {
    }
}
