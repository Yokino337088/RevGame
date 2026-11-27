#if UNITY_EDITOR || (!UNITY_WEBGL && !UNITY_WEIXINMINIGAME && !UNITY_BYTEDANCE_MINIGAME && !UNITY_BYTEDANCEMINIGAME)
#define REV_ENDLESS_SAVE_WORKER
#endif
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Collections.Generic;
#if REV_ENDLESS_SAVE_WORKER
using System.Threading;
#endif
using Revolution;
using RevGame.Board;
using RevGame.Core;
using RevGame.Data;
using RevGame.Pieces;
using UnityEngine;
using Stopwatch = System.Diagnostics.Stopwatch;
namespace RevGame.Endless
{
    /// <summary>无尽局持有棋盘与存档状态，通过框架存档门面提交可继续的快照。</summary>
    public sealed partial class EndlessService
    {
        [Serializable]
        private sealed class Envelope
        {
            public string payload, checksum;
        }
        /// <summary>同一次 Play 内的内存测试存档，多个测试实例可以读取。进入下一次 Play 时清空。</summary>
        private static string _isolatedSave;
        private const string RunFileName = "endless-v1.json";
        private readonly string _saveDirectory;
        // 从备份恢复后的首次写档必须保留这份可用备份。
        private bool _preserveRunBackup;
        /// <summary>新局首屏还在交割时冻结续档槽写入，直到本次流程提交或释放候选。</summary>
        private bool _awaitingFirstSave;
#if REV_ENDLESS_SAVE_WORKER
        /// <summary>本局当前唯一的后台写入。任务只持有脱离棋局的快照，主线程负责消费结果。</summary>
        private CheckpointWrite _checkpointWrite;
        /// <summary>已有写入时合并的新检查点请求。旧任务完成后才采集当前最新状态。</summary>
        private bool _checkpointRequested;
        /// <summary>隔离压力局是否复用原生后台编码。仅诊断入口显式开启，每局结束后清除。</summary>
        private bool _backgroundIsolatedCheckpoints;
#endif
#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
        /// <summary>本局隔离模式发出的自动检查点请求数，包含合并请求。</summary>
        private int _isolatedCheckpointRequests;
        /// <summary>本局在主线程消费的后台内存检查点数量，拒绝或合并的请求不计入。</summary>
        private int _isolatedCheckpointCommits;
        /// <summary>本局隔离检查点请求的主线程最长耗时，单位毫秒。合并请求稍后补采的开销不计在此处。</summary>
        private double _isolatedCheckpointRequestMaxMs;
#endif
        /// <summary>本局最近一次快照写入失败。写入成功或切换棋局后清除，HUD 据此保留重试入口。</summary>
        public bool RunSavePending { get; private set; }
        private string SavePath => Path.Combine(_saveDirectory, RunFileName);

        public EndlessService() : this(Application.persistentDataPath)
        {
        }

        // 测试实例可指定独立目录，仍走正式的存档读写链。
        private EndlessService(string saveDirectory)
        {
            _saveDirectory = saveDirectory;
        }

        private static string Digest(string value)
        {
            using (SHA256 hash = SHA256.Create())
            {
                return Convert.ToBase64String(hash.ComputeHash(Encoding.UTF8.GetBytes(value)));
            }
        }
        public static bool HasSavedRun(bool isolated = false)
        {
            return isolated ? !string.IsNullOrEmpty(_isolatedSave) : RevFileSave.Exists(Path.Combine(Application.persistentDataPath, RunFileName));
        }

        /// <summary>每次 Play 都从没有测试续档开始，正式磁盘存档继续由 RevFileSave 管理。</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetIsolatedSave()
        {
            _isolatedSave = null;
        }

        /// <summary>只读投影续玩槽。无档时描述为空，已结算时附带战绩；读取异常保留真实恢复入口。</summary>
        public (string Description, bool Settled) DescribeSavedRun(bool isolated)
        {
            if (isolated ? string.IsNullOrEmpty(_isolatedSave) : !RevFileSave.Exists(SavePath))
            {
                return (string.Empty, false);
            }

            try
            {
                EndlessRun saved = isolated ? Decode(_isolatedSave) : RevFileSave.Read(SavePath, Decode, out _);
                string formation = saved.formation switch
                {
                    0 => "弩雨阵",
                    1 => "炎环阵",
                    _ => "守眼阵"
                };
                TimeSpan elapsed = TimeSpan.FromSeconds(Math.Max(0d, saved.seconds));
                string detail = $"{formation} · {(int)elapsed.TotalMinutes:00}:{elapsed.Seconds:00} · 击破 {saved.kills + saved.invaderKills}";
                bool settled = saved.version == 1 && (saved.settled || saved.phase == EndlessPhase.Result);
                return settled
                    ? ($"{detail}\n战绩 {saved.bankedScore} · 领主 {saved.bossKills}", true)
                    : ("原续档 · " + detail, false);
            }
            catch (Exception error)
            {
                RevLog.Warn("无法读取旧续档摘要：" + error.Message, "RevGame");
                return ("当前已有一份无尽续档。", false);
            }
        }

