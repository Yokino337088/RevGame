// ============================================================
// RevUIUnityHooks.cs —— Unity 生命周期接入
//
// 位置：Runtime\RevUISystem\Support\
//
// 【为什么需要它】
//   Unity 编辑器里有一个"关闭域重载（Enter Play Mode Options）"的开关。关掉它之后，
//   静态字段会**跨 Play 存活**：上一次运行打开过的面板实例早就随场景销毁了，
//   但管理器里还留着那些已经变成"假 null"的引用 —— 表现就是"第二次进 Play，界面开不出来"。
//   这里在进入 Play 之前（SubsystemRegistration）主动把索引清空，让每一轮运行都是干净的。
//
//   ★ 真机上这段同样会执行：进程启动时清一次（本来就是空的，代价为零）。
//
// 【为什么只清索引，不清元数据缓存】
//   RevUIPanelMeta / RevUIBindPlan / RevUIPartMeta 缓存的是**类型信息**（Type、FieldInfo、路径字符串），
//   它们不引用任何场景对象，跨运行依然有效 —— 清掉只会白白多一次反射。
// ============================================================
using UnityEngine;

namespace Revolution
{
    /// <summary>UI 系统的 Unity 生命周期接入（进 Play 前清理索引）</summary>
    internal static class RevUIUnityHooks
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void OnSubsystemRegistration()
        {
            // 控件事件特性（[RevButtonClick] / [RevToggleChanged] / …）的问题从统一日志出口出：
            // 签名不支持 / 控件名写空都能在第一次装配时就看见（而不是"点了没反应"）
            RevUIWidgetEvents.OnException = (e, what) => RevUILog.Error($"{what}：{e.Message}");

            // 让管理器回到"没打开任何界面"的初始状态
            RevUIManager.Instance.ResetForNewSession();
            RevUILog.Info("UI 系统已重置（新一轮运行开始）");
        }
    }
}
