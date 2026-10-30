using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Globalization;
using RevGame.Core;
using RevGame.Flow;
using RevGame.Services;
using RevGame.View;
using Revolution;
using Unity.Profiling;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.Profiling;

namespace RevGame.Editor
{
    /// <summary>
    /// 本机Editor性能诊断：复用48棋固定输入，分开记录吞吐、Profiler与画面，避免截图/字符串格式化污染稳定期。
    /// 仅由显式测试入口启动；每轮隔离积分，finally恢复日志、帧率和Profiler，并经正式流程返回大厅。
    /// GPU缺失用-1；Editor内存包含工具，不能当Player内存。关闭背景的分段只用于归因，不算画质达标。
    /// </summary>
    public static class GameBattleDeviceProfileChecks
    {
        public static string LastResult { get; private set; } = "未运行";
        public static string Progress { get; private set; } = "";
        [Serializable] public sealed class Row
        {
            public int chapter, frames, minPieces = int.MaxValue, maxPieces, attacks, gpuSamples, gcCollections;
            public double fps, p50, p95, p99, max, over12_5Percent, over33Percent, gpuP50 = -1, gpuP95 = -1, cpuP50 = -1, mainP50 = -1, renderP50 = -1;
            public double batches, draws, setPass, gcBytesPerFrame = -1, gcPeakBytes = -1, allocatedMiB, monoMiB;
            public bool gcRecorderValid;
            public string resolution, sampleStartedUtc, sampleEndedUtc;
        }
        [Serializable] public sealed class MemoryPoint
        {
            public string stage;
            public long allocated, reserved, mono, monoReserved, processWorking, processPrivate, textureBytes, audioBytes;
            public int textures, audioClips, materials, gameObjects, bundles, logic;
            public string atlasState;
        }
        [Serializable] public sealed class Report
        {
            public string label, cpu, gpu, graphicsApi, unity, mode, error, quietPolicy = "RevLog>=Error; Unity logger>=Error";
            public int systemMemoryMB, graphicsMemoryMB, originalVsync, originalTarget, errors;
            public bool deepProfiling, cleanup;
            public List<Row> rows = new List<Row>();
            public List<MemoryPoint> memory = new List<MemoryPoint>();
        }
        // 逐帧只写值类型缓冲；最多90秒/段，容量预留足够本机目标帧率，不每帧构造CSV字符串。
        private struct Sample
        {
            public double ms, gpu, cpu, main, render;
            public long gc, allocated, mono;
            public int pieces, batches, draws, pass;
        }

        /// <summary>chapter=0覆盖六章，否则只测指定章；秒数为测量参数，不是玩法参数。非法/并发请求在改变状态前拒绝。</summary>
        public static void Begin(string label, int chapter, int seconds)
        {
            if (!Application.isPlaying || LastResult == "运行中") { throw new InvalidOperationException("需要空闲Play"); }
            if ((label != "baseline" && label != "repeat" && label != "background-off" && label != "hud-off" && label != "original-sync")
                || chapter < 0 || chapter > 6 || seconds < 10 || seconds > 90)
            {
                throw new ArgumentException("不支持的诊断参数");
            }
            if ((label == "background-off" || label == "hud-off") && chapter == 0) { throw new ArgumentException("归因对照必须指定单章"); }
            LastResult = "运行中";
            Progress = "等待首页";
            RunAsync(label, chapter, seconds);
        }

