// RevAutomationScenario.cs —— 通用业务场景驱动：GM 操作 / 条件 / 断言 / 等待 / 清理。
// RevGM.Execute 仅表示同步命令体返回，不能代表跨帧业务已经结束；until 轮询业务查询命令才是完成依据。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Profiling;

namespace Revolution.Editor
{
    [Serializable]
    // ============================================================================
    //  RevAutomationScenario —— "场景脚本"执行引擎（自动化测试的核心）
    //
    //  一个场景 = 一组步骤 + 重复轮数 + 性能门禁，结构是：
    //      setup → [ 每轮：steps → cleanup ] × repeat → teardown
    //  其中 warmup 轮只跑不计分（让引擎预热、JIT、资源缓存稳定后再统计）。
    //
    //  步骤只有 4 种（够用且好懂）：
    //      gm     —— 执行一条 GM 指令（准备数据、触发战斗、查询状态）
    //      wait   —— 等 N 秒 / N 帧
    //      until  —— 反复执行 GM，直到返回的 message 等于期望值（带超时，防死等）
    //      assert —— 一次性断言：GM 返回的 message 必须等于期望值
    //
    //  ⚠️ 两个新手必知：
    //    1. <c>until/assert</c> 是**精确字符串相等**，不是"包含"，期望值要和 GM 返回的一模一样；
    //    2. 配置了帧耗时门禁却一个样本都没采到 → 判**失败**（不是跳过），避免"空跑也绿灯"。
    // ============================================================================

    internal sealed class RevAutomationStep
    {
        public string kind;
        public string command;
        public string expected;
        public int frames;
        public double seconds;
        public double timeoutSeconds;
        public double intervalSeconds = .1;
    }

    [Serializable]
    internal sealed class RevAutomationThresholds
    {
        public double maxFrameP95Ms;
        public double maxFrameMs;
        public long maxMonoUsedBytes;
    }

    [Serializable]
    internal sealed class RevAutomationScenarioSpec
    {
        public string name = "scenario";
        public int repeat = 1;
        public int warmup;
        public bool stopOnFailure = true;
        public bool failOnUnityError = true;
        public RevAutomationStep[] setup = Array.Empty<RevAutomationStep>();
        public RevAutomationStep[] steps = Array.Empty<RevAutomationStep>();
        public RevAutomationStep[] cleanup = Array.Empty<RevAutomationStep>();
        public RevAutomationStep[] teardown = Array.Empty<RevAutomationStep>();
        public RevAutomationThresholds thresholds = new RevAutomationThresholds();
    }

    [Serializable]
    internal sealed class RevAutomationIteration
    {
        public int iteration;
        public bool warmup;
        public bool success;
        public double durationMs;
        public long frames;
        public double frameP95Ms;
        public double frameMaxMs;
        public long monoUsedBytes;
        public long allocatedMemoryBytes;
        public int gc0;
        public int gc1;
        public int gc2;
        public string message;
    }

    [Serializable]
    internal sealed class RevAutomationScenarioResult
    {
        public string name;
        public string resultPath;
        public string startedUtc;
        public string finishedUtc;
        public string unityVersion;
        public string environment = "Unity Editor PlayMode";
        public string unityLogPath;
        public int requested;
        public int completed;
        public int failedIterations;
        public int warmupCompleted;
        public bool success;
        public bool cancelled;
        public double elapsedSeconds;
        public long frameSamples;
        public double frameMeanMs;
        public double frameP95Ms;
        public double frameMaxMs;
        public long peakMonoUsedBytes;
        public long peakAllocatedMemoryBytes;
        public int unityErrors;
        public string[] errors;
    }

    [Serializable]
    internal sealed class RevAutomationProgress
    {
        public string id;
        public string phase;
        public int completed;
        public int requested;
        public int warmupCompleted;
        public int failed;
        public string command;
    }

