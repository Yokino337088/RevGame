using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Collections.Generic;
using RevGame.Board;
using RevGame.Core;
using RevGame.Data;
using RevGame.Pieces;
using UnityEngine;
namespace RevGame.Endless
{
    public sealed partial class EndlessService
    {
        [Serializable]
        private sealed class Envelope
        {
            public string payload, checksum;
        }
        private static string _isolatedSave;
        private const string RunFileName = "endless-v1.json";
        private readonly string _saveDirectory;
        // 从备份恢复后的首次写档必须保留这份可用备份。
        private bool _preserveRunBackup;
        private string SavePath => Path.Combine(_saveDirectory, RunFileName);

        public EndlessService() : this(Application.persistentDataPath)
        {
        }

        // 测试实例可指定独立目录，仍走正式的存档读写链。
        private EndlessService(string saveDirectory)
        {
            _saveDirectory = saveDirectory;
        }

        private static bool SaveFileExists(string path)
        {
            return File.Exists(path) || File.Exists(path + ".bak");
        }

        /// <summary>按顺序读取并验证主档和备份。读取失败保留两份原文件。</summary>
        private static T ReadSaveFile<T>(string path, Func<string, T> validate, out bool fromBackup)
        {
            fromBackup = false;
            try
            {
                return validate(File.ReadAllText(path, Encoding.UTF8));
            }
            catch (Exception mainError) when (IsSaveReadFailure(mainError))
            {
                try
                {
                    T data = validate(File.ReadAllText(path + ".bak", Encoding.UTF8));
                    fromBackup = true;
                    return data;
                }
                catch (Exception backupError) when (IsSaveReadFailure(backupError))
                {
                    throw new InvalidDataException("主档与备份均不可用", new AggregateException(mainError, backupError));
                }
            }
        }

        private static bool IsSaveReadFailure(Exception error)
        {
            return error is IOException || error is UnauthorizedAccessException || error is InvalidDataException || error is ArgumentException;
        }

        private static void ReplaceSaveFile(string temporary, string path, bool preserveBackup)
        {
            if (File.Exists(path))
            {
                File.Replace(temporary, path, preserveBackup ? null : path + ".bak");
            }
            else
            {
                File.Move(temporary, path);
            }
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
            return isolated ? !string.IsNullOrEmpty(_isolatedSave) : SaveFileExists(Path.Combine(Application.persistentDataPath, RunFileName));
        }
        /// <summary>先保存完整一致性状态到临时文件并校验，再原子替换；写失败冻结计分并保留旧档。</summary>
        public bool Save()
        {
            if (Run == null)
            {
                return Reject("没有可保存的无尽局");
            }
            try
            {
                Run.units.Clear();
                for (int i = 0; i < Units.Count; i++)
                {
                    EndlessUnit u = Units[i];
                    Piece p = u.Piece;
                    Run.units.Add(new EndlessSavedUnit
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
                        auraAt = u.AuraAt, comboCount = u.ComboCount, holdUntil = u.HoldUntil,
                        outside = u.Outside, centerX = u.Center.x, centerY = u.Center.y, damageBudgetAt = u.DamageBudgetAt, damageBudgetUsed = u.DamageBudgetUsed
                    }
                    );
                }
                Run.horde = Horde.Export();
                string payload = JsonUtility.ToJson(Run);
                Run.horde = null;
                string encoded = JsonUtility.ToJson(new Envelope
                {
                    payload = payload, checksum = Digest(payload)
                }
                );
                if (Isolated)
                {
                    _isolatedSave = encoded;
                    return true;
                }
                string temporary = SavePath + ".tmp";
                File.WriteAllText(temporary, encoded, Encoding.UTF8);
                Decode(File.ReadAllText(temporary, Encoding.UTF8));
                ReplaceSaveFile(temporary, SavePath, _preserveRunBackup);
                _preserveRunBackup = false;
                return true;
            }
            catch (Exception e)
            {
                Run.scoreStopped = true;
                Run.error = "保存失败，已停止计分；旧档保留：" + e.Message;
                return Reject(Run.error);
            }
        }
        private static EndlessRun Decode(string encoded)
        {
            Envelope envelope = JsonUtility.FromJson<Envelope>(encoded);
            if (envelope == null || string.IsNullOrEmpty(envelope.payload) || Digest(envelope.payload) != envelope.checksum)
            {
                throw new InvalidDataException("无尽存档校验失败");
            }
            EndlessRun run = JsonUtility.FromJson<EndlessRun>(envelope.payload);
            if (run == null || string.IsNullOrEmpty(run.runId) || run.score < 0 || run.bankedScore < 0
                || run.difficulty < 0 || run.difficulty > 2 || run.formation < 0 || run.formation > 2)
            {
                throw new InvalidDataException("无尽存档版本或结构不兼容");
            }
            return run;
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
                    AuraAt = s.auraAt, ComboCount = s.comboCount, HoldUntil = s.holdUntil,
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

        /// <summary>先解码校验，再重建模型；从快照恢复随机源和选择，不调用RollChoices。</summary>
        public bool Restore(bool isolated)
        {
            try
            {
                bool fromBackup = false;
                var snapshot = isolated ? PrepareRestore(_isolatedSave) : ReadSaveFile(SavePath, PrepareRestore, out fromBackup);
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
                if (Run.phase == EndlessPhase.Paused)
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