        /// <summary>开始入口先同步校验；长任务在帧间等待，异常与清理结果写入状态，不让async void的前置异常逃出MCP。</summary>
        private static async void RunAsync(string label, int chapter, int seconds)
        {
            string dir = "Temp/DeviceProfile/" + label;
            Directory.CreateDirectory(dir);
            var report = new Report { label = label, cpu = SystemInfo.processorType, gpu = SystemInfo.graphicsDeviceName,
                graphicsApi = SystemInfo.graphicsDeviceVersion, unity = Application.unityVersion,
                systemMemoryMB = SystemInfo.systemMemorySize, graphicsMemoryMB = SystemInfo.graphicsMemorySize,
                mode = RevResBootstrap.UseABInEditor ? "AB" : "Direct", originalVsync = QualitySettings.vSyncCount,
                originalTarget = Application.targetFrameRate, deepProfiling = ProfilerDriver.deepProfiling };
            RevLogLevel logLevel = RevLog.MinLevel;
            LogType filter = Debug.unityLogger.filterLogType;
            bool profiling = ProfilerDriver.enabled;
            bool cpuArea = Profiler.GetAreaEnabled(ProfilerArea.CPU);
            bool uiArea = Profiler.GetAreaEnabled(ProfilerArea.UI);
            bool detailArea = Profiler.GetAreaEnabled(ProfilerArea.UIDetails);
            object owner = new object();
            GameFlow flow = null;
            IDisposable score = null;
            SpriteRenderer hiddenBackground = null;
            GameObject hiddenHud = null;
            Application.LogCallback errors = (message, stack, type) =>
            {
                if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert)
                {
                    report.errors++;
                    File.AppendAllText(dir + "/errors.txt", message + "\n" + stack + "\n");
                }
            };
            try
            {
                RevLog.MinLevel = RevLogLevel.Error;
                Debug.unityLogger.filterLogType = LogType.Error;
                Application.logMessageReceived += errors;
                if (ProfilerDriver.deepProfiling) { throw new InvalidOperationException("请先关闭Deep Profiling，避免混淆测量口径"); }
                ProfilerDriver.enabled = false;
                QualitySettings.vSyncCount = label == "original-sync" ? report.originalVsync : 0;
                Application.targetFrameRate = label == "original-sync" ? report.originalTarget : -1;
                flow = RevServices.GetRequired<GameFlow>();
                await GameSceneTransitionChecks.ReturnHomeAsync(flow);
                var battle = (BattleService)RevServices.GetRequired<IBattleService>();
                // 隔离Play的Bootstrap已经持有积分租约；不能嵌套申请，更不能释放别人的租约。
                if (!RevServices.GetRequired<StoryProgress>().IsIsolated) { score = battle.BeginScorePreview(); }
                report.memory.Add(Memory("home-before"));
                ScreenCapture.CaptureScreenshot(dir + "/home-before.png");
                if (!flow.OpenLevels() || !flow.EnterBattle(1)) { throw new InvalidOperationException(flow.LastError); }
                await GameSceneTransitionChecks.WaitForFlowAsync(flow);
                for (int c = chapter == 0 ? 1 : chapter; c <= (chapter == 0 ? 6 : chapter); c++)
                {
                    Progress = label + " 第" + c + "章准备";
                    if (!battle.LoadPerformanceScenario(c)) { throw new InvalidOperationException(battle.LastError); }
                    var presentation = UnityEngine.Object.FindObjectOfType<GamePresentation>();
                    await presentation.WaitUntilReadyAsync(GameScreen.Battle, null);
                    double deadline = Time.realtimeSinceStartupAsDouble + 20;
                    while (!battle.PreparationGranted)
                    {
                        if (Time.realtimeSinceStartupAsDouble > deadline) { throw new TimeoutException("准备时钟未推进"); }
                        // 章节剧情会锁住准备时钟；复用玩家翻页入口读完，不能直接改门禁或推进模拟来伪造准备完成。
                        if (flow.IsStoryPlaying) { flow.AdvanceStory(); }
                        await RevTask.Yield();
                    }
                    if (!battle.StartBattle()) { throw new InvalidOperationException(battle.LastError); }
                    // 只关闭当前场景背景Renderer做归因，保留战斗、UI和全部反馈；换场景时对象自然销毁。
                    if (label == "background-off")
                    {
                        var field = typeof(GamePresentation).GetField("backgroundRenderer", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                        var background = (SpriteRenderer)field.GetValue(presentation);
                        if (background == null) { throw new InvalidOperationException("背景Renderer不存在"); }
                        if (!background.enabled) { throw new InvalidOperationException("背景原本不可见，不能作为对照"); }
                        hiddenBackground = background;
                        background.enabled = false;
                    }
                    if (label == "hud-off")
                    {
                        var hud = RevUI.Get<RevGame.UI.BattleHudPanel>();
                        if (hud == null || !hud.gameObject.activeSelf) { throw new InvalidOperationException("HUD原本不可见，不能作为对照"); }
                        hiddenHud = hud.gameObject;
                        hiddenHud.SetActive(false);
                    }
                    await RevTask.Delay(2500);
                    battle.SustainPerformanceScenario();
                    var row = new Row { chapter = c, resolution = UnityStats.screenRes };
                    RevEvent.AddEventListener<PieceAttackInfo>(Events.PieceAttacked, info => row.attacks++, owner: owner);
                    var samples = new List<Sample>(20000);
                    var timing = new FrameTiming[1];
                    ulong previousTimestamp = 0;
                    var gc = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC Allocated In Frame", 1);
                    row.gcRecorderValid = gc.Valid;
                    int collections = GC.CollectionCount(0);
                    Progress = label + " 第" + c + "章采样";
                    row.sampleStartedUtc = DateTime.UtcNow.ToString("o");
                    double start = Time.realtimeSinceStartupAsDouble, previous = start, heal = start;
                    try
                    {
                        while (Time.realtimeSinceStartupAsDouble - start < seconds)
                        {
                            await RevTask.Yield();
                            double now = Time.realtimeSinceStartupAsDouble;
                            var sample = new Sample { ms = (now - previous) * 1000, gpu = -1, cpu = -1, main = -1, render = -1,
                                gc = gc.Valid && gc.Count > 0 ? gc.LastValue : -1,
                                batches = UnityStats.batches, draws = UnityStats.drawCalls, pass = UnityStats.setPassCalls,
                                allocated = Profiler.GetTotalAllocatedMemoryLong(), mono = Profiler.GetMonoUsedSizeLong() };
                            previous = now;
                            foreach (var piece in battle.PlayerPieces) { if (piece.IsAlive) { sample.pieces++; } }
                            foreach (var piece in battle.EnemyPieces) { if (piece.IsAlive) { sample.pieces++; } }
                            FrameTimingManager.CaptureFrameTimings();
                            if (FrameTimingManager.GetLatestTimings(1, timing) > 0 && timing[0].frameStartTimestamp != previousTimestamp)
                            {
                                previousTimestamp = timing[0].frameStartTimestamp;
                                sample.gpu = timing[0].gpuFrameTime > 0 ? timing[0].gpuFrameTime : -1;
                                sample.cpu = timing[0].cpuFrameTime;
                                sample.main = timing[0].cpuMainThreadFrameTime;
                                sample.render = timing[0].cpuRenderThreadFrameTime;
                            }
                            samples.Add(sample);
                            if (now >= heal) { battle.SustainPerformanceScenario(); heal = now + 1; }
                        }
                    }
                    finally { gc.Dispose(); RevEvent.RemoveAllByOwner(owner); }
                    row.sampleEndedUtc = DateTime.UtcNow.ToString("o");
                    row.gcCollections = GC.CollectionCount(0) - collections;
                    Summarize(row, samples, previous - start, dir);
                    report.rows.Add(row);
                    File.WriteAllText(dir + "/result.json", JsonUtility.ToJson(report, true));
                    if (row.minPieces < 40 || row.attacks < 40) { throw new InvalidOperationException("持续负载不足，第" + c + "章"); }
                    report.memory.Add(Memory("chapter-" + c));
                    // 用户要求后续不导出Profiler数据：只保留轻量计时/内存摘要与真实画面，不自动录制或保存Profile。
                    Progress = label + " 第" + c + "章画面取证";
                    // 连拍只在性能窗口结束之后执行，避免图片编码开销进入FPS分位数。
                    for (int shot = 0; shot < (c == 4 ? 12 : 1); shot++)
                    {
                        ScreenCapture.CaptureScreenshot(dir + "/chapter-" + c + "-" + shot.ToString("D2") + ".png");
                        await RevTask.Delay(120);
                    }
                }
                if (report.errors > 0) { throw new InvalidOperationException("采样期间新增Unity错误：" + report.errors); }
                LastResult = "通过：" + report.rows.Count + "段负载及计时采集完成；性能是否达标需读取分位数";
            }
            catch (Exception error) { report.error = error.ToString(); LastResult = "失败：" + error.Message; }
            finally
            {
                ProfilerDriver.enabled = false;
                RevEvent.RemoveAllByOwner(owner);
                // 即使返回场景失败，也恢复本次临时停绘对象；不让诊断设置留在用户画面里。
                if (hiddenBackground != null) { hiddenBackground.enabled = true; }
                if (hiddenHud != null) { hiddenHud.SetActive(true); }
                try
                {
                    if (flow != null)
                    {
                        await GameSceneTransitionChecks.ReturnHomeAsync(flow);
                        await RevTask.Delay(1000);
                        report.memory.Add(Memory("home-after"));
                        report.cleanup = flow.Screen == GameScreen.Home && !flow.IsTransitioning && RevLogic.Count == 0
                            && UnityEngine.Object.FindObjectOfType<BoardView>() == null
                            && RevResManager.GetRefCount(RevResPath.Atlases, "PieceFrames") == 0;
                        if (!report.cleanup) { throw new InvalidOperationException("退出后场景/域/图集引用未回收"); }
                    }
                }
                catch (Exception error) { report.error += "\n清理失败：" + error; LastResult = "失败：清理未完成"; }
                score?.Dispose();
                Application.logMessageReceived -= errors;
                RevLog.MinLevel = logLevel;
                Debug.unityLogger.filterLogType = filter;
                QualitySettings.vSyncCount = report.originalVsync;
                Application.targetFrameRate = report.originalTarget;
                Profiler.SetAreaEnabled(ProfilerArea.CPU, cpuArea);
                Profiler.SetAreaEnabled(ProfilerArea.UI, uiArea);
                Profiler.SetAreaEnabled(ProfilerArea.UIDetails, detailArea);
                ProfilerDriver.enabled = profiling;
                File.WriteAllText(dir + "/result.json", JsonUtility.ToJson(report, true));
                File.WriteAllText(dir + "/status.txt", LastResult);
                Progress = LastResult;
            }
        }