    // 固定大小的直方图：一万轮、几千万帧也不会无限保存每帧数据。
    internal sealed class RevAutomationFrameStats
    {
        private readonly long[] _bins = new long[2001];
        internal long Count;
        internal double Sum;
        internal double Max;
        internal void Reset()
        {
            Array.Clear(_bins, 0, _bins.Length);
            Count = 0; Sum = 0; Max = 0;
        }
        internal void Sample(double milliseconds)
        {
            if (double.IsNaN(milliseconds) || double.IsInfinity(milliseconds) || milliseconds < 0) { return; }
            Count++;
            Sum += milliseconds;
            Max = Math.Max(Max, milliseconds);
            _bins[Math.Min(2000, (int)Math.Min(2000, Math.Ceiling(milliseconds * 10)))]++;
        }
        internal double P95()
        {
            if (Count == 0) { return 0; }
            long rank = (long)Math.Ceiling(Count * .95);
            long total = 0;
            for (int i = 0; i < _bins.Length; i++)
            {
                total += _bins[i];
                if (total >= rank) { return i == 2000 ? Max : i / 10d; }
            }
            return Max;
        }
    }

    /// <summary>
    /// 场景执行器。生命周期：<c>new</c> → 每帧 <see cref="Tick"/> → 结束 <c>Dispose</c>。
    ///
    /// 它自己**不跑游戏**：只是在每帧决定"下一步该执行哪条 GM"，真正的业务靠项目注册的 GM 指令。
    /// 这样测试脚本与游戏实现解耦 —— 游戏改了，只要 GM 契约不变，脚本就不用改。
    /// </summary>

    internal sealed class RevAutomationScenario : IDisposable
    {
        private readonly RevAutomationRequest _request;
        private readonly RevAutomationScenarioSpec _spec;
        private readonly string _work;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly RevAutomationScenarioResult _result;
        private readonly RevAutomationFrameStats _frames = new RevAutomationFrameStats();
        private readonly List<string> _errors = new List<string>();
        private RevAutomationFrameStats _roundFrames;
        private readonly RevAutomationFrameStats _reusableRoundFrames = new RevAutomationFrameStats();
        private string _phase = "setup";
        private int _step;
        private int _round;
        private int _lastFrame = -1;
        private int _stepFrame;
        private int _gc0, _gc1, _gc2;
        private double _stepStart = -1;
        private double _nextPoll;
        private double _roundStart;
        private double _progressAt;
        private double _cleanupDeadline;
        private bool _roundFailed;
        private bool _failed;
        private bool _cancelled;
        private bool _disposed;
        private string _roundMessage;

        internal bool Success { get { return _result.success; } }
        internal string Message { get { return Success ? "场景完成" : string.Join("\n", _errors); } }
        internal object Result { get { return _result; } }

        internal RevAutomationScenario(RevAutomationRequest request)
        {
            if (!EditorApplication.isPlaying || EditorApplication.isPaused) { throw new InvalidOperationException("场景执行需要未暂停的 Play；请先 editor.play"); }
            _request = request;
            _spec = JsonUtility.FromJson<RevAutomationScenarioSpec>(request.payloadJson);
            if (_spec == null || _spec.repeat < 1 || _spec.repeat > 1000000 || _spec.warmup < 0 || _spec.warmup > 1000000
                || _spec.steps == null || _spec.steps.Length == 0)
            {
                throw new ArgumentException("repeat 必须为 1..1000000，warmup 为 0..1000000，且必须有 steps");
            }
            Validate(_spec.setup); Validate(_spec.steps); Validate(_spec.cleanup); Validate(_spec.teardown);
            RevAutomationThresholds thresholds = _spec.thresholds;
            if (thresholds != null && (!Finite(thresholds.maxFrameP95Ms) || thresholds.maxFrameP95Ms < 0
                || !Finite(thresholds.maxFrameMs) || thresholds.maxFrameMs < 0 || thresholds.maxMonoUsedBytes < 0))
            { throw new ArgumentException("性能门禁必须是有限非负数，0 表示关闭"); }
            _work = RevAutomationBridge.JobDirectory(request.id);
            Directory.CreateDirectory(_work);
            _round = -_spec.warmup;
            _result = new RevAutomationScenarioResult
            {
                name = _spec.name, requested = _spec.repeat, startedUtc = DateTimeOffset.UtcNow.ToString("O"),
                unityVersion = Application.unityVersion, resultPath = Path.Combine(_work, "scenario-result.json")
            };
            _result.unityLogPath = Path.Combine(_work, "unity-errors.jsonl");
            Application.logMessageReceived += OnLog;
        }

