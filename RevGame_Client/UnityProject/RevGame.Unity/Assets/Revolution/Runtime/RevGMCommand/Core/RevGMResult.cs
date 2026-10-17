// RevGMResult.cs —— 一次执行的返回值（面板拿它显示结果）
//
// 【为什么要有它（而不是只返回 string）】王者那套只有 "done"/"fail" 两个字符串约定，
//   于是"为什么失败、花了多久、是不是命令本身就没找到"全都看不出来（真机调试时只能猜）。
//   这里把三件事分开：成功与否、给人看的消息、耗时。

namespace Revolution
{
    /// <summary>一次 GM 命令执行的结果。</summary>
    public readonly struct RevGMResult
    {
        /// <summary>是否成功执行（命令没找到、参数不对、业务抛异常都算失败）</summary>
        public readonly bool Success;

        /// <summary>给人看的消息：成功时是命令的返回值（或 <see cref="RevGM.Done"/>）；失败时是可执行的原因</summary>
        public readonly string Message;

        /// <summary>这次执行花了多少毫秒（面板会显示；排查"这条 GM 卡住了"很有用）</summary>
        public readonly double ElapsedMs;

        internal RevGMResult(bool success, string message, double elapsedMs)
        {
            Success = success;
            Message = message;
            ElapsedMs = elapsedMs;
        }

        /// <summary>成功</summary>
        public static RevGMResult Ok(string message, double elapsedMs = 0d)
            => new RevGMResult(true, string.IsNullOrEmpty(message) ? RevGM.Done : message, elapsedMs);

        /// <summary>失败（参数不对 / 业务拒绝 / 环境不满足……）</summary>
        public static RevGMResult Fail(string message, double elapsedMs = 0d)
            // ★ 与 Ok（为空时兜底成 done）对齐：消息为空时面板会显示一个空的红框，
            //   使用者看不到任何原因 —— 等于把"永不静默失败"这条目标漏掉。
            => new RevGMResult(false, string.IsNullOrEmpty(message) ? "执行失败（没有给出原因）" : message, elapsedMs);

        /// <summary>调试显示：例如 <c>[OK] done (1.2ms)</c></summary>
        public override string ToString()
            => $"[{(Success ? "OK" : "FAIL")}] {Message} ({ElapsedMs:F2}ms)";
    }
}