        /// <summary>新局首屏已交付后解除写入门禁，并使用原子保存替换当前续玩槽。</summary>
        public void CommitFirstSave()
        {
            if (_awaitingFirstSave)
            {
                _awaitingFirstSave = false;
                Save();
            }
        }

        /// <summary>显式保存等到落盘才返回结果。先收齐旧任务，再提交最新快照，失败时保留旧档并冻结计分。</summary>
        public bool Save()
        {
            if (_awaitingFirstSave)
            {
                return false;
            }
            if (Run == null)
            {
                return Reject("没有可保存的无尽局");
            }
            try
            {
                // 显式保存覆盖待合并的请求。等待已提交的旧快照，防止它在本次成功后再替换主档。
                FinishRunCheckpoint();
                EndlessRun snapshot = CaptureSaveSnapshot();
                if (Isolated)
                {
                    _isolatedSave = EncodeSave(snapshot);
                }
                else
                {
                    WriteSaveSnapshot(SavePath, snapshot, _preserveRunBackup);
                    _preserveRunBackup = false;
                }
                RunSavePending = false;
                return true;
            }
            catch (Exception error)
            {
                return FailRunSave(error);
            }
        }

        /// <summary>在主线程复制演员、敌槽和局状态。从返回开始，编码仅使用这份独立快照。</summary>
        private EndlessRun CaptureSaveSnapshot()
        {
            var units = new List<EndlessSavedUnit>(Units.Count);
            for (int i = 0; i < Units.Count; i++)
            {
                EndlessUnit u = Units[i];
                Piece p = u.Piece;
                units.Add(new EndlessSavedUnit
                {
                    id = p.Id, kind = (int)p.Def.Kind, star = (int)p.Def.Star, side = (int)p.Side,
                    x = p.Pos.X, y = p.Pos.Y, size = p.Size, hp = p.Hp, maxHp = p.MaxHp, attackScale = p.AttackScale,
                    summoned = u.Summoned, stone = u.Stone, revived = u.Revived, elite = u.Elite,
                    attackAt = u.AttackAt, moveAt = u.MoveAt, burnUntil = u.BurnUntil, burnAt = u.BurnAt,
                    burnDamage = u.BurnDamage, slowUntil = u.SlowUntil, counterAt = u.CounterAt,
                    brokenUntil = u.BrokenUntil, nextSkillAt = u.NextSkillAt, warningAt = u.WarningAt,
                    skillCasts = u.SkillCasts, bossChapter = u.BossChapter, chapterSkill = u.ChapterSkill, skillStage = u.SkillStage,
                    skillAX = u.SkillA.X, skillAY = u.SkillA.Y, skillBX = u.SkillB.X, skillBY = u.SkillB.Y, skillCX = u.SkillC.X, skillCY = u.SkillC.Y,
                    bornAt = u.BornAt, bossTemplate = u.BossTemplate, warningLine = u.WarningLine,
                    role = u.Role, gate = u.Gate, breakEyeAt = u.BreakEyeAt, breakEyeTarget = u.BreakEyeTarget,
                    furyUntil = u.FuryUntil, stationarySince = u.StationarySince, steadyShield = u.SteadyShield,
                    auraAt = u.AuraAt, comboCount = u.ComboCount,
                    outside = u.Outside, centerX = u.Center.x, centerY = u.Center.y, damageBudgetAt = u.DamageBudgetAt, damageBudgetUsed = u.DamageBudgetUsed
                });
            }
            return Run.CaptureForSave(units, Horde.Export());
        }

        /// <summary>将独立快照编码为既有存档信封，内层原文和 SHA-256 校验值一起写入。</summary>
        private static string EncodeSave(EndlessRun snapshot)
        {
            string payload = JsonUtility.ToJson(snapshot);
            return JsonUtility.ToJson(new Envelope { payload = payload, checksum = Digest(payload) });
        }

