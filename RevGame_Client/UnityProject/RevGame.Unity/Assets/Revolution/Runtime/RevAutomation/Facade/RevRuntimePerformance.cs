// 自动化门面的性能契约：不依赖Editor，面板和MCP读取相同采样器。正式包需显式REV_AUTOMATION。
#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
using System;
using UnityEngine;
using UnityEngine.Scripting;

namespace Revolution
{
    /// <summary>计数器原生单位由unit标明；缺失值为-1，不能解释成零成本。</summary>
    [Serializable, Preserve]
    public sealed class RevPerformanceMetric
    {
        public string name, unit;
        public bool available;
        public double value = -1;
    }

    /// <summary>最近最多2048帧的窗口快照。FPS来自帧数/墙钟秒，非instantaneous FPS平均。</summary>
    [Serializable, Preserve]
    public sealed class RevPerformanceSnapshot
    {
        public string product, buildId, unityVersion, platform, backend, cpu, gpu, graphicsApi, resolution;
        public bool editor, development, focused, runInBackground, enabled;
        public int frames, targetFrameRate, vSyncCount;
        public double seconds, fps, p50Ms, p95Ms, p99Ms, maxMs, gpuMs = -1;
        public RevPerformanceMetric[] metrics;
    }

    /// <summary>一次固定时长作业；结束后才有分位数，success不代表达到任何项目性能预算。</summary>
    [Serializable, Preserve]
    public sealed class RevPerformanceReport
    {
        public string runId, status = "idle", error, startedUtc, endedUtc;
        public bool success, cancelled;
        public int frames, gpuSamples, unfocusedFrames, errors;
        public double requestedSeconds, warmupSeconds, seconds, fps, p50Ms, p95Ms, p99Ms, maxMs;
        public double gpuP50Ms = -1, gpuP95Ms = -1, over125Ratio, over167Ratio, over333Ratio;
        public RevPerformanceSnapshot environment;
        public RevPerformanceMetric[] averages;
    }

    public static partial class RevRuntimeAutomation
    {
        /// <summary>启动常驻轻量采样，不写盘、不启动Profiler录制；重复调用复用同一宿主。</summary>
        public static void EnablePerformance()
        {
            MainThread();
            RevRuntimePerformanceSampler.Ensure();
        }

        /// <summary>返回窗口快照，调用时才排序/装配DTO；面板应低频读取，测量时应隐藏。</summary>
        public static RevPerformanceSnapshot PerformanceSnapshot()
        {
            MainThread();
            return RevRuntimePerformanceSampler.Snapshot();
        }

        /// <summary>关闭并释放Recorder；进行中的固定测量必须先取消，避免假装完成。</summary>
        public static void DisablePerformance()
        {
            MainThread();
            RevRuntimePerformanceSampler.Disable();
        }

        internal static void RegisterPerformance()
        {
            const string empty = "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}";
            const string options = "{\"type\":\"object\",\"properties\":{\"seconds\":{\"type\":\"number\",\"minimum\":1,\"maximum\":600},\"warmup\":{\"type\":\"number\",\"minimum\":0,\"maximum\":60}},\"required\":[\"seconds\"],\"additionalProperties\":false}";
            Register("runtime.performance.enable", "启动常驻性能采样；不修改帧率/画质", empty, _ =>
            {
                EnablePerformance();
                return PerformanceSnapshot();
            });
            Register("runtime.performance.snapshot", "只读最近窗口FPS/计数器；缺失为-1", empty, _ => PerformanceSnapshot(), true);
            Register("runtime.performance.begin", "开始有界固定时长测量，先预热；不修改业务负载", options, json =>
            {
                var p = JsonUtility.FromJson<PerformanceOptions>(json);
                return RevRuntimePerformanceSampler.Begin(p.seconds, p.warmup);
            });
            Register("runtime.performance.state", "只读当前runId、完成/取消/错误与最终摘要", empty, _ => RevRuntimePerformanceSampler.State(), true);
            Register("runtime.performance.cancel", "取消固定测量，保留失败状态，不回滚业务", empty, _ => RevRuntimePerformanceSampler.Cancel());
            Register("runtime.performance.disable", "释放采样Recorder；运行作业须先取消", empty, _ =>
            {
                DisablePerformance();
                return PerformanceSnapshot();
            });
            Register("runtime.identity", "只读Player构建/设备身份", empty, _ => RevRuntimePerformanceSampler.Identity(), true);
            Register("runtime.quit", "延迟退出当前Player以便先返回响应；Editor中拒绝", empty, _ =>
            {
                if (Application.isEditor)
                {
                    throw new InvalidOperationException("Editor请使用editor.stop");
                }
                RevRuntimePerformanceSampler.Ensure().ScheduleQuit();
                return new { accepted = true };
            });
        }

        [Serializable, Preserve]
        private sealed class PerformanceOptions
        {
            public double seconds, warmup;
        }
    }
}
#endif