        /// <summary>无截图的稳定窗口结束后才排序/格式化；百分位按逐帧分布计算，不能拿平均FPS代替稳定性。</summary>
        private static void Summarize(Row row, List<Sample> samples, double seconds, string dir)
        {
            row.frames = samples.Count;
            row.fps = samples.Count / seconds;
            row.p50 = Percentile(samples.Select(s => s.ms), .5); row.p95 = Percentile(samples.Select(s => s.ms), .95);
            row.p99 = Percentile(samples.Select(s => s.ms), .99); row.max = samples.Max(s => s.ms);
            row.over12_5Percent = 100d * samples.Count(s => s.ms > 12.5) / samples.Count;
            row.over33Percent = 100d * samples.Count(s => s.ms > 33.333) / samples.Count;
            row.gpuSamples = samples.Count(s => s.gpu > 0);
            row.gpuP50 = Percentile(samples.Select(s => s.gpu), .5); row.gpuP95 = Percentile(samples.Select(s => s.gpu), .95);
            row.cpuP50 = Percentile(samples.Select(s => s.cpu), .5); row.mainP50 = Percentile(samples.Select(s => s.main), .5);
            row.renderP50 = Percentile(samples.Select(s => s.render), .5);
            row.minPieces = samples.Min(s => s.pieces); row.maxPieces = samples.Max(s => s.pieces);
            row.batches = samples.Average(s => s.batches); row.draws = samples.Average(s => s.draws); row.setPass = samples.Average(s => s.pass);
            var allocations = samples.Where(s => s.gc >= 0).ToArray();
            if (allocations.Length > 0) { row.gcBytesPerFrame = allocations.Average(s => s.gc); row.gcPeakBytes = allocations.Max(s => s.gc); }
            row.allocatedMiB = samples.Max(s => s.allocated) / 1048576d; row.monoMiB = samples.Max(s => s.mono) / 1048576d;
            using (var csv = new StreamWriter(dir + "/chapter-" + row.chapter + ".csv"))
            {
                csv.WriteLine("ms,pieces,batches,draws,setPass,gpuMs,cpuMs,mainMs,renderMs,gcBytes,allocated,mono");
                foreach (Sample s in samples)
                {
                    csv.WriteLine(string.Join(",", new double[] { s.ms, s.pieces, s.batches, s.draws, s.pass, s.gpu, s.cpu, s.main, s.render,
                        s.gc, s.allocated, s.mono }.Select(v => v.ToString("R", CultureInfo.InvariantCulture))));
                }
            }
        }
        private static double Percentile(IEnumerable<double> source, double p)
        {
            double[] values = source.Where(v => v >= 0).OrderBy(v => v).ToArray();
            return values.Length == 0 ? -1 : values[Math.Min(values.Length - 1, (int)(values.Length * p))];
        }
        /// <summary>重对象枚举仅放在采样窗口外；原生对象大小不是独立显存统计，也不能与进程内存相加。</summary>
        private static MemoryPoint Memory(string stage)
        {
            var point = new MemoryPoint { stage = stage, allocated = Profiler.GetTotalAllocatedMemoryLong(), reserved = Profiler.GetTotalReservedMemoryLong(),
                mono = Profiler.GetMonoUsedSizeLong(), monoReserved = Profiler.GetMonoHeapSizeLong(), logic = RevLogic.Count,
                bundles = AssetBundle.GetAllLoadedAssetBundles().Count(), atlasState = RevResManager.GetRefCount(RevResPath.Atlases, "PieceFrames").ToString() };
            using (var process = System.Diagnostics.Process.GetCurrentProcess())
            {
                // 此Unity Mono可能不支持进程内存查询；零值标为缺失，外部Windows计数器另行取证。
                point.processWorking = process.WorkingSet64 > 0 ? process.WorkingSet64 : -1;
                point.processPrivate = process.PrivateMemorySize64 > 0 ? process.PrivateMemorySize64 : -1;
            }
            foreach (Texture texture in Resources.FindObjectsOfTypeAll<Texture>()) { point.textures++; point.textureBytes += Profiler.GetRuntimeMemorySizeLong(texture); }
            foreach (AudioClip clip in Resources.FindObjectsOfTypeAll<AudioClip>()) { point.audioClips++; point.audioBytes += Profiler.GetRuntimeMemorySizeLong(clip); }
            point.materials = Resources.FindObjectsOfTypeAll<Material>().Length;
            point.gameObjects = Resources.FindObjectsOfTypeAll<GameObject>().Length;
            return point;
        }
        /// <summary>分别聚合主线程和渲染线程；包含子调用的时间不可相加，等待GPU/呈现也不等于CPU计算。</summary>
        private static string ReadMarkers()
        {
            var text = new System.Text.StringBuilder();
            for (int thread = 0; thread < 64; thread++)
            {
                var totals = new Dictionary<string, double>();
                int frames = 0;
                string threadName = "";
                for (int frame = ProfilerDriver.firstFrameIndex; frame <= ProfilerDriver.lastFrameIndex; frame++)
                {
                    using (var view = ProfilerDriver.GetRawFrameDataView(frame, thread))
                    {
                        if (!view.valid) { continue; }
                        threadName = view.threadName ?? "未命名线程";
                        if (thread != 0 && !threadName.Contains("Render")) { break; }
                        frames++;
                        for (int i = 0; i < view.sampleCount; i++)
                        {
                            // Unity的原始录制允许无名称标记；保留其时间，不让汇总器丢掉整场有效样本。
                            string name = view.GetSampleName(i) ?? "<unnamed>";
                            totals.TryGetValue(name, out double ms);
                            totals[name] = ms + view.GetSampleTimeMs(i);
                        }
                    }
                }
                if (frames == 0) { continue; }
                text.AppendLine("thread=" + threadName + "; frames=" + frames + "; inclusive ms/frame");
                foreach (var pair in totals.OrderByDescending(p => p.Value).Take(70)) { text.AppendLine(pair.Key + "=" + (pair.Value / frames).ToString("F5", CultureInfo.InvariantCulture)); }
            }
            return text.ToString();
        }
    }
}