        /// <summary>检查快照并经框架原子提交。后台调用方提供清理异常回调，在工作结束后交回主线程。</summary>
        private static void WriteSaveSnapshot(string path, EndlessRun snapshot, bool preserveBackup, Action<Exception> onCleanupError = null)
        {
            ValidateSaveHeader(snapshot);
            string encoded = EncodeSave(snapshot);
            RevFileSave.Write(path, encoded, value => VerifySaveReadback(encoded, value), preserveBackup, onCleanupError);
        }

        /// <summary>只在主线程交付写入失败。旧档继续可用，界面提供重试并停止新增计分。</summary>
        private bool FailRunSave(Exception error)
        {
            RunSavePending = true;
            Run.scoreStopped = true;
            Run.error = "保存失败，已停止计分；旧档保留：" + error.Message;
            return Reject("存档未写入，本局已停止新增计分");
        }

        /// <summary>接受一次战斗检查点请求。隔离测试记录主线程占用，正式对局直接沿现有提交入口。</summary>
        private void RequestRunCheckpoint()
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
            if (Isolated)
            {
                long began = Stopwatch.GetTimestamp();
                SubmitRunCheckpoint();
                _isolatedCheckpointRequests++;
                double milliseconds = (Stopwatch.GetTimestamp() - began) * 1000d / Stopwatch.Frequency;
                _isolatedCheckpointRequestMaxMs = Math.Max(_isolatedCheckpointRequestMaxMs, milliseconds);
                return;
            }
#endif
            SubmitRunCheckpoint();
        }

        /// <summary>原生磁盘档和显式后台试验的内存档延后完成，其余入口保持同步提交。</summary>
        private void SubmitRunCheckpoint()
        {
#if REV_ENDLESS_SAVE_WORKER
            if (!Isolated || _backgroundIsolatedCheckpoints)
            {
                if (!_awaitingFirstSave && Run != null)
                {
                    _checkpointRequested = true;
                    AdvanceRunCheckpoint();
                }
                return;
            }
#endif
            Save();
        }

        /// <summary>接收已完成的后台结果，并在写槽空闲时采集一份最新棋局。战斗更新期间不等待编码或写盘。</summary>
        private void AdvanceRunCheckpoint()
        {
#if REV_ENDLESS_SAVE_WORKER
            CompleteRunCheckpoint(wait: false);
            if (!_checkpointRequested || _checkpointWrite != null)
            {
                return;
            }
            _checkpointRequested = false;
            try
            {
                // 快照只拥有脱离本局的值。隔离试验用空路径指定内存目的地，编码后由主线程发布。
                EndlessRun snapshot = CaptureSaveSnapshot();
                string path = Isolated ? null : SavePath;
                bool preserveBackup = _preserveRunBackup;
                var write = new CheckpointWrite();
                if (!ThreadPool.QueueUserWorkItem(_ => write.Run(path, snapshot, preserveBackup)))
                {
                    throw new InvalidOperationException("无法启动后台存档");
                }
                _checkpointWrite = write;
            }
            catch (Exception error)
            {
                FailRunSave(error);
            }
#endif
        }

        /// <summary>在显式保存、换局或退场前等待已经提交的检查点，并收走尚未采样的合并请求。</summary>
        private void FinishRunCheckpoint()
        {
#if REV_ENDLESS_SAVE_WORKER
            _checkpointRequested = false;
            CompleteRunCheckpoint(wait: true);
#endif
        }

        /// <summary>新局、续档和退出先收完旧工单，再清除本次试验模式和采样计数。</summary>
        private void ResetRunCheckpointState()
        {
            FinishRunCheckpoint();
#if REV_ENDLESS_SAVE_WORKER
            _backgroundIsolatedCheckpoints = false;
#endif
#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
            _isolatedCheckpointRequests = 0;
            _isolatedCheckpointCommits = 0;
            _isolatedCheckpointRequestMaxMs = 0;
#endif
        }

#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
        /// <summary>由已确认隔离的压力入口切换模式。旧工单先交付，无法后台编码的平台拒绝开启。</summary>
        private bool SetIsolatedCheckpointBackground(bool enabled)
        {
#if REV_ENDLESS_SAVE_WORKER
            if (_backgroundIsolatedCheckpoints != enabled)
            {
                FinishRunCheckpoint();
                _backgroundIsolatedCheckpoints = enabled;
            }
            return true;
#else
            return !enabled;
#endif
        }

