// ============================================================
// RevLogicBase.cs —— 业务单元（RevLogic）的基类："没有界面，但要活一段时间，活完要干净结束"的业务
//
// 位置：Runtime\RevScope\Core\
//
// 【为什么需要它】框架里原来只有两种"能装业务的东西"：
//   · UI 面板（RevUIPanel）  —— 有界面，跟着界面生灭；
//   · 服务（RevServices）    —— 全局能力，一注册就活到被注销。
//   中间缺一层：**没有界面、只活一局 / 一屏 / 一段玩法**的业务 —— 战斗结算统计、新手引导推进、
//   成就检查、限时活动判定、局内数据上报。这些以前只能塞进某个面板（于是跟着面板生灭，很别扭）
//   或塞进服务（于是一辈子不死）。RevLogic 就是给它们的家。
//
// 【最短写法】
//     [RevLogic(RevDomain.Battle)]                       // 属于战斗域：战斗域一关，它自动 Stop
//     public sealed class DamageBoardLogic : RevLogicBase
//     {
//         private int _total;
//
//         protected override void OnStart()              // ① 开始：放心订事件 / 起计时器 / 取服务
//         {
//             Listen<DamageArgs>(GameEventId.DamageDone, OnDamage);   // 基类帮你带好了 owner: this
//             Every(0.5f, Flush);                                      // 同上
//         }
//
//         protected override void OnTick(float dt) { }   // ② 可选：不重写 = 不参与每帧，零开销
//
//         protected override void OnStop()               // ③ 结束：★ 通常一行都不用写（见下）
//         {
//             RevLog.Info("本局总伤害 " + _total, "Battle");
//         }
//     }
//
//     RevLogic.Start<DamageBoardLogic>();                // 启动
//     RevLogic.Stop<DamageBoardLogic>();                 // 手动停（也可以不停：域关闭时自动停）
//
// 【★ 最核心的一条：Stop 时自动解绑】
//   Stop 的内部固定动作（顺序很重要）：
//     ① 摘掉这个 Logic（owner: this）注册的全部事件监听
//     ② 停掉它的全部计时器
//     ③ 摘掉它的每帧回调、注销它注册的服务
//     ④ 最后才调用你的 OnStop()
//   所以你在 OnStart 里注册的东西，**只要带了 owner: this（或用基类的 Every / After / Listen）**，
//   一行清理代码都不用写 —— "忘记清理"在结构上不可能发生。
//   为什么"先解绑、后 OnStop"：OnStop 是"我确定要做的最后一件事"，它不该再被自己之前注册的回调打扰；
//   但此时资源 / 服务都还在，OnStop 里仍然可以正常使用它们。
//
// 【什么东西不该做成 Logic】
//   · 有界面、要显示的东西          → RevUIPanel（UI 系统有池化 / 层级 / 数据绑定）
//   · 全局能力（网络 / 配置 / 音效） → RevServices 注册的服务
//   · 纯工具函数                    → 静态类
//   · 需要跨局存活的进度数据         → 存档 / 配置服务（域关闭会回收 Logic，数据也跟着没了）
//
// 【注意】Logic 之间不要互相持有引用、也不要互相 Get 对方的字段 —— 想通信用事件。
// ============================================================
using System;

namespace Revolution
{
    /// <summary>业务单元基类。重写 OnStart / OnTick / OnStop；启动停止走 <see cref="RevLogic"/>。</summary>
    public abstract class RevLogicBase
    {
        /// <summary>它属于哪个域（域名）。没有任何域时为 null。</summary>
        public string Domain { get; internal set; }

        /// <summary>Start 时传进来的参数（没传为 null）。</summary>
        public object Param { get; internal set; }

        /// <summary>是否正在运行（Stop 之后为 false）。</summary>
        public bool IsRunning { get; internal set; }

        /// <summary>它挂在哪个域节点上（没有域为 null）。框架内部用。</summary>
        internal RevScopeHandle ScopeNode;

        // ==================== 生命周期（子类重写）====================

        /// <summary>开始。在这里订事件、起计时器、取服务、加载资源。</summary>
        protected virtual void OnStart()
        {
        }

        /// <summary>
        /// 带参数的开始。默认转给无参的 <see cref="OnStart()"/>；需要用 Start 时传的参数就重写这个
        /// （也可以不重写，直接读 <see cref="Param"/>）。
        /// </summary>
        protected virtual void OnStart(object param)
        {
            OnStart();
        }

        /// <summary>
        /// 每帧（可选）。★ 不重写它 = 这个 Logic 完全不参与每帧驱动，不付任何每帧成本。
        /// 它所在的域被暂停（<c>RevScope.SetTickEnabled(域, false)</c>）时，这里也会跟着停。
        /// </summary>
        protected virtual void OnTick(float deltaTime)
        {
        }

        /// <summary>
        /// 结束。★ 调用它之前，框架已经把你注册的事件 / 计时器 / 每帧回调 / 服务全部解绑了，
        /// 所以这里只写"业务上的收尾"（比如把统计结果发出去），不要再写清理代码。
        /// </summary>
        protected virtual void OnStop()
        {
        }

        // ==================== 受控注册入口（自动带 owner: this）====================
        // 新手最容易漏的就是 owner。用这几个方法，漏不掉。

        /// <summary>N 秒后做一次（计时器自动归本 Logic，Stop 时自动停）。</summary>
        protected RevTimerHandle After(float seconds, Action callback, RevTimeDomain domain = RevTimeDomain.Scaled)
            => RevTimer.After(seconds, callback, domain, this);

        /// <summary>每隔 interval 秒一次（times = -1 无限；计时器自动归本 Logic，Stop 时自动停）。</summary>
        protected RevTimerHandle Every(float interval, Action callback, int times = -1, RevTimeDomain domain = RevTimeDomain.Scaled)
            => RevTimer.Every(interval, callback, times, domain, this);

        /// <summary>监听事件（0 参数；监听自动归本 Logic，Stop 时自动摘）。</summary>
        protected void Listen(string eventName, Action handler)
            => RevEvent.AddEventListener(eventName, handler, 0, this);

        /// <summary>监听事件（1 参数，推荐传事件参数结构体；监听自动归本 Logic，Stop 时自动摘）。</summary>
        protected void Listen<T1>(string eventName, Action<T1> handler)
            => RevEvent.AddEventListener(eventName, handler, 0, this);

        // ==================== 框架内部入口 ====================
        // 子类的 OnStart / OnTick / OnStop 是 protected 的，框架（同程序集）通过这几个 internal 方法转调。

        internal void InvokeStart(object param) => OnStart(param);

        internal void InvokeTick(float deltaTime) => OnTick(deltaTime);

        internal void InvokeStop() => OnStop();
    }
}
