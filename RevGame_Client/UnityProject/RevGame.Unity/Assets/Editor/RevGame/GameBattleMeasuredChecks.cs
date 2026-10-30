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
    // Editor专用性能作业，由RevMCP固定入口启动；它主动维持固定输入并采集摘要，不参与正式玩家战斗。
    // 每份结果有唯一runId，只有完整段数/无错误/清理成功才能作为完成证据；高速桌面结果不能代表笔记本。
    public static class GameBattleMeasuredChecks
    {
        [Serializable]
        // 一段已完成采样的汇总结果（可序列化成JSON）。各字段口径如下；CPU/GPU与墙钟不相加。
        public sealed class Row
        {
            // Tick同步调用的独立Stopwatch摘要；毫秒/次与毫秒/整段分开，不能当成整帧或GPU耗时。
            public int modelTicks;
            public int maxSkillTileStarts;
            public double modelStepSeconds, tickTotalMs, tickMeanMs, tickMaxMs;
            // 真实事件后的两秒窗口包含落地、动画、GPU与后续帧；仅是相关窗口，不将GPU延迟计时冒充同帧因果。
            public int bombImpacts, regionWarnings, bombDamageEvents, regionDamageEvents;
            public double bombTriggerMs, regionTriggerMs, bombWindowMaxMs, regionWindowMaxMs, bombGpuMaxMs, regionGpuMaxMs;
            // 慢帧关联摘要来自同窗口预分配样本；父标记不可相加，最后值可能跨帧延迟，不自动宣称因果。
            public double drawCalls, dynamicBatches, instancedBatches, editorMs = -1, playerMs = -1, maxGcBytes;
            public long logHits, logMisses, scoreScans, scoreCacheHits;
            public List<SlowFrame> slowFrames = new List<SlowFrame>();
            // 一段完成的采样：帧时单位毫秒，fps=总帧/墙钟秒；-1代表计数器无有效样本而非零成本。
            // chapter为1～6，repeat从0计；frames为真实采样帧数，gpuSamples是新时间戳有效GPU帧数。
            // attacks为期间真实攻击事件数，min/maxPieces核对负载，防止空盘高FPS被当优化成功。
            public int chapter, repeat, frames, gpuSamples, attacks, minPieces, maxPieces;
            // 名单来自真实攻击/掉血事件；满盘必须每一棋都出手。视觉预算记录实际活跃对象峰值。
            public int uniqueAttackers, hits, rangedHits, floatingPeak, projectilePeak, meleePeak, minViews, maxViews;
            public long actualDamage;
            // seconds为墙钟采样秒，fps=frames/seconds；p50/p95/p99/maxMs为帧间隔毫秒。
            // over125/167/333Ratio为超过12.5/16.67/33.33ms的帧占比0～1，不是毫秒。
            public double seconds, fps, p50Ms, p95Ms, p99Ms, maxMs, over125Ratio, over167Ratio, over333Ratio;
            // 有效GPU/CPU线程帧时为毫秒，gcBytesPerFrame为每Editor整帧分配字节。
            // -1是没有有效样本，不能显示成0或声称零GC；主/渲染线程和GPU并行，不可简单相加。
            public double gpuP50Ms = -1, gpuP95Ms = -1, mainP95Ms = -1, renderP95Ms = -1, gcBytesPerFrame = -1;
            // batches/setPass为帧均绘制统计；hud/feedback/arrange为自定义标记帧均毫秒。
            // 父标记可能包含子标记，两个毫秒不是彼此独立额外开销。
            public double batches, setPass, hudMs = -1, feedbackMs = -1, arrangeMs = -1;
            // 采样末Unity总已分配/保留/Mono使用字节；含Editor和作业缓冲，不等同生产棋子缓存或显存。
            public long allocatedBytes, reservedBytes, monoBytes;
            // 射界命中/重算、试盘刷新/棋子复用累计计数的采样差值；固定盘不移动时试盘数可能为0。
            public long attackCacheHits, attackCacheMisses, snapshotCopies, snapshotPieceReuses;
            // 命中/(命中+重算)，范围0～1；这反映业务复用，不是处理器硬件缓存命中。
            public double attackCacheHitRatio;
            // 伤害文案缓存命中/新键格式化次数，取采样前后差；可按两数计算实际文案复用率。
            public long damageTextHits, damageTextMisses;
            // Unity Game实际渲染分辨率字符串；不同窗口不能冒充同输入对照。
            public string resolution;
            // 该段平均FPS和P95是否符合80/90帧预算。它只属于当前设备、当前段，不保证每帧或目标笔记本达标。
            public bool budget80, budget90;
        }
        [Serializable]
        public sealed class SlowFrame
        {
            public int frame;
            public double ms, gpuMs, mainMs, editorMs, playerMs;
            public long gcBytes;
        }
        [Serializable]
        // 整个作业请求/设备/状态与多个Row的容器。status只能与runId、完整请求段数、cleaned一起解释。
        public sealed class Result
        {
            // runId确定本次身份，不能读取旧result.json冒充完成；cleaned只在正式回首页成功后设置。
            // runId唯一，label只作人类标签；设备/API/Unity版本随结果存档，UTC起止用于机器证据定位。
            // 用户报告使用2026-10-30本地日期，不能把UTC标签误当另一台机或另一次作业。
            public string runId, label, status = "running", error, cpu, gpu, graphicsApi, unityVersion, startedUtc, endedUtc, folder;
            // ab记录是否真实AB，cleaned记录正式回首页后无棋盘，cancelled记录取消；三个条件各有含义。
            public bool ab, cleaned, cancelled;
            // 请求章节范围/重复数/每段秒；errors累计新增错误，返回包数/逻辑数/图集引用用于收尾核对。
            // 首页仍可保留大厅共享包，不能要求总AB数量为0再强制卸载。
            public int first, last, repeats, requestedSeconds, errors, returnBundles, returnLogic, returnPieceRefs;
            public int requestedPieces;
            // 仅GPU诊断：交替保持原魔法格面/替成内置Sprite采样，不作为正式画质优化结果。
            public bool boardProbe;
            public bool skillSpikes;
            // 只在一段完整Measure返回后追加；失败段不填伪造Row，最终数量要等于(终章-首章+1)×次数。
            public List<Row> chapters = new List<Row>();
        }
        // 逐帧原始记录采用值类型，只写预分配数组；稳定窗口内不用StringBuilder/CSV或截图。
        private struct Sample
        {
            // 计时窗口只写预分配值类型记录，避免采样工具逐帧格式化字符串成为新的GC热点。
            // 墙钟/GPU/主线程/渲染线程毫秒；GPU/线程缺样时-1，便于汇总时忽略而不把缺样算零。
            public double frame, gpu, main, render;
            // GC字节以及自定义标记纳秒原值；汇总时/1e6转毫秒，单位必须分清。
            public long gc, hud, feedback, arrange;
            public long editor, player;
            public bool bombWindow, regionWindow;
            public int skillTileStarts;
            // 本帧绘制统计与存活数，只取必要计数，不逐帧创建完整场景对象快照。
            public int batches, setPass, pieces;
            public int views, floating, projectiles, melee;
            public int drawCalls, dynamicBatches, instancedBatches;
        }
        // 当前Editor作业结果，下一次Begin替换。旧result.json只是证据，不是当前作业仍成功的标记。
        private static Result _result;
        // 协作式取消请求，由Guard在逐帧边界观察；不能直接stop代码而跳过finally还原设置。
        private static bool _cancel;
        // 只读当前作业JSON快照；查询本身可能有分配，因此不应高频轮询干扰性能窗口。
        public static string State()
        {
            return _result == null ? "{\"status\":\"idle\"}" : JsonUtility.ToJson(_result);
        }
        // 只标记取消，不声称收尾完成；必须继续等state直到清理状态明确。
        public static string Cancel()
        {
            _cancel = true;
            return "取消已请求，等待回首页清理";
        }
        /// <summary>600秒热态及十次往返可在目标笔记本执行；时长仅用于测量，不调整规则。</summary>
        public static string Begin(string label, int first, int last, int seconds, int repeats, int pieces = 48, bool boardProbe = false, bool skillSpikes = false)
        {
            if (!Application.isPlaying || EditorApplication.isPaused || _result?.status == "running")
            {
                throw new InvalidOperationException("需要未暂停Play且无运行压测");
            }
            if (!RevServices.GetRequired<StoryProgress>().IsIsolated)
            {
                throw new InvalidOperationException("需要隔离进度");
            }
            if (first < 1 || last > 6 || first > last || seconds < 10 || seconds > 600 || repeats < 1 || repeats > 10)
            {
                throw new ArgumentException("章节1至6，时长10至600秒，重复1至10");
            }
            if (pieces != 48 && pieces != 81)
            {
                throw new ArgumentException("压力输入只接受48或81棋");
            }
            string id = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            _result = new Result
            {
                runId = id,
                label = label,
                first = first,
                last = last,
                repeats = repeats,
                requestedSeconds = seconds,
                requestedPieces = pieces,
                boardProbe = boardProbe,
                skillSpikes = skillSpikes,
                cpu = SystemInfo.processorType,
                gpu = SystemInfo.graphicsDeviceName,
                graphicsApi = SystemInfo.graphicsDeviceType.ToString(),
                unityVersion = Application.unityVersion,
                ab = RevResBootstrap.UseABInEditor,
                startedUtc = DateTime.UtcNow.ToString("O"),
                folder = Path.GetFullPath("../../../.codex/work/perf-2026-10-30/" + id)
            };
            Directory.CreateDirectory(_result.folder);
            _cancel = false;
            Run();
            return State();
        }
        /// <summary>第四/五章ABAB诊断格面Shader成本；每段实际棋盘/弹幕仍81，不替生产材质或关闭背景。</summary>
        public static string BeginBoardProbe()
        {
            return Begin("board-shader-probe", 4, 5, 15, 4, 81, true);
        }
        // 逐帧看取消或Play退出，抛异常交给统一catch/finally，不把半段测量算完成。
        private static void Guard()
        {
            if (_cancel || !Application.isPlaying)
            {
                throw new OperationCanceledException("取消或Play退出");
            }
        }
        // 轮询真实业务就绪，最多60秒；剧情只通过正式推进入口翻完，accepted不等于加载完成。
        private static async RevTask Wait(Func<bool> ready, string reason)
        {
            float until = Time.realtimeSinceStartup + 60;
            while (!ready())
            {
                Guard();

                if (Time.realtimeSinceStartup > until)
                {
                    throw new TimeoutException(reason);
                }
                var flow = RevServices.GetRequired<GameFlow>();

                if (flow.IsStoryPlaying && !flow.IsTransitioning)
                {
                    flow.AdvanceStory();
                }
                await RevTask.Yield();
            }
        }
        // 流程：首页→正式切战斗→固定输入→预热→Measure→窗口外截图→正式回首页。
        // 先保存原同步/时间/日志设置，try里改变测试状态，任何失败都由finally还原与退订。
        private static async void Run()
        {
            var r = _result;
            var flow = RevServices.GetRequired<GameFlow>();
            var b = (BattleService)RevServices.GetRequired<IBattleService>();
            int vsync = QualitySettings.vSyncCount, target = Application.targetFrameRate;
            float scale = Time.timeScale;
            RevLogLevel log = RevLog.MinLevel;
            LogType filter = Debug.unityLogger.filterLogType;
            bool profiler = UnityEditorInternal.ProfilerDriver.enabled;
            Application.LogCallback logger = (message, stack, type) =>
            {
                if (type == LogType.Error || type == LogType.Exception)
                {
                    r.errors++;
                    r.error = message;
                }
            };
            Application.logMessageReceived += logger;
            try
            {
                QualitySettings.vSyncCount = 0;
                Application.targetFrameRate = -1;
                Time.timeScale = 1;
                RevLog.MinLevel = RevLogLevel.Error;
                Debug.unityLogger.filterLogType = LogType.Error;
                UnityEditorInternal.ProfilerDriver.enabled = false;
                await Wait(() => flow.Screen == GameScreen.Home && !flow.IsTransitioning && !flow.IsGameplayBlocked, "首页未就绪");
                b.UnlockAllChaptersForDevelopment();
                for (int repeat = 0; repeat < r.repeats; repeat++)
                {
                    for (int chapter = r.first; chapter <= r.last; chapter++)
                    {
                        Guard();
                        flow.OpenLevels();

                        if (!flow.EnterBattle(chapter))
                        {
                            throw new InvalidOperationException(flow.LastError);
                        }
                        await Wait(() => !flow.IsTransitioning && !flow.IsGameplayBlocked && b.Phase == GamePhase.Prepare, "准备未交付");

                        if (!b.LoadPerformanceScenario(chapter, r.requestedPieces))
                        {
                            throw new InvalidOperationException(b.LastError);
                        }
                        await Wait(() => !flow.IsGameplayBlocked && b.PreparationGranted && UnityEngine.Object.FindObjectOfType<BoardView>()?.IsReady == true, "固定输入未就绪");

                        if (!RevLogic.Get<BattleLogic>().BeginBattle())
                        {
                            throw new InvalidOperationException(b.LastError);
                        }
                        Material diagnostic = null;
                        BoardCellView[] probeCells = null;
                        Material normal = null;

                        if (r.boardProbe && repeat % 2 == 1)
                        {
                            // 只改变棋格采样Shader作归因，六章大背景和所有战斗Renderer仍保留。
                            // 新材质属本测试段，Measure失败也在finally还原引用并Destroy，不能泄露进正式战斗。
                            probeCells = UnityEngine.Object.FindObjectsOfType<BoardCellView>();
                            normal = UnityEngine.Object.FindObjectOfType<BoardView>().SurfaceMaterial;
                            diagnostic = new Material(Shader.Find("Sprites/Default")) { hideFlags = HideFlags.HideAndDontSave };
                            foreach (BoardCellView cell in probeCells)
                            {
                                cell.ApplySurface(diagnostic);
                            }
                        }

                        try
                        {
                            double until = Time.realtimeSinceStartupAsDouble + 2.5, heal = 0;
                            while (Time.realtimeSinceStartupAsDouble < until)
                            {
                                Guard();

                                if (Time.realtimeSinceStartupAsDouble >= heal)
                                {
                                    b.SustainPerformanceScenario();
                                    heal = Time.realtimeSinceStartupAsDouble + 1;
                                }
                                await RevTask.Yield();
                            }
                            r.chapters.Add(await Measure(chapter, repeat));
                            Write();
                            // 连拍独立于采样窗口，不将截图阻塞作为游戏帧时；画面保留全部动态效果。
                            for (int frame = 0; frame < 12; frame++)
                            {
                                ScreenCapture.CaptureScreenshot(Path.Combine(r.folder, "ch" + chapter + "-r" + repeat + "-" + frame.ToString("00") + ".png"));
                                await RevTask.Delay(120);
                            }
                        }
                        finally
                        {
                            if (probeCells != null)
                            {
                                foreach (BoardCellView cell in probeCells)
                                {
                                    if (cell != null)
                                    {
                                        cell.ApplySurface(normal);
                                    }
                                }
                            }
                            if (diagnostic != null)
                            {
                                UnityEngine.Object.Destroy(diagnostic);
                            }
                        }

                        if (!flow.ReturnHome())
                        {
                            throw new InvalidOperationException(flow.LastError);
                        }
                        await Wait(() => flow.Screen == GameScreen.Home && !flow.IsTransitioning, "返回首页超时");

                        if (UnityEngine.Object.FindObjectOfType<BoardView>() != null || RevResManager.GetRefCount("Atlases", "PieceFrames") != 0)
                        {
                            throw new InvalidOperationException("棋盘/图集引用未回收");
                        }
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
                        while (flow.IsTransitioning && Time.realtimeSinceStartup < until)
                        {
                            await RevTask.Yield();
                        }
                        flow.ReturnHome();
                        while ((flow.Screen != GameScreen.Home || flow.IsTransitioning) && Application.isPlaying && Time.realtimeSinceStartup < until)
                        {
                            await RevTask.Yield();
                        }
                    }

                    r.cleaned = Application.isPlaying && flow.Screen == GameScreen.Home && !flow.IsTransitioning && UnityEngine.Object.FindObjectOfType<BoardView>() == null;
                    foreach (var bundle in AssetBundle.GetAllLoadedAssetBundles())
                    {
                        r.returnBundles++;
                    }
                    r.returnLogic = RevLogic.Count;
                    r.returnPieceRefs = RevResManager.GetRefCount("Atlases", "PieceFrames");

                    if (!r.cleaned)
                    {
                        r.status = "failed";
                        r.error += "\n清理未完成";
                    }
                }
                catch (Exception e) { r.status = "failed"; r.error += "\n清理异常：" + e; }
                QualitySettings.vSyncCount = vsync;
                Application.targetFrameRate = target;
                Time.timeScale = scale;
                RevLog.MinLevel = log;
                Debug.unityLogger.filterLogType = filter;
                UnityEditorInternal.ProfilerDriver.enabled = profiler;
                Application.logMessageReceived -= logger;
                r.endedUtc = DateTime.UtcNow.ToString("O");
                Write();
            }
        }
        // 一段稳定窗口：预分配样本与FrameTiming数组，期间只记录数值；退出后才排序/统计/格式化JSON。
        // 容量=秒×1000+1，超出就报错，不能悄悄截断后继续声称采足请求时长。
        private static async RevTask<Row> Measure(int chapter, int repeat)
        {
            var b = (BattleService)RevServices.GetRequired<IBattleService>();
            var buffer = new Sample[_result.requestedSeconds * 1000 + 1];
            var timing = new FrameTiming[1];
            BoardView board = UnityEngine.Object.FindObjectOfType<BoardView>();

            if (board == null)
            {
                throw new InvalidOperationException("真实棋盘视图不存在");
            }
            // 只在窗口前分配集合，81身份先登记计数器，事件内不扩容、不拼字符串。
            var attackIds = new HashSet<int>();
            foreach (var piece in b.PlayerPieces)
            {
                attackIds.Add(piece.Id);
            }
            foreach (var piece in b.EnemyPieces)
            {
                attackIds.Add(piece.Id);
            }
            var acted = new Dictionary<int, bool>(81);
            foreach (int id in attackIds)
            {
                acted.Add(id, false);
            }
            int count = 0, attacks = 0, hits = 0, rangedHits = 0, errorStart = _result.errors;
            int bombImpacts = 0, regionWarnings = 0, bombDamage = 0, regionDamage = 0;
            double bombAt = -100, regionAt = -100, bombTriggerMs = 0, regionTriggerMs = 0;
            bool triggeredBomb = false, triggeredRegion = false;
            long damage = 0;
            object owner = new object();
            RevEvent.AddEventListener<EnergyBombInfo>(Events.EnergyBombImpact, info =>
            {
                bombImpacts++;
                bombAt = Time.realtimeSinceStartupAsDouble;
            }, owner: owner);
            RevEvent.AddEventListener<RegionPulseInfo>(Events.RegionPulseWarning, info =>
            {
                regionWarnings++;
                regionAt = Time.realtimeSinceStartupAsDouble;
            }, owner: owner);
            RevEvent.AddEventListener<PieceAttackInfo>(Events.PieceAttacked, info =>
            {
                attacks++;

                if (acted.ContainsKey(info.PieceId))
                {
                    acted[info.PieceId] = true;
                }
            }, owner: owner);
            RevEvent.AddEventListener<BattleHitInfo>(Events.BattleHit, info =>
            {
                hits++;
                damage += info.Damage;
                if (info.DamageKind == BattleDamageKind.EnergyBomb)
                {
                    bombDamage++;
                }
                if (info.DamageKind == BattleDamageKind.RegionPulse)
                {
                    regionDamage++;
                }

                if (info.SourceIsRanged && !info.FromSkill)
                {
                    rangedHits++;
                }
            }, owner: owner);
            var gc = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC Allocated In Frame", 1);
            var hud = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "RevGame.Hud.Refresh", 1);
            var feedback = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "RevGame.Feedback.Frame", 1);
            var arrange = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "RevGame.Feedback.Arrange", 1);
            var editor = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "EditorLoop", 1);
            var player = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "PlayerLoop", 1);
            double start = Time.realtimeSinceStartupAsDouble, previous = start, heal = start;
            BattleLogic.ResetTickMeasurements();
            ulong stamp = 0;
            // 保存测量前计数，结束取差值。把预热命中也算进窗口会虚高命中率。
            long hitsBefore = b.Board.AttackCacheHits, missesBefore = b.Board.AttackCacheMisses;
            long copiesBefore = b.Board.TacticalSnapshotCopies, reusesBefore = b.Board.TacticalPieceReuses;
            long textHitsBefore = GameTextConfig.DamageTextCacheHits, textMissesBefore = GameTextConfig.DamageTextCacheMisses;
            long logHits = b.AttackLogHits, logMisses = b.AttackLogMisses, scoreScans = b.ScorePopulationScans, scoreHits = b.ScoreMultiplierHits;
            try
            {
                while (Time.realtimeSinceStartupAsDouble - start < _result.requestedSeconds)
                {
                    await RevTask.Yield();
                    Guard();
                    double now = Time.realtimeSinceStartupAsDouble;

                    if (count >= buffer.Length)
                    {
                        throw new InvalidOperationException("预分配采样容量不足，拒绝截断");
                    }
                    var s = new Sample
                    {
                        frame = (now - previous) * 1000,
                        bombWindow = now - bombAt <= 2,
                        regionWindow = now - regionAt <= 4,
                        skillTileStarts = board.LastSkillTileStarts,
                        gpu = -1,
                        main = -1,
                        render = -1,
                        gc = gc.Valid ? gc.LastValue : -1,
                        hud = hud.Valid ? hud.LastValue : -1,
                        feedback = feedback.Valid ? feedback.LastValue : -1,
                        arrange = arrange.Valid ? arrange.LastValue : -1,
                        batches = UnityStats.batches,
                        setPass = UnityStats.setPassCalls,
                        views = board.PieceCount,
                        floating = board.ActiveFloatingTextCount,
                        projectiles = board.ActiveProjectileCount,
                        melee = board.ActiveMeleeCount,
                        drawCalls = UnityStats.drawCalls,
                        dynamicBatches = UnityStats.dynamicBatches,
                        instancedBatches = UnityStats.instancedBatches,
                        editor = editor.Valid ? editor.LastValue : -1,
                        player = player.Valid ? player.LastValue : -1
                    };
                    previous = now;
                    foreach (var p in b.PlayerPieces)
                    {
                        if (p.IsAlive)
                        {
                            s.pieces++;
                        }
                    }
                    foreach (var p in b.EnemyPieces)
                    {
                        if (p.IsAlive)
                        {
                            s.pieces++;
                        }
                    }
                    FrameTimingManager.CaptureFrameTimings();

                    // 只收新时间戳，避免驱动未更新时把同一GPU帧重复记几十次；GPU>0才是有效成本样本。
                    if (FrameTimingManager.GetLatestTimings(1, timing) > 0 && timing[0].frameStartTimestamp != stamp)
                    {
                        stamp = timing[0].frameStartTimestamp;

                        if (timing[0].gpuFrameTime > 0)
                        {
                            s.gpu = timing[0].gpuFrameTime;
                        }
                        s.main = timing[0].cpuMainThreadFrameTime;
                        s.render = timing[0].cpuRenderThreadFrameTime;
                    }
                    buffer[count++] = s;

                    // 先记录上一完整帧再发动作，下一帧包含真实触发成本；只执行一次，不因拒绝重放变更。
                    if (_result.skillSpikes && !triggeredBomb && now - start >= 2)
                    {
                        triggeredBomb = true;
                        long began = System.Diagnostics.Stopwatch.GetTimestamp();
                        var response = RevGM.Execute("RevGame/能量巨大");
                        bombTriggerMs = (System.Diagnostics.Stopwatch.GetTimestamp() - began) * 1000d / System.Diagnostics.Stopwatch.Frequency;
                        if (!response.Success)
                        {
                            throw new InvalidOperationException(response.Message);
                        }
                    }
                    if (_result.skillSpikes && !triggeredRegion && now - start >= 8)
                    {
                        triggeredRegion = true;
                        long began = System.Diagnostics.Stopwatch.GetTimestamp();
                        var response = RevGM.Execute("RevGame/区域波动");
                        regionTriggerMs = (System.Diagnostics.Stopwatch.GetTimestamp() - began) * 1000d / System.Diagnostics.Stopwatch.Frequency;
                        if (!response.Success)
                        {
                            throw new InvalidOperationException(response.Message);
                        }
                    }

                    if (now >= heal)
                    {
                        b.SustainPerformanceScenario();
                        heal = now + 1;
                    }
                }
            }
            // 先停止监听/Recorder，再进入汇总；任何异常也清理，避免下一场还累计上一场攻击。
            finally { RevEvent.RemoveAllByOwner(owner); gc.Dispose(); hud.Dispose(); feedback.Dispose(); arrange.Dispose(); editor.Dispose(); player.Dispose(); }
            var row = new Row { chapter = chapter, repeat = repeat, frames = count, seconds = previous - start, attacks = attacks, hits = hits, rangedHits = rangedHits, actualDamage = damage, minPieces = int.MaxValue, minViews = int.MaxValue, resolution = UnityStats.screenRes };
            row.modelStepSeconds = RevGame.Data.Balance.BattleSimulationStepSeconds;
            row.modelTicks = BattleLogic.MeasuredTickCount;
            row.tickTotalMs = BattleLogic.MeasuredTickTotalMs;
            row.tickMeanMs = row.modelTicks == 0 ? -1 : row.tickTotalMs / row.modelTicks;
            row.tickMaxMs = BattleLogic.MeasuredTickMaxMs;
            row.bombImpacts = bombImpacts;
            row.regionWarnings = regionWarnings;
            row.bombDamageEvents = bombDamage;
            row.regionDamageEvents = regionDamage;
            row.bombTriggerMs = bombTriggerMs;
            row.regionTriggerMs = regionTriggerMs;
            if (_result.skillSpikes && (bombImpacts < 1 || regionWarnings < 1 || bombDamage < 1 || regionDamage < 1))
            {
                throw new InvalidOperationException("专项必须完成真实炸弹落地/区域波动且两者均实际扣血");
            }
            foreach (bool didAttack in acted.Values)
            {
                if (didAttack)
                {
                    row.uniqueAttackers++;
                }
            }
            row.logHits = b.AttackLogHits - logHits;
            row.logMisses = b.AttackLogMisses - logMisses;
            row.scoreScans = b.ScorePopulationScans - scoreScans;
            row.scoreCacheHits = b.ScoreMultiplierHits - scoreHits;
            var times = new double[count];
            var gpuTimes = new List<double>(count);
            var mainTimes = new List<double>(count);
            var renderTimes = new List<double>(count);
            long gcSum = 0, hudSum = 0, feedbackSum = 0, arrangeSum = 0;
            bool gcValid = false, hudValid = false, feedbackValid = false, arrangeValid = false;
            long editorSum = 0, playerSum = 0;
            bool editorValid = false, playerValid = false;
            for (int i = 0; i < count; i++)
            {
                var s = buffer[i];
                times[i] = s.frame;
                row.maxSkillTileStarts = Math.Max(row.maxSkillTileStarts, s.skillTileStarts);
                if (s.skillTileStarts > 12)
                {
                    throw new InvalidOperationException("技能格面装配超过每帧12格预算");
                }
                if (s.bombWindow)
                {
                    row.bombWindowMaxMs = Math.Max(row.bombWindowMaxMs, s.frame);
                    row.bombGpuMaxMs = Math.Max(row.bombGpuMaxMs, s.gpu);
                }
                if (s.regionWindow)
                {
                    row.regionWindowMaxMs = Math.Max(row.regionWindowMaxMs, s.frame);
                    row.regionGpuMaxMs = Math.Max(row.regionGpuMaxMs, s.gpu);
                }
                row.minPieces = Math.Min(row.minPieces, s.pieces);
                row.maxPieces = Math.Max(row.maxPieces, s.pieces);
                row.batches += s.batches;
                row.setPass += s.setPass;
                row.drawCalls += s.drawCalls;
                row.dynamicBatches += s.dynamicBatches;
                row.instancedBatches += s.instancedBatches;

                if (s.editor >= 0)
                {
                    editorSum += s.editor;
                    editorValid = true;
                }
                if (s.player >= 0)
                {
                    playerSum += s.player;
                    playerValid = true;
                }
                row.maxGcBytes = Math.Max(row.maxGcBytes, s.gc);

                if (s.frame > 33.3333)
                {
                    var slow = new SlowFrame { frame = i, ms = s.frame, gpuMs = s.gpu, mainMs = s.main, gcBytes = s.gc, editorMs = s.editor < 0 ? -1 : s.editor / 1e6, playerMs = s.player < 0 ? -1 : s.player / 1e6 };
                    int index = 0;
                    while (index < row.slowFrames.Count && row.slowFrames[index].ms >= slow.ms)
                    {
                        index++;
                    }
                    if (index < 12)
                    {
                        row.slowFrames.Insert(index, slow);

                        if (row.slowFrames.Count > 12)
                        {
                            row.slowFrames.RemoveAt(12);
                        }
                    }
                }
                row.minViews = Math.Min(row.minViews, s.views);
                row.maxViews = Math.Max(row.maxViews, s.views);
                row.floatingPeak = Math.Max(row.floatingPeak, s.floating);
                row.projectilePeak = Math.Max(row.projectilePeak, s.projectiles);
                row.meleePeak = Math.Max(row.meleePeak, s.melee);

                if (s.frame > 12.5)
                {
                    row.over125Ratio++;
                }
                if (s.frame > 16.6667)
                {
                    row.over167Ratio++;
                }
                if (s.frame > 33.3333)
                {
                    row.over333Ratio++;
                }
                if (s.gpu > 0)
                {
                    gpuTimes.Add(s.gpu);
                }
                if (s.main > 0)
                {
                    mainTimes.Add(s.main);
                }
                if (s.render > 0)
                {
                    renderTimes.Add(s.render);
                }
                if (s.gc >= 0)
                {
                    gcSum += s.gc;
                    gcValid = true;
                }
                if (s.hud >= 0)
                {
                    hudSum += s.hud;
                    hudValid = true;
                }
                if (s.feedback >= 0)
                {
                    feedbackSum += s.feedback;
                    feedbackValid = true;
                }
                if (s.arrange >= 0)
                {
                    arrangeSum += s.arrange;
                    arrangeValid = true;
                }
            }
            // 只有测量结束后才排序求分位数，不让排序和临时列表制造采样期间的慢帧。
            Array.Sort(times);
            gpuTimes.Sort();
            mainTimes.Sort();
            renderTimes.Sort();
            row.fps = count / row.seconds;
            row.p50Ms = P(times, .5);
            row.p95Ms = P(times, .95);
            row.p99Ms = P(times, .99);
            row.maxMs = times[count - 1];
            row.gpuSamples = gpuTimes.Count;

            if (gpuTimes.Count > 0)
            {
                row.gpuP50Ms = P(gpuTimes, .5);
                row.gpuP95Ms = P(gpuTimes, .95);
            }
            if (mainTimes.Count > 0)
            {
                row.mainP95Ms = P(mainTimes, .95);
            }
            if (renderTimes.Count > 0)
            {
                row.renderP95Ms = P(renderTimes, .95);
            }
            row.gcBytesPerFrame = gcValid ? gcSum / (double)count : -1;
            row.hudMs = hudValid ? hudSum / (double)count / 1e6 : -1;
            row.feedbackMs = feedbackValid ? feedbackSum / (double)count / 1e6 : -1;
            row.arrangeMs = arrangeValid ? arrangeSum / (double)count / 1e6 : -1;
            row.batches /= count;
            row.setPass /= count;
            row.over125Ratio /= count;
            row.over167Ratio /= count;
            row.over333Ratio /= count;
            row.drawCalls /= count;
            row.dynamicBatches /= count;
            row.instancedBatches /= count;
            row.editorMs = editorValid ? editorSum / (double)count / 1e6 : -1;
            row.playerMs = playerValid ? playerSum / (double)count / 1e6 : -1;
            row.allocatedBytes = Profiler.GetTotalAllocatedMemoryLong();
            row.reservedBytes = Profiler.GetTotalReservedMemoryLong();
            row.monoBytes = Profiler.GetMonoUsedSizeLong();
            row.attackCacheHits = b.Board.AttackCacheHits - hitsBefore;
            row.attackCacheMisses = b.Board.AttackCacheMisses - missesBefore;
            row.attackCacheHitRatio = (row.attackCacheHits + row.attackCacheMisses) > 0 ? row.attackCacheHits / (double)(row.attackCacheHits + row.attackCacheMisses) : 0;
            row.snapshotCopies = b.Board.TacticalSnapshotCopies - copiesBefore;
            row.snapshotPieceReuses = b.Board.TacticalPieceReuses - reusesBefore;
            row.damageTextHits = GameTextConfig.DamageTextCacheHits - textHitsBefore;
            row.damageTextMisses = GameTextConfig.DamageTextCacheMisses - textMissesBefore;
            // 预算是平均FPS+P95联合检查；P99和最慢帧仍保留，不能拿budget=true掩盖慢帧。
            row.budget80 = row.fps >= 80 && row.p95Ms <= 12.5;
            row.budget90 = row.fps >= 90 && row.p95Ms <= 1000d / 90;

            if (row.minPieces < 40 || attacks < 40 || row.seconds < _result.requestedSeconds || _result.errors > errorStart)
            {
                throw new InvalidOperationException("负载/时长/错误门禁失败：章" + chapter);
            }
            if (_result.requestedPieces == 81 && (row.minPieces != 81 || row.maxPieces != 81 || row.minViews != 81 || row.maxViews != 81 || row.uniqueAttackers != 81 || rangedHits < 81 || damage <= 0))
            {
                throw new InvalidOperationException("满盘未满足81存活/81视图/81真实出手与扣血，拒绝空负载：章" + chapter);
            }
            if (row.floatingPeak > BoardView.MaxFloatingTexts || row.projectilePeak > BoardView.MaxProjectiles || row.meleePeak > BoardView.MaxMeleeEffects)
            {
                throw new InvalidOperationException("真实反馈峰值超过显示预算");
            }
            return row;
        }
        // 输入必须已经按升序排序。fraction=0.95代表95%位置，返回的是帧时，不是95%CPU利用率。
        private static double P(IList<double> sorted, double fraction)
        {
            return sorted[Math.Min(sorted.Count - 1, (int)((sorted.Count - 1) * fraction))];
        }
        // 只在窗口外将当前结果写到忽略证据目录。无Profiler导出/ZIP，生产资源加载不走此文件路径。
        private static void Write()
        {
            File.WriteAllText(Path.Combine(_result.folder, "result.json"), JsonUtility.ToJson(_result, true));
        }
    }
}
