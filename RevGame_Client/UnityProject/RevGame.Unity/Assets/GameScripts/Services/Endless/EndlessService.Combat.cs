using System;
using RevGame.Battle;
using RevGame.Core;
using RevGame.Data;
using RevGame.Pieces;
using UnityEngine;
namespace RevGame.Endless
{
    public sealed partial class EndlessService
    {
        /// <summary>固定步内先攻击再移动；只遍历现有名单，死亡移除统一延后，避免漏掉交换到当前索引的单位。</summary>
        private void UpdateUnits(float step)
        {
            for (int i = 0; i < Units.Count; i++)
            {
                EndlessUnit unit = Units[i];
                Piece p = unit.Piece;
                if (!p.IsAlive || unit.Stone)
                {
                    continue;
                }
                if (unit.BurnUntil > Run.seconds && unit.BurnAt <= Run.seconds)
                {
                    p.TakeDamage(Mathf.CeilToInt(unit.BurnDamage));
                    unit.BurnAt = Run.seconds + 1;
                }
                if (!p.IsAlive)
                {
                    continue;
                }
                if (unit.BossTemplate >= 0)
                {
                    UpdateBoss(unit);
                }
                if (p.Side == GameSide.Player)
                {
                    if (Run.rare[3] && Run.seconds - unit.StationarySince >= V("SteadySeconds"))
                    {
                        unit.SteadyShield = true;
                    }
                    if (unit.AttackAt <= Run.seconds && MoveRules.CanAttack(p))
                    {
                        PlayerAttack(unit);
                        float speed = Mathf.Min(V("AttackSpeedCap"), p.EyeSpeedMultiplier * (unit.FuryUntil > Run.seconds ? 1 + V("BrokenEyeSpeed") : 1));
                        unit.AttackAt = Run.seconds + V("PlayerAttackSeconds") / speed;
                    }
                }
                else
                {
                    UpdateBreaker(unit);
                    EnemyAct(unit);
                }
            }
        }
        /// <summary>玩家正常攻击沿用盘内射界；外围目标另按格距/炮架检查，不把外围怪当气或炮架。</summary>
        private void PlayerAttack(EndlessUnit unit)
        {
            Piece p = unit.Piece;
            MoveRules.GetAttackTargets(Board, p, _targets);
            int route = p.Def.Kind == PieceKind.MageFire ? 1 : p.Def.Kind == PieceKind.Priest ? 2 : p.Def.Kind == PieceKind.MageWater ? 4 : p.Def.IsRanged ? 0 : 5;
            float damage = p.Def.Attack * p.AttackScale * p.EyeAttackMultiplier;
            if (Run.rare[0] && p.ConnectedCells <= p.Size * p.Size)
            {
                damage *= 1 + V("LoneAttackBonus");
            }
            damage *= 1 + Run.mains[route] * V("BlessingDamage") + Run.seals[route] * V("SealDamage");
            if (Run.evolved[route])
            {
                damage *= V("EvolutionDamage");
            }
            Vector2 from = Position(p);
            if (_targets.Count > 0)
            {
                Piece target = Board.GetPiece(_targets[0]);
                EndlessUnit victim = Find(target.Id);
                float front = victim != null && victim.Role == 2 && FacingGate(victim.Gate, Position(p), Position(target)) ? V("ShieldFrontRatio") : 1;
                HitUnit(victim, damage * front, route);
                Impact(from, Position(target), route);
                if (Run.mains[1] > 0 && route == 1)
                {
                    Area(Position(target), Mathf.Min(V("AreaLimit"), 1 + Run.mains[1] * V("BlessingDamage")), damage * V("BurnRatio"), 1, target.Id);
                }
                return;
            }
            if (!p.Def.IsRanged && Run.mains[5] == 0)
            {
                return;
            }
            int best = -1;
            float nearest = float.MaxValue;
            for (int i = 0; i < Run.horde.Length; i++)
            {
                EndlessHorde h = Run.horde[i];
                if (!h.active || h.hp <= 0 || !OuterInRange(p, h))
                {
                    continue;
                }
                float distance = (from - new Vector2(h.x, h.y)).sqrMagnitude;
                if (distance < nearest)
                {
                    nearest = distance;
                    best = i;
                }
            }
            if (best < 0)
            {
                return;
            }
            Vector2 to = new Vector2(Run.horde[best].x, Run.horde[best].y);
            HitHorde(best, damage, route);
            if (Run.doubleShotUntil > Run.seconds)
            {
                HitHorde(best, damage, route);
            }
            Impact(from, to, route);
            // 派生链只在主攻击这里展开；Area/Hit不会递归重新调用PlayerAttack，天然没有无限反应链。
            if (route == 0 && Run.mains[0] > 0)
            {
                int remaining = Mathf.Min(N("PierceLimit"), Run.mains[0] + (Run.evolved[0] ? 1 : 0));
                Vector2 ray = (to - from).normalized;
                for (int i = 0; i < Run.horde.Length && remaining > 0; i++)
                {
                    EndlessHorde h = Run.horde[i];
                    Vector2 delta = new Vector2(h.x, h.y) - from;
                    if (i != best && h.active && Vector2.Dot(delta, ray) > 0 && Mathf.Abs(delta.x * ray.y - delta.y * ray.x) < 0.7f && delta.magnitude <= V("OuterRange"))
                    {
                        HitHorde(i, damage, route);
                        Impact(from, new Vector2(h.x, h.y), route);
                        remaining--;
                    }
                }
            }
            if (route == 1 && Run.mains[1] > 0)
            {
                Area(to, Mathf.Min(V("AreaLimit"), 1 + Run.mains[1] * V("BlessingDamage")), damage * V("BurnRatio"), route, -1);
            }
            if (Run.mains[3] > 0)
            {
                Chain(to, best, damage * V("BurnRatio"));
            }
        }
        private bool OuterInRange(Piece p, EndlessHorde h)
        {
            Vector2 from = Position(p);
            float distance = Mathf.Max(Mathf.Abs(h.x - from.x), Mathf.Abs(h.y - from.y));
            float range = p.Def.IsRanged ? Mathf.Min(V("OuterRange"), p.Def.IsRanged ? RevGame.Data.Balance.AttackProfile(p.Def.Kind).Range : 1) : 1;
            if (Run.rare[0] && p.ConnectedCells <= p.Size * p.Size)
            {
                range = Mathf.Min(V("OuterRange"), range * (1 + V("LoneRangeBonus")));
            }
            if (distance > range)
            {
                return false;
            }
            if (p.Def.Kind == PieceKind.Crossbow && Mathf.Abs(h.x - from.x) > 0.65f && Mathf.Abs(h.y - from.y) > 0.65f)
            {
                return false;
            }
            if (p.Def.Class == PieceClass.Mage)
            {
                int dx = Math.Sign(h.x - from.x), dy = Math.Sign(h.y - from.y);
                bool horizontal = Mathf.Abs(h.x - from.x) >= Mathf.Abs(h.y - from.y);
                for (int n = 1; n <= N("OuterRange"); n++)
                {
                    var pos = new BoardPos(p.Pos.X + (horizontal ? dx * n : 0), p.Pos.Y + (horizontal ? 0 : dy * n));
                    Piece screen = Board.GetPiece(pos);
                    if (screen != null && screen != p)
                    {
                        return true;
                    }
                }
                return false;
            }
            return true;
        }
        // 每次雷链只用固定索引标记，最大8跳；不new集合，不反复命中同一个敌人。
        private readonly int[] _chainHit = new int[8];
        private void Chain(Vector2 origin, int first, float damage)
        {
            _chainHit[0] = first;
            int count = 1, limit = Math.Min(_chainHit.Length, Math.Min(N("ChainLimit"), Run.mains[3] + 1));
            while (count < limit)
            {
                int best = -1;
                float nearest = V("AreaLimit") * V("AreaLimit");
                for (int i = 0; i < Run.horde.Length; i++)
                {
                    if (!Run.horde[i].active || Run.horde[i].hp <= 0)
                    {
                        continue;
                    }
                    bool seen = false;
                    for (int n = 0; n < count; n++)
                    {
                        seen |= _chainHit[n] == i;
                    }
                    float distance = (new Vector2(Run.horde[i].x, Run.horde[i].y) - origin).sqrMagnitude;
                    if (!seen && distance < nearest)
                    {
                        best = i;
                        nearest = distance;
                    }
                }
                if (best < 0)
                {
                    break;
                }
                Vector2 next = new Vector2(Run.horde[best].x, Run.horde[best].y);
                HitHorde(best, damage * (Run.evolved[3] ? V("EvolutionDamage") : 1), 3);
                Impact(origin, next, 3);
                _chainHit[count++] = best;
                origin = next;
            }
        }
        private void HitHorde(int index, float damage, int route)
        {
            EndlessHorde h = Run.horde[index];
            if (!h.active || h.hp <= 0)
            {
                return;
            }
            Run.damage[route] += Math.Min(h.hp, damage);
            h.hp -= damage;
            if ((route == 1 && Run.mains[1] > 0) || (route == 0 && Run.seals[1] > 0))
            {
                h.burnUntil = Run.seconds + V("BurnSeconds");
                h.burnDamage = damage * V("BurnRatio");
            }
            if (Run.mains[4] > 0)
            {
                h.slowUntil = Run.seconds + V("FreezeSeconds");
                if (Run.evolved[4])
                {
                    h.frozenUntil = h.slowUntil;
                }
            }
            Run.horde[index] = h;
        }
        private void HitUnit(EndlessUnit unit, float damage, int route)
        {
            if (unit == null || !unit.Piece.IsAlive)
            {
                return;
            }
            if (unit.BrokenUntil > Run.seconds)
            {
                damage *= V("BossBreakDamage");
            }
            int applied = unit.Piece.TakeDamage(Mathf.CeilToInt(damage));
            Run.damage[route] += applied;
            if (route == 1 && Run.mains[1] > 0)
            {
                unit.BurnUntil = Run.seconds + V("BurnSeconds");
                unit.BurnDamage = damage * V("BurnRatio");
            }
            if (Run.mains[4] > 0)
            {
                unit.SlowUntil = Run.seconds + V("FreezeSeconds");
            }
        }
        private void Area(Vector2 center, float radius, float damage, int route, int excluded)
        {
            damage *= Run.rare[2] ? 1 + V("PaperAreaBonus") : 1;
            float squared = radius * radius;
            for (int i = 0; i < Run.horde.Length; i++)
            {
                EndlessHorde h = Run.horde[i];
                if (h.active && (new Vector2(h.x, h.y) - center).sqrMagnitude <= squared)
                {
                    HitHorde(i, damage, route);
                }
            }
            for (int i = 0; i < Units.Count; i++)
            {
                Piece p = Units[i].Piece;
                if (p.Side == GameSide.Enemy && p.Id != excluded && (Position(p) - center).sqrMagnitude <= squared)
                {
                    HitUnit(Units[i], damage, route);
                }
            }
            Impact(center, center, route, radius);
        }
        private void EnemyAct(EndlessUnit unit)
        {
            Piece p = unit.Piece;
            if (unit.BrokenUntil > Run.seconds || (p.IsLocked && unit.BossTemplate < 0))
            {
                return;
            }
            if (unit.AttackAt <= Run.seconds)
            {
                MoveRules.GetAttackTargets(Board, p, _targets);
                if (_targets.Count > 0)
                {
                    Piece target = Board.GetPiece(_targets[0]);
                    HurtPlayer(Find(target.Id), p.Def.Attack * p.AttackScale, unit);
                    Impact(Position(p), Position(target), 6);
                }
                else if (PieceAttackRules.Distance(p, new BoardPos(4, 4)) <= 1)
                {
                    // 领主体型大、很快就能走到棋心旁边。如果贴身伤害和普通棋一样，几十秒就磨死棋心，
                    // 玩家根本来不及清献祭潮。所以单独配倍率，让棋心主要被“看得见、躲得开”的技能威胁。
                    bool lord = unit.BossTemplate >= 0;
                    DamageHeart(p.Def.Attack * p.AttackScale * (lord ? V("BossContactHeartScale") : 1), lord ? "第" + unit.BossChapter + "章领主贴身" : "入侵棋接触棋心");
                }
                float slow = unit.SlowUntil > Run.seconds ? 1 - (unit.BossTemplate >= 0 ? V("BossSlowRatio") : V("SlowRatio")) : 1;
                unit.AttackAt = Run.seconds + V("EnemyAttackSeconds") / slow;
            }
            if (unit.MoveAt <= Run.seconds)
            {
                MoveRules.GetMoveTargets(Board, p, _targets);
                BoardPos chosen = p.Pos;
                int distance = Math.Abs(p.Pos.X - 4) + Math.Abs(p.Pos.Y - 4);
                for (int i = 0; i < _targets.Count; i++)
                {
                    BoardPos pos = _targets[i];
                    int candidate = Math.Abs(pos.X - 4) + Math.Abs(pos.Y - 4);
                    if (candidate < distance && Fits(pos, p.Size, p.Side, p))
                    {
                        chosen = pos;
                        distance = candidate;
                    }
                }
                if (!chosen.Equals(p.Pos))
                {
                    Board.Move(p, chosen);
                    RefreshRules();
                }
                unit.MoveAt = Run.seconds + V("EnemyMoveSeconds") / (unit.Role == 1 ? V("CircuitSpeedCap") : 1);
            }
        }
        private void HurtPlayer(EndlessUnit victim, float damage, EndlessUnit attacker)
        {
            if (victim == null || !victim.Piece.IsAlive)
            {
                return;
            }
            Piece p = victim.Piece;
            if (victim.SteadyShield)
            {
                victim.SteadyShield = false;
                victim.StationarySince = Run.seconds;
                return;
            }
            float defense = p.EyeDefenseMultiplier * p.EyeGuardDefenseMultiplier;
            damage *= Mathf.Max(1 - V("DamageReductionCap"), 1 / defense);
            int applied = p.TakeDamage(Mathf.CeilToInt(damage));
            if (applied > 0 && Run.mains[5] > 0 && victim.CounterAt <= Run.seconds)
            {
                victim.CounterAt = Run.seconds + V("CounterSeconds");
                float power = p.Def.Attack * (1 + Run.mains[5] * V("BlessingDamage"));
                Area(Position(p), Mathf.Min(V("AreaLimit"), 1 + Run.mains[5] * V("BlessingDamage")), power, 5, -1);
            }
        }
        /// <summary>Boss不走六章气零胜利事件。破阵窗口结束再等待重新开气，避免气零每帧重置8秒。</summary>
        private void UpdateBoss(EndlessUnit boss)
        {
            if (boss.Piece.OpenLiberties == 0 && boss.BrokenUntil == 0)
            {
                boss.BrokenUntil = Run.seconds + V("BossBreakSeconds");
                boss.WarningAt = 0;
            }
            if (boss.BrokenUntil > Run.seconds)
            {
                return;
            }
            if (boss.BrokenUntil > 0 && boss.BrokenUntil <= Run.seconds)
            {
                SpawnHorde();
                boss.BrokenUntil = -1;
                boss.Piece.IgnoreEncirclement = true;
            }
            if (boss.Piece.OpenLiberties > 0 && boss.BrokenUntil < 0)
            {
                boss.BrokenUntil = 0;
                boss.Piece.IgnoreEncirclement = false;
            }
            TickChapterSkill(boss);
        }
        /// <summary>地面预警和真实伤害共用章节几何，避免显示安全的格子仍被技能命中。</summary>
        public bool IsWarned(EndlessUnit boss, int x, int y)
        {
            if (boss == null || boss.WarningAt <= 0 || boss.ChapterSkill < (int)BossSkillKind.CometRails)
            {
                return false;
            }
            var kind = (BossSkillKind)boss.ChapterSkill;
            var def = BossSkillCatalog.Get(kind);
            return BossExpandedGeometry.Contains(kind, boss.SkillA, boss.SkillB, boss.SkillC, def.Radius, boss.SkillStage, new BoardPos(x, y));
        }
        private void ResolveDeaths()
        {
            // 主攻击加两代爆炸最多三轮；先耗尽本步死亡链，再允许存档和升级弹窗。
            for (int generation = 0; generation < 3; generation++)
            {
                for (int i = 0; i < Run.horde.Length; i++)
                {
                    if (Run.horde[i].active && Run.horde[i].hp <= 0)
                    {
                        KillHorde(i);
                    }
                }
            }
            for (int i = Units.Count - 1; i >= 0; i--)
            {
                EndlessUnit unit = Units[i];
                Piece p = unit.Piece;
                if (p.IsAlive)
                {
                    continue;
                }
                if (p.Side == GameSide.Player && p.IsEyeGarrison && !unit.Revived)
                {
                    unit.Revived = true;
                    p.TryReviveInEye();
                    for (int n = 0; n < Units.Count; n++)
                    {
                        Piece ally = Units[n].Piece;
                        if (ally != p && ally.Side == GameSide.Player && PieceAttackRules.Distance(ally, p) == 1)
                        {
                            ally.Heal(Mathf.CeilToInt(ally.MaxHp * V("EyeReviveHeal")));
                        }
                    }
                    Impact(Position(p), Position(p), 2, 1);
                    continue;
                }
                Board.Remove(p);
                Units.RemoveAt(i);
                if (p.Side == GameSide.Enemy)
                {
                    RewardEnemy(unit.Role, unit.Gate);
                }
            }
        }
        private void KillHorde(int index)
        {
            EndlessHorde h = Run.horde[index];
            if (!h.active)
            {
                return;
            }
            h.active = false;
            Run.horde[index] = h;
            RewardEnemy(h.kind, h.gate);
            Vector2 pos = new Vector2(h.x, h.y);
            if (h.surge)
            {
                OnSurgeKilled(h, pos);
            }
            Impact(pos, pos, h.burnUntil > Run.seconds ? 1 : 0, 0.5f);
            // 二代伤害写入待死亡目标，但不再赋予灼烧标志，避免递归爆炸。
            if (h.burnUntil > Run.seconds && Run.evolved[1] && h.chainGeneration < 2)
            {
                if (Run.breakthroughs.Contains(1) && h.chainGeneration == 0)
                {
                    int cell = Mathf.Clamp(Mathf.RoundToInt(h.y), 0, 8) * 9 + Mathf.Clamp(Mathf.RoundToInt(h.x), 0, 8);
                    Run.burnZoneUntil[cell] = Run.seconds + V("BurnSeconds");
                    Run.burnZoneDamage[cell] = h.burnDamage;
                }
                float damage = h.burnDamage * V("ExplosionSecondRatio");
                int chain = 0;
                for (int i = 0; i < Run.horde.Length; i++)
                {
                    EndlessHorde other = Run.horde[i];
                    if (other.active && other.hp > 0 && (new Vector2(other.x, other.y) - pos).sqrMagnitude <= V("AreaLimit"))
                    {
                        other.hp -= damage;
                        other.chainGeneration = Math.Max(other.chainGeneration, h.chainGeneration + 1);
                        Run.horde[i] = other;
                        chain++;
                    }
                }
                Run.maximumChain = Math.Max(Run.maximumChain, chain);
                Impact(pos, pos, 1, 1.5f);
            }
        }
        private void UpdateEyes(float step)
        {
            Run.healRemainder += step;
            if (Run.healRemainder >= 1)
            {
                Run.healRemainder -= 1;
                for (int i = 0; i < Units.Count; i++)
                {
                    Piece p = Units[i].Piece;
                    if (p.Side == GameSide.Player && p.HasEyeGuardAura)
                    {
                        p.Heal(Mathf.CeilToInt(p.MaxHp * Mathf.Min(V("HealRatioCap"), V("EyeHealRatio"))));
                    }
                }
            }
            if (Run.nextPulse <= Run.seconds && Run.mains[2] > 0)
            {
                for (int i = 0; i < Units.Count; i++)
                {
                    Piece p = Units[i].Piece;
                    if (p.Side == GameSide.Player && p.IsEyeGarrison)
                    {
                        Area(Position(p), V("AreaLimit"), p.Def.Attack * (1 + Run.mains[2] * V("BlessingDamage")), 2, -1);
                        if (Run.evolved[2])
                        {
                            PercentPulse(Position(p), V("AreaLimit"), V("PulseRatio"), 2);
                        }
                    }
                }
                Run.nextPulse = Run.seconds + V("PulseSeconds");
            }
            if (Run.evolved[2] && Run.nextBomb <= Run.seconds)
            {
                PercentPulse(new Vector2(4, 4), 20, V("BombRatio"), 2);
                Run.nextBomb = Run.seconds + V("BombSeconds");
            }
            if (Run.nextSummon > Run.seconds)
            {
                return;
            }
            Run.nextSummon = Run.seconds + V("SummonSeconds");
            int summons = 0;
            for (int i = 0; i < Units.Count; i++)
            {
                if (Units[i].Summoned)
                {
                    summons++;
                }
            }
            if (summons >= N("SummonLimit") || Occupied(GameSide.Player) >= N("PlayerCells"))
            {
                return;
            }
            for (int y = 0; y < 9; y++)
            {
                for (int x = 0; x < 9; x++)
                {
                    var pos = new BoardPos(x, y);
                    var cell = Board.GetCell(pos);
                    if (cell.IsEmpty && cell.EyeSide == GameSide.Player && Fits(pos, 1, GameSide.Player))
                    {
                        AddPlayer(PieceKind.Crossbow, x, y, false, true);
                        RefreshRules();
                        return;
                    }
                }
            }
        }
        public static Vector2 Position(Piece p)
        {
            return new Vector2(p.Pos.X + (p.Size - 1) * 0.5f, p.Pos.Y + (p.Size - 1) * 0.5f);
        }
    }
}
