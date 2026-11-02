#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
using System;
using System.Collections.Generic;
using System.Diagnostics;
using RevGame.Core;
using UnityEngine;

namespace RevGame.Endless
{
    public sealed partial class EndlessService
    {
        /// <summary>独立规则夹具：实体寿命、空间查询顺序、无限额布阵、固定一格升星、盘外Boss与总承伤预算。</summary>
        public static object CheckEcsRules()
        {
            var passed = new List<string>();
            string previousSave = _isolatedSave;
            try
            {
                using (var s = new EndlessService())
                {
                    s.Begin(0, 0, 1102, true);
                    Check(s.Board.GetPiece(new BoardPos(4, 4)) == null && !s.CanPlaceAt(4, 4), "棋心禁放，保持中立不占炮架", passed);
                    s.Units.Clear();
                    s.Board.Reset();
                    for (int y = 0; y < 9; y++)
                    {
                        for (int x = 0; x < 9; x++)
                        {
                            if (x == 4 && y == 4)
                            {
                                continue;
                            }
                            s.AddPlayer((PieceKind)((x + y) % 2), x, y, false);
                        }
                    }
                    Check(s.PlayerCount() == 80 && s.Occupied(GameSide.Player) == 81, "80个可用格均可放，突破旧37枚/49格配额", passed);
                    Check(s.DebugSpawnBoss(6) && s.BossUnit().Outside && s.Occupied(GameSide.Enemy) == 0, "满盘时领主仍从盘外出场，不占玩家格", passed);
                    EndlessUnit boss = s.BossUnit();
                    int hp = boss.Piece.Hp;
                    s.HitUnit(boss, hp * 100f, 0);
                    Check(boss.Piece.Hp == hp, "入场交付前不受伤，不在出生首帧秒杀", passed);
                    s.Run.seconds = boss.BornAt + V("BossEntranceSeconds");
                    for (int i = 0; i < 200; i++)
                    {
                        s.HitUnit(boss, hp, i % 6);
                    }
                    Check(boss.Piece.Hp > 0 && hp - boss.Piece.Hp <= hp * V("BossDamagePerSecond") * V("SimulationStep"), "200次多来源伤害共用一个模拟步预算", passed);
                    float xBefore = boss.Center.x;
                    s.MarchOutsideBoss(boss, 1);
                    Check(boss.Center.x != xBefore && (boss.Center.x < 0 || boss.Center.x > 8), "领主真实步进逼近，始终保持盘外", passed);
                    Check(s.Save() && s.Restore(true) && s.BossUnit().Outside && s.PlayerCount() == 80, "ECS与盘外Boss存档恢复，不减玩家配额", passed);
                }
                using (var s = new EndlessService())
                {
                    s.Begin(0, 0, 1202, true);
                    s.Units.Clear();
                    s.Board.Reset();
                    s.AddPlayer(PieceKind.Archer, 0, 0, false);
                    s.AddPlayer(PieceKind.Archer, 1, 0, false);
                    s.AddPlayer(PieceKind.Archer, 2, 0, false);
                    s.Run.selectedUnit = s.Units[0].Piece.Id;
                    Check(s.MergeSelected() && s.Units.Count == 1 && s.Units[0].Piece.Size == 1 && s.Units[0].Piece.Def.Star == StarTier.Two, "二星升星只改变战力，身体仍一格", passed);
                    s.Run.seconds = V("MinuteSeconds") * 12;
                    Check(s.WaveDensityScale() > 1 && s.WaveSpeedScale() > 1, "后续波次增加出生数量和现存敌人移速", passed);
                }
                return new Dictionary<string, object> { ["success"] = true, ["passed"] = passed.Count, ["checks"] = passed };
            }
            finally
            {
                _isolatedSave = previousSave;
            }
        }

        /// <summary>显式压力造局只允许隔离局：沿正式出生入口填满预算，不访问玩家存档。</summary>
        public bool DebugStress(int enemies)
        {
            if (!Isolated || Run == null || enemies < 1 || enemies > Horde.Length)
            {
                return Reject("压力夹具需要隔离局与有效并发数");
            }
            Horde.Clear();
            for (int i = 0; i < enemies; i++)
            {
                if (!SpawnHorde())
                {
                    return Reject("压力夹具生成未完成");
                }
            }
            Run.phase = EndlessPhase.Fighting;
            return true;
        }

