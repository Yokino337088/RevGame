// 缓存正反例链接生产MoveRules/BoardModel，不复制算法；逐次与未缓存版本比对有序结果。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using RevGame.Battle;
using RevGame.Board;
using RevGame.Core;
using RevGame.Data;
using RevGame.Pieces;

// 控制台链接真实生产源码的对照测试，不复制另一套算法。
// 逐次比较缓存/原函数的候选数与顺序，再检验随机变盘、快照独立和清理；通过不代表GPU或笔记本FPS。
internal static class QueryCacheRegression
{
    // 本轮成功断言累计数，失败立即抛异常而不是继续“计通过”；重复坐标检查也计数，不等于场景数。
    private static int _checks;
    // ok必须来自实际结果比较。失败包含中文原因并由Program返回非零退出，方便自动化识别。
    private static void Check(bool ok, string reason)
    {
        _checks++; if (!ok) { throw new InvalidOperationException("查询缓存回归：" + reason); }
    }
    // 同一盘、同一actor、两个独立缓冲分别运行缓存入口和未缓存原函数。
    // 目标顺序也必须一致，因为AI严格大于才换同分目标，数量相同但顺序不同仍是规则回归。
    private static void Compare(BoardModel board, Piece actor, List<BoardPos> cached, List<BoardPos> expected)
    {
        MoveRules.GetAttackTargets(board, actor, cached); MoveRules.CalculateAttackTargets(board, actor, expected);
        Check(cached.Count == expected.Count, "候选数相同 " + actor.Def.Kind);
        for (int i = 0; i < cached.Count; i++) { Check(cached[i] == expected[i], "候选顺序相同 " + actor.Def.Kind + "/" + i); }
    }
    // 遍历全部棋种和阵营，依次制造掉血/死亡/隐藏/移动/冻结/升星/重置，再比较真实查询结果。
    public static int Run()
    {
        _checks = 0; var cached = new List<BoardPos>(81); var expected = new List<BoardPos>(81);
        foreach (PieceKind kind in Enum.GetValues(typeof(PieceKind)))
        {
            for (int side = 0; side < 2; side++)
            {
                var board = new BoardModel(9); var actor = new Piece(1, PieceCatalog.Get(kind, StarTier.One), (GameSide)side, 1);
                Check(board.Place(actor, new BoardPos(4, 4), respectEnemyEyes: false), "建立棋种射界对照");
                var targets = new List<Piece>();
                for (int i = 0; i < 8; i++)
                {
                    var p = new Piece(2 + i, PieceCatalog.Get(i % 2 == 0 ? PieceKind.Shield : PieceKind.Archer, StarTier.One), (GameSide)(1 - side), 1, hpScale: 10);
                    var pos = new BoardPos(i % 3 * 3, i / 3 * 3); Check(board.Place(p, pos, respectEnemyEyes: false), "固定目标合法"); targets.Add(p);
                }
                GoRules.Evaluate(board); Compare(board, actor, cached, expected);
                long misses = board.AttackCacheMisses;
                Compare(board, actor, cached, expected);
                Check(board.AttackCacheMisses == misses && board.AttackCacheHits > 0, "相同输入命中缓存");
                targets[0].TakeDamage(1); Compare(board, actor, cached, expected);
                Check(board.AttackCacheMisses == misses, "非致死伤害不使几何失效");
                targets[0].BossHidden = true; Compare(board, actor, cached, expected);
                Check(board.AttackCacheMisses > misses, "隐藏目标立即失效");
                targets[0].BossHidden = false; Compare(board, actor, cached, expected);
                targets[1].TakeDamage(int.MaxValue); Compare(board, actor, cached, expected);
                Check(board.FindPiece(targets[1].Id) == targets[1], "致死到移除之间身份仍可查，候选与原规则相同");
                board.Remove(targets[1]); Compare(board, actor, cached, expected);
                Check(board.FindPiece(targets[1].Id) == null, "移除同步清Id索引");
                Check(board.Move(targets[2], new BoardPos(8, 8), respectEnemyEyes: false), "移动目标成功"); Compare(board, actor, cached, expected);
                actor.BossFrozen = true; Compare(board, actor, cached, expected); Check(cached.Count == 0, "冻结禁攻不复用旧候选");
                actor.BossFrozen = false; Compare(board, actor, cached, expected);
                actor.GoState = GroupState.Locked; Compare(board, actor, cached, expected);
                actor.IgnoreEncirclement = true; Compare(board, actor, cached, expected);
                actor.IgnoreEncirclement = false; actor.GoState = GroupState.Normal;
                Check(actor.UpgradeInPlace(), "升星仍原身份"); Compare(board, actor, cached, expected);
                Check(board.FindPiece(actor.Id) == actor, "升星不丢索引");
                var trial = board.Tactics.Trial(board); var copy = trial.FindPiece(actor.Id);
                Check(copy != actor && copy.Def == actor.Def && copy.Hp == actor.Hp, "深副本独立，Def只读共享");
                Check(trial.Move(copy, new BoardPos(7, 4), respectEnemyEyes: false), "试盘移动不改变实盘");
                copy.TakeDamage(2); Check(actor.Pos == new BoardPos(4, 4), "实盘位置不被草稿污染");
                var refreshed = board.Tactics.Trial(board);
                Check(ReferenceEquals(trial, refreshed) && ReferenceEquals(copy, refreshed.FindPiece(actor.Id)), "试盘与副本复用同一对象");
                Check(copy.Pos == actor.Pos && copy.Hp == actor.Hp && copy.Def == actor.Def, "试盘重新覆盖真实状态");
                board.Reset(); Check(board.FindPiece(actor.Id) == null && actor.QueryBoard == null, "重置清索引与棋盘归属");
                var newcomer = new Piece(actor.Id, actor.Def, actor.Side, 1); Check(board.Place(newcomer, new BoardPos(1, 1)), "同Id新一局身份合法");
                Check(!board.Place(new Piece(actor.Id, actor.Def, actor.Side, 1), new BoardPos(2, 2)), "重复Id拒绝，索引不覆盖");
                Compare(board, newcomer, cached, expected);
            }
        }
        RandomBoardChanges(cached, expected);
        RevGame.Services.BattleService.VerifyPresentationCaches(Check);
        Benchmark(); Console.WriteLine("查询缓存通过：" + _checks + "条断言"); return _checks;
    }
    /// <summary>反复改变占格与目标状态；每次逐棋比对原算法，检查缓存失效和连通块顺序。</summary>
    private static void RandomBoardChanges(List<BoardPos> cached, List<BoardPos> expected)
    {
        var random = new Random(1030);
        var board = new BoardModel(9);
        var pieces = new List<Piece>(81);
        int id = 1;
        for (int step = 0; step < 300; step++)
        {
            board.CopyPieces(pieces);
            if (pieces.Count == 0 || random.Next(4) == 0)
            {
                var piece = new Piece(id++, PieceCatalog.Get((PieceKind)random.Next(9), StarTier.One), (GameSide)random.Next(2), 1);
                board.Place(piece, new BoardPos(random.Next(9), random.Next(9)), respectEnemyEyes: false);
            }
            else
            {
                Piece piece = pieces[random.Next(pieces.Count)];
                switch (random.Next(5))
                {
                    case 0: board.Move(piece, new BoardPos(random.Next(9), random.Next(9)), respectEnemyEyes: false); break;
                    case 1: board.Remove(piece); break;
                    case 2: piece.BossHidden = !piece.BossHidden; break;
                    case 3: piece.BossFrozen = !piece.BossFrozen; break;
                    case 4: piece.TakeDamage(random.Next(1, 12)); break;
                }
            }
            GoRules.Evaluate(board);
            board.CopyPieces(pieces);
            foreach (Piece piece in pieces) { Compare(board, piece, cached, expected); Compare(board, piece, cached, expected); }
            List<List<Piece>> actual = board.TacticalGroups();
            List<List<Piece>> original = GoRules.BuildGroups(board);
            Check(actual.Count == original.Count, "随机盘棋群数量");
            for (int g = 0; g < actual.Count; g++)
            {
                Check(actual[g].Count == original[g].Count, "随机盘棋群成员数");
                for (int n = 0; n < actual[g].Count; n++) { Check(actual[g][n] == original[g][n], "随机盘棋群BFS顺序"); }
            }
        }
        BoardModel trial = board.Tactics.Trial(board);
        board.Reset();
        Check(trial.GetAllPieces().Count == 0, "源盘重置同步清掉试盘旧棋引用");
        Check(board.Tactics.FormationRanged.Count == 0 && board.Tactics.FormationCopies.Count == 0, "整队工作区不跨局持有棋子");
    }
    /// <summary>相同48棋与相同查询次数，对照未缓存和缓存计算；只测CPU/分配，不报告手机FPS。</summary>
    // 测试排除首次字典扩容和JIT预热，每组同48棋同4800次查询重复五轮取中位。
    // 快照对照也包含气眼Evaluate，不能只比清列表；.NET该方法分配为0不等于Unity整场零GC。
    private static void Benchmark()
    {
        var board = new BoardModel(9); int id = 1;
        for (int side = 0; side < 2; side++)
        {
            for (int y = 0; y < 3; y++)
            {
                for (int x = 0; x < 8; x++)
                {
                    var p = new Piece(id++, PieceCatalog.Get(x % 2 == 0 ? PieceKind.Archer : PieceKind.Crossbow, StarTier.One), (GameSide)side, 1, hpScale: 1000);
                    board.Place(p, new BoardPos(x, side == 0 ? y : 8 - y));
                }
            }
        }
        GoRules.Evaluate(board); var actors = board.GetAllPieces(); var list = new List<BoardPos>(81);
        foreach (Piece p in actors) { MoveRules.GetAttackTargets(board, p, list); }
        var before = new List<double>();
        var after = new List<double>();
        for (int round = 0; round < 5; round++)
        {
            var watch = Stopwatch.StartNew();
            for (int n = 0; n < 100; n++) { foreach (Piece p in actors) { MoveRules.CalculateAttackTargets(board, p, list); } }
            watch.Stop(); before.Add(watch.Elapsed.TotalMilliseconds);
            watch.Restart(); for (int n = 0; n < 100; n++) { foreach (Piece p in actors) { MoveRules.GetAttackTargets(board, p, list); } }
            watch.Stop(); after.Add(watch.Elapsed.TotalMilliseconds);
        }
        before.Sort(); after.Sort();
        long bytes = GC.GetAllocatedBytesForCurrentThread();
        for (int n = 0; n < 100; n++) { foreach (Piece p in actors) { MoveRules.GetAttackTargets(board, p, list); } }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - bytes;
        Check(board.AttackCacheHits > board.AttackCacheMisses * 100, "固定输入缓存命中稳定");
        Console.WriteLine("固定48棋4800次查询：未缓存中位ms=" + before[2].ToString("F3") + "，缓存=" + after[2].ToString("F3") + "；稳定段分配Bytes=" + allocated);
        Console.WriteLine("射界命中=" + board.AttackCacheHits + "，重算=" + board.AttackCacheMisses);
        // 快照对照包含相同GoRules.Evaluate，不能仅测清列表并声称AI变快。
        board.Tactics.Trial(board);
        before.Clear(); after.Clear();
        for (int round = 0; round < 5; round++)
        {
            var watch = Stopwatch.StartNew();
            for (int n = 0; n < 100; n++) { board.CreateSnapshot(); }
            watch.Stop(); before.Add(watch.Elapsed.TotalMilliseconds);
            watch.Restart();
            for (int n = 0; n < 100; n++) { board.Tactics.Trial(board); }
            watch.Stop(); after.Add(watch.Elapsed.TotalMilliseconds);
        }
        before.Sort(); after.Sort();
        Check(board.TacticalPieceReuses >= 48 * 500, "深快照棋子复用确实发生");
        Console.WriteLine("48棋100次深快照含气眼刷新：原版中位ms=" + before[2].ToString("F3") + "，工作区=" + after[2].ToString("F3") + "；复用棋子=" + board.TacticalPieceReuses);
    }
}