        private static void Validate(RevAutomationStep[] steps)
        {
            foreach (RevAutomationStep step in steps ?? Array.Empty<RevAutomationStep>())
            {
                if (step == null || (step.kind != "gm" && step.kind != "wait" && step.kind != "until" && step.kind != "assert"))
                {
                    throw new ArgumentException("步骤仅支持 gm / wait / until / assert");
                }
                if (step.kind != "wait" && string.IsNullOrWhiteSpace(step.command)) { throw new ArgumentException("缺少步骤 command"); }
                if ((step.kind == "until" || step.kind == "assert") && step.expected == null) { throw new ArgumentException("缺少 expected"); }
                if (!Finite(step.seconds) || !Finite(step.timeoutSeconds) || !Finite(step.intervalSeconds) || step.frames < 0
                    || step.seconds < 0 || step.intervalSeconds < 0 || (step.kind == "until" && step.timeoutSeconds <= 0))
                {
                    throw new ArgumentException("步骤等待/轮询值必须有限且非负，until 需要大于 0 的超时");
                }
            }
        }

        private static bool Finite(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }

        internal void Cancel(string reason)
        {
            if (_cancelled || _phase == "done") { return; }
            _cancelled = true;
            Error(reason);
            _cleanupDeadline = _clock.Elapsed.TotalSeconds + 10;
            if (_phase == "steps") { SetPhase("cleanup"); }
            else if (_phase == "setup") { SetPhase("teardown"); }
        }

        /// <summary>
        /// 每帧推进一步。返回 true 表示"整个场景已结束"（上层据此写回执）。
        ///
        /// 一帧只做一个决策（用 Time.frameCount 去重），所以：
        /// · 极短的 wait 也至少跨 1 帧；
        /// · 不会因为帧率高而在同帧把几十条 GM 全跑完（那样测出来的数据不可信）。
        /// </summary>

        internal bool Tick()
        {
            if (_phase == "done") { return true; }
            double now = _clock.Elapsed.TotalSeconds;
            if (!EditorApplication.isPlaying || EditorApplication.isPaused)
            {
                Error("Play 已停止或暂停，无法继续调用业务 GM 清理");
                Finish();
                return true;
            }
            if (_cancelled && now > _cleanupDeadline) { Error("取消后的清理超时（10秒）"); Finish(); return true; }
            if (Time.frameCount == _lastFrame) { return false; }
            _lastFrame = Time.frameCount;
            // Unity 错误可能发生在业务后续帧。及时退出工作步骤，让 cleanup 仍有机会执行。
            if (_roundFailed && _phase == "steps") { SetPhase("cleanup"); }
            if (_failed && _phase == "setup") { SetPhase("teardown"); }
            if (_roundFrames != null)
            {
                double ms = Time.unscaledDeltaTime * 1000d;
                _roundFrames.Sample(ms);
                if (_round >= 0) { _frames.Sample(ms); }
                if (_round >= 0 && Time.frameCount % 30 == 0) { SampleMemory(); }
            }
            if (now >= _progressAt)
            {
                _progressAt = now + 1;
                RevAutomationBridge.WriteJson(Path.Combine(_work, "progress.json"), new RevAutomationProgress
                {
                    id = _request.id, phase = _phase, completed = _result.completed, requested = _spec.repeat,
                    warmupCompleted = _result.warmupCompleted, failed = _result.failedIterations,
                    command = _step < Steps.Length ? Steps[_step].command : ""
                });
            }
            if (_step >= Steps.Length) { AdvancePhase(); return _phase == "done"; }
            RevAutomationStep step = Steps[_step];
            if (_stepStart < 0) { _stepStart = now; _stepFrame = Time.frameCount; _nextPoll = now; }
            try
            {
                if (step.kind == "wait")
                {
                    if (now - _stepStart >= step.seconds && Time.frameCount - _stepFrame >= step.frames) { NextStep(); }
                    return false;
                }
                if (now < _nextPoll) { return false; }
                string command = Expand(step.command);
                RevGMResult result = RevAutomationActions.ExecuteGM(command, _request.confirm);
                if (!result.Success) { throw new InvalidOperationException(command + ": " + result.Message); }
                if (step.kind == "until" || step.kind == "assert")
                {
                    if (!string.Equals(result.Message, Expand(step.expected), StringComparison.Ordinal))
                    {
                        if (step.kind == "assert" || now - _stepStart >= step.timeoutSeconds)
                        {
                            throw new InvalidOperationException($"{command}: 预期「{Expand(step.expected)}」，实际「{result.Message}」" + (step.kind == "until" ? "（等待超时）" : ""));
                        }
                        _nextPoll = now + Math.Max(.001, step.intervalSeconds);
                        return false;
                    }
                }
                NextStep();
            }
            catch (Exception e)
            {
                Error(_phase + "[" + _step + "]: " + e.Message);
                if (_phase == "steps") { SetPhase("cleanup"); }
                else if (_phase == "setup") { SetPhase("teardown"); }
                else { NextStep(); } // 清理失败仍尝试其余清理动作。
            }
            return false;
        }