        /// <summary>读取隔离压力局的检查点模式、工单状态和请求用时，供同负载安卓对照。</summary>
        public object DebugCheckpointState()
        {
            var state = new Dictionary<string, object>
            {
                ["requests"] = _isolatedCheckpointRequests,
                ["backgroundCommits"] = _isolatedCheckpointCommits,
                ["requestMaxMs"] = _isolatedCheckpointRequestMaxMs
            };
#if REV_ENDLESS_SAVE_WORKER
            state["backgroundMemory"] = _backgroundIsolatedCheckpoints;
            state["inFlight"] = _checkpointWrite != null;
            state["queued"] = _checkpointRequested;
#else
            state["backgroundMemory"] = false;
            state["inFlight"] = false;
            state["queued"] = false;
#endif
            return state;
        }
#endif

#if REV_ENDLESS_SAVE_WORKER
        /// <summary>消费一次后台结果。内存档在这里才提交，磁盘档则在收到回执后更新备份状态。</summary>
        private void CompleteRunCheckpoint(bool wait)
        {
            CheckpointWrite write = _checkpointWrite;
            if (write == null || !write.TryGetResult(wait, out Exception error, out string isolatedJson))
            {
                return;
            }
            _checkpointWrite = null;
            if (error != null)
            {
                FailRunSave(error);
                return;
            }
            if (isolatedJson != null)
            {
                _isolatedSave = isolatedJson;
#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
                _isolatedCheckpointCommits++;
#endif
            }
            else
            {
                _preserveRunBackup = false;
            }
            RunSavePending = false;
        }

        /// <summary>一个原生检查点的回执。工作线程只读独立快照，主线程消费结果。</summary>
        private sealed class CheckpointWrite
        {
            /// <summary>只保护回执字段，编码和磁盘操作都在锁外。</summary>
            private readonly object _receipt = new object();
            /// <summary>后台编码或文件操作已完成，回执可以读取。</summary>
            private bool _finished;
            /// <summary>成功时为空，失败时保留原异常供主线程更新提示。</summary>
            private Exception _error;
            /// <summary>隔离档编码结果。磁盘任务和失败时为空，不允许后台直接修改续玩槽。</summary>
            private string _isolatedJson;

            /// <summary>在线程池处理快照。空路径只编码到内存，非空路径走现有原子落盘，完成才发布回执。</summary>
            internal void Run(string path, EndlessRun snapshot, bool preserveBackup)
            {
                Exception failure = null;
                string isolatedJson = null;
                try
                {
                    if (path == null)
                    {
                        isolatedJson = EncodeSave(snapshot);
                    }
                    else
                    {
                        WriteRunCheckpoint(path, snapshot, preserveBackup);
                    }
                }
                catch (Exception error)
                {
                    failure = error;
                }
                lock (_receipt)
                {
                    _error = failure;
                    _isolatedJson = isolatedJson;
                    _finished = true;
                    Monitor.PulseAll(_receipt);
                }
            }

            /// <summary>取后台错误和内存档。固定步只查已完成工单，显式保存和换局才等待。</summary>
            internal bool TryGetResult(bool wait, out Exception error, out string isolatedJson)
            {
                lock (_receipt)
                {
                    while (wait && !_finished)
                    {
                        Monitor.Wait(_receipt);
                    }
                    error = _error;
                    isolatedJson = _isolatedJson;
                    return _finished;
                }
            }
        }

        /// <summary>工作线程只编码独立快照和写文件。临时文件删除失败也随原错误回传，由主线程更新提示。</summary>
        private static void WriteRunCheckpoint(string path, EndlessRun snapshot, bool preserveBackup)
        {
            Exception cleanupError = null;
            try
            {
                WriteSaveSnapshot(path, snapshot, preserveBackup, error => cleanupError = error);
            }
            catch (Exception error) when (cleanupError != null)
            {
                throw new IOException(error.Message + "，且清理临时档失败：" + cleanupError.Message,
                    new AggregateException(error, cleanupError));
            }
        }
#endif

        /// <summary>逐字符核对临时文件读回和本次已编码的快照。有差异时抛出异常，由框架放弃替换并清理临时文件。</summary>
        private static void VerifySaveReadback(string expected, string actual)
        {
            if (!string.Equals(expected, actual, StringComparison.Ordinal))
            {
                throw new InvalidDataException("无尽存档读回内容与本次快照不一致");
            }
        }

