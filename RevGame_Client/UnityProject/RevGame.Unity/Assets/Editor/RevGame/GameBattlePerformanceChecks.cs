// ============================================================
// GameBattlePerformanceChecks.cs —— 固定输入的战斗性能对照实验。
// 只在编辑器非Play状态运行，避免独立模拟的事件污染玩家正在看的战斗。
// 先预热，再取五次中位数；测的是本机规则CPU时间，不冒充手机帧率或GPU性能。
// ============================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using RevGame.Battle;
using RevGame.Board;
using RevGame.Core;
using RevGame.Data;
using RevGame.Pieces;
using RevGame.Services;

namespace RevGame.Editor
{
    public static class GameBattlePerformanceChecks
    {
        /// <summary>本次前后对照固定为同一名单/棋盘/AI档位；不以必胜作为性能断言，也不让Excel人数变化改变工作量。</summary>
        public static string RunFixed(string label)
        {
            if (UnityEditor.EditorApplication.isPlaying) { throw new InvalidOperationException("请退出Play后测量规则"); }
            GameConfigTestSupport.Ensure();
            var formation = new List<double>();
            var deployment = new List<double>();
            for (int sample = 0; sample < 6; sample++)
            {
                var roster = new List<Piece>();
                for (int i = 0; i < 10; i++)
                {
                    roster.Add(new Piece(i + 1, PieceCatalog.Get(i < 5 ? PieceKind.Shield : PieceKind.Archer, StarTier.One), GameSide.Enemy));
                }
                var players = new List<Piece>();
                var watch = Stopwatch.StartNew();
                if (!EnemyFormationPlanner.TryArrange(roster, players, true, out string reason, 4)) { throw new InvalidOperationException(reason); }
                watch.Stop();
                double deploy = MeasureDeployment();
                if (sample > 0) { formation.Add(watch.Elapsed.TotalMilliseconds); deployment.Add(deploy); }
            }
            formation.Sort(); deployment.Sort();
            long start = GC.GetAllocatedBytesForCurrentThread();
            GC.KeepAlive(new byte[102400]);
            string report = label + "\n固定10棋整队中位数(ms)=" + formation[2].ToString("F3")
                + "\n固定棋盘AI4补子中位数(ms/次)=" + deployment[2].ToString("F3")
                + "\n分配计数器有效=" + (GC.GetAllocatedBytesForCurrentThread() > start)
                + "\n整队样本=" + string.Join(",", formation) + "\n补子样本=" + string.Join(",", deployment);
            Directory.CreateDirectory("Temp/ScoreUpgrade");
            File.WriteAllText("Temp/ScoreUpgrade/performance-" + label + ".txt", report);
            return report;
        }
        /// <summary>保存可重复的前后测量；label建议使用before/after，结果进入Temp，不进入资源包。</summary>
        public static string Run(string label)
        {
            if (UnityEditor.EditorApplication.isPlaying) { throw new InvalidOperationException("请退出Play后测量独立规则"); }
            GameConfigTestSupport.Ensure();
            MeasureBattle();
            var battle = new List<double>();
            var deployment = new List<double>();
            for (int i = 0; i < 5; i++)
            {
                battle.Add(MeasureBattle());
                deployment.Add(MeasureDeployment());
            }
            battle.Sort();
            deployment.Sort();
            // Unity此Mono版本的分配计数器可能永远返回0，必须用已知分配探针验证，不能误报“零GC”。
            long before = GC.GetAllocatedBytesForCurrentThread();
            var probe = new byte[102400];
            GC.KeepAlive(probe);
            bool counterWorks = GC.GetAllocatedBytesForCurrentThread() > before;
            string report = label + "\n战斗规则中位数(ms)=" + battle[2].ToString("F3")
                + "\n战术落子中位数(ms/次)=" + deployment[2].ToString("F3")
                + "\n线程分配计数器有效=" + counterWorks
                + "\n战斗五次(ms)=" + string.Join(",", battle)
                + "\n落子五次(ms/次)=" + string.Join(",", deployment);
            Directory.CreateDirectory("Temp/TacticsVerification");
            File.WriteAllText("Temp/TacticsVerification/performance-" + label + ".txt", report);
            return report;
        }

        /// <summary>固定第6章第9轮、随机种子381与单兵落点；排除初始化，只计时完整战斗模拟。</summary>
        private static double MeasureBattle()
        {
            var service = new BattleService(true, 381);
            if (!service.StartRound(6, 9) || !service.TryPlayCard(service.Hand[0].Id, new BoardPos(0, 0)))
            {
                throw new InvalidOperationException(service.LastError);
            }
            service.Tick(3f);
            if (!service.StartBattle()) { throw new InvalidOperationException(service.LastError); }
            var watch = Stopwatch.StartNew();
            for (int step = 0; step < 300 && service.Phase == GamePhase.Battle; step++)
            {
                service.Tick(Balance.BattleSimulationStepSeconds);
            }
            watch.Stop();
            if (service.LastWinner != GameSide.Player) { throw new InvalidOperationException("性能优化改变了基准战斗胜负"); }
            return watch.Elapsed.TotalMilliseconds;
        }

        /// <summary>独立测最复杂AI档位；重复同一棋盘，不提交落点，保证每次测量的工作量相同。</summary>
        private static double MeasureDeployment()
        {
            var board = new BoardModel(9);
            int id = 1;
            for (int x = 0; x < 9; x += 2)
            {
                board.Place(new Piece(id++, PieceCatalog.Get(PieceKind.Archer, StarTier.One), GameSide.Player), new BoardPos(x, 1));
                board.Place(new Piece(id++, PieceCatalog.Get(PieceKind.Shield, StarTier.One), GameSide.Enemy), new BoardPos(x, 6));
            }
            GoRules.Evaluate(board);
            var newcomer = new Piece(100, PieceCatalog.Get(PieceKind.Archer, StarTier.One), GameSide.Enemy);
            var watch = Stopwatch.StartNew();
            for (int i = 0; i < 10; i++)
            {
                BoardPos pos;
                string reason;
                if (!EnemyTactics.TryChooseDeployment(board, newcomer, 4, out pos, out reason))
                {
                    throw new InvalidOperationException("基准棋盘没有合法落点");
                }
            }
            watch.Stop();
            return watch.Elapsed.TotalMilliseconds / 10;
        }
    }
}