        private RevAutomationStep[] Steps
        {
            get
            {
                switch (_phase)
                {
                    case "setup": return _spec.setup ?? Array.Empty<RevAutomationStep>();
                    case "steps": return _spec.steps;
                    case "cleanup": return _spec.cleanup ?? Array.Empty<RevAutomationStep>();
                    case "teardown": return _spec.teardown ?? Array.Empty<RevAutomationStep>();
                    default: return Array.Empty<RevAutomationStep>();
                }
            }
        }

        private string Expand(string value)
        {
            return (value ?? "").Replace("{iteration}", Math.Max(0, _round).ToString(CultureInfo.InvariantCulture))
                .Replace("{round}", (_round + _spec.warmup).ToString(CultureInfo.InvariantCulture));
        }

        private void NextStep() { _step++; _stepStart = -1; }
        private void SetPhase(string phase) { _phase = phase; _step = 0; _stepStart = -1; }

        private void AdvancePhase()
        {
            switch (_phase)
            {
                case "setup": BeginRound(); break;
                case "steps": SetPhase("cleanup"); break;
                case "cleanup":
                    EndRound();
                    _round++;
                    if (_cancelled || (_failed && _spec.stopOnFailure) || _round >= _spec.repeat) { SetPhase("teardown"); }
                    else { BeginRound(); }
                    break;
                case "teardown": Finish(); break;
            }
        }

        private void BeginRound()
        {
            _roundFailed = false;
            _roundMessage = "";
            _roundStart = _clock.Elapsed.TotalSeconds;
            _roundFrames = _reusableRoundFrames;
            _roundFrames.Reset();
            _gc0 = GC.CollectionCount(0); _gc1 = GC.CollectionCount(1); _gc2 = GC.CollectionCount(2);
            SetPhase("steps");
        }

        /// <summary>
        /// 一轮结束：统计这一轮的耗时/帧耗时/内存，并**逐行追加**到 iterations.jsonl。
        /// 为什么追加而不是攒在内存里：一万轮的结果全放内存会直接 OOM。
        /// 预热轮只计数、不计入正式统计（否则首次 JIT 的抖动会污染数据）。
        /// </summary>

        private void EndRound()
        {
            long mono = Profiler.GetMonoUsedSizeLong();
            long allocated = Profiler.GetTotalAllocatedMemoryLong();
            var row = new RevAutomationIteration
            {
                iteration = _round, warmup = _round < 0, success = !_roundFailed, durationMs = (_clock.Elapsed.TotalSeconds - _roundStart) * 1000,
                frames = _roundFrames.Count, frameP95Ms = _roundFrames.P95(), frameMaxMs = _roundFrames.Max,
                monoUsedBytes = mono, allocatedMemoryBytes = allocated, gc0 = GC.CollectionCount(0) - _gc0,
                gc1 = GC.CollectionCount(1) - _gc1, gc2 = GC.CollectionCount(2) - _gc2, message = _roundMessage
            };
            // 逐轮追加而非把 10000 份结果塞进内存；每个请求目录独立，取消后仍保留已完成数据。
            File.AppendAllText(Path.Combine(_work, "iterations.jsonl"), JsonUtility.ToJson(row) + "\n");
            if (_round < 0) { _result.warmupCompleted++; }
            else
            {
                _result.completed++;
                if (_roundFailed) { _result.failedIterations++; }
                _result.peakMonoUsedBytes = Math.Max(_result.peakMonoUsedBytes, mono);
                _result.peakAllocatedMemoryBytes = Math.Max(_result.peakAllocatedMemoryBytes, allocated);
            }
            _roundFrames = null;
        }