        /// <summary>验证存档原文的校验和，再读取当前业务字段。Unity 忽略旧版多出的保持计时，原坐标继续用于恢复占格。</summary>
        private static EndlessRun Decode(string encoded)
        {
            Envelope envelope = JsonUtility.FromJson<Envelope>(encoded);
            if (envelope == null || string.IsNullOrEmpty(envelope.payload) || Digest(envelope.payload) != envelope.checksum)
            {
                throw new InvalidDataException("无尽存档校验失败");
            }
            EndlessRun run = JsonUtility.FromJson<EndlessRun>(envelope.payload);
            ValidateSaveHeader(run);
            return run;
        }

        /// <summary>提交前和读档解码后共用的基本信息检查，包括棋局身份、分数及阵容范围。棋盘完整性由恢复预检负责。</summary>
        private static void ValidateSaveHeader(EndlessRun run)
        {
            if (run == null || string.IsNullOrEmpty(run.runId) || run.score < 0 || run.bankedScore < 0
                || run.difficulty < 0 || run.difficulty > 2 || run.formation < 0 || run.formation > 2)
            {
                throw new InvalidDataException("无尽存档版本或结构不兼容");
            }
        }
        /// <summary>在独立试盘验证快照，验证通过后才允许 Restore 替换当前棋局。</summary>
        private static (EndlessRun run, List<EndlessUnit> units) PrepareRestore(string encoded)
        {
            EndlessRun loaded = Decode(encoded);
            if (loaded.version != 1)
            {
                return (loaded, null);
            }
            if (loaded.horde == null || loaded.horde.Length > N("HordeLimit") || loaded.units == null || loaded.units.Count > 81
            || loaded.random == 0 || loaded.mains == null || loaded.mains.Length != 6 || loaded.seals == null || loaded.seals.Length != 6
            || loaded.rare == null || loaded.rare.Length != 4 || loaded.banned == null || loaded.banned.Length != 16
                || loaded.choices == null || loaded.choices.Length != 3 || loaded.hand == null || loaded.hand.Count > N("HandLimit")
                || float.IsNaN(loaded.seconds) || float.IsInfinity(loaded.seconds) || loaded.seconds < 0
                || float.IsNaN(loaded.heart) || loaded.heart < 0 || loaded.heart > V("HeartHp"))
            {
                throw new InvalidDataException("存档容量与当前配置不兼容");
            }
            // 独立试盘预检整份快照，任一单位损坏都不清当前棋局；提交前不写生产Board。
            var trial = new BoardModel(9, new BoardPos(4, 4));
            var units = new List<EndlessUnit>(loaded.units.Count);
            var identities = new HashSet<int>();
            for (int i = 0; i < loaded.units.Count; i++)
            {
                EndlessSavedUnit s = loaded.units[i];
                if (s == null || !Enum.IsDefined(typeof(PieceKind), s.kind) || s.star < 1 || s.star > 3
                    || s.side < 0 || s.side > 1 || s.hp <= 0 || s.hp > s.maxHp || !identities.Add(s.id))
                {
                    throw new InvalidDataException("存档单位字段非法或身份重复");
                }
                var def = PieceCatalog.Get((PieceKind)s.kind, (StarTier)s.star);
                Piece p = new Piece(s.id, def, (GameSide)s.side, s.side == (int)GameSide.Player ? 1 : s.size, s.bossTemplate >= 0, false, (float)s.maxHp / def.MaxHp, s.attackScale);
                p.RestoreDetachedVitals(s.maxHp, s.hp, s.revived);
                p.Pos = new BoardPos(s.x, s.y);
                if (!s.outside && s.bossTemplate < 0 && !trial.Place(p, p.Pos))
                {
                    throw new InvalidDataException("存档占格冲突：" + s.id);
                }
                bool outside = s.outside || s.bossTemplate >= 0;
                // 旧存档的盘内Boss迁为同一身份/生命的盘外演员，玩家高星身体已显式缩为一格。
                if (outside && !s.outside)
                {
                    trial.Remove(p);
                }
                units.Add(new EndlessUnit
                {
                    Piece = p, Summoned = s.summoned, Stone = s.stone, Revived = s.revived, Elite = s.elite,
                    AttackAt = s.attackAt, MoveAt = s.moveAt, BurnUntil = s.burnUntil, BurnAt = s.burnAt,
                    BurnDamage = s.burnDamage, SlowUntil = s.slowUntil, CounterAt = s.counterAt,
                    BrokenUntil = s.brokenUntil, NextSkillAt = s.nextSkillAt, WarningAt = s.warningAt,
                    SkillCasts = s.skillCasts, BossChapter = s.bossChapter, ChapterSkill = s.chapterSkill, SkillStage = s.skillStage,
                    SkillA = new BoardPos(s.skillAX, s.skillAY), SkillB = new BoardPos(s.skillBX, s.skillBY), SkillC = new BoardPos(s.skillCX, s.skillCY),
                    BornAt = s.bornAt, BossTemplate = s.bossTemplate, WarningLine = s.warningLine,
                    Role = s.role, Gate = s.gate, BreakEyeAt = s.breakEyeAt, BreakEyeTarget = s.breakEyeTarget,
                    FuryUntil = s.furyUntil, StationarySince = s.stationarySince, SteadyShield = s.steadyShield,
                    AuraAt = s.auraAt, ComboCount = s.comboCount,
                    Outside = outside, Center = s.outside ? new Vector2(s.centerX, s.centerY) : new Vector2(s.gate == 0 ? -V("BossStandoff") : 8 + V("BossStandoff"), Mathf.Clamp(s.y, 1, 7)), DamageBudgetAt = s.damageBudgetAt, DamageBudgetUsed = s.damageBudgetUsed
                }
                );
            }
            for (int i = 0; i < loaded.horde.Length; i++)
            {
                EndlessHorde h = loaded.horde[i];
                if (h.active && (h.hp <= 0 || h.hp > h.maxHp || !identities.Add(h.id)))
                {
                    throw new InvalidDataException("存档外围身份与棋盘重复或生命非法");
                }
            }
            trial.Reset();
            return (loaded, units);
        }

