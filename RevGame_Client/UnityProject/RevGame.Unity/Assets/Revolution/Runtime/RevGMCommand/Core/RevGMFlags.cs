// RevGMFlags.cs —— 命令标记（面板据此决定怎么展示、要不要拦一道）
// 【HighRisk】高危：面板执行前会弹二次确认（王者没有风险等级，只能把注意事项写进命令名里，这是补上的短板）
// 【Hidden】  不进联想列表（给自己留的"废弃 / 临时"命令用），但直接输入完整名仍可执行

using System;

namespace Revolution
{
    /// <summary>GM 命令的标记。</summary>
    [Flags]
    public enum RevGMFlags
    {
        /// <summary>普通命令</summary>
        None = 0,

        /// <summary>高危命令：面板执行前会二次确认（例如"清空所有存档""模拟扣款"）</summary>
        HighRisk = 1,

        /// <summary>不在联想列表里出现（直接输入完整名仍可执行）</summary>
        Hidden = 2,
    }
}
