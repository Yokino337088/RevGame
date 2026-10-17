// ============================================================
// RevMonoDefines.cs —— 公共 Mono 模块的定义与上限（纯 C#）
//
// 位置：Runtime\RevPublicMono\Core\
//
// 【这个模块解决什么】
//   "一个**纯 C# 类**（管理器 / 服务 / 工具），想每帧做点事、或者想跑一段协程。"
//   它自己没有 Update、也不能 StartCoroutine —— 本模块给它一个公共宿主：
//     · 每帧回调：Update / LateUpdate / FixedUpdate 三个相位的监听列表；
//     · 协程：把 IEnumerator 交给隐藏宿主去跑。
//
// 【和框架里另外两个"每帧/异步"设施的分工（别选错）】
//   RevTimer    → "多久之后 / 每隔多久 / 到某个时刻"  —— 定时语义
//   RevTask     → "await 一帧 / 等资源加载完"          —— 异步语义
//   本模块       → "给我一个每帧回调" 或 "跑一段协程"     —— 宿主语义
//
// 【★ 相位是什么】就是 MonoBehaviour 的 Update / LateUpdate / FixedUpdate。
//   它们在同一帧里的执行顺序是固定的：FixedUpdate（可能 0~N 次）→ Update → LateUpdate。
//   选错相位的典型症状：跟相机/跟随类逻辑放 Update 会抖（该用 LateUpdate）；物理相关放 Update 会飘（该用 FixedUpdate）。
//
// 【上限】每个相位最多 256 个监听者。到上限拒绝新增并报 Overflow ——
//   监听列表只有"忘记移除"这一种增长方式，明确失败比静默超载更容易查（旧实现连去重都没有：
//   同一个方法加两次 = 每帧跑两次，而你看不出来）。
// ============================================================
using System;

namespace Revolution
{
    /// <summary>三个相位（= MonoBehaviour 的三个帧回调）。</summary>
    public enum RevMonoPhase
    {
        /// <summary>每帧（跟 Update 同频）。最常用。</summary>
        Update = 0,

        /// <summary>每帧最后（跟 LateUpdate 同频）：相机跟随、UI 跟随这类"要在别人之后"的逻辑。</summary>
        LateUpdate = 1,

        /// <summary>物理帧（跟 FixedUpdate 同频，一帧可能 0 次或多次）：物理、帧率无关的推进。</summary>
        FixedUpdate = 2,
    }

    /// <summary>没能正常工作的原因（配 <see cref="RevMono.Failed"/> 事件用）。</summary>
    public enum RevMonoErrorReason
    {
        /// <summary>某个相位的监听者超过上限（见 <see cref="RevMonoLimits.MaxListenersPerPhase"/>）：本次新增被拒绝。</summary>
        Overflow = 0,

        /// <summary>监听者（或协程）抛了异常：已隔离，只影响它自己。</summary>
        CallbackThrew = 1,

        /// <summary>非运行状态（编辑器里没 Play）访问：不会偷偷创建宿主，返回失败。</summary>
        NotPlaying = 2,

        /// <summary>传入了未定义的 Update/LateUpdate/FixedUpdate 相位。</summary>
        InvalidPhase = 3,
    }

    /// <summary>容量上限（集中在这里，便于阅读与调优）。</summary>
    internal static class RevMonoLimits
    {
        /// <summary>每个相位最多多少个监听者。</summary>
        internal const int MaxListenersPerPhase = 256;
    }

    /// <summary>一个监听者（值类型：进列表不装箱）。owner 用于"随对象销毁一行清干净"。</summary>
    internal readonly struct RevMonoListener
    {
        internal readonly Action Action;
        internal readonly bool Infrastructure;
        internal readonly object Owner;

        /// <summary>
        /// ★ 作用域门：只有"调用方没写 owner、由当前域兜底"的监听者才带它（其余为 null）。
        /// 每帧派发时看一眼 <c>Scope.TickOn</c>：域被暂停（RevScope.SetTickEnabled(域, false)）就跳过。
        /// 显式写了 owner 的监听者不带它 —— "显式永远优先，域不插手"。
        /// </summary>
        internal readonly RevScopeHandle Scope;

        internal RevMonoListener(Action action, object owner, RevScopeHandle scope, bool infrastructure = false)
        {
            Action = action;
            Infrastructure = infrastructure;
            Owner = owner;
            Scope = scope;
        }
    }
}
