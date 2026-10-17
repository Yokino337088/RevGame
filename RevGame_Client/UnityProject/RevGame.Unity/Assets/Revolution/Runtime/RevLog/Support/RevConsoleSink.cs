// ============================================================
// RevConsoleSink.cs —— Unity 控制台通道（本模块唯一"必须碰引擎"的通道）
//
// 位置：Runtime\RevLog\Support\
//
// 【为什么它单独一个文件】内核（Core\ / Implementation\）是纯 C#，能脱离 Unity 跑断言。
//   把"往 UnityEngine.Debug 打"这件事收在这一个薄文件里，边界就永远是清楚的。
//
// 【级别 → 控制台方法】Error/Exception 走红字（Error 带堆栈文本；Exception 额外给
//   Debug.LogException —— 那是可点击跳转的原始堆栈，排查时差别很大）。
//   ★ 例外：Exception 的**消息文本**也要打一次，否则消息全被堆栈淹掉。
//
// 【零配置】由 Support\RevLogUnityHooks 在启动阶段自动装上（业务不用管）。
// ============================================================
using UnityEngine;

namespace Revolution
{
    /// <summary>把日志打到 Unity 控制台（按级别选 Debug.Log / LogWarning / LogError / LogException）。</summary>
    internal sealed class RevConsoleSink : IRevLogSink
    {
        public string Name => "Console";

        public void Write(in RevLogEntry entry)
        {
            string line = entry.ToString();

            switch (entry.Level)
            {
                case RevLogLevel.Error:
                    Debug.LogError(line);
                    break;

                case RevLogLevel.Exception:
                    Debug.LogError(line);                     // 消息先打（不然被堆栈淹掉）
                    if (entry.Error != null) Debug.LogException(entry.Error);
                    break;

                case RevLogLevel.Warn:
                    Debug.LogWarning(line);
                    break;

                default:
                    Debug.Log(line);
                    break;
            }
        }

        public void Flush()
        {
            // 控制台是即时的，没有缓冲要落
        }

        public void Dispose()
        {
        }
    }
}
