// RevGMUsageException.cs —— "使用者用错了"的异常（参数不对 / 环境不满足 / 前置条件没到）
//
// 【谁抛它】① 取参数的 API（RevGMArgs.Int/Float/Bool/Enum）在解析失败时抛；
//           ② 业务命令自己抛（例如"当前不在战斗里，用不了"）。
//
// 【为什么单独一个类型】框架要区分两类失败并且**用不同方式回显**：
//   · RevGMUsageException → 面板上原样显示这句话（这是给"人"看的，不需要堆栈）
//   · 其它异常          → 显示类型 + 消息 + 堆栈首行（这是给"写命令的人"看的 bug）
// 王者那套的痛点是"两种混在一起，界面上连报错都没有"，这里把它们分开。

using System;

namespace Revolution
{
    /// <summary>GM 命令的"用法错误"：参数不对、环境不满足、前置条件没到 —— 消息会原样显示在面板上。</summary>
    public sealed class RevGMUsageException : Exception
    {
        /// <summary>用法错误（消息会被原样显示在 GM 面板上，所以请写成人话）</summary>
        public RevGMUsageException(string message) : base(message) { }
    }
}
