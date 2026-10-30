// 常驻采样宿主。每帧仅读Recorder/写预分配数组；排序、JSON、截图和文件输出不在采样循环中执行。
#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
using System;
using Unity.Profiling;
using UnityEngine;

namespace Revolution
{
    internal sealed class RevRuntimePerformanceSampler : MonoBehaviour
    {
        private static RevRuntimePerformanceSampler _instance;
        private static RevPerformanceReport _report = new RevPerformanceReport();
        // 环形窗口归本宿主所有，保留容量不保留无限历史。固定测量最多每秒1000样本，超出明确失败。
        private readonly double[] _rolling = new double[2048];
        private readonly FrameTiming[] _timings = new FrameTiming[1];
        private ProfilerRecorder[] _recorders;
        private int _cursor, _count, _errorStart;
        private double _previous, _start, _warmUntil, _lastGpu = -1;
        private ulong _stamp;
        private double[] _frames, _gpu, _sums;
        private int[] _valid;
        private bool _collecting;
        private double _quitAt = -1;
        private static readonly string[] Names =
        {
            "Draw Calls Count", "Total Batches Count", "SetPass Calls Count", "Triangles Count", "Vertices Count",
            "Dynamic Batches Count", "Static Batches Count", "Instanced Batches Count", "GC Allocated In Frame",
            "Total Used Memory", "Total Reserved Memory", "GC Used Memory", "Main Thread", "Render Thread"
        };

        internal static RevRuntimePerformanceSampler Ensure()
        {
            if (_instance == null)
            {
                var go = new GameObject("[RevRuntimePerformance]");
                DontDestroyOnLoad(go);
                _instance = go.AddComponent<RevRuntimePerformanceSampler>();
            }
            return _instance;
        }

        private void Awake()
        {
            _recorders = new ProfilerRecorder[Names.Length];
            for (int i = 0; i < Names.Length; i++)
            {
                ProfilerCategory category = i < 8 ? ProfilerCategory.Render : i < 12 ? ProfilerCategory.Memory : ProfilerCategory.Internal;
                // 不存在的计数器保持Invalid；不同平台/Release构建不得把缺样伪装成零。
                _recorders[i] = ProfilerRecorder.StartNew(category, Names[i], 1);
                if (i == 1 && !_recorders[i].Valid)
                {
                    // 2022.3 Player的原生标记名为Batches Count，手册展示名为Total Batches Count。
                    // 只在首选名称不存在时尝试已知别名；两者均缺失仍报告N/A。
                    _recorders[i].Dispose();
                    _recorders[i] = ProfilerRecorder.StartNew(category, "Batches Count", 1);
                }
            }
            _previous = Time.realtimeSinceStartupAsDouble;
        }

        private void Update()
        {
            double now = Time.realtimeSinceStartupAsDouble;
            if (_quitAt >= 0 && now >= _quitAt)
            {
                Application.Quit();
                return;
            }
            double ms = (now - _previous) * 1000;
            _previous = now;
            _rolling[_cursor] = ms;
            _cursor = (_cursor + 1) % _rolling.Length;
            _count = Math.Min(_count + 1, _rolling.Length);
            FrameTimingManager.CaptureFrameTimings();
            bool freshGpu = FrameTimingManager.GetLatestTimings(1, _timings) > 0 && _timings[0].frameStartTimestamp != _stamp && _timings[0].gpuFrameTime > 0;
            if (freshGpu)
            {
                _stamp = _timings[0].frameStartTimestamp;
                _lastGpu = _timings[0].gpuFrameTime;
            }
            else
            {
                _lastGpu = -1;
            }
            if (_report.status != "running")
            {
                return;
            }
            if (!_collecting)
            {
                if (now < _warmUntil)
                {
                    return;
                }
                _collecting = true;
                _start = now;
                return;
            }
            if (_report.frames >= _frames.Length)
            {
                Finish("failed", "超过预分配样本上限，结果不截断冒充通过");
                return;
            }
            _frames[_report.frames++] = ms;
            if (freshGpu)
            {
                _gpu[_report.gpuSamples++] = _lastGpu;
            }
            if (!Application.isFocused)
            {
                _report.unfocusedFrames++;
            }
            for (int i = 0; i < _recorders.Length; i++)
            {
                if (_recorders[i].Valid && _recorders[i].Count > 0)
                {
                    _sums[i] += _recorders[i].LastValue;
                    _valid[i]++;
                }
            }
            _report.seconds = now - _start;
            if (_report.seconds >= _report.requestedSeconds)
            {
                Finish("completed", "");
            }
        }

        internal static RevPerformanceReport Begin(double seconds, double warmup)
        {
            if (double.IsNaN(seconds) || double.IsNaN(warmup) || seconds < 1 || seconds > 600 || warmup < 0 || warmup > 60)
            {
                throw new ArgumentOutOfRangeException("seconds=1..600，warmup=0..60");
            }
            if (_report.status == "running")
            {
                throw new InvalidOperationException("已有测量作业，不能覆盖");
            }
            var host = Ensure();
            _report = new RevPerformanceReport
            {
                runId = Guid.NewGuid().ToString("N"), status = "running", requestedSeconds = seconds,
                warmupSeconds = warmup, startedUtc = DateTime.UtcNow.ToString("O"), environment = Identity()
            };
            int capacity = (int)Math.Ceiling(seconds * 1000) + 1;
            host._frames = new double[capacity];
            host._gpu = new double[capacity];
            host._sums = new double[Names.Length];
            host._valid = new int[Names.Length];
            host._errorStart = RevRuntimeAutomation.ErrorCount;
            host._warmUntil = Time.realtimeSinceStartupAsDouble + warmup;
            host._collecting = false;
            return _report;
        }

