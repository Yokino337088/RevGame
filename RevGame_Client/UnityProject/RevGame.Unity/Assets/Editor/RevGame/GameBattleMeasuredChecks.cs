// ============================================================
// GameBattleMeasuredChecks.cs —— 固定输入的性能摘要与可核验收尾。
// 固定48棋实测。计时期间只写预分配数组，不截图、格式化CSV或导出Profiler录制。
// 唯一runId、请求范围、设备与完成状态必须共同匹配；中断结果不得视作性能通过。
// 所有修改的同步/时间/日志设置在finally还原；测试容量属于Editor作业，不属于生产战斗。
// ============================================================
using System;
using System.Collections.Generic;
using System.IO;
using RevGame.Core;
using RevGame.Flow;
using RevGame.Services;
using RevGame.View;
using Revolution;
using Unity.Profiling;
using UnityEditor;
using UnityEngine;
using UnityEngine.Profiling;
namespace RevGame.Editor
{
    public static class GameBattleMeasuredChecks
    {
        [Serializable]
        public sealed class Row
        {
            // 一段完成的采样：帧时单位毫秒，fps=总帧/墙钟秒；-1代表计数器无有效样本而非零成本。
            public int chapter, repeat, frames, gpuSamples, attacks, minPieces, maxPieces;
            public double seconds, fps, p50Ms, p95Ms, p99Ms, maxMs, over125Ratio, over167Ratio, over333Ratio;
            public double gpuP50Ms = -1, gpuP95Ms = -1, mainP95Ms = -1, renderP95Ms = -1, gcBytesPerFrame = -1;
            public double batches, setPass, hudMs = -1, feedbackMs = -1, arrangeMs = -1;
            public long allocatedBytes, reservedBytes, monoBytes;
            public long attackCacheHits, attackCacheMisses, snapshotCopies, snapshotPieceReuses;
            public double attackCacheHitRatio;
            public long damageTextHits, damageTextMisses;
            public string resolution;
            public bool budget80, budget90;
        }
        [Serializable]
        public sealed class Result
        {
            // runId确定本次身份，不能读取旧result.json冒充完成；cleaned只在正式回首页成功后设置。
            public string runId, label, status = "running", error, cpu, gpu, graphicsApi, unityVersion, startedUtc, endedUtc, folder;
            public bool ab, cleaned, cancelled;
            public int first, last, repeats, requestedSeconds, errors, returnBundles, returnLogic, returnPieceRefs;
            public List<Row> chapters = new List<Row>();
        }
        private struct Sample
        {
            // 计时窗口只写预分配值类型记录，避免采样工具逐帧格式化字符串成为新的GC热点。
            public double frame, gpu, main, render;
            public long gc, hud, feedback, arrange;
            public int batches, setPass, pieces;
        }
        private static Result _result;
        private static bool _cancel;
        public static string State() { return _result == null ? "{\"status\":\"idle\"}" : JsonUtility.ToJson(_result); }
        public static string Cancel() { _cancel = true; return "取消已请求，等待回首页清理"; }
        /// <summary>600秒热态及十次往返可在目标笔记本执行；时长仅用于测量，不调整规则。</summary>
        public static string Begin(string label, int first, int last, int seconds, int repeats)
        {
            if (!Application.isPlaying || EditorApplication.isPaused || _result?.status == "running") { throw new InvalidOperationException("需要未暂停Play且无运行压测"); }
            if (!RevServices.GetRequired<StoryProgress>().IsIsolated) { throw new InvalidOperationException("需要隔离进度"); }
            if (first < 1 || last > 6 || first > last || seconds < 10 || seconds > 600 || repeats < 1 || repeats > 10) { throw new ArgumentException("章节1至6，时长10至600秒，重复1至10"); }
            string id = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            _result = new Result
            {
                runId = id,
                label = label,
                first = first,
                last = last,
                repeats = repeats,
                requestedSeconds = seconds,
                cpu = SystemInfo.processorType,
                gpu = SystemInfo.graphicsDeviceName,
                graphicsApi = SystemInfo.graphicsDeviceType.ToString(),
                unityVersion = Application.unityVersion,
                ab = RevResBootstrap.UseABInEditor,
                startedUtc = DateTime.UtcNow.ToString("O"),
                folder = Path.GetFullPath("../../../.codex/work/perf-2026-10-30/" + id)
            };
            Directory.CreateDirectory(_result.folder); _cancel = false; Run(); return State();
        }
        private static void Guard() { if (_cancel || !Application.isPlaying) { throw new OperationCanceledException("取消或Play退出"); } }
        private static async RevTask Wait(Func<bool> ready, string reason)
        {
            float until = Time.realtimeSinceStartup + 60;
            while (!ready())
            {
                Guard(); if (Time.realtimeSinceStartup > until) { throw new TimeoutException(reason); }
                var flow = RevServices.GetRequired<GameFlow>(); if (flow.IsStoryPlaying && !flow.IsTransitioning) { flow.AdvanceStory(); }
                await RevTask.Yield();
            }
        }
        private static async void Run()
        {
            var r = _result; var flow = RevServices.GetRequired<GameFlow>(); var b = (BattleService)RevServices.GetRequired<IBattleService>();
            int vsync = QualitySettings.vSyncCount, target = Application.targetFrameRate; float scale = Time.timeScale;
            RevLogLevel log = RevLog.MinLevel; LogType filter = Debug.unityLogger.filterLogType; bool profiler = UnityEditorInternal.ProfilerDriver.enabled;
            Application.LogCallback logger = (message, stack, type) => { if (type == LogType.Error || type == LogType.Exception) { r.errors++; r.error = message; } };
            Application.logMessageReceived += logger;
            try
            {
                QualitySettings.vSyncCount = 0; Application.targetFrameRate = -1; Time.timeScale = 1;
                RevLog.MinLevel = RevLogLevel.Error; Debug.unityLogger.filterLogType = LogType.Error; UnityEditorInternal.ProfilerDriver.enabled = false;
                await Wait(() => flow.Screen == GameScreen.Home && !flow.IsTransitioning && !flow.IsGameplayBlocked, "首页未就绪");
                b.UnlockAllChaptersForDevelopment();
                for (int repeat = 0; repeat < r.repeats; repeat++)
                {
                    for (int chapter = r.first; chapter <= r.last; chapter++)
                    {
                        Guard(); flow.OpenLevels(); if (!flow.EnterBattle(chapter)) { throw new InvalidOperationException(flow.LastError); }
                        await Wait(() => !flow.IsTransitioning && !flow.IsGameplayBlocked && b.Phase == GamePhase.Prepare, "准备未交付");
                        if (!b.LoadPerformanceScenario(chapter)) { throw new InvalidOperationException(b.LastError); }
                        await Wait(() => !flow.IsGameplayBlocked && b.PreparationGranted && UnityEngine.Object.FindObjectOfType<BoardView>()?.IsReady == true, "固定输入未就绪");
                        if (!RevLogic.Get<BattleLogic>().BeginBattle()) { throw new InvalidOperationException(b.LastError); }
                        double until = Time.realtimeSinceStartupAsDouble + 2.5, heal = 0;
                        while (Time.realtimeSinceStartupAsDouble < until)
                        {
                            Guard(); if (Time.realtimeSinceStartupAsDouble >= heal) { b.SustainPerformanceScenario(); heal = Time.realtimeSinceStartupAsDouble + 1; }
                            await RevTask.Yield();
                        }
                        r.chapters.Add(await Measure(chapter, repeat)); Write();
                        // 连拍独立于采样窗口，不将截图阻塞作为游戏帧时；画面保留全部动态效果。
                        for (int frame = 0; frame < 12; frame++)
                        { ScreenCapture.CaptureScreenshot(Path.Combine(r.folder, "ch" + chapter + "-r" + repeat + "-" + frame.ToString("00") + ".png")); await RevTask.Delay(120); }
                        if (!flow.ReturnHome()) { throw new InvalidOperationException(flow.LastError); }
                        await Wait(() => flow.Screen == GameScreen.Home && !flow.IsTransitioning, "返回首页超时");
                        if (UnityEngine.Object.FindObjectOfType<BoardView>() != null || RevResManager.GetRefCount("Atlases", "PieceFrames") != 0) { throw new InvalidOperationException("棋盘/图集引用未回收"); }
                    }
                }
                r.status = "completed";
            }
            catch (Exception e) { r.status = "failed"; r.cancelled = e is OperationCanceledException; r.error = e.ToString(); }
            finally
            {
                try
                {
                    if (Application.isPlaying && (flow.Screen != GameScreen.Home || flow.IsTransitioning))
                    {
                        float until = Time.realtimeSinceStartup + 45;
                        while (flow.IsTransitioning && Time.realtimeSinceStartup < until) { await RevTask.Yield(); }
                        flow.ReturnHome(); while ((flow.Screen != GameScreen.Home || flow.IsTransitioning) && Application.isPlaying && Time.realtimeSinceStartup < until) { await RevTask.Yield(); }
                    }
                    r.cleaned = Application.isPlaying && flow.Screen == GameScreen.Home && !flow.IsTransitioning && UnityEngine.Object.FindObjectOfType<BoardView>() == null;
                    foreach (var bundle in AssetBundle.GetAllLoadedAssetBundles()) { r.returnBundles++; }
                    r.returnLogic = RevLogic.Count; r.returnPieceRefs = RevResManager.GetRefCount("Atlases", "PieceFrames");
                    if (!r.cleaned) { r.status = "failed"; r.error += "\n清理未完成"; }
                }
                catch (Exception e) { r.status = "failed"; r.error += "\n清理异常：" + e; }
                QualitySettings.vSyncCount = vsync; Application.targetFrameRate = target; Time.timeScale = scale; RevLog.MinLevel = log; Debug.unityLogger.filterLogType = filter;
                UnityEditorInternal.ProfilerDriver.enabled = profiler; Application.logMessageReceived -= logger; r.endedUtc = DateTime.UtcNow.ToString("O"); Write();
            }
        }
        private static async RevTask<Row> Measure(int chapter, int repeat)
        {
            var b = (BattleService)RevServices.GetRequired<IBattleService>(); var buffer = new Sample[_result.requestedSeconds * 1000 + 1]; var timing = new FrameTiming[1];
            int count = 0, attacks = 0, errorStart = _result.errors; object owner = new object(); RevEvent.AddEventListener<PieceAttackInfo>(Events.PieceAttacked, _ => attacks++, owner: owner);
            var gc = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC Allocated In Frame", 1);
            var hud = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "RevGame.Hud.Refresh", 1);
            var feedback = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "RevGame.Feedback.Frame", 1);
            var arrange = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "RevGame.Feedback.Arrange", 1);
            double start = Time.realtimeSinceStartupAsDouble, previous = start, heal = start; ulong stamp = 0;
            long hitsBefore = b.Board.AttackCacheHits, missesBefore = b.Board.AttackCacheMisses;
            long copiesBefore = b.Board.TacticalSnapshotCopies, reusesBefore = b.Board.TacticalPieceReuses;
            long textHitsBefore = GameTextConfig.DamageTextCacheHits, textMissesBefore = GameTextConfig.DamageTextCacheMisses;
            try
            {
                while (Time.realtimeSinceStartupAsDouble - start < _result.requestedSeconds)
                {
                    await RevTask.Yield(); Guard(); double now = Time.realtimeSinceStartupAsDouble;
                    if (count >= buffer.Length) { throw new InvalidOperationException("预分配采样容量不足，拒绝截断"); }
                    var s = new Sample
                    {
                        frame = (now - previous) * 1000,
                        gpu = -1,
                        main = -1,
                        render = -1,
                        gc = gc.Valid ? gc.LastValue : -1,
                        hud = hud.Valid ? hud.LastValue : -1,
                        feedback = feedback.Valid ? feedback.LastValue : -1,
                        arrange = arrange.Valid ? arrange.LastValue : -1,
                        batches = UnityStats.batches,
                        setPass = UnityStats.setPassCalls
                    }; previous = now;
                    foreach (var p in b.PlayerPieces) { if (p.IsAlive) { s.pieces++; } }
                    foreach (var p in b.EnemyPieces) { if (p.IsAlive) { s.pieces++; } }
                    FrameTimingManager.CaptureFrameTimings();
                    if (FrameTimingManager.GetLatestTimings(1, timing) > 0 && timing[0].frameStartTimestamp != stamp)
                    { stamp = timing[0].frameStartTimestamp; if (timing[0].gpuFrameTime > 0) { s.gpu = timing[0].gpuFrameTime; } s.main = timing[0].cpuMainThreadFrameTime; s.render = timing[0].cpuRenderThreadFrameTime; }
                    buffer[count++] = s; if (now >= heal) { b.SustainPerformanceScenario(); heal = now + 1; }
                }
            }
            finally { RevEvent.RemoveAllByOwner(owner); gc.Dispose(); hud.Dispose(); feedback.Dispose(); arrange.Dispose(); }
            var row = new Row { chapter = chapter, repeat = repeat, frames = count, seconds = previous - start, attacks = attacks, minPieces = int.MaxValue, resolution = UnityStats.screenRes };
            var times = new double[count]; var gpuTimes = new List<double>(count); var mainTimes = new List<double>(count); var renderTimes = new List<double>(count);
            long gcSum = 0, hudSum = 0, feedbackSum = 0, arrangeSum = 0; bool gcValid = false, hudValid = false, feedbackValid = false, arrangeValid = false;
            for (int i = 0; i < count; i++)
            {
                var s = buffer[i]; times[i] = s.frame; row.minPieces = Math.Min(row.minPieces, s.pieces); row.maxPieces = Math.Max(row.maxPieces, s.pieces); row.batches += s.batches; row.setPass += s.setPass;
                if (s.frame > 12.5) { row.over125Ratio++; }
                if (s.frame > 16.6667) { row.over167Ratio++; }
                if (s.frame > 33.3333) { row.over333Ratio++; }
                if (s.gpu > 0) { gpuTimes.Add(s.gpu); }
                if (s.main > 0) { mainTimes.Add(s.main); }
                if (s.render > 0) { renderTimes.Add(s.render); }
                if (s.gc >= 0) { gcSum += s.gc; gcValid = true; }
                if (s.hud >= 0) { hudSum += s.hud; hudValid = true; }
                if (s.feedback >= 0) { feedbackSum += s.feedback; feedbackValid = true; }
                if (s.arrange >= 0) { arrangeSum += s.arrange; arrangeValid = true; }
            }
            Array.Sort(times); gpuTimes.Sort(); mainTimes.Sort(); renderTimes.Sort(); row.fps = count / row.seconds; row.p50Ms = P(times, .5); row.p95Ms = P(times, .95); row.p99Ms = P(times, .99); row.maxMs = times[count - 1];
            row.gpuSamples = gpuTimes.Count; if (gpuTimes.Count > 0) { row.gpuP50Ms = P(gpuTimes, .5); row.gpuP95Ms = P(gpuTimes, .95); }
            if (mainTimes.Count > 0) { row.mainP95Ms = P(mainTimes, .95); }
            if (renderTimes.Count > 0) { row.renderP95Ms = P(renderTimes, .95); }
            row.gcBytesPerFrame = gcValid ? gcSum / (double)count : -1; row.hudMs = hudValid ? hudSum / (double)count / 1e6 : -1; row.feedbackMs = feedbackValid ? feedbackSum / (double)count / 1e6 : -1; row.arrangeMs = arrangeValid ? arrangeSum / (double)count / 1e6 : -1;
            row.batches /= count; row.setPass /= count; row.over125Ratio /= count; row.over167Ratio /= count; row.over333Ratio /= count;
            row.allocatedBytes = Profiler.GetTotalAllocatedMemoryLong(); row.reservedBytes = Profiler.GetTotalReservedMemoryLong(); row.monoBytes = Profiler.GetMonoUsedSizeLong();
            row.attackCacheHits = b.Board.AttackCacheHits - hitsBefore; row.attackCacheMisses = b.Board.AttackCacheMisses - missesBefore;
            row.attackCacheHitRatio = (row.attackCacheHits + row.attackCacheMisses) > 0 ? row.attackCacheHits / (double)(row.attackCacheHits + row.attackCacheMisses) : 0;
            row.snapshotCopies = b.Board.TacticalSnapshotCopies - copiesBefore; row.snapshotPieceReuses = b.Board.TacticalPieceReuses - reusesBefore;
            row.damageTextHits = GameTextConfig.DamageTextCacheHits - textHitsBefore; row.damageTextMisses = GameTextConfig.DamageTextCacheMisses - textMissesBefore;
            row.budget80 = row.fps >= 80 && row.p95Ms <= 12.5; row.budget90 = row.fps >= 90 && row.p95Ms <= 1000d / 90;
            if (row.minPieces < 40 || attacks < 40 || row.seconds < _result.requestedSeconds || _result.errors > errorStart) { throw new InvalidOperationException("负载/时长/错误门禁失败：章" + chapter); }
            return row;
        }
        private static double P(IList<double> sorted, double fraction) { return sorted[Math.Min(sorted.Count - 1, (int)((sorted.Count - 1) * fraction))]; }
        private static void Write() { File.WriteAllText(Path.Combine(_result.folder, "result.json"), JsonUtility.ToJson(_result, true)); }
    }
}
