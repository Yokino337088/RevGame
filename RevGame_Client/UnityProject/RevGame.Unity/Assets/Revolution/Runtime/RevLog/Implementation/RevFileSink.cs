// ============================================================
// RevFileSink.cs —— 异步写文件通道（纯 C#，可脱离 Unity 单测）
//
// 位置：Runtime\RevLog\Implementation\
//
// 【为什么要异步】文件 I/O 是毫秒级的，绝不能出现在游戏帧里。做法是王者的那一套：
//   主线程只入队（一次 List.Add，微秒级）→ 后台线程（Lowest 优先级）消费并写盘。
//
// 【★ 比王者原版多做的三件事】
//   ① 入队的是**条目**不是格式化好的字符串 —— 格式化在后台线程做，
//      所以主线程连 ToString 的分配都不产生（王者是把格式化放在锁里做的）；
//   ② **单文件大小上限**：写满就轮转（王者只有"份数"上限，size 参数在文档里失传）；
//   ③ 目录不可写 / 写法失败 → 不是静默降级，而是**明确报到 RevLog.SinkErrors**（失能可见）。
//
// 【行为约定】
//   · 默认不启用：要不要落盘是产品决策（手机存储、隐私、性能预算都相关）；
//   · Flush() 会等后台排空（最多 1 秒），超时后自己把剩下的写掉 —— 崩溃前不会丢在队列里；
//   · Dispose() 停线程 + 排空 + 关句柄（Unity 下由 RevLogUnityHooks 在退出/进 Play 时调）；
//   · ★ 平台能力：WebGL / 微信小游戏 / 抖音小游戏是单线程的（new Thread 直接抛异常），
//       且那边的"文件系统"是内存虚拟盘 —— 本通道在这些平台上**自动禁用**并明确报到
//       RevLog.SinkErrors（见 SupportsBackgroundWriter），不会假死、也不会静默不写。
// ============================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace Revolution
{
    /// <summary>把日志异步写到文件（定长队列 + 后台线程 + 大小/份数轮转）。</summary>
    public sealed class RevFileSink : IRevLogSink
    {
        /// <summary>
        /// 本平台能不能跑"后台线程 + 真实文件系统"这套写盘机制（默认按平台判定，可手动覆盖）。
        ///
        /// 【为什么必须有这个开关】
        ///   ★ WebGL / 微信小游戏 / 抖音小游戏是**单线程**的：`new Thread(...)` 一调用就抛
        ///     PlatformNotSupportedException。而这类平台偏偏能过掉前置的"目录可写"探测 ——
        ///     persistentDataPath 在那边是内存里的虚拟文件系统（IDBFS），试写**会成功**。
        ///     也就是说光靠探测拦不住，必须在建线程之前就把整条通道关掉，否则是"启动即崩"。
        ///
        ///   ★ 做成本类自己的开关、而不是直接 #if 关掉整段代码，是为了保住"纯 C#、可脱离 Unity 单测"：
        ///     工程外测试可以手动把它设成 false，把"无后台线程"这条分支也测到。
        /// </summary>
        public static bool SupportsBackgroundWriter { get; set; } = DetectBackgroundWriterSupport();

        /// <summary>默认判定：只有 WebGL（含各类小游戏）不支持；其余平台与工程外纯 C# 都支持。</summary>
        private static bool DetectBackgroundWriterSupport()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            return false;          // WebGL / 小游戏：单线程，建线程即崩
#else
            return true;
#endif
        }

        private readonly string _directory;
        private readonly string _prefix;
        private readonly long _sizeLimit;
        private readonly int _keepCount;

        // 双缓冲：主线程只动 _pending（锁内一次 Add），后台只动 _writing
        private readonly object _gate = new object();
        private List<RevLogEntry> _pending = new List<RevLogEntry>(256);
        private List<RevLogEntry> _writing = new List<RevLogEntry>(256);

        // ★ Bug 修复（2026-09-30）新增：写段的互斥锁。
        //   双缓冲只保证"批次不重叠"，但 Flush（等 1 秒超时后主线程自己 Drain 兜底）、
        //   Dispose（Join 超时后主线程接管）都可能让**主线程**与后台线程同时进入写段 ——
        //   StreamWriter / FileStream 非线程安全：并发写会导致输出交错、抛异常丢日志
        //   （还被误计成通道故障）。写段（开文件/写行/刷缓冲/关文件）必须整段串行；
        //   锁内是毫秒级磁盘操作，主线程只在 Flush/Dispose 兜底时才会撞上，可接受。
        private readonly object _ioGate = new object();

        private readonly AutoResetEvent _signal = new AutoResetEvent(false);
        private readonly Thread _thread;
        private volatile bool _running = true;

        private StreamWriter _writer;
        private string _path;
        private long _written;

        public RevFileSink(string directory, string prefix = "revlog", long sizeLimit = RevLogLimits.FileSizeLimit, int keepCount = RevLogLimits.FileKeepCount)
        {
            _directory = directory;
            _prefix = string.IsNullOrEmpty(prefix) ? "revlog" : prefix;
            _sizeLimit = sizeLimit > 0 ? sizeLimit : RevLogLimits.FileSizeLimit;
            _keepCount = keepCount > 0 ? keepCount : RevLogLimits.FileKeepCount;

            // ★ 顺序不能反：先判"平台能力"，再探"目录可写"。
            //   WebGL / 小游戏上目录探测会"成功"（IDBFS 虚拟文件系统），先探就一定会走到
            //   下面的 new Thread 那一步 —— 在单线程平台上那是直接抛异常。
            if (!SupportsBackgroundWriter)
            {
                Enabled = false;
                RevLog.ReportSinkError("当前平台（WebGL / 小游戏）是单线程且没有真实文件系统，文件日志通道已禁用");
                return;
            }

            Enabled = ProbeDirectory();                       // 试写一次：不可写就禁用，绝不让游戏跟着出问题

            if (!Enabled) return;

            _thread = new Thread(Loop) { IsBackground = true, Name = "RevLog", Priority = ThreadPriority.Lowest };
            _thread.Start();
        }

        public string Name => "File";

        /// <summary>目录可用吗（false = 只入队不写盘；原因在构造时就报到 SinkErrors 里了）。</summary>
        public bool Enabled { get; }

        /// <summary>当前文件路径（未启用时为 null）。</summary>
        public string CurrentPath => _path;

        /// <summary>主线程入口：只入队（微秒级、无格式化、无 I/O）。</summary>
        public void Write(in RevLogEntry entry)
        {
            if (!Enabled) return;

            lock (_gate)
            {
                if (!_running) return;

                _pending.Add(entry);                           // struct：不加锁分配、不装箱
            }

            _signal.Set();
        }

        /// <summary>等后台排空（最多 1 秒），超时就把剩下的自己写掉。</summary>
        public void Flush()
        {
            if (!Enabled) return;

            _signal.Set();

            var watch = Stopwatch.StartNew();
            while (PendingCount() > 0 && watch.ElapsedMilliseconds < 1000) Thread.Sleep(1);

            // ★ Bug 修复（2026-09-30）：Drain 的写段（锁内）本来就是"写完即刷"，
            //   原来这里又裸调了一次 _writer?.Flush() —— 无锁、无 try：
            //   与后台线程正在进行的写/关并发时会炸出 ObjectDisposedException /
            //   NullReferenceException（业务直接拿 sink Flush 时没人兜）。删掉这行冗余调用。
            Drain();
        }

        /// <summary>停线程 + 排空 + 关句柄（幂等）。</summary>
        public void Dispose()
        {
            if (!Enabled) return;

            _running = false;
            _signal.Set();

            if (_thread != null && _thread.IsAlive) _thread.Join(1000);

            Drain();
            CloseWriter();
        }

        // ==================== 后台 ====================

        private void Loop()
        {
            while (_running)
            {
                _signal.WaitOne(200);
                Drain();
            }

            Drain();                                           // 退出前把剩下的写完
        }

        /// <summary>换缓冲 + 写盘（锁只保护"换缓冲"，写文件全程无锁）。</summary>
        private void Drain()
        {
            lock (_gate)
            {
                if (_pending.Count == 0) return;

                (_pending, _writing) = (_writing, _pending);     // 交换：主线程继续写新的那半
            }

            try
            {
                // ★ Bug 修复（2026-09-30）：写段整体进 _ioGate —— 主线程 Flush/Dispose 的
                //   兜底 Drain 可能与后台线程的 Drain 并发到这里，没有这把锁两边会同时
                //   操作同一个 StreamWriter（交错/异常丢日志）。批次内容不受影响：
                //   双缓冲已保证两个线程拿到的是不同批次，这里只是把"落盘动作"串行。
                lock (_ioGate)
                {
                    if (_writing.Count > 0) EnsureWriter();

                    for (int i = 0; i < _writing.Count; i++) WriteLine(_writing[i].ToString());

                    _writer?.Flush();                            // 提示级刷新：崩溃时尽量少丢
                }
            }
            catch (Exception e)
            {
                RevLog.ReportSinkError($"写日志文件失败（{_path}）：{e.Message}");
            }
            finally
            {
                _writing.Clear();
            }
        }

        private void WriteLine(string text)
        {
            if (_writer == null) return;

            // ★ 单文件大小上限：写满就轮转（王者原版缺的就是这条）
            if (_written >= _sizeLimit) Rotate();

            _writer.WriteLine(text);

            // ★ Bug 修复（2026-09-30）：必须按 UTF-8 字节数累计 —— 原来用 text.Length（UTF-16
            //   字符数）近似字节数，中文每字要写 3 字节，中文日志为主时实际文件能涨到上限的
            //   ~3 倍才轮转，"单文件上限"的承诺严重失真。写盘线程不在乎这点统计成本。
            _written += System.Text.Encoding.UTF8.GetByteCount(text) + 2;
        }

        private void EnsureWriter()
        {
            if (_writer != null) return;

            // ★ 时间戳精确到毫秒：只到秒的话，同一秒内的多次轮转会重开同一个文件（等于没轮转）
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
            _path = Path.Combine(_directory, $"{_prefix}_{stamp}.log");

            int suffix = 1;
            while (File.Exists(_path)) _path = Path.Combine(_directory, $"{_prefix}_{stamp}_{suffix++}.log");

            _writer = new StreamWriter(new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read), System.Text.Encoding.UTF8);
            _written = _writer.BaseStream.Length;
        }

        private void Rotate()
        {
            CloseWriter();
            CleanupOld();
            EnsureWriter();                                    // 立刻开新文件（旧文件已关，内容已落盘）
        }

        private void CloseWriter()
        {
            if (_writer == null) return;

            // ★ Bug 修复（2026-09-30）：关文件也必须进 _ioGate —— Dispose 超时接管路径上，
            //   主线程关句柄的同时后台线程可能正在写（同 Bug：StreamWriter 非线程安全）
            lock (_ioGate)
            {
                try
                {
                    _writer.Flush();
                    _writer.Dispose();
                }
                catch
                {
                    // 关文件失败没什么可做的（句柄会随进程退出释放），绝不往上报链里再抛
                }
                finally
                {
                    _writer = null;
                }
            }
        }

        /// <summary>只留最近 <c>_keepCount</c> 份（按名字里的时间戳排序，删最旧）。</summary>
        private void CleanupOld()
        {
            try
            {
                var files = new List<string>(Directory.GetFiles(_directory, _prefix + "_*.log"));
                if (files.Count < _keepCount) return;

                files.Sort(StringComparer.Ordinal);
                int remove = files.Count - _keepCount + 1;       // 连同"这一份"一起算，多删一份

                for (int i = 0; i < remove && i < files.Count; i++) File.Delete(files[i]);
            }
            catch
            {
                // 清理失败不影响写日志（磁盘满/权限问题会在写的时候报出来）
            }
        }

        private bool ProbeDirectory()
        {
            try
            {
                if (string.IsNullOrEmpty(_directory)) return false;
                if (!Directory.Exists(_directory)) Directory.CreateDirectory(_directory);

                string probe = Path.Combine(_directory, ".revlog_probe");
                File.WriteAllText(probe, "ok");
                File.Delete(probe);
                return true;
            }
            catch (Exception e)
            {
                // ★ 明确报出来：日志系统失能必须有人知道（王者原版这里是静默的）
                //   走 ReportSinkError → 计入 RevLog.SinkErrors，业务能查到"日志在丢"
                RevLog.ReportSinkError($"日志目录不可写，文件通道已禁用：{_directory}（{e.Message}）");
                return false;
            }
        }

        private static void SafeFallback(string message)
        {
            try { Console.Error.WriteLine(message); }
            catch { /* 连标准错误都写不了就真没办法了 */ }
        }

        private int PendingCount()
        {
            lock (_gate) return _pending.Count;
        }
    }
}
