// ============================================================
// 开发回归：直接调用真实缓存，覆盖生死/章回/配置输入与文案完全一致，不另造计分公式用于生产。
// 验证只构造独立内存服务，缓存与直接路径使用同一生产公式，不写真实存档。
// ============================================================
#if UNITY_EDITOR || REVGAME_OFFLINE_TESTS
using System;
using RevGame.Core;
using RevGame.Data;
using RevGame.Pieces;
namespace RevGame.Services
{
    public sealed partial class BattleService
    {
        /// <summary>固定造局比较文案/积分、生死失效与数量阈值；失败由调用方立即抛出原因。</summary>
        public static void VerifyPresentationCaches(Action<bool, string> check)
        {
            var service = new BattleService(false, 731);
            // 默认路径真实跨过0→1→0→1边界，防止低人数分支沿用上次大盘面的旧结果。
            service.ScoreCacheThresholdOverride = -1;
            service.Chapter = 1;
            service.Round = 1;
            check(service.ScoreMultiplier == 1f && service.ScorePopulationScans == 1, "0棋走直接路径");
            var boundary = new Piece(999, PieceCatalog.Get(PieceKind.Archer, StarTier.One), GameSide.Player);
            service._board.Place(boundary, new BoardPos(0, 0));
            service._playerPieces.Add(boundary);
            float boundaryValue = service.ScoreMultiplier;
            long boundaryScans = service.ScorePopulationScans;
            check(service.ScoreMultiplier == boundaryValue && service.ScorePopulationScans == boundaryScans, "达到实测阈值1棋切换缓存");
            service._board.Remove(boundary);
            service._playerPieces.Remove(boundary);
            check(service.ScoreMultiplier == 1f && service.ScorePopulationScans == boundaryScans + 1, "降到0棋立即切回直接路径");
            service._board.Place(boundary, new BoardPos(0, 0));
            service._playerPieces.Add(boundary);
            check(service.ScoreMultiplier == boundaryValue && service.ScorePopulationScans == boundaryScans + 2, "再次跨入阈值必须重算不能沿用旧收据");
            service._board.Remove(boundary);
            service._playerPieces.Remove(boundary);
            service.ScoreCacheThresholdOverride = 0;
            service.Chapter = 1;
            service.Round = 1;
            Piece first = new Piece(1, PieceCatalog.Get(PieceKind.Archer, StarTier.One), GameSide.Player);
            Piece second = new Piece(2, PieceCatalog.Get(PieceKind.Shield, StarTier.One), GameSide.Player);
            Piece target = new Piece(3, PieceCatalog.Get(PieceKind.Crossbow, StarTier.One), GameSide.Enemy);
            service._board.Place(first, new BoardPos(0, 0));
            service._playerPieces.Add(first);
            service._board.Place(target, new BoardPos(8, 8));
            service._enemyPieces.Add(target);
            check(service.ScoreMultiplier == 1f, "初始计分倍率");
            long scans = service.ScorePopulationScans;
            for (int i = 0; i < 100; i++)
            {
                check(service.ScoreMultiplier == 1f, "稳定计分缓存命中");
            }
            check(service.ScorePopulationScans == scans, "100次计分不扫描玩家名单");
            service._board.Place(second, new BoardPos(1, 0));
            service._playerPieces.Add(second);
            float two = Math.Min(Balance.ScoreMultiplierCap, 1f + Balance.ScorePieceGrowth);
            check(service.ScoreMultiplier == two, "新增棋子失效人数与倍率");
            second.TakeDamage(int.MaxValue);
            check(service.ScoreMultiplier == 1f, "致死尚未Remove也失效人数");
            second.IsEyeGarrison = true;
            check(second.TryReviveInEye() && service.ScoreMultiplier == two, "复活立刻重算人数");
            service._board.Remove(second);
            service._playerPieces.Remove(second);
            check(service.ScoreMultiplier == 1f, "移除同步倍率");
            service.Chapter = 2;
            service.Round = 3;
            float progress = Math.Min(Balance.ScoreMultiplierCap, 1f + ((Balance.RoundsPerChapter + 2) * Balance.ScoreRoundGrowth));
            check(service.ScoreMultiplier == progress, "章回变化不复用旧倍率");
            target.GoState = GroupState.Locked;
            string expected = first.Def.DisplayName + " → " + target.Def.DisplayName + " -7（锁死×"
                + Balance.LockedDamageMultiplier.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "）";
            string actual = service.FormatAttackLog(first, target, 7);
            check(actual == expected, "StringBuilder文案与原版逐字一致");
            check(ReferenceEquals(actual, service.FormatAttackLog(first, target, 7)), "重复战报返回同字符串实例");
            for (int i = 0; i < 300; i++)
            {
                service.FormatAttackLog(first, target, i);
            }
            check(service._attackLogCache.Count <= 256, "文案缓存有界不无限保留伤害");
            target.IgnoreEncirclement = true;
            check(service.FormatAttackLog(first, target, 7) == first.Def.DisplayName + " → " + target.Def.DisplayName + " -7", "免疫后文案键失效");
            service.StopEnemyAI();
            BenchmarkPresentationCaches(service, first, target);
        }
        /// <summary>相同输入对照原字符串/人数扫描与新缓存，五轮取中位，仅.NET局部CPU非整场帧率。</summary>
        private static void BenchmarkPresentationCaches(BattleService service, Piece attacker, Piece target)
        {
            // 此历史对照把原公式内嵌在循环，新公式走属性，JIT内联条件不同；不能用它确定数量阈值。
            // 阈值由MeasureScoreCacheThreshold经同一属性强制两条路径、AB/BA多轮单独测定。
            for (int i = 1; i < 36; i++)
            {
                var piece = new Piece(10 + i, PieceCatalog.Get(PieceKind.Crossbow, StarTier.One), GameSide.Player);
                service._board.Place(piece, new BoardPos(i % 9, i / 9));
                service._playerPieces.Add(piece);
            }
            float warmed = service.ScoreMultiplier;
            var oldLog = new double[5];
            var newLog = new double[5];
            var oldScore = new double[5];
            var newScore = new double[5];
            string content = null;
            float score = 0;
            service.FormatAttackLog(attacker, target, 7);
            var watch = new System.Diagnostics.Stopwatch();
            for (int round = 0; round < 5; round++)
            {
                watch.Restart();
                for (int i = 0; i < 100000; i++)
                {
                    content = attacker.Def.DisplayName + " → " + target.Def.DisplayName + " -" + (i % 8);
                }
                watch.Stop();
                oldLog[round] = watch.Elapsed.TotalMilliseconds;
                watch.Restart();
                for (int i = 0; i < 100000; i++)
                {
                    content = service.FormatAttackLog(attacker, target, i % 8);
                }
                watch.Stop();
                newLog[round] = watch.Elapsed.TotalMilliseconds;
                watch.Restart();
                for (int i = 0; i < 100000; i++)
                {
                    int alive = 0;
                    foreach (Piece piece in service._playerPieces)
                    {
                        if (piece.IsAlive)
                        {
                            alive++;
                        }
                    }
                    int progress = Math.Max(0, (service.Chapter - 1) * Balance.RoundsPerChapter + service.Round - 1);
                    score = Math.Min(Balance.ScoreMultiplierCap, (1f + progress * Balance.ScoreRoundGrowth) * (1f + Math.Max(0, alive - 1) * Balance.ScorePieceGrowth));
                }
                watch.Stop();
                oldScore[round] = watch.Elapsed.TotalMilliseconds;
                watch.Restart();
                for (int i = 0; i < 100000; i++)
                {
                    score = service.ScoreMultiplier;
                }
                watch.Stop();
                newScore[round] = watch.Elapsed.TotalMilliseconds;
            }
            Array.Sort(oldLog);
            Array.Sort(newLog);
            Array.Sort(oldScore);
            Array.Sort(newScore);
            GC.KeepAlive(content);
            GC.KeepAlive(score);
            Console.WriteLine("100000次战报原拼接/缓存ms=" + oldLog[2].ToString("F3") + "/" + newLog[2].ToString("F3"));
            Console.WriteLine("100000次计分原扫描/缓存ms=" + oldScore[2].ToString("F3") + "/" + newScore[2].ToString("F3") + "；人数=" + service._playerPieces.Count);
            long bytes = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 100000; i++)
            {
                content = attacker.Def.DisplayName + " → " + target.Def.DisplayName + " -" + (i % 8);
            }
            long oldBytes = GC.GetAllocatedBytesForCurrentThread() - bytes;
            bytes = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 100000; i++)
            {
                content = service.FormatAttackLog(attacker, target, i % 8);
            }
            long newBytes = GC.GetAllocatedBytesForCurrentThread() - bytes;
            GC.KeepAlive(content);
            GC.KeepAlive(warmed);
            Console.WriteLine("100000次战报分配Bytes原/缓存=" + oldBytes + "/" + newBytes);
        }
    }
}
#endif
