// ============================================================
// GameRuleChecks.cs —— 在 Unity 内执行真实业务代码的断言。
// 不复制伤害或规则算法；只准备输入并检查对外结果。通过 MCP 调用 Run，失败立即带上下文抛错。
// ============================================================
using RevGame.Board;
using RevGame.Core;
using RevGame.Data;
using RevGame.Pieces;
using RevGame.Services;

namespace RevGame.Editor
{
    public static class GameRuleChecks
    {
        private static int _count;
        private static void Check(bool condition, string message)
        {
            if (!condition)
            {
                throw new System.InvalidOperationException("验收失败：" + message);
            }

            _count++;
        }

        /// <summary>在非 Play 状态跑，避免事件通知到当前游戏的 UI。</summary>
        public static string Run()
        {
            if (UnityEngine.Application.isPlaying)
            {
                return "请先退出 Play 再运行独立规则断言";
            }

            _count = 0;
            var board = new BoardModel(9);
            var large = new Piece(1, PieceCatalog.Get(PieceKind.Shield, StarTier.Three), GameSide.Player);
            Check(board.Place(large, new BoardPos(3, 3)), "2×2 可以放置");
            Check(board.GetPiece(new BoardPos(4, 4)) == large, "每个占格都能查到同一颗大棋");
            Check(board.GetAllPieces().Count == 1, "遍历大棋不重复四次");
            Check(GoRules.CountLiberties(board, board.GetAllPieces()) == 8, "2×2 有八口气");
            Check(board.Move(large, new BoardPos(4, 3)), "大棋移动可以覆盖原来的身体");
            Check(board.GetPiece(new BoardPos(3, 3)) == null, "大棋移动清掉旧格");
            Check(!board.Move(large, new BoardPos(8, 8)), "大棋不能部分出界");
            Check(large.Pos == new BoardPos(4, 3), "移动失败位置不变");
            var adjacent = new Piece(90, PieceCatalog.Get(PieceKind.Shield, StarTier.One), GameSide.Enemy);
            board.Place(adjacent, new BoardPos(6, 4));
            Check(RevGame.Battle.MoveRules.GetAttackTargets(board, large).Contains(adjacent.Pos), "大棋可攻击贴住身体边角的敌人");
            board.Move(adjacent, new BoardPos(7, 4));
            Check(!RevGame.Battle.MoveRules.GetAttackTargets(board, large).Contains(adjacent.Pos), "大棋近战射程仍只有一格");
            board.Remove(adjacent);
            board.Remove(large);
            Check(board.GetPiece(new BoardPos(5, 4)) == null, "移除大棋清掉全部身体");
            var a = new Piece(2, PieceCatalog.Get(PieceKind.Shield, StarTier.One), GameSide.Player);
            var b = new Piece(3, PieceCatalog.Get(PieceKind.Shield, StarTier.One), GameSide.Player);
            board.Place(a, new BoardPos(1, 0));
            board.Place(b, new BoardPos(0, 1));
            GoRules.Evaluate(board);
            Check(board.GetCell(new BoardPos(0, 0)).IsEyeMarked, "角落两邻成眼");
            Check(!board.CanFit(new BoardPos(0, 0), 1, GameSide.Enemy), "不能在对方眼里落子");
            board.Remove(a);
            GoRules.Evaluate(board);
            Check(!board.GetCell(new BoardPos(0, 0)).IsEyeMarked, "拆掉守子立即破眼");
            var battle = new BattleService();
            Check(battle.StartRound(1, 1) && battle.Hand.Count == 6 && battle.CardsRemaining == 6, "第一回合六张牌和六次落子");
            int id = battle.Hand[0].Id;
            Check(!battle.TryPlayCard(id, new BoardPos(-1, 8)), "越界拒绝落子");
            Check(battle.Hand.Count == 6 && battle.PlayerPieces.Count == 0, "拒绝后牌和棋盘不变");
            Check(battle.TryPlayCard(id, new BoardPos(0, 0)), "合法落子");
            Check(battle.Hand.Count == 5 && battle.CardsRemaining == 5, "落子同时消费牌和额度");
            Check(!battle.TryPlayCard(id, new BoardPos(1, 0)), "重复牌编号不能再次消费");
            battle.StartRound(1, 4);
            Check(battle.Hand.Count == 6, "第四回合仍发六张");
            battle.StartRound(1, 8);
            Check(battle.Hand.Count == 6, "第八回合仍发六张");
            battle.LoadDevelopmentScenario("merge");
            Check(battle.Hand.Count == 6, "合成验收场景也保留完整六张手牌");
            Check(battle.TryPlayCard(battle.Hand[0].Id, new BoardPos(2, 0)), "第二张同名牌可以放下");
            Check(battle.Board.GetPiecesOf(GameSide.Player).Count == 2 && battle.PlayerPieces[0].Def.Star == StarTier.Two, "双牌使原棋二星，第二颗保留");
            battle.LoadDevelopmentScenario("large-merge");
            battle.TryPlayCard(battle.Hand[0].Id, new BoardPos(2, 0));
            Check(battle.PlayerPieces.Count == 2 && battle.PlayerPieces[0].Size == 1 && battle.PlayerPieces[0].Def.Star == StarTier.Three, "原位三星保留单格和新落子");
            for (int chapter = 1; chapter <= 6; chapter++)
            {
                battle = new BattleService();
                battle.StartRound(chapter, 11);
                int expected = chapter <= 2 ? 1 : (chapter <= 4 ? 2 : (chapter == 5 ? 3 : 2));
                int bosses = 0;
                int kings = 0;
                foreach (Piece piece in battle.EnemyPieces)
                {
                    if (piece.IsBoss)
                    {
                        bosses++;
                    }

                    if (piece.IsKing)
                    {
                        kings++;
                    }
                }

                Check(bosses == expected && kings == 1, "第 " + chapter + " 章大棋和主将数量");
                Check(float.IsPositiveInfinity(battle.BattleDuration), "Boss 不按60秒判负");
            }

            foreach (BattleEventKind kind in System.Enum.GetValues(typeof(BattleEventKind)))
            {
                battle = new BattleService();
                battle.LoadDevelopmentScenario("events");
                battle.Tick(3f);
                battle.StartBattle();
                Check(battle.TriggerBattleEvent(kind), "事件可触发：" + kind);
                Check(!battle.TriggerBattleEvent(kind), "冷却中拒绝重复事件：" + kind);
            }

            battle = new BattleService();
            battle.LoadDevelopmentScenario("round10");
            Check(battle.SkipRound() && battle.Round == 11 && battle.TeamAttackBonus >= 0.1f, "跳过第10回合换强化");
            Check(!battle.SkipRound(), "Boss不能跳过");
            BattleService.VerifyDevelopmentContracts(Check);
            return "通过 " + _count + " 条真实业务断言（全盘落子/重摆、守眼、刷新事务、分时远程攻击、升星、Boss、支援与清理）";
        }
    }
}
