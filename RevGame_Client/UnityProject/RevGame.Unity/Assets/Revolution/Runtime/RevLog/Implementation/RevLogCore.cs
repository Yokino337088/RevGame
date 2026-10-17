// ============================================================
// RevLogCore.cs —— 日志内核（纯 C#，不引用 UnityEngine）
//
// 位置：Runtime\RevLog\Implementation\
//
// 【一条日志在这里走完四步】
//   ① 过滤：级别阈值 + tag 静音表（两者都是运行期可改的"开关"）
//   ② 重复抑制：和上一条**完全相同**（级别+tag+消息）的连续日志只累计，
//      等出现不同的日志时补一条"重复 N 次" —— 刷屏的根治办法（王者原版承认这块是空白）
//   ③ 留底：写进定长环形缓冲（内存有硬顶，随时可 Dump 现场）
//   ④ 派发：逐个 Sink 输出；**单个 Sink 抛异常绝不影响其他 Sink，也绝不传染给调用方**
//
// 【★ 观测系统的故障不许传导】所有 Sink 调用都包在 try/catch 里；
//   失败会累计到 SinkErrors 并在**第一次**失败时喊一声（不静默、也不刷屏）——
//   王者原版"目录没权限就静默降级、日志系统失能无人知晓"是个真实的坑，这里补上。
//
// 【为什么按"级别契约"采堆栈】堆栈是微秒到毫秒级的东西；Error 及以上才采（见 RevLogDefines）。
// ============================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Revolution
{
    /// <summary>日志内核（纯 C#；门面 <see cref="RevLog"/> 持有一个默认实例）。</summary>
    internal sealed class RevLogCore
    {
        private readonly RevLogRing _ring = new RevLogRing(RevLogLimits.RingCapacity);
        private readonly List<IRevLogSink> _sinks = new List<IRevLogSink>(4);
        private readonly HashSet<string> _mutedTags = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>上一条已派发的日志（用于连续重复抑制；全是值/引用比较，不产生分配）。</summary>
        private RevLogEntry _last;
        private int _lastRepeat;

        internal RevLogLevel MinLevel = RevLogLevel.Info;

        /// <summary>堆栈采集阈值（默认 Error：便宜的信息不付堆栈的钱）。</summary>
        internal RevLogLevel StackLevel = RevLogLevel.Error;

        /// <summary>当前帧号来源（Support 层装 Unity 的实现；没有 → -1，绝不拿 0 冒充）。</summary>
        internal Func<int> FrameProvider;

        /// <summary>没有 Sink 时的兜底输出（默认标准错误；Unity 下由钩子接管到 Debug）。</summary>
        internal Action<string> Fallback = message => Console.Error.WriteLine(message);

        /// <summary>异常上报出口（默认不接 = 不上报；王者把 CrashSight 硬编码在门面里，这里拆成可插拔）。</summary>
        internal Action<Exception, string> OnReport;
        private bool _reporting;

        /// <summary>Sink 失败次数（失能可见：0 = 一切正常）。</summary>
        internal int SinkErrors { get; private set; }

        internal int SinkCount => _sinks.Count;

        // ==================== 开关 ====================

        internal bool IsEnabled(RevLogLevel level, string tag)
        {
            if (level < MinLevel) return false;
            return tag == null || !_mutedTags.Contains(tag);
        }

        internal void MuteTag(string tag, bool muted)
        {
            if (string.IsNullOrEmpty(tag)) return;

            if (muted) _mutedTags.Add(tag);
            else _mutedTags.Remove(tag);
        }

        internal bool IsTagMuted(string tag) => tag != null && _mutedTags.Contains(tag);

        // ==================== 写一条 ====================

        internal void Log(RevLogLevel level, string tag, string message, Exception error)
        {
            tag = string.IsNullOrEmpty(tag) ? RevLogLimits.DefaultTag : tag;
            message ??= "";

            if (!IsEnabled(level, tag)) return;

            // ① 连续重复：只累计，不派发（等出现不同的日志时补一条汇总）
            if (_lastRepeat > 0 && level == _last.Level && tag == _last.Tag && message == _last.Message)
            {
                _lastRepeat++;
                return;
            }

            // ② 上一条有重复 → 先把它补出来（"重复 N 次"）
            if (_lastRepeat > 1) Emit(WithRepeat(_last, _lastRepeat));

            _lastRepeat = 1;
            _last = new RevLogEntry(level, tag, message, CaptureStack(level), DateTime.Now, CurrentFrame(), 1, error);

            Emit(_last);

            // ③ 异常上报（可插拔；默认没人接 = 不上报）
            if (error != null) { ReportException(error, $"[{tag}] {message}"); }
        }

        private void ReportException(Exception error, string context)
        {
            // 上报 SDK 自己可能写日志或抛异常；不能递归上报，更不能击穿调用方的 catch。
            Action<Exception, string> reporters = OnReport;
            if (_reporting || reporters == null) { return; }
            _reporting = true;
            try
            {
                foreach (Action<Exception, string> reporter in reporters.GetInvocationList())
                {
                    try { reporter(error, context); }
                    catch (Exception e) { SafeFallback($"[RevLog] 异常上报失败：{e}"); }
                }
            }
            finally { _reporting = false; }
        }

        private void Emit(in RevLogEntry entry)
        {
            _ring.Write(entry);

            if (_sinks.Count == 0)
            {
                SafeFallback(entry.ToString());                  // 纯 C# 环境：绝不静默
                return;
            }

            for (int i = 0; i < _sinks.Count; i++)
            {
                IRevLogSink sink = _sinks[i];
                if (sink == null) continue;

                try
                {
                    sink.Write(entry);
                }
                catch (Exception e)
                {
                    SinkErrors++;

                    // 只在第一次失败时喊一声：不静默，也不刷屏
                    if (SinkErrors == 1)
                    {
                        SafeFallback($"[RevLog][错误] 输出通道 \"{sink.Name}\" 抛异常，已隔离（日志不会因此丢失在别处）：{e.Message}");
                    }
                }
            }
        }

        private static RevLogEntry WithRepeat(in RevLogEntry entry, int repeat)
            => new RevLogEntry(entry.Level, entry.Tag, entry.Message, entry.Stack, entry.Time, entry.Frame, repeat, entry.Error);

        private string CaptureStack(RevLogLevel level)
        {
            if (level < StackLevel) return null;

            // 跳过前两帧（CaptureStack + Log），让第一行就是业务调用点
            return new StackTrace(2, true).ToString();
        }

        private int CurrentFrame() => FrameProvider != null ? FrameProvider() : -1;

        private void SafeFallback(string message)
        {
            try
            {
                Fallback?.Invoke(message);
            }
            catch
            {
                // 兜底出口自己都炸了 —— 那就真的没办法了，也绝不能传染给调用方
            }
        }

        // ==================== Sink 管理 ====================

        internal void AddSink(IRevLogSink sink)
        {
            if (sink == null || _sinks.Contains(sink)) return;

            _sinks.Add(sink);
        }

        internal bool RemoveSink(IRevLogSink sink)
        {
            if (sink == null) return false;

            bool removed = _sinks.Remove(sink);
            if (removed) SafeDispose(sink);
            return removed;
        }

        internal void Flush()
        {
            // 先把待补的"重复 N 次"补出来，再 flush 各通道
            FlushRepeat();

            for (int i = 0; i < _sinks.Count; i++)
            {
                try
                {
                    _sinks[i]?.Flush();
                }
                catch (Exception e)
                {
                    SinkErrors++;
                    SafeFallback($"[RevLog][错误] 输出通道 \"{_sinks[i]?.Name}\" Flush 失败：{e.Message}");
                }
            }
        }

        internal void Clear()
        {
            FlushRepeat();
            _ring.Clear();
        }

        /// <summary>进 Play / 换账号时的复位：丢掉历史、关掉所有 Sink（句柄与后台线程一起收干净）。</summary>
        internal void ResetForNewSession()
        {
            _last = default;
            _lastRepeat = 0;
            _ring.Clear();
            DisposeSinks();
            SinkErrors = 0;
            OnReport = null;
        }

        internal RevLogEntry[] Recent(int count) => _ring.Snapshot(count);

        internal string Dump(int count) => _ring.Dump(count);

        internal int RingCount => _ring.Count;

        /// <summary>当前通道的快照（门面用它按名字判断"是否已经装过"）。</summary>
        internal IRevLogSink[] SnapshotSinks() => _sinks.ToArray();

        /// <summary>Sink 自己出错时调它：累计 + 报到兜底出口（只喊第一次，不刷屏）。</summary>
        internal void ReportSinkError(string message)
        {
            SinkErrors++;

            if (SinkErrors == 1) SafeFallback("[RevLog][错误] " + message);
        }

        // ==================== 内部小工具 ====================

        private void FlushRepeat()
        {
            if (_lastRepeat <= 1) return;

            Emit(WithRepeat(_last, _lastRepeat));
            _lastRepeat = 1;
        }

        private void DisposeSinks()
        {
            for (int i = 0; i < _sinks.Count; i++) SafeDispose(_sinks[i]);

            _sinks.Clear();
        }

        private void SafeDispose(IRevLogSink sink)
        {
            if (sink == null) return;

            try
            {
                sink.Dispose();
            }
            catch (Exception e)
            {
                SinkErrors++;
                SafeFallback($"[RevLog][错误] 输出通道 \"{sink.Name}\" 收尾失败（句柄可能没关干净）：{e.Message}");
            }
        }
    }
}
