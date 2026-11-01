#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
using System;
using System.Collections.Generic;
using RevGame.Core;
namespace RevGame.Endless
{
    public sealed partial class EndlessService
    {
        /// <summary>规则夹具只建立前置条件，断言读真实业务结果；不对生产局操作，不代表正常难度通关。</summary>
        public static object CheckExtendedRules()
        {
            var passed = new List<string>();
            string previousSave = _isolatedSave;
            try
            {
                using (var s = new EndlessService())
                {
                    s.Begin(0, 0, 1101, true);
                    // 身份转换夹具：空棋盘隔离掉玩家攻击，让转盘前后生命可以精确比较。
                    for (int i = s.Units.Count - 1; i >= 0; i--)
                    {
                        s.Board.Remove(s.Units[i].Piece);
                    }
                    s.Units.Clear();
                    s.QueueInvader(false, 0);
                    int index = -1;
                    for (int i = 0; i < s.Run.horde.Length; i++)
                    {
                        if (s.Run.horde[i].active)
                        {
                            index = i;
                            break;
                        }
                    }
                    int identity = s.Run.horde[index].id;
                    s.HitHorde(index, 10, 0);
                    float remaining = s.Run.horde[index].hp;
                    s.StartWave();
                    // 精确推进外围移动，保持真实预警和转盘入口。
                    for (int i = 0; i < 150 && s.Find(identity) == null; i++)
                    {
                        s.Run.seconds += .1f;
                        s.UpdateHorde(.1f);
                    }
                    EndlessUnit boss = s.Find(identity);
                    Check(boss != null && !s.Run.horde[index].active && boss.Piece.Hp == remaining, "外围转盘保留唯一身份与生命，不复制敌人", passed);
                    Check(s.Run.bossKills == 0 && s.Run.cores == 0, "转盘不发死亡奖励", passed);
                    s.Board.Move(boss.Piece, new BoardPos(0, 0));
                    for (int i = 0; i < 3; i++)
                    {
                        s.AddPlayer(PieceKind.Shield, 3, i, true);
                        s.AddPlayer(PieceKind.Shield, i, 3, true);
                    }
                    s.RefreshRules();
                    s.UpdateBoss(boss);
                    Check(boss.Piece.OpenLiberties == 0 && boss.BrokenUntil > s.Run.seconds && boss.Piece.IsAlive && s.Run.bossKills == 0, "Boss气零进入破阵，不自动击杀或结束本局", passed);
                    s.HitUnit(boss, boss.Piece.MaxHp, 0);
                    s.ResolveDeaths();
                    Check(s.Run.bossKills == 1 && s.Run.cores == 1, "真实伤害击杀Boss仅结算一个核心", passed);
                    s.ResolveDeaths();
                    Check(s.Run.bossKills == 1 && s.Run.cores == 1, "重复死亡清理不重复发奖", passed);
                    s.Run.score = long.MaxValue - 1;
                    s.AddScore(10);
                    Check(s.Run.score == long.MaxValue, "long计分饱和不溢出", passed);
                    s.Run.phase = EndlessPhase.Preparation;
                    for (int route = 0; route < 6; route++)
                    {
                        // 配方门禁夹具，明确不是自然升级速度测试。
                        s.Run.mains[route] = N("MainMax");
                        s.Run.seals[route] = 1;
                        s.Run.cores = 1;
                        Check(s.Evolve(route) && s.Run.cores == 0 && s.Run.evolved[route], "流派" + route + "完整配方进化消耗核心", passed);
                        Check(!s.Evolve(route) && s.Run.cores == 0, "流派" + route + "重复进化拒绝", passed);
                    }
                    for (int i = 0; i < 5; i++)
                    {
                        s.Run.phase = EndlessPhase.Circuit;
                        Check(s.ContinueCircuit(i % 4, i % 4), "合法巡回续战" + i, passed);
                    }
                    Check(s.Run.breakthroughs.Count <= N("SealSlots") && s.Run.affixes.Count <= N("SealSlots"), "巡回词条和突破印有界", passed);
                    s.Save();
                    int count = s.Units.Count, version = s.Board.TopologyVersion;
                    _isolatedSave = "{\"payload\":\"invalid\",\"checksum\":\"invalid\"}";
                    Check(!s.Restore(true) && s.Units.Count == count && s.Board.TopologyVersion == version, "损坏快照拒绝且不清空当前棋局", passed);
                }
                using (var high = new EndlessService())
                using (var low = new EndlessService())
                {
                    high.Begin(0, 0, 1101, true);
                    low.Begin(0, 0, 1101, true);
                    low.Run.lowEffects = true;
                    low.Run.flashes = false;
                    low.Run.shake = false;
                    high.StartWave();
                    low.StartWave();
                    for (int i = 0; i < 900; i++)
                    {
                        AutoChoice(high);
                        AutoChoice(low);
                        high.Tick(.1f);
                        low.Tick(.1f);
                    }
                    Check(high.Run.random == low.Run.random && high.Run.score == low.Run.score && high.Run.heart == low.Run.heart && high.Run.kills == low.Run.kills, "同种子高低特效90战斗秒结果一致", passed);
                    Check(high.HordeCount() <= N("HordeLimit") && high.Occupied(GameSide.Enemy) <= N("EnemyCells"), "持续敌潮并发与入侵占地上限", passed);
                }
                return new Dictionary<string, object>
                {
                    ["success"] = true, ["passed"] = passed.Count, ["checks"] = passed,
                    ["scope"] = "独立规则夹具，包含空盘转换/满配方/计分边界；不代表自然通关或长测"
                };
            }
            finally
            {
                _isolatedSave = previousSave;
            }
        }
        private static void AutoChoice(EndlessService s)
        {
            if (s.Run.phase == EndlessPhase.Upgrade)
            {
                s.Choose(0);
            }
            else if (s.Run.phase == EndlessPhase.Preparation)
            {
                s.StartWave();
            }
            else if (s.Run.phase == EndlessPhase.Event)
            {
                s.SelectEvent(false);
            }
        }
        private static void Check(bool result, string name, List<string> passed)
        {
            if (!result)
            {
                throw new InvalidOperationException("无尽规则断言失败：" + name);
            }
            passed.Add(name);
        }
    }
}
#endif
