using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RevGame.Core;
using RevGame.Flow;
using RevGame.Services;
using RevGame.View;
using Revolution;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.Profiling;

namespace RevGame.Editor
{
    /// <summary>六章同输入图形Play压测。夹具维持48棋持续攻击，采样不包含读条/截图/Profiler分析自身；所有设置在finally恢复。</summary>
    public static class GameBattleStressChecks
    {
        public static string LastResult { get; private set; } = "未运行";
        [Serializable] public sealed class Row
        {
            public int chapter, frames, minPieces, maxPieces, attacks;
            public double fps, p50Ms, p95Ms, p99Ms, batches, drawCalls, setPass, dynamicBatches, instancedBatches, allocatedMB, reservedMB, monoMB;
            public string resolution;
        }
        [Serializable] public sealed class Report
        {
            public string label, device, cpu, gpu, mode, error;
            public int originalVsync, originalTarget, homeBundles, battleBundles, returnBundles;
            public List<Row> chapters = new List<Row>();
            public string[] returnBundleNames;
        }

        /// <summary>固定标签避免覆盖其它文件；仅在真实Play首页发起，积分租约隔离玩家存档。</summary>
        public static async void Begin(string label)
        {
            if (!Application.isPlaying || LastResult == "运行中") { return; }
            if (label != "before" && label != "after") { throw new ArgumentException("只接受before/after"); }
            LastResult = "运行中";
            string dir = "Temp/BattleStress/" + label;
            Directory.CreateDirectory(dir);
            var report = new Report { label = label, cpu = SystemInfo.processorType, gpu = SystemInfo.graphicsDeviceName,
                device = SystemInfo.operatingSystem, mode = RevResBootstrap.UseABInEditor ? "AB" : "Direct",
                originalVsync = QualitySettings.vSyncCount, originalTarget = Application.targetFrameRate };
            GameFlow flow = null;
            IDisposable score = null;
            bool wasProfiler = ProfilerDriver.enabled;
            object owner = new object();
            try
            {
                flow = RevServices.GetRequired<GameFlow>();
                await GameSceneTransitionChecks.ReturnHomeAsync(flow);
                report.homeBundles = AssetBundle.GetAllLoadedAssetBundles().Count();
                var battle = (BattleService)RevServices.GetRequired<IBattleService>();
                score = battle.BeginScorePreview();
                // 解开同步上限只用于测量吞吐；退出恢复，不修改玩家项目质量设置。
                QualitySettings.vSyncCount = 0;
                Application.targetFrameRate = -1;
                if (!flow.OpenLevels() || !flow.EnterBattle(1)) { throw new InvalidOperationException(flow.LastError); }
                await GameSceneTransitionChecks.WaitForFlowAsync(flow);
                for (int chapter = 1; chapter <= 6; chapter++)
                {
                    ProfilerDriver.enabled = false;
                    if (!battle.LoadPerformanceScenario(chapter)) { throw new InvalidOperationException(battle.LastError); }
                    await UnityEngine.Object.FindObjectOfType<GamePresentation>().WaitUntilReadyAsync(GameScreen.Battle, null);
                    await RevTask.Delay(3500);
                    if (!battle.StartBattle()) { throw new InvalidOperationException(battle.LastError); }
                    await RevTask.Delay(2000);
                    var row = new Row { chapter = chapter, minPieces = int.MaxValue, resolution = UnityStats.screenRes };
                    RevEvent.AddEventListener<PieceAttackInfo>(Events.PieceAttacked, info => row.attacks++, owner: owner);
                    var frames = new List<double>(4096);
                    var csv = new System.Text.StringBuilder("frame,ms,pieces,batches,drawCalls,setPass,dynamicBatches,allocatedBytes\n");
                    double start = Time.realtimeSinceStartupAsDouble;
                    double previous = start;
                    double healAt = start;
                    while (Time.realtimeSinceStartupAsDouble - start < 12.0)
                    {
                        await RevTask.Yield();
                        double now = Time.realtimeSinceStartupAsDouble;
                        double ms = (now - previous) * 1000.0;
                        previous = now;
                        int pieces = 0;
                        foreach (var piece in battle.PlayerPieces) { if (piece.IsAlive) { pieces++; } }
                        foreach (var piece in battle.EnemyPieces) { if (piece.IsAlive) { pieces++; } }
                        frames.Add(ms); row.minPieces = Math.Min(row.minPieces, pieces); row.maxPieces = Math.Max(row.maxPieces, pieces);
                        row.batches += UnityStats.batches; row.drawCalls += UnityStats.drawCalls; row.setPass += UnityStats.setPassCalls;
                        row.dynamicBatches += UnityStats.dynamicBatches; row.instancedBatches += UnityStats.instancedBatches;
                        long allocated = Profiler.GetTotalAllocatedMemoryLong();
                        row.allocatedMB = Math.Max(row.allocatedMB, allocated / 1048576.0);
                        row.reservedMB = Math.Max(row.reservedMB, Profiler.GetTotalReservedMemoryLong() / 1048576.0);
                        row.monoMB = Math.Max(row.monoMB, Profiler.GetMonoUsedSizeLong() / 1048576.0);
                        csv.Append(frames.Count).Append(',').Append(ms.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)).Append(',').Append(pieces).Append(',')
                            .Append(UnityStats.batches).Append(',').Append(UnityStats.drawCalls).Append(',').Append(UnityStats.setPassCalls).Append(',').Append(UnityStats.dynamicBatches).Append(',').Append(allocated).Append('\n');
                        if (now >= healAt) { battle.SustainPerformanceScenario(); healAt = now + 1.0; }
                    }
                    RevEvent.RemoveAllByOwner(owner);
                    row.frames = frames.Count; row.fps = frames.Count / (previous - start);
                    frames.Sort(); row.p50Ms = Percentile(frames, .5); row.p95Ms = Percentile(frames, .95); row.p99Ms = Percentile(frames, .99);
                    row.batches /= row.frames; row.drawCalls /= row.frames; row.setPass /= row.frames; row.dynamicBatches /= row.frames; row.instancedBatches /= row.frames;
                    if (row.minPieces < 40 || row.attacks < 40) { throw new InvalidOperationException("持续负载不足：章节" + chapter + "，棋子" + row.minPieces + "，攻击" + row.attacks); }
                    report.chapters.Add(row);
                    File.WriteAllText(dir + "/chapter-" + chapter + ".csv", csv.ToString());
                    ScreenCapture.CaptureScreenshot(dir + "/chapter-" + chapter + ".png");
                    // 单独录制短片，避免Profiler/UI刷新成本污染上面的正常帧率统计。
                    ProfilerDriver.ClearAllFrames();
                    Profiler.SetAreaEnabled(ProfilerArea.CPU, true);
                    Profiler.SetAreaEnabled(ProfilerArea.UI, false);
                    Profiler.SetAreaEnabled(ProfilerArea.UIDetails, false);
                    ProfilerDriver.enabled = true;
                    await RevTask.Delay(1800);
                    // 编辑器偶发没有开始录制；有界补采，不能把4字节空raw当作性能证据。
                    float captureDeadline = Time.realtimeSinceStartup + 5f;
                    while (ProfilerDriver.firstFrameIndex < 0 && Time.realtimeSinceStartup < captureDeadline)
                    {
                        ProfilerDriver.enabled = true;
                        await RevTask.Delay(250);
                    }
                    ProfilerDriver.enabled = false;
                    if (ProfilerDriver.firstFrameIndex < 0) { throw new InvalidOperationException("Profiler未生成有效帧：章节" + chapter); }
                    ProfilerDriver.SaveProfile(dir + "/chapter-" + chapter + ".raw");
                    File.WriteAllText(dir + "/markers-" + chapter + ".txt", ReadMarkers());
                    File.WriteAllText(dir + "/result.json", JsonUtility.ToJson(report, true));
                }
                report.battleBundles = AssetBundle.GetAllLoadedAssetBundles().Count();
                await GameSceneTransitionChecks.ReturnHomeAsync(flow);
                await RevTask.Delay(500);
                report.returnBundles = AssetBundle.GetAllLoadedAssetBundles().Count();
                report.returnBundleNames = AssetBundle.GetAllLoadedAssetBundles().Select(b => b.name).ToArray();
                if (report.returnBundleNames.Contains("battle_feedback") || report.returnBundleNames.Contains("battle_units") || report.returnBundleNames.Contains("battle_board"))
                {
                    throw new InvalidOperationException("六章连续战斗退出后仍残留战斗专用AB");
                }
                LastResult = "通过：六章48棋持续负载，报告=" + dir + "/result.json";
            }
            catch (Exception error) { report.error = error.ToString(); LastResult = "失败：" + error; Debug.LogException(error); }
            finally
            {
                ProfilerDriver.enabled = wasProfiler;
                RevEvent.RemoveAllByOwner(owner);
                QualitySettings.vSyncCount = report.originalVsync; Application.targetFrameRate = report.originalTarget;
                if (flow != null)
                {
                    try { await GameSceneTransitionChecks.ReturnHomeAsync(flow); }
                    catch (Exception error) { report.error += "\n清理失败：" + error; LastResult += "\n清理失败：" + error; }
                }
                score?.Dispose();
                File.WriteAllText(dir + "/result.json", JsonUtility.ToJson(report, true));
                File.WriteAllText(dir + "/status.txt", LastResult);
            }
        }

