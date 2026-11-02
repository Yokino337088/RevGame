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
        private static string SavePath
        {
            get
            {
                // 此路径仅为玩家存档，不是游戏资源；资源仍全部经RevResManager。
                return Path.Combine(Application.persistentDataPath, "endless-v1.json");
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
            return isolated ? !string.IsNullOrEmpty(_isolatedSave) : File.Exists(SavePath);
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
                        auraAt = u.AuraAt, comboCount = u.ComboCount
                    }
                    );
                }
                string payload = JsonUtility.ToJson(Run);
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
                if (File.Exists(SavePath))
                {
                    File.Replace(temporary, SavePath, SavePath + ".bak");
                }
                else
                {
                    File.Move(temporary, SavePath);
                }
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
            if (run == null || string.IsNullOrEmpty(run.runId) || run.score < 0 || run.bankedScore < 0)
            {
                throw new InvalidDataException("无尽存档版本或结构不兼容");
            }
            return run;
        }
        /// <summary>先解码校验，再重建模型；从快照恢复随机源和选择，不调用RollChoices。</summary>
        public bool Restore(bool isolated)
        {
            try
            {
                EndlessRun loaded = Decode(isolated ? _isolatedSave : File.ReadAllText(SavePath, Encoding.UTF8));
                if (loaded.version != 1)
                {
                    // 只接受可识别的旧版结算摘要，不把旧单位强行塞入新规则；原存档不覆盖。
                    Clear();
                    Isolated = isolated;
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
                if (loaded.horde == null || loaded.horde.Length != N("HordeLimit") || loaded.units == null || loaded.units.Count > 81
                || loaded.random == 0 || loaded.mains == null || loaded.mains.Length != 6 || loaded.seals == null || loaded.seals.Length != 6
                || loaded.rare == null || loaded.rare.Length != 4 || loaded.banned == null || loaded.banned.Length != 16
                || loaded.choices == null || loaded.choices.Length != 3 || loaded.hand == null || loaded.hand.Count > N("HandLimit")
                || float.IsNaN(loaded.seconds) || float.IsInfinity(loaded.seconds) || loaded.seconds < 0 || loaded.heart < 0 || loaded.heart > V("HeartHp"))
                {
                    return Reject("存档容量与当前配置不兼容");
                }
                // 独立试盘预检整份快照，任一单位损坏都不清当前棋局；提交前不写生产Board。
                var trial = new BoardModel(9, new BoardPos(4, 4));
                var units = new List<EndlessUnit>(loaded.units.Count);
                var identities = new HashSet<int>();
                for (int i = 0; i < loaded.units.Count; i++)
                {
                    EndlessSavedUnit s = loaded.units[i];
                    if (!Enum.IsDefined(typeof(PieceKind), s.kind) || s.star < 1 || s.star > 3 || s.side < 0 || s.side > 1 || s.hp <= 0 || s.hp > s.maxHp || !identities.Add(s.id))
                    {
                        return Reject("存档单位字段非法或身份重复");
                    }
                    var def = PieceCatalog.Get((PieceKind)s.kind, (StarTier)s.star);
                    Piece p = new Piece(s.id, def, (GameSide)s.side, s.size, s.bossTemplate >= 0, false, (float)s.maxHp / def.MaxHp, s.attackScale);
                    p.RestoreDetachedVitals(s.maxHp, s.hp, s.revived);
                    if (!trial.Place(p, new BoardPos(s.x, s.y)))
                    {
                        throw new InvalidDataException("存档占格冲突：" + s.id);
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
                        AuraAt = s.auraAt, ComboCount = s.comboCount
                    }
                    );
                }
                for (int i = 0; i < loaded.horde.Length; i++)
                {
                    EndlessHorde h = loaded.horde[i];
                    if (h.active && (h.hp <= 0 || h.hp > h.maxHp || !identities.Add(h.id)))
                    {
                        return Reject("存档外围身份与棋盘重复或生命非法");
                    }
                }
                trial.Reset();
                Clear();
                Run = loaded;
                Isolated = isolated;
                for (int i = 0; i < units.Count; i++)
                {
                    Board.Place(units[i].Piece, units[i].Piece.Pos);
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
