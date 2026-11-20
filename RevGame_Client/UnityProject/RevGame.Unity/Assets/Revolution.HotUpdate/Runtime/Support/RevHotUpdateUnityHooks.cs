// ============================================================
// RevHotUpdateUnityHooks.cs —— Unity 生命周期钩子（关闭 Domain Reload 时的防残留闸门）
//
// 位置：Assets\Revolution.HotUpdate\Runtime\Support\
//
// 【为什么需要】
//   热更包里有一堆静态状态（配置、当前版本、路径快照、下载会话、错误记录……）。
//   在"关闭 Domain Reload 的快速进入 Play"模式下，这些静态字段**不会**被清空 ——
//   上一次 Play 的"当前资源版本 1.2.0.36"会原封不动活到这一次，于是：
//   你在编辑器里删了版本目录做测试，运行时却还在按旧版本解析路径，怎么查都"像是缓存"。
//
// 【做法】与框架其它模块（RevEventSystem / RevLog / RevGMCommand）同一套：
//   进入 Play 的最早阶段（SubsystemRegistration）把内存状态复位一次。
//   ★ 只复位内存 —— 磁盘上的版本目录 / current.txt 是玩家数据，绝对不碰。
// ============================================================
using UnityEngine;

namespace Revolution.HotUpdate
{
    /// <summary>进 Play 时复位热更包的内存状态（磁盘数据不动）。</summary>
    internal static class RevHotUpdateUnityHooks
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetOnEnterPlay()
        {
            RevHotUpdate.ResetForNewSession();
            RevHotStore.ResetForNewSession();
            RevHotResBridge.Reset();
        }
    }
}