        /// <summary>
        /// 校验快照后重建模型、保留随机源和选择。玩家从大厅续玩时保留手动暂停，
        /// 也会让布阵、整备和战斗档先暂停，待玩家确认继续后再推进计时。
        /// </summary>
        public bool Restore(bool isolated, bool pauseOnEntry = false)
        {
            try
            {
                // 先完成此前已提交的写入，再读取主备档。旧任务不会晚到并覆盖续玩的文件。
                FinishRunCheckpoint();
                bool fromBackup = false;
                var snapshot = isolated ? PrepareRestore(_isolatedSave) : RevFileSave.Read(SavePath, PrepareRestore, out fromBackup);
                EndlessRun loaded = snapshot.run;
                List<EndlessUnit> units = snapshot.units;
                if (loaded.version != 1)
                {
                    // 只接受可识别的旧版结算摘要，不把旧单位强行塞入新规则；原存档不覆盖。
                    Clear();
                    _preserveRunBackup = fromBackup;
                    Isolated = isolated;
                    World = new EndlessEcsWorld(N("HordeLimit"));
                    Run = new EndlessRun
                    {
                        runId = loaded.runId, seed = loaded.seed, difficulty = loaded.difficulty,
                        score = Math.Max(loaded.score, loaded.bankedScore), bankedScore = Math.Max(loaded.score, loaded.bankedScore),
                        phase = EndlessPhase.Result, settled = true, horde = new EndlessHorde[N("HordeLimit")],
                        error = "旧版存档与当前规则不兼容，已安全结算可识别收益；原档保留"
                    };
                    RecordProgress();
                    return true;
                }
                Clear();
                _preserveRunBackup = fromBackup;
                Run = loaded;
                World = new EndlessEcsWorld(N("HordeLimit"));
                World.Horde.Import(loaded.horde);
                Run.horde = null;
                Isolated = isolated;
                for (int i = 0; i < units.Count; i++)
                {
                    if (!units[i].Outside)
                    {
                        Board.Place(units[i].Piece, units[i].Piece.Pos);
                    }
                    Units.Add(units[i]);
                }
                RefreshRules();
                // 暂停记录中的来源阶段供继续按钮恢复。已结算和待选择的界面按快照落屏。
                if (pauseOnEntry && CanArrange())
                {
                    Pause();
                }
                else if (!pauseOnEntry && Run.phase == EndlessPhase.Paused)
                {
                    Run.phase = Run.previousPhase;
                }
                return true;
            }
            catch (Exception error)
            {
                return Reject("继续本局失败，存档未覆盖：" + error.Message);
            }
        }
    }
}
