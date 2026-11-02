using System;
using RevGame.Battle;
using RevGame.Core;
using RevGame.Pieces;
using UnityEngine;
namespace RevGame.Endless
{
    public sealed partial class EndlessService
    {
        private void UpdateBurnZones()
        {
            if (Run.nextZoneTick > Run.seconds)
            {
                return;
            }
            Run.nextZoneTick = Run.seconds + 1;
            for (int cell = 0; cell < Run.burnZoneUntil.Length; cell++)
            {
                if (Run.burnZoneUntil[cell] <= Run.seconds)
                {
                    continue;
                }
                Vector2 center = new Vector2(cell % 9, cell / 9);
                for (int i = 0; i < Run.horde.Length; i++)
                {
                    EndlessHorde h = Run.horde[i];
                    if (h.active && (new Vector2(h.x, h.y) - center).sqrMagnitude <= V("AreaLimit"))
                    {
                        ReportDamage(h.id, new Vector2(h.x, h.y), Mathf.Min(h.hp, Run.burnZoneDamage[cell]), GameSide.Enemy);
                        h.hp -= Run.burnZoneDamage[cell];
                        h.chainGeneration = 2;
                        Run.horde[i] = h;
                    }
                }
                Impact(center, center, 1, 1);
            }
        }
        private readonly bool[] _previousEyes = new bool[81];
        public int RareSlots()
        {
            int count = 0;
            for (int i = 0; i < Run.rare.Length; i++)
            {
                if (Run.rare[i])
                {
                    count++;
                }
            }
            return count;
        }
        public static string RareText(int id)
        {
            switch (id)
            {
                case 0: return "孤棋独行\n不相邻：攻击 +" + EndlessConfig.Value("LoneAttackBonus").ToString("P0") + "；射程 +" + EndlessConfig.Value("LoneRangeBonus").ToString("P0");
                case 1: return "逆风换眼\n主动破眼后附近棋短时加速；同眼每分钟一次";
                case 2: return "纸甲洪流\n生命上限 -" + EndlessConfig.Value("PaperHealthLoss").ToString("P0") + "；范围伤害 +" + EndlessConfig.Value("PaperAreaBonus").ToString("P0");
                default: return "稳阵静守\n持续不移动后获得一次抵挡；移动立即消失";
            }
        }
        private static bool FacingGate(int gate, Vector2 attacker, Vector2 shield)
        {
            return gate == 0 ? attacker.x >= shield.x : gate == 1 ? attacker.x <= shield.x : gate == 2 ? attacker.y >= shield.y : attacker.y <= shield.y;
        }
        /// <summary>破眼师只有一个可见目标与一个蓄力。命中前再核对目标仍是守眼骨架，移走可以躲开。</summary>
        private void UpdateBreaker(EndlessUnit unit)
        {
            if (unit.Role != 3 || !unit.Piece.IsAlive || unit.Piece.IsLocked)
            {
                return;
            }
            if (unit.BreakEyeAt > 0)
            {
                if (unit.BreakEyeAt <= Run.seconds)
                {
                    EndlessUnit target = Find(unit.BreakEyeTarget);
                    if (target != null && target.Piece.HasEyeGuardAura && PieceAttackRules.Distance(unit.Piece, target.Piece) <= N("OuterRange"))
                    {
                        int applied = target.Piece.TakeDamage(target.Piece.Hp);
                        // 破眼是一击处决，数字沿用六章“处决”样式。
                        ReportDamage(target.Piece.Id, Position(target.Piece), applied, GameSide.Player, BattleDamageKind.Execution);
                        Impact(Position(unit.Piece), Position(target.Piece), 6, 1, unit.Piece);
                    }
                    unit.BreakEyeAt = 0;
                    unit.NextSkillAt = Run.seconds + V("BreakEyeInterval");
                }
                return;
            }
            if (unit.NextSkillAt > Run.seconds)
            {
                return;
            }
            for (int i = 0; i < Units.Count; i++)
            {
                Piece target = Units[i].Piece;
                if (target.Side == GameSide.Player && target.HasEyeGuardAura && PieceAttackRules.Distance(unit.Piece, target) <= N("OuterRange"))
                {
                    unit.BreakEyeTarget = target.Id;
                    unit.BreakEyeAt = Run.seconds + V("WarningSeconds");
                    return;
                }
            }
            unit.NextSkillAt = Run.seconds + V("BreakEyeInterval");
        }
        /// <summary>每个眼的冷却绑定棋盘坐标，而非可被拆建重置的临时眼对象。</summary>
        private void RememberEyes()
        {
            for (int y = 0; y < 9; y++)
            {
                for (int x = 0; x < 9; x++)
                {
                    _previousEyes[y * 9 + x] = Board.GetCell(new BoardPos(x, y)).EyeSide == GameSide.Player;
                }
            }
        }
        private void ApplyBrokenEyeBonus()
        {
            if (!Run.rare[1])
            {
                return;
            }
            for (int y = 0; y < 9; y++)
            {
                for (int x = 0; x < 9; x++)
                {
                    int index = y * 9 + x;
                    if (!_previousEyes[index] || Board.GetCell(new BoardPos(x, y)).EyeSide == GameSide.Player || Run.brokenEyeReady[index] > Run.seconds)
                    {
                        continue;
                    }
                    Run.brokenEyeReady[index] = Run.seconds + V("MinuteSeconds");
                    for (int i = 0; i < Units.Count; i++)
                    {
                        Piece p = Units[i].Piece;
                        if (p.Side == GameSide.Player && PieceAttackRules.Distance(p, new BoardPos(x, y)) <= N("AreaLimit"))
                        {
                            Units[i].FuryUntil = Run.seconds + V("BrokenEyeSeconds");
                        }
                    }
                }
            }
        }
        /// <summary>高阶百分比攻击分类封顶；参考攻强只用主力基础攻击，不带临时乘区。</summary>
        private void PercentPulse(Vector2 center, float radius, float fraction, int route)
        {
            float attack = 0;
            int count = 0;
            for (int i = 0; i < Units.Count; i++)
            {
                if (Units[i].Piece.Side == GameSide.Player && !Units[i].Summoned && !Units[i].Stone)
                {
                    attack += Units[i].Piece.Def.Attack;
                    count++;
                }
            }
            attack /= Math.Max(1, count);
            float squared = radius * radius;
            for (int i = 0; i < Run.horde.Length; i++)
            {
                EndlessHorde h = Run.horde[i];
                if (h.active && (new Vector2(h.x, h.y) - center).sqrMagnitude <= squared)
                {
                    float damage = h.maxHp * fraction;
                    if (h.kind >= 5)
                    {
                        damage = Mathf.Min(damage, attack * V(h.kind >= 6 ? "BossPercentCap" : "ElitePercentCap"));
                    }
                    HitHorde(i, damage, route);
                }
            }
            for (int i = 0; i < Units.Count; i++)
            {
                EndlessUnit unit = Units[i];
                if (unit.Piece.Side == GameSide.Enemy && (Position(unit.Piece) - center).sqrMagnitude <= squared)
                {
                    float damage = unit.Piece.MaxHp * fraction;
                    if (unit.Elite || unit.BossTemplate >= 0)
                    {
                        damage = Mathf.Min(damage, attack * V(unit.BossTemplate >= 0 ? "BossPercentCap" : "ElitePercentCap"));
                    }
                    HitUnit(unit, damage, route);
                }
            }
            Impact(center, center, route, radius);
        }
    }
}
