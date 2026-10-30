// ============================================================
// BattleService.CacheThresholdChecks.cs —— 用真实计分属性扫描1～81颗玩家棋的路径成本。
// 只构造独立内存服务，不派发攻击、不写存档。两路径都经同一ScoreMultiplier属性和相同配置公式。
// 缓存优势取决于查询间盘面变化率，报告分稳定盘和每次失效两组，不将.NET阈值冒充Unity阈值。
// ============================================================
#if UNITY_EDITOR || REVGAME_OFFLINE_TESTS
using System;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using RevGame.Core;
using RevGame.Data;
using RevGame.Pieces;
namespace RevGame.Services
{
    public sealed partial class BattleService
    {
        /// <summary>返回CSV，只在全部计时结束后生成；rounds/iterations是测试工作量，不修改玩法配置。</summary>
        public static string MeasureScoreCacheThreshold(int iterations = 100000, int rounds = 7)
        {
            if (iterations < 1000 || iterations > 1000000 || rounds < 3 || rounds > 11) { throw new ArgumentException("阈值测量工作量越界"); }
            var directTimes = new double[81]; var cachedTimes = new double[81]; var invalidatedTimes = new double[81];
            var direct = new double[rounds]; var cached = new double[rounds]; var invalidated = new double[rounds];
            var service = new BattleService(false, 830);
            service.Chapter = 4; service.Round = 3;
            var watch = new Stopwatch(); float sink = 0;
            // 从一棋逐次加到81棋，同一服务同一状态；先预热每条路径，原与缓存查询次数相同。
            for (int n = 1; n <= 81; n++)
            {
                var piece = new Piece(n, PieceCatalog.Get(PieceKind.Crossbow, StarTier.One), GameSide.Player);
                if (!service._board.Place(piece, new BoardPos((n - 1) % 9, (n - 1) / 9), respectEnemyEyes: false))
                { throw new InvalidOperationException("阈值夹具占格失败"); }
                service._playerPieces.Add(piece);
                for (int mode = 0; mode < 2; mode++)
                {
                    service.ScoreCacheThresholdOverride = mode == 0 ? 82 : 0;
                    for (int i = 0; i < 2000; i++) { sink += service.ScoreMultiplier; }
                }
                for (int sample = 0; sample < rounds; sample++)
                {
                    // AB/BA交替，避免总先扫描路径把JIT/温度变化固定算到缓存收益上。
                    for (int path = 0; path < 2; path++)
                    {
                        bool useCache = (sample + path) % 2 == 0;
                        service.ScoreCacheThresholdOverride = useCache ? 0 : 82;
                        watch.Restart();
                        for (int i = 0; i < iterations; i++) { sink += service.ScoreMultiplier; }
                        watch.Stop();
                        (useCache ? cached : direct)[sample] = watch.Elapsed.TotalMilliseconds;
                    }
                    service.ScoreCacheThresholdOverride = 0;
                    watch.Restart();
                    for (int i = 0; i < iterations; i++)
                    {
                        // 最坏场景每次生死/可见版本变，缓存必须重算，不能靠少算失效获得假优势。
                        service._board.TargetStateChanged(); sink += service.ScoreMultiplier;
                    }
                    watch.Stop(); invalidated[sample] = watch.Elapsed.TotalMilliseconds;
                }
                Array.Sort(direct); Array.Sort(cached); Array.Sort(invalidated);
                directTimes[n - 1] = direct[rounds / 2]; cachedTimes[n - 1] = cached[rounds / 2]; invalidatedTimes[n - 1] = invalidated[rounds / 2];
            }
            GC.KeepAlive(sink);
            // 热窗口不生成CSV；所有数据已测完再格式化，减少观察者对阈值的污染。
            var output = new StringBuilder("players,iterations,rounds,directMs,cachedMs,invalidatedMs,reductionPercent\n");
            for (int i = 0; i < 81; i++)
            {
                output.Append(i + 1).Append(',').Append(iterations).Append(',').Append(rounds).Append(',');
                output.Append(directTimes[i].ToString("F6", CultureInfo.InvariantCulture)).Append(',');
                output.Append(cachedTimes[i].ToString("F6", CultureInfo.InvariantCulture)).Append(',');
                output.Append(invalidatedTimes[i].ToString("F6", CultureInfo.InvariantCulture)).Append(',');
                output.Append(((1 - cachedTimes[i] / directTimes[i]) * 100).ToString("F3", CultureInfo.InvariantCulture)).Append('\n');
            }
            return output.ToString();
        }
    }
}
#endif