        private void Finish(string status, string error)
        {
            _report.status = status;
            _report.cancelled = status == "cancelled";
            _report.errors = RevRuntimeAutomation.ErrorCount - _errorStart;
            _report.error = error;
            _report.endedUtc = DateTime.UtcNow.ToString("O");
            _report.success = status == "completed" && _report.errors == 0 && _report.seconds >= _report.requestedSeconds && _report.frames > 0;
            if (status == "completed" && !_report.success)
            {
                _report.status = "failed";
                _report.error = "时长/样本/新增运行错误门禁失败";
            }
            if (_report.frames > 0)
            {
                Array.Sort(_frames, 0, _report.frames);
                _report.fps = _report.seconds > 0 ? _report.frames / _report.seconds : 0;
                _report.p50Ms = Percentile(_frames, _report.frames, .5);
                _report.p95Ms = Percentile(_frames, _report.frames, .95);
                _report.p99Ms = Percentile(_frames, _report.frames, .99);
                _report.maxMs = _frames[_report.frames - 1];
                foreach (double threshold in new[] { 12.5, 16.6667, 33.3333 })
                {
                    int over = 0;
                    for (int i = 0; i < _report.frames; i++)
                    {
                        if (_frames[i] > threshold)
                        {
                            over++;
                        }
                    }
                    double ratio = over / (double)_report.frames;
                    if (threshold == 12.5)
                    {
                        _report.over125Ratio = ratio;
                    }
                    else if (threshold < 20)
                    {
                        _report.over167Ratio = ratio;
                    }
                    else
                    {
                        _report.over333Ratio = ratio;
                    }
                }
            }
            if (_report.gpuSamples > 0)
            {
                Array.Sort(_gpu, 0, _report.gpuSamples);
                _report.gpuP50Ms = Percentile(_gpu, _report.gpuSamples, .5);
                _report.gpuP95Ms = Percentile(_gpu, _report.gpuSamples, .95);
            }
            _report.averages = Metrics(true);
            _frames = null;
            _gpu = null;
            _sums = null;
            _valid = null;
        }

        private static double Percentile(double[] sorted, int count, double fraction)
        {
            return count == 0 ? 0 : sorted[Math.Max(0, (int)Math.Ceiling(count * fraction) - 1)];
        }

        private RevPerformanceMetric[] Metrics(bool average)
        {
            var metrics = new RevPerformanceMetric[Names.Length];
            for (int i = 0; i < metrics.Length; i++)
            {
                bool valid = average ? _valid[i] > 0 : _recorders[i].Valid && _recorders[i].Count > 0;
                double value = valid ? (average ? _sums[i] / _valid[i] : _recorders[i].LastValue) : -1;
                if (valid && i >= 12)
                {
                    value /= 1000000;
                }
                metrics[i] = new RevPerformanceMetric { name = Names[i], unit = i < 8 ? "count" : i < 12 ? "bytes" : "ms", available = valid, value = value };
            }
            return metrics;
        }

        internal static RevPerformanceSnapshot Identity()
        {
            return new RevPerformanceSnapshot
            {
                product = Application.productName, buildId = Application.buildGUID, unityVersion = Application.unityVersion,
                platform = Application.platform.ToString(), editor = Application.isEditor, development = Debug.isDebugBuild,
#if ENABLE_IL2CPP
                backend = "IL2CPP",
#else
                backend = "Mono",
#endif
                cpu = SystemInfo.processorType, gpu = SystemInfo.graphicsDeviceName, graphicsApi = SystemInfo.graphicsDeviceType.ToString(),
                resolution = Screen.width + "x" + Screen.height, focused = Application.isFocused, runInBackground = Application.runInBackground,
                targetFrameRate = Application.targetFrameRate, vSyncCount = QualitySettings.vSyncCount, enabled = _instance != null
            };
        }

        internal static RevPerformanceSnapshot Snapshot()
        {
            var result = Identity();
            if (_instance == null)
            {
                result.metrics = Array.Empty<RevPerformanceMetric>();
                return result;
            }
            int count = _instance._count;
            var sorted = new double[count];
            Array.Copy(_instance._rolling, sorted, count);
            double sum = 0;
            foreach (double value in sorted)
            {
                sum += value;
            }
            Array.Sort(sorted);
            result.frames = count;
            result.seconds = sum / 1000;
            result.fps = sum > 0 ? count * 1000 / sum : 0;
            result.p50Ms = Percentile(sorted, count, .5);
            result.p95Ms = Percentile(sorted, count, .95);
            result.p99Ms = Percentile(sorted, count, .99);
            result.maxMs = count > 0 ? sorted[count - 1] : 0;
            result.gpuMs = _instance._lastGpu;
            result.metrics = _instance.Metrics(false);
            return result;
        }

        internal static RevPerformanceReport State()
        {
            return _report;
        }

        internal static RevPerformanceReport Cancel()
        {
            if (_instance != null && _report.status == "running")
            {
                _instance.Finish("cancelled", "调用方取消；已发生的业务动作不回滚");
            }
            return _report;
        }

        internal static void Disable()
        {
            if (_report.status == "running")
            {
                throw new InvalidOperationException("进行中的测量请先cancel");
            }
            if (_instance != null)
            {
                Destroy(_instance.gameObject);
            }
        }

        internal void ScheduleQuit()
        {
            _quitAt = Time.realtimeSinceStartupAsDouble + .75;
        }

        private void OnDestroy()
        {
            if (_report.status == "running")
            {
                Finish("cancelled", "采样宿主销毁");
            }
            if (_recorders != null)
            {
                for (int i = 0; i < _recorders.Length; i++)
                {
                    _recorders[i].Dispose();
                }
            }
            if (_instance == this)
            {
                _instance = null;
            }
        }
    }
}
#endif