        private void SampleMemory()
        {
            _result.peakMonoUsedBytes = Math.Max(_result.peakMonoUsedBytes, Profiler.GetMonoUsedSizeLong());
            _result.peakAllocatedMemoryBytes = Math.Max(_result.peakAllocatedMemoryBytes, Profiler.GetTotalAllocatedMemoryLong());
        }

        internal void Abort(string reason)
        {
            Error("执行器中断：" + reason);
            Finish();
        }

        private void Error(string value)
        {
            _failed = true;
            _roundFailed = true;
            _roundMessage = value;
            if (_errors.Count < 50) { _errors.Add(value); }
        }

        /// <summary>
        /// 监听 Unity 日志。只关心 Error / Exception / Assert 三类。
        /// 前 50 条会逐行写进 unity-errors.jsonl（方便失败后定位），
        /// 若场景配置了 failOnUnityError（默认 true），**任何一条错误日志都会让这次测试失败**。
        /// </summary>

        private void OnLog(string condition, string stack, LogType type)
        {
            if (type != LogType.Error && type != LogType.Exception && type != LogType.Assert) { return; }
            _result.unityErrors++;
            if (_result.unityErrors <= 50)
            {
                try
                {
                    var entry = new RevAutomationLog { utc = DateTimeOffset.UtcNow.ToString("O"), phase = _phase,
                        iteration = _round, step = _step, message = condition, stack = stack };
                    File.AppendAllText(_result.unityLogPath, JsonUtility.ToJson(entry) + "\n");
                }
                catch (IOException e) { Error("写错误日志失败：" + e.Message); }
                catch (UnauthorizedAccessException e) { Error("写错误日志失败：" + e.Message); }
            }
            if (_spec.failOnUnityError) { Error("Unity " + type + ": " + condition); }
        }

        /// <summary>
        /// 全部跑完（或被取消/失败）：算总指标、过门禁、写 scenario-result.json。
        ///
        /// 门禁规则（failOnUnityError / maxFrameP95Ms / maxMonoUsedBytes…）：
        /// 任何一项超标都记一条 error；最终 success = 没有 error 且完成的轮数等于要求的轮数。
        /// </summary>

        private void Finish()
        {
            if (_roundFrames != null) { EndRound(); }
            _result.frameSamples = _frames.Count;
            _result.frameMeanMs = _frames.Count == 0 ? 0 : _frames.Sum / _frames.Count;
            _result.frameP95Ms = _frames.P95();
            _result.frameMaxMs = _frames.Max;
            RevAutomationThresholds t = _spec.thresholds ?? new RevAutomationThresholds();
            if ((t.maxFrameP95Ms > 0 || t.maxFrameMs > 0) && _frames.Count == 0) { Error("没有帧样本，不能判定帧耗时门禁通过"); }
            if (t.maxFrameP95Ms > 0 && _result.frameP95Ms > t.maxFrameP95Ms) { Error("frameP95Ms 超出门禁"); }
            if (t.maxFrameMs > 0 && _frames.Max > t.maxFrameMs) { Error("frameMaxMs 超出门禁"); }
            if (t.maxMonoUsedBytes > 0 && _result.peakMonoUsedBytes > t.maxMonoUsedBytes) { Error("monoUsedBytes 超出门禁"); }
            _result.cancelled = _cancelled;
            _result.success = !_failed && _result.completed == _spec.repeat;
            _result.finishedUtc = DateTimeOffset.UtcNow.ToString("O");
            _result.elapsedSeconds = _clock.Elapsed.TotalSeconds;
            _result.errors = _errors.ToArray();
            RevAutomationBridge.WriteJson(_result.resultPath, _result);
            SetPhase("done");
            Dispose();
        }

        public void Dispose()
        {
            if (_disposed) { return; }
            _disposed = true;
            Application.logMessageReceived -= OnLog;
        }
    }

    [Serializable]
    internal sealed class RevAutomationLog
    {
        public string utc;
        public string phase;
        public int iteration;
        public int step;
        public string message;
        public string stack;
    }
}
