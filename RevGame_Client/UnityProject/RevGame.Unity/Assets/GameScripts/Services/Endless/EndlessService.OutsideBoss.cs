using RevGame.Core;
using RevGame.Data;
using RevGame.Pieces;
using UnityEngine;

namespace RevGame.Endless
{
    public sealed partial class EndlessService
    {
        /// <summary>按巡回槽创建盘外领主。沿同一出生口走来，完全不占用玩家格子，也不参与围棋气零。</summary>
        private bool SpawnOutsideBoss(int template)
        {
            if (HasBossIdentity())
            {
                return false;
            }
            NextLane(out int gate, out int row);
            float scale = V("BossHpScale") * WaveHpScale();
            scale *= Mathf.Min(V("BossHpCap"), 1 + (Run.circuit - 1) * V("CircuitHpGrowth"));
            scale *= Run.difficulty == 1 ? V("DifficultyHp") : 1;
            var piece = new Piece(Run.nextId++, PieceCatalog.Get(PieceKind.HeavySword, StarTier.One), GameSide.Enemy, 3, true, false, scale, V("EnemyAttackScale") * WaveAttackScale());
            var boss = new EndlessUnit
            {
                Piece = piece, Outside = true, Center = new Vector2(gate == 0 ? -3 : 11, Mathf.Clamp(row, 1, 7)),
                BossTemplate = template, Role = template + 6, Gate = gate,
                BossChapter = ((Run.circuit - 1) * 4 + template) % 6 + 1,
                BornAt = Run.seconds, NextSkillAt = Run.seconds + V("BossEntranceSeconds")
            };
            piece.Pos = new BoardPos(Mathf.RoundToInt(boss.Center.x) - 1, Mathf.RoundToInt(boss.Center.y) - 1);
            Units.Add(boss);
            return true;
        }
        /// <summary>领主在盘外朝边缘靠近；不跨入格子，技能仍投射到棋盘，玩家可移动躲避预警。</summary>
        private void MarchOutsideBoss(EndlessUnit boss, float step)
        {
            if (!boss.Outside)
            {
                return;
            }
            float destination = boss.Gate == 0 ? -V("BossStandoff") : 8 + V("BossStandoff");
            float speed = V("HordeSpeed") * V("BossMoveScale") * WaveSpeedScale();
            boss.Center.x = Mathf.MoveTowards(boss.Center.x, destination, speed * step);
            boss.Piece.Pos = new BoardPos(Mathf.RoundToInt(boss.Center.x) - 1, Mathf.RoundToInt(boss.Center.y) - 1);
        }
        /// <summary>连续盘外坐标与盘内格坐标统一为表现中心；传null代表已死亡/已移除。</summary>
        public Vector2 ActorPosition(EndlessUnit actor)
        {
            return actor == null ? Vector2.zero : actor.Outside ? actor.Center : Position(actor.Piece);
        }
        /// <summary>射线先取覆盖矩形，精确宽度仍由OnRay校验；结果归调用者，不缓存随HP变化的目标。</summary>
        private void QueryRay(Vector2 origin, Vector2 direction, float length, System.Collections.Generic.List<int> buffer)
        {
            Vector2 end = origin + direction * length;
            const float padding = .7f; // 与现有穿刺几何容差配对，非伤害参数。
            World.Query(Mathf.Min(origin.x, end.x) - padding, Mathf.Min(origin.y, end.y) - padding,
                Mathf.Max(origin.x, end.x) + padding, Mathf.Max(origin.y, end.y) + padding, buffer);
        }
        /// <summary>
        /// 所有领主伤害共用本步总预算。入口演出期间可见但尚未交战；之后持续有效掉血，
        /// 高速箭雨、剑气、献祭潮均计入同一预算，不能靠多来源绕过并在首步击杀。
        /// 上限为最大生命的每秒比例×模拟步长，实际扣血回执使用最终值，溢出不记成绩。
        /// </summary>
        private int ApplyUnitDamage(EndlessUnit unit, float damage)
        {
            if (unit.Outside && unit.BossTemplate >= 0)
            {
                if (Run.seconds < unit.BornAt + V("BossEntranceSeconds"))
                {
                    return 0;
                }
                if (unit.DamageBudgetAt != Run.seconds)
                {
                    unit.DamageBudgetAt = Run.seconds;
                    unit.DamageBudgetUsed = 0;
                }
                float budget = unit.Piece.MaxHp * V("BossDamagePerSecond") * V("SimulationStep");
                int allowed = Mathf.Max(0, Mathf.FloorToInt(budget - unit.DamageBudgetUsed));
                // 防御倍率来自无尽Excel，先减伤再进入同一步总预算；任何伤害来源都不能绕过防御。
                // 倍率1为旧无额外防御，1.5为提高50%防御，等价承受原伤害的2/3；不改变六章Boss。
                float defended = damage / V("BossDefenseScale");
                int actual = unit.Piece.TakeDamage(Mathf.Min(allowed, Mathf.CeilToInt(defended)));
                unit.DamageBudgetUsed += actual;
                return actual;
            }
            return unit.Piece.TakeDamage(Mathf.CeilToInt(damage));
        }
    }
}
