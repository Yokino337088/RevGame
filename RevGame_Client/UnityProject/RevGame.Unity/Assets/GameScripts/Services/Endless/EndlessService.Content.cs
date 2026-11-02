using System;
using RevGame.Board;
using RevGame.Core;
using RevGame.Data;
using RevGame.Pieces;
using UnityEngine;
namespace RevGame.Endless
{
    public sealed partial class EndlessService
    {
        /// <summary>把入侵者先登记在外围队列。转盘不再生成新Id，因此外围/盘内只能领一次死亡奖励。</summary>
        private bool QueueInvader(bool elite, int boss)
        {
            int slot = -1;
            for (int i = 0; i < Run.horde.Length; i++)
            {
                if (!Run.horde[i].active)
                {
                    slot = i;
                    break;
                }
            }
            if (slot < 0)
            {
                return false;
            }
            int role = boss >= 0 ? boss + 6 : elite ? 5 : 1 + Next(2);
            if (!elite && boss < 0 && Run.seconds >= V("BossInterval") * 2 && !HasRole(3) && (Run.affixes.Contains(3) || Next(4) == 0))
            {
                role = 3;
            }
            else if (!elite && boss < 0 && Next(4) == 0)
            {
                role = 4;
            }
            if (Run.affixes.Contains(1) && role <= 2)
            {
                role = 2;
            }
            PieceKind kind = RoleKind(role);
            float scale = V("EnemyHpScale") * (elite ? V("EliteHpScale") : 1) * (boss >= 0 ? V("BossHpScale") : 1);
            scale *= boss >= 0 ? Mathf.Min(V("BossHpCap"), 1 + (Run.circuit - 1) * V("CircuitHpGrowth")) : 1 + Math.Min(Run.circuit - 1, N("CircuitHpCap")) * V("CircuitHpGrowth");
            scale *= Run.difficulty == 1 ? V("DifficultyHp") : 1;
            float hp = Mathf.Max(1, Mathf.Floor(PieceCatalog.Get(kind, StarTier.One).MaxHp * scale));
            // 入侵者和杂兵共用左右两侧 × 九行的出生口，进盘时也沿这一行进入（见 TryEnterBoard）。
            NextLane(out int gate, out int row);
            Run.horde[slot] = new EndlessHorde
            {
                active = true, id = Run.nextId++, gate = gate, kind = role, hp = hp, maxHp = hp,
                x = gate == 0 ? -3 : 11,
                y = row,
                speed = V("HordeSpeed") * (role == 1 ? V("CircuitSpeedCap") : 1)
            };
            return true;
        }
        private bool HasBossIdentity()
        {
            if (BossUnit() != null)
            {
                return true;
            }
            for (int i = 0; i < Run.horde.Length; i++)
            {
                if (Run.horde[i].active && Run.horde[i].kind >= 6)
                {
                    return true;
                }
            }
            return false;
        }
        private bool HasRole(int role)
        {
            for (int i = 0; i < Units.Count; i++)
            {
                if (Units[i].Role == role)
                {
                    return true;
                }
            }
            for (int i = 0; i < Run.horde.Length; i++)
            {
                if (Run.horde[i].active && Run.horde[i].kind == role)
                {
                    return true;
                }
            }
            return false;
        }
        private static PieceKind RoleKind(int role)
        {
            if (role >= 6)
            {
                return PieceKind.HeavySword;
            }
            if (role == 2)
            {
                return PieceKind.Shield;
            }
            if (role == 3 || role == 5)
            {
                return PieceKind.MageFire;
            }
            if (role == 4)
            {
                return PieceKind.Priest;
            }
            return PieceKind.Swordman;
        }
        /// <summary>沿原门点寻找完整身体；先Board.Place成功，再清外围active。满盘继续门外排队且可被攻击。</summary>
        private bool TryEnterBoard(int index)
        {
            EndlessHorde h = Run.horde[index];
            int size = h.kind >= 6 ? 3 : 1;
            if (Occupied(GameSide.Enemy) + size * size > N("EnemyCells"))
            {
                return false;
            }
            int along = Mathf.Clamp(Mathf.RoundToInt(h.gate < 2 ? h.y : h.x), 0, 9 - size);
            for (int offset = 0; offset <= 9 - size; offset++)
            {
                int n = (along + offset) % (10 - size);
                BoardPos pos = new BoardPos(h.gate == 0 ? 0 : h.gate == 1 ? 9 - size : n, h.gate == 2 ? 0 : h.gate == 3 ? 9 - size : n);
                if (!Fits(pos, size, GameSide.Enemy))
                {
                    continue;
                }
                var def = PieceCatalog.Get(RoleKind(h.kind), StarTier.One);
                Piece piece = new Piece(h.id, def, GameSide.Enemy, size, h.kind >= 6, false, h.maxHp / def.MaxHp, V("EnemyAttackScale"));
                piece.RestoreDetachedVitals(Mathf.CeilToInt(h.maxHp), Mathf.Max(1, Mathf.CeilToInt(h.hp)), false);
                if (!Board.Place(piece, pos))
                {
                    return false;
                }
                Units.Add(new EndlessUnit
                {
                    Piece = piece, Role = h.kind, Gate = h.gate, Elite = h.kind == 5, BossTemplate = h.kind >= 6 ? h.kind - 6 : -1,
                    // 四个巡回槽位连续轮换六章演员；数字6是现有章节资产数量，不是战斗数值。
                    BossChapter = h.kind >= 6 ? ((Run.circuit - 1) * 4 + h.kind - 6) % 6 + 1 : 0,
                    BornAt = Run.seconds, NextSkillAt = Run.seconds + V("BossSkillInterval"),
                    AttackAt = Run.seconds + V("WarningSeconds"), MoveAt = Run.seconds + V("WarningSeconds"),
                    BurnUntil = h.burnUntil, BurnAt = h.burnAt, BurnDamage = h.burnDamage, SlowUntil = h.slowUntil
                }
                );
                h.active = false;
                Run.horde[index] = h;
                RefreshRules();
                return true;
            }
            return false;
        }
        /// <summary>所有死亡形态共用此处。外围转盘不调用它，奖励与来源身份一一对应。</summary>
        private void RewardEnemy(int role, int gate = -1)
        {
            if (Run.riskKind == 3 && gate == Run.relicGate)
            {
                Run.riskObjectiveDone = true;
            }
            if (role >= 6)
            {
                Run.bossKills++;
                Run.cores++;
                GainExperience(N("ExperienceBoss"));
                AddScore(N("ScoreBoss"));
                if (role == 9)
                {
                    Run.completedCircuits++;
                    AddScore(N("ScoreCircuit"));
                    Run.bankedScore = Run.score;
                    Run.phase = EndlessPhase.Circuit;
                    RecordProgress();
                }
                _saveAtStepEnd = true;
            }
            else if (role == 5)
            {
                Run.eliteKills++;
                Run.fragments++;
                ConvertFragments();
                GainExperience(Mathf.CeilToInt(N("ExperienceElite") * (Run.riskUntil > Run.seconds ? V("RiskExperience") : 1)));
                AddScore(N("ScoreElite"));
                GiveCards(1);
                if (Run.breakthroughs.Contains(0))
                {
                    Run.doubleShotUntil = Run.seconds + V("BurnSeconds");
                }
            }
            else if (role > 0)
            {
                Run.invaderKills++;
                GainExperience(N("ExperienceInvader"));
                AddScore(N("ScoreInvader"));
                if (role == 4)
                {
                    GiveCards(1);
                    Run.heart = Mathf.Min(V("HeartHp"), Run.heart + V("SupplyHeal"));
                }
            }
            else
            {
                Run.kills++;
                GainExperience(N("ExperienceHorde"));
                AddScore(N("ScoreHorde"));
            }
        }
        private void ConvertFragments()
        {
            while (Run.fragments >= N("FragmentsPerCore"))
            {
                Run.fragments -= N("FragmentsPerCore");
                Run.cores++;
            }
        }
        private void UpdateMinuteBudget()
        {
            // 只在下一分钟生效，幅度有界且可在诊断读取；不会按玩家流派改变敌人抗性或血量。
            float adjustment = V("DirectorAdjustment");
            Run.adaptiveBudget = Run.minuteHeart > Run.heart || HordeCount() > N("HordeLimit") * V("DirectorCrowdedRatio") ? 1 - adjustment : 1 + adjustment;
            Run.minuteHeart = Run.heart;
        }
        private void UpdateRisk()
        {
            if (Run.riskKind < 0 || Run.riskEnds > Run.seconds)
            {
                return;
            }
            bool success = Run.riskKind == 0 ? Run.relicHp > 0 : Run.riskKind == 3 ? Run.riskObjectiveDone : Run.kills + Run.invaderKills > Run.riskKillsAt;
            if (success)
            {
                Run.fragments++;
                ConvertFragments();
                GiveNamedCard();
                int route = Run.formation;
                if (Run.mains[route] < N("MainMax"))
                {
                    Run.mains[route]++;
                }
            }
            Run.riskKind = -1;
            Run.relicGate = -1;
            _saveAtStepEnd = true;
        }
        private void GiveNamedCard()
        {
            for (int i = 0; i < Units.Count && Run.hand.Count < N("HandLimit"); i++)
            {
                if (Units[i].Piece.Side == GameSide.Player && !Units[i].Summoned && !Units[i].Stone)
                {
                    Run.hand.Add((int)Units[i].Piece.Def.Kind);
                    return;
                }
            }
        }
        /// <summary>选择辅印替换目标前可读预览；只能整备修改，失败不扣任何等级或材料。</summary>
        public bool ReplaceRoute(int from, int to, bool seal)
        {
            if (Run.phase != EndlessPhase.Preparation || from < 0 || from >= 6 || to < 0 || to >= 6 || from == to)
            {
                return Reject("请在整备选择两条不同路线");
            }
            int[] slots = seal ? Run.seals : Run.mains;
            if (slots[from] == 0 || slots[to] != 0 || (!seal && Run.evolved[from]))
            {
                return Reject("只能替换已有未进化路线到空路线");
            }
            if (!seal && Run.replacementMinute == (int)(Run.seconds / V("MinuteSeconds")))
            {
                return Reject("本轮整备已替换主祝福");
            }
            int old = slots[from];
            slots[from] = 0;
            slots[to] = seal ? Math.Max(1, Mathf.FloorToInt(old * V("ReplacementRefund"))) : 1;
            if (!seal)
            {
                Run.replacementMinute = (int)(Run.seconds / V("MinuteSeconds"));
                if (Run.breakthroughs.Contains(3))
                {
                    slots[to] = old;
                }
                else
                {
                    GainExperience(Mathf.FloorToInt(old * N("ExperienceBase") * V("ReplacementRefund")));
                }
            }
            return true;
        }
        public bool ExchangeCard(int kind)
        {
            if (Run.phase != EndlessPhase.Preparation || Run.disassemblyPoints < N("CardExchangePoints") || Run.hand.Count >= N("HandLimit"))
            {
                return Reject("需要整备、足够补牌点与空手牌槽");
            }
            for (int i = 0; i < Units.Count; i++)
            {
                if (Units[i].Piece.Side == GameSide.Player && (int)Units[i].Piece.Def.Kind == kind && !Units[i].Summoned && !Units[i].Stone)
                {
                    Run.disassemblyPoints -= N("CardExchangePoints");
                    Run.hand.Add(kind);
                    return true;
                }
            }
            return Reject("只能兑换已上场主力同名牌");
        }
        /// <summary>满主力时仍可把手牌当第三枚一星：先检验两枚同名与完整升星占地，再一次提交。</summary>
        private bool MergeWithCard(Piece target)
        {
            int index = Run.selectedCard;
            EndlessUnit first = Find(target.Id), second = null;
            if (Run.phase == EndlessPhase.Fighting || index < 0 || index >= Run.hand.Count || first == null || first.Stone || first.Summoned || target.Side != GameSide.Player || target.Def.Star != StarTier.One || Run.hand[index] != (int)target.Def.Kind)
            {
                return Reject("手牌合星需要两枚同名一星主力");
            }
            for (int i = 0; i < Units.Count; i++)
            {
                EndlessUnit candidate = Units[i];
                if (candidate != first && !candidate.Summoned && !candidate.Stone && candidate.Piece.Side == GameSide.Player && candidate.Piece.Def.Kind == target.Def.Kind && candidate.Piece.Def.Star == StarTier.One)
                {
                    second = candidate;
                    break;
                }
            }
            if (second == null)
            {
                return Reject("场上还缺一枚同名一星，手牌未消耗");
            }
            _consumed.Clear();
            _consumed.Add(first.Piece);
            _consumed.Add(second.Piece);
            int size = PieceCatalog.PlayerBodySize(StarTier.Two);
            if (!Fits(target.Pos, size, GameSide.Player, null, _consumed))
            {
                _consumed.Clear();
                return Reject("升级占地不合法，未扣牌");
            }
            var merged = new Piece(target.Id, PieceCatalog.Get(target.Def.Kind, StarTier.Two), GameSide.Player);
            if (Run.rare[2])
            {
                merged.SetRoundHealthScale(1 - V("PaperHealthLoss"));
            }
            Board.Remove(target);
            Board.Remove(second.Piece);
            Units.Remove(first);
            Units.Remove(second);
            Board.Place(merged, target.Pos);
            Units.Add(new EndlessUnit
            {
                Piece = merged, Revived = first.Revived || second.Revived
            }
            );
            Run.hand.RemoveAt(index);
            Run.selectedCard = -1;
            _consumed.Clear();
            RefreshRules();
            return true;
        }
    }
}
