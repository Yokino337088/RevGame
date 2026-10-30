// 真图形Play逐招验收。输入为高生命固定靶场，结果仅回答技能/音频/镜头/棋格表现，不回答正常难度胜率。
using System;
using System.Collections.Generic;
using System.IO;
using RevGame.Battle;
using RevGame.Core;
using RevGame.Data;
using RevGame.Flow;
using RevGame.Pieces;
using RevGame.Services;
using RevGame.View;
using Revolution;
using UnityEngine;

namespace RevGame.Editor
{
    public static class GameBossImpactPlayChecks
    {
        [Serializable]
        private sealed class CaseResult
        {
            public int chapter, skill, casts, expectedCasts, damageEvents, damage, maxAnimatedCells, maxFx;
            public int maxTileStarts;
            public string name, sound, screenshot;
            // 失败仍保留原断言，只允许换回合后继续测其它技能；不是把失败改成通过。
            public string failure;
            public List<string> observedBossClips = new List<string>();
            public float realSeconds;
            public bool passed, heard, sawWave, sawVisualJump, sawFx, sawShake, rootsStayed;
        }
        [Serializable]
        private sealed class Result
        {
            public string status = "idle", error, current, folder;
            public int first, last, fromSkill, toSkill, errors, audioErrors;
            public bool extended, cleaned;
            public List<CaseResult> cases = new List<CaseResult>();
            public List<string> assertions = new List<string>();
        }
        private static Result _result = new Result();
        private static bool _cancel;
        private static readonly object ObserverOwner = new object();
        public static string State()
        {
            return JsonUtility.ToJson(_result);
        }
        public static string Cancel()
        {
            _cancel = true;
            return "cancellation-requested";
        }
        /// <summary>first/last限定章节；技能区间按真实枚举校验并过滤章节，extended追加区间外的本章专属。请求不阻塞MCP轮询。</summary>
        public static string Begin(int first = 1, int last = 6, bool extended = true, int fromSkill = 0, int toSkill = 17)
        {
            if (!Application.isPlaying || !RevServices.GetRequired<StoryProgress>().IsIsolated
                || RevServices.GetRequired<GameFlow>().Screen != GameScreen.Home || _result.status == "running")
            {
                throw new InvalidOperationException("需要隔离首页且没有运行中的Boss表现测试");
            }
            if (first < 1 || last > 6 || first > last || fromSkill < 0 || toSkill >= Enum.GetValues(typeof(BossSkillKind)).Length || fromSkill > toSkill)
            {
                throw new ArgumentException("章节范围1~6、技能范围须属于真实枚举，起点不得大于终点");
            }
            _cancel = false;
            string folder = Path.GetFullPath("../../../.codex/work/boss-support-2026-10-29/impact-play/"
                + DateTime.Now.ToString("HHmmss") + "-ch" + first + "-" + last);
            Directory.CreateDirectory(folder);
            _result = new Result
            {
                status = "running",
                first = first,
                last = last,
                extended = extended,
                fromSkill = fromSkill,
                toSkill = toSkill,
                folder = folder
            };
            Run();
            return State();
        }
        private static void Check(bool ok, string message)
        {
            if (!ok)
            {
                throw new InvalidOperationException(_result.current + ": " + message);
            }
            _result.assertions.Add((_result.current ?? "setup") + ": " + message);
        }
        /// <summary>等待业务完成而非把GM accepted当完成；允许推进隔离故事，45秒超时必须报首个原因。</summary>
        private static async RevTask Wait(Func<bool> ready, string reason, BattleService battle = null)
        {
            float deadline = Time.realtimeSinceStartup + 45f;
            while (!ready())
            {
                if (_cancel || !Application.isPlaying)
                {
                    throw new OperationCanceledException("用户取消或Play退出");
                }
                if (Time.realtimeSinceStartup > deadline)
                {
                    throw new TimeoutException(reason);
                }
                var flow = RevServices.GetRequired<GameFlow>();

                if (flow.IsStoryPlaying && !flow.IsTransitioning)
                {
                    flow.AdvanceStory();
                }
                battle?.HoldBossImpactFixture();
                await RevTask.Yield();
            }
        }
        private static async void Run()
        {
            var flow = RevServices.GetRequired<GameFlow>();
            var battle = (BattleService)RevServices.GetRequired<IBattleService>();
            float oldScale = Time.timeScale;
            // FSM创建/切换诊断会输出大量带堆栈的Warning，足以跨过短音效播放窗口。
            // 仅压住测试期间普通日志，继续保留Error/Exception；finally恢复用户设置，不改变游戏声音与文字。
            RevLogLevel oldLog = RevLog.MinLevel;
            LogType oldFilter = Debug.unityLogger.filterLogType;
            bool previousInvincible = battle.PlayerInvincible;
            Application.LogCallback logger = (message, stack, type) =>
            {
                if (type == LogType.Error || type == LogType.Exception)
                {
                    _result.errors++;

                    if (string.IsNullOrEmpty(_result.error))
                    {
                        _result.error = message;
                    }
                }

            };
            Action<string, RevSoundErrorReason> audioError = (name, reason) =>
            {
                if (reason != RevSoundErrorReason.Duplicated)
                {
                    _result.audioErrors++;
                }
            };
            Application.logMessageReceived += logger;
            RevSound.Failed += audioError;
            try
            {
                Time.timeScale = 1f;
                RevLog.MinLevel = RevLogLevel.Error;
                Debug.unityLogger.filterLogType = LogType.Error;
                for (int chapter = _result.first; chapter <= _result.last; chapter++)
                {
                    _result.current = "chapter-" + chapter;
                    Check(RevGM.Execute("RevGame/进入Boss关卡 " + chapter).Success, "正式GM进入目标Boss关卡");
                    await Wait(() => !flow.IsGameplayBlocked && !flow.IsTransitioning && battle.Phase == GamePhase.Prepare, "章节入场未完成");
                    for (int index = _result.fromSkill; index <= _result.toSkill; index++)
                    {
                        if (!BossSkillCatalog.IsUnlocked((BossSkillKind)index, chapter) || (BossSkillKind)index == BossSkillKind.Enrage)
                        {
                            continue;
                        }
                        await RunCase(chapter, (BossSkillKind)index, battle, flow, index == _result.fromSkill);
                    }
                    if (_result.extended)
                    {
                        foreach (BossSkillKind kind in Enum.GetValues(typeof(BossSkillKind)))
                        {
                            if (!BossSkillCatalog.IsSpecial(kind) || !BossSkillCatalog.IsUnlocked(kind, chapter) || ((int)kind >= _result.fromSkill && (int)kind <= _result.toSkill))
                            {
                                continue;
                            }
                            await RunCase(chapter, kind, battle, flow, false);
                        }
                    }

                    battle.CloseBossImpactFixture();
                    Check(flow.ReturnHome(), "正式ReturnHome接受");
                    await Wait(() => flow.Screen == GameScreen.Home && !flow.IsTransitioning, "章节清理未返回首页");
                    Check(UnityEngine.Object.FindObjectsOfType<BossSkillFxView>().Length == 0, "Boss技能池对象离场清理");
                    Check(UnityEngine.Object.FindObjectsOfType<BoardCellView>().Length == 0, "棋格Tween随离场回收");
                }
                Check(_result.errors == 0, "本轮无新增Unity Error/Exception");
                Check(_result.audioErrors == 0, "本轮无音频加载或播放失败");
                foreach (CaseResult item in _result.cases)
                {
                    if (!item.passed)
                    {
                        throw new InvalidOperationException("逐招执行完成，但存在失败：" + item.name + " / " + item.failure);
                    }
                }
                _result.status = "passed";
            }
            catch (Exception error)
            {
                _result.status = _cancel ? "cancelled" : "failed";
                _result.error = error.ToString();
                Capture("failed-" + _result.cases.Count.ToString("000"));
            }
            finally
            {
                RevEvent.RemoveAllByOwner(ObserverOwner);
                battle.CloseBossImpactFixture();
                battle.SetPlayerInvincible(previousInvincible);
                try
                {
                    if (flow.Screen != GameScreen.Home || flow.IsTransitioning)
                    {
                        flow.ReturnHome();
                        float deadline = Time.realtimeSinceStartup + 45f;
                        while ((flow.Screen != GameScreen.Home || flow.IsTransitioning) && Time.realtimeSinceStartup < deadline && Application.isPlaying)
                        {
                            await RevTask.Yield();
                        }
                    }

                    _result.cleaned = flow.Screen == GameScreen.Home && !flow.IsTransitioning
                        && UnityEngine.Object.FindObjectsOfType<BossSkillFxView>().Length == 0;

                    if (!_result.cleaned)
                    {
                        _result.status = "failed";
                        _result.error += "\n清理未完成";
                    }
                }
                catch (Exception cleanupError) { _result.status = "failed"; _result.error += "\n" + cleanupError; }
                Time.timeScale = oldScale;
                RevLog.MinLevel = oldLog;
                Debug.unityLogger.filterLogType = oldFilter;
                RevSound.Failed -= audioError;
                Application.logMessageReceived -= logger;
                Write();
            }
        }
        /// <summary>每招都复位规则场景，使用正式GM施法，等待完整段数与真实伤害；护盾/伤害/技能数均不在结果端伪造。</summary>
        private static async RevTask RunCase(int chapter, BossSkillKind kind, BattleService battle, GameFlow flow, bool firstCase)
        {
            _result.current = "ch" + chapter + "/" + kind;

            if (battle.Phase != GamePhase.Prepare)
            {
                Check(battle.StartRound(chapter, Balance.BossRoundIndex), "通过正式StartRound重建本轮");
            }
            Check(battle.PrepareBossImpactFixture(chapter), "建立高生命固定输入");
            await Wait(() => !flow.IsGameplayBlocked && !flow.IsTransitioning && UnityEngine.Object.FindObjectOfType<BoardView>()?.IsReady == true,
                "本招准备视图/章节材质尚未就绪", battle);
            await Wait(() => battle.PreparationGranted || !battle.VictoryAssistEnabled, "本招准备增益尚未完成", battle);

            if (kind == BossSkillKind.SoulDrain)
            {
                Check(RevGM.Execute("RevGame/Boss压血").Success, "汲取需要的Boss缺血前置");
            }
            if (kind == BossSkillKind.CrossLaser)
            {
                AlignCross(battle);
            }
            Check(RevLogic.Get<BattleLogic>().BeginBattle(), "正式Logic开战入口");
            battle.HoldBossImpactFixture();
            var presentation = UnityEngine.Object.FindObjectOfType<GamePresentation>();
            var board = UnityEngine.Object.FindObjectOfType<BoardView>();
            BoardCellView[] cells = UnityEngine.Object.FindObjectsOfType<BoardCellView>();
            var origins = new Vector3[cells.Length];
            for (int i = 0; i < cells.Length; i++)
            {
                origins[i] = cells[i].transform.position;
            }
            Check(cells.Length == Consts.BoardSize * Consts.BoardSize, "完整81格前置");
            var current = new CaseResult
            {
                chapter = chapter,
                skill = (int)kind,
                name = kind.ToString(),
                sound = GameAudio.GetBossSkillSoundName(kind),
                rootsStayed = true
            };
            _result.cases.Add(current);
            BossSkillDef def = BossSkillCatalog.Get(kind);
            current.expectedCasts = kind == BossSkillKind.BulletRing || kind == BossSkillKind.SoulDrain || kind == BossSkillKind.Summon ? 1 : def.Count;
            int initialFeedback = presentation.BossHitFeedbackCount;
            float firstCastAt = -1f;
            RevEvent.AddEventListener<BossSkillInfo>(Events.BossSkillCast, info =>
            {
                if (info.Kind != kind)
                {
                    return;
                }
                current.casts++;

                if (firstCastAt < 0f)
                {
                    firstCastAt = Time.realtimeSinceStartup;
                }
            }, owner: ObserverOwner);
            RevEvent.AddEventListener<BattleHitInfo>(Events.BattleHit, hit =>
            {
                if (hit.DamageKind != (BattleDamageKind)((int)BattleDamageKind.BossCleave + (int)kind) || hit.TargetSide != GameSide.Player)
                {
                    return;
                }
                current.damageEvents++;
                current.damage += hit.Damage;
            }, owner: ObserverOwner);
            try
            {
                int beforeErrors = _result.audioErrors;
                Check(RevGM.Execute("RevGame/Boss技能 " + (int)kind).Success, "实际GM进入指定技能状态");
                float begin = Time.realtimeSinceStartup;
                float simulateUntil = battle.BattleElapsed + def.Windup + Mathf.Max(0, current.expectedCasts - 1) * def.Interval
                    + Mathf.Max(1.4f, def.Duration) + Balance.BossRecoverSeconds;
                int frames = 0;
                float nextFrameAt = 0f;
                bool movie = true; // 动态技能逐招留连拍，不能用一张截图代替动画证据。
                while (battle.BattleElapsed < simulateUntil || battle.BossState != BossAIState.Idle)
                {
                    if (_cancel || !Application.isPlaying)
                    {
                        throw new OperationCanceledException("用户取消或Play退出");
                    }
                    if (Time.realtimeSinceStartup - begin > 45f)
                    {
                        throw new TimeoutException("技能模拟计时或收招超时");
                    }
                    battle.HoldBossImpactFixture();
                    current.sawFx |= board.ActiveBossFxCount > 0;
                    current.maxFx = Mathf.Max(current.maxFx, board.ActiveBossFxCount);
                    current.maxTileStarts = Mathf.Max(current.maxTileStarts, board.LastSkillTileStarts);
                    int animated = 0;
                    for (int i = 0; i < cells.Length; i++)
                    {
                        if (cells[i].HasBossSkillAnimation)
                        {
                            animated++;
                        }
                        current.rootsStayed &= (cells[i].transform.position - origins[i]).sqrMagnitude < .000001f;
                        Transform wave = cells[i].transform.Find("VictorySurface");
                        current.sawVisualJump |= wave != null && Mathf.Abs(wave.localPosition.y) > .04f;
                    }
                    current.sawWave |= animated > 0;
                    current.maxAnimatedCells = Mathf.Max(current.maxAnimatedCells, animated);
                    current.sawShake |= presentation.ImpactAmplitude > 0f && presentation.BossHitFeedbackCount > initialFeedback;
                    current.heard |= Heard(current.sound, current.observedBossClips);

                    if (firstCastAt >= 0f && Time.realtimeSinceStartup - firstCastAt >= .12f)
                    {
                        if (string.IsNullOrEmpty(current.screenshot))
                        {
                            current.screenshot = Capture("ch" + chapter + "-" + kind + "-hit");
                        }
                        if (movie && frames < 12 && Time.realtimeSinceStartup >= nextFrameAt)
                        {
                            Capture("ch" + chapter + "-movie-" + kind + "-" + frames.ToString("00"));
                            frames++;
                            nextFrameAt = Time.realtimeSinceStartup + .12f;
                        }
                    }

                    await RevTask.Yield();
                }
                current.realSeconds = Time.realtimeSinceStartup - begin;
                Check(current.casts == current.expectedCasts, "真实施放段数=" + current.casts);
                Check(current.sawFx && current.sawWave && current.sawVisualJump, "技能主体/格面Shader/可见双跳实际出现");
                Check(current.rootsStayed, "全部棋格根位置保持不变，动画不移动Collider");
                Check(current.heard && _result.audioErrors == beforeErrors, "实际AudioSource播放=" + current.sound);
                bool damaging = kind != BossSkillKind.Summon && kind != BossSkillKind.Enrage;
                Check(!damaging || current.damageEvents > 0, "伤害技能必须观察到真实玩家扣血事件=" + current.damageEvents);
                Check(!damaging || current.sawShake, "真实Boss技能命中后观察到相机震动");
                Check(damaging || presentation.BossHitFeedbackCount == initialFeedback, "无伤害强化/召唤不伪造玩家受击震动");
                Check(current.maxFx <= 8, "Boss特效分组预算未超过8");
                Check(current.maxTileStarts <= 12, "技能格面分帧装配每帧最多12格");
                current.passed = true;
                Write();
            }
            catch (InvalidOperationException error)
            {
                current.failure = error.Message;
                Write();
            }
            finally { RevEvent.RemoveAllByOwner(ObserverOwner); }
        }
        private static void AlignCross(BattleService battle)
        {
            Piece boss = null, player = null;
            foreach (Piece piece in battle.EnemyPieces)
            {
                if (piece.IsAlive && piece.IsKing)
                {
                    boss = piece;
                    break;
                }
            }
            foreach (Piece piece in battle.PlayerPieces)
            {
                if (piece.IsAlive && piece.Size == 1)
                {
                    player = piece;
                    break;
                }
            }
            Check(boss != null && player != null, "十字线靶场包含双方");
            for (int y = 0; y < Consts.BoardSize; y++)
            {
                var target = new BoardPos(boss.Pos.X + boss.Size / 2, y);

                if (battle.Board.GetPiece(target) != null)
                {
                    continue;
                }
                Check(battle.HandleBoardClick(player.Pos) && battle.HandleBoardClick(target), "通过玩家共用点击入口把目标移至十字线上");
                return;
            }
            throw new InvalidOperationException("十字线无可放置空格");
        }
        private static bool Heard(string clipName, List<string> observed)
        {
            foreach (AudioSource source in UnityEngine.Object.FindObjectsOfType<AudioSource>())
            {
                if (source.clip != null && source.clip.name.StartsWith("Boss_", StringComparison.Ordinal))
                {
                    string entry = source.clip.name + "/playing=" + source.isPlaying + "/seconds=" + source.clip.length.ToString("F3");
                    if (observed.Count < 24 && !observed.Contains(entry))
                    {
                        observed.Add(entry);
                    }
                }
                if (source.isPlaying && source.clip != null && source.clip.name == clipName)
                {
                    return true;
                }
            }
            return false;
        }
        private static string Capture(string name)
        {
            string path = Path.Combine(_result.folder, name + ".png");
            ScreenCapture.CaptureScreenshot(path);
            return path;
        }
        private static void Write()
        {
            File.WriteAllText(Path.Combine(_result.folder, "result.json"), JsonUtility.ToJson(_result, true));
        }
    }
}