        private static double Percentile(List<double> values, double p) { return values[Math.Min(values.Count - 1, (int)(values.Count * p))]; }

        /// <summary>第四章同场交替启停积分子Canvas，检查合批收益；只在独立采样中录Profiler，结果不混入六章FPS。</summary>
        public static async void InspectHud()
        {
            if (!Application.isPlaying || LastResult == "运行中") { return; }
            LastResult = "运行中";
            string dir = "Temp/BattleStress/hud-inspection";
            Directory.CreateDirectory(dir);
            var flow = RevServices.GetRequired<GameFlow>();
            var battle = (BattleService)RevServices.GetRequired<IBattleService>();
            IDisposable score = battle.BeginScorePreview();
            bool profiler = ProfilerDriver.enabled;
            int vsync = QualitySettings.vSyncCount;
            try
            {
                await GameSceneTransitionChecks.ReturnHomeAsync(flow);
                QualitySettings.vSyncCount = 0;
                if (!flow.OpenLevels() || !flow.EnterBattle(1)) { throw new InvalidOperationException(flow.LastError); }
                await GameSceneTransitionChecks.WaitForFlowAsync(flow);
                if (!battle.LoadPerformanceScenario(4)) { throw new InvalidOperationException(battle.LastError); }
                await UnityEngine.Object.FindObjectOfType<GamePresentation>().WaitUntilReadyAsync(GameScreen.Battle, null);
                float deadline = Time.realtimeSinceStartup + 10f;
                while (!battle.PreparationGranted)
                {
                    if (Time.realtimeSinceStartup > deadline) { throw new TimeoutException("准备时间未推进"); }
                    await RevTask.Yield();
                }
                if (!battle.StartBattle()) { throw new InvalidOperationException(battle.LastError); }
                var hud = UnityEngine.Object.FindObjectOfType<RevGame.UI.BattleHudPanel>();
                // 正式代码不采用无收益子Canvas；此处只在诊断场景临时建立，随HUD销毁。
                var scoreField = typeof(RevGame.UI.BattleHudPanel).GetField("scoreFrame", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                var scoreFrame = (RectTransform)scoreField.GetValue(hud);
                Canvas child = scoreFrame.GetComponent<Canvas>();
                if (child == null) { child = scoreFrame.gameObject.AddComponent<Canvas>(); }
                for (int segment = 0; segment < 4; segment++)
                {
                    // 交替顺序减弱编辑器随时间升温的偏差；保留相同交战、积分与动画，不能关特效来刷帧。
                    child.enabled = segment % 2 == 1;
                    ProfilerDriver.enabled = false;
                    await RevTask.Delay(500);
                    battle.SustainPerformanceScenario();
                    ProfilerDriver.ClearAllFrames();
                    Profiler.SetAreaEnabled(ProfilerArea.UI, false);
                    Profiler.SetAreaEnabled(ProfilerArea.UIDetails, false);
                    Profiler.SetAreaEnabled(ProfilerArea.CPU, true);
                    ProfilerDriver.enabled = true;
                    await RevTask.Delay(2500);
                    ProfilerDriver.enabled = false;
                    string name = segment + (child.enabled ? "-canvas-on" : "-canvas-off");
                    if (ProfilerDriver.firstFrameIndex < 0) { throw new InvalidOperationException("Profiler未产生有效帧：" + name); }
                    ProfilerDriver.SaveProfile(dir + "/" + name + ".raw");
                    File.WriteAllText(dir + "/" + name + ".txt", ReadMarkers());
                }
                child.enabled = true;
                for (int frame = 0; frame < 12; frame++)
                {
                    ScreenCapture.CaptureScreenshot(dir + "/animation-" + frame.ToString("D2") + ".png");
                    await RevTask.Delay(120);
                }
                LastResult = "通过：第四章积分子Canvas交替对照与动态图取证";
            }
            catch (Exception error) { LastResult = "失败：" + error; Debug.LogException(error); }
            finally
            {
                ProfilerDriver.enabled = profiler;
                QualitySettings.vSyncCount = vsync;
                try { await GameSceneTransitionChecks.ReturnHomeAsync(flow); }
                catch (Exception error) { LastResult += "\n清理失败：" + error; }
                score.Dispose();
                File.WriteAllText(dir + "/status.txt", LastResult);
            }
        }

        /// <summary>按标记聚合包含子调用的时间；父子不可相加。原始raw保留供Profiler Timeline进一步定位。</summary>
        private static string ReadMarkers()
        {
            var totals = new Dictionary<string, double>();
            int count = 0;
            for (int frame = ProfilerDriver.firstFrameIndex; frame <= ProfilerDriver.lastFrameIndex; frame++)
            {
                using (var view = ProfilerDriver.GetRawFrameDataView(frame, 0))
                {
                    if (!view.valid) { continue; }
                    count++;
                    for (int i = 0; i < view.sampleCount; i++)
                    {
                        string name = view.GetSampleName(i);
                        totals.TryGetValue(name, out double ms); totals[name] = ms + view.GetSampleTimeMs(i);
                    }
                }
            }
            return "frames=" + count + "\n" + string.Join("\n", totals.OrderByDescending(p => p.Value).Take(60).Select(p => p.Key + "=" + (p.Value / Math.Max(1, count)).ToString("F4") + " ms/frame inclusive"));
        }
    }
}