        /// <summary>
        /// 固定输入对照旧全扫描与ECS空间查询：同一组1024/240实体、2000矩形、逐个核对数量与顺序。
        /// 计时预热后各执行5轮，中位数只代表查询CPU，不替代整帧/GPU或手机验收。
        /// </summary>
        public static object BenchmarkEcs()
        {
            var rows = new List<object>();
            foreach (int count in new[] { 240, 1024 })
            {
                var world = new EndlessEcsWorld(count);
                var random = new System.Random(1102);
                var old = new EndlessHorde[count];
                for (int i = 0; i < count; i++)
                {
                    old[i] = new EndlessHorde { active = true, id = i + 1, hp = 100, maxHp = 100, x = (float)random.NextDouble() * 24 - 8, y = (float)random.NextDouble() * 12 - 2 };
                    world.Horde[i] = old[i];
                }
                var queries = new Vector2[2000];
                for (int i = 0; i < queries.Length; i++)
                {
                    queries[i] = new Vector2((float)random.NextDouble() * 12 - 2, (float)random.NextDouble() * 8);
                }
                var baseline = new List<int>(count);
                var result = new List<int>(count);
                foreach (Vector2 q in queries)
                {
                    Scan(old, q, baseline);
                    world.Query(q.x - 1.5f, q.y - 1.5f, q.x + 1.5f, q.y + 1.5f, result);
                    if (baseline.Count != result.Count)
                    {
                        throw new InvalidOperationException("ECS候选数量不一致");
                    }
                    for (int i = 0; i < result.Count; i++)
                    {
                        if (baseline[i] != result[i])
                        {
                            throw new InvalidOperationException("ECS候选顺序不一致");
                        }
                    }
                }
                var before = new double[5];
                var after = new double[5];
                for (int repeat = 0; repeat < 5; repeat++)
                {
                    long started = Stopwatch.GetTimestamp();
                    foreach (Vector2 q in queries)
                    {
                        Scan(old, q, baseline);
                    }
                    before[repeat] = (Stopwatch.GetTimestamp() - started) * 1000d / Stopwatch.Frequency;
                    started = Stopwatch.GetTimestamp();
                    foreach (Vector2 q in queries)
                    {
                        world.Query(q.x - 1.5f, q.y - 1.5f, q.x + 1.5f, q.y + 1.5f, result);
                    }
                    after[repeat] = (Stopwatch.GetTimestamp() - started) * 1000d / Stopwatch.Frequency;
                }
                Array.Sort(before);
                Array.Sort(after);
                rows.Add(new Dictionary<string, object> { ["entities"] = count, ["queries"] = queries.Length, ["repeats"] = 5, ["scanMedianMs"] = before[2], ["ecsMedianMs"] = after[2], ["scanRoundsMs"] = before, ["ecsRoundsMs"] = after, ["sameCandidatesAndOrder"] = true });
                int generation = world.Horde.Generation(0);
                world.Horde[0] = default;
                world.Horde[0] = new EndlessHorde { active = true, id = 100000, hp = 10, maxHp = 10 };
                if (world.Horde.Generation(0) == generation)
                {
                    throw new InvalidOperationException("实体槽复用未更新代次");
                }
            }
            return new Dictionary<string, object> { ["success"] = true, ["scope"] = "Unity主线程固定查询CPU，非整帧/设备", ["rows"] = rows };
        }
        private static void Scan(EndlessHorde[] data, Vector2 center, List<int> result)
        {
            result.Clear();
            for (int i = 0; i < data.Length; i++)
            {
                EndlessHorde h = data[i];
                if (h.active && h.hp > 0 && h.x >= center.x - 1.5f && h.x <= center.x + 1.5f && h.y >= center.y - 1.5f && h.y <= center.y + 1.5f)
                {
                    result.Add(i);
                }
            }
        }
    }
}
#endif
