// ============================================================
// GameOptimizationChecks.cs —— 优化算法与原有易读算法的交叉验证。
// 用固定随机棋盘比较气、眼、连通占格，防止只在几张手工棋盘上测过就宣称优化等价。
// ============================================================
using System;
using System.Collections.Generic;
using RevGame.Board;
using RevGame.Core;
using RevGame.Data;
using RevGame.Pieces;

namespace RevGame.Editor
{
    public static class GameOptimizationChecks
    {
        /// <summary>300张含大棋的随机棋盘，复用原有BuildGroups/CountLiberties作为易读参照算法。</summary>
        public static string Run()
        {
            GameConfigTestSupport.Ensure();
            int checks = 0;
            var random = new Random(20261018);
            for (int sample = 0; sample < 300; sample++)
            {
                var board = new BoardModel(9);
                for (int id = 1; id <= 30; id++)
                {
                    int size = id % 7 == 0 ? 2 : 1;
                    var side = id % 2 == 0 ? GameSide.Player : GameSide.Enemy;
                    var piece = new Piece(id, PieceCatalog.Get(PieceKind.Shield, StarTier.One), side, size);
                    board.Place(piece, new BoardPos(random.Next(9), random.Next(9)), respectEnemyEyes: false);
                }
                Verify(board, ref checks);
                // 再移除一颗，验证草稿数组没有留下上一轮棋群/气的痕迹。
                var all = board.GetAllPieces();

                if (all.Count > 0)
                {
                    board.Remove(all[0]);
                }
                Verify(board, ref checks);
            }
            return "通过 " + checks + " 条优化等价断言（300张随机棋盘及移除后重算）";
        }
        private static void Verify(BoardModel board, ref int checks)
        {
            GoRules.Evaluate(board);
            foreach (var group in GoRules.BuildGroups(board))
            {
                int liberties = GoRules.CountLiberties(board, group);
                int cells = 0;
                var eyes = new HashSet<BoardPos>();
                foreach (Piece piece in group)
                {
                    cells += piece.Size * piece.Size;
                    foreach (BoardPos pos in BoardModel.Boundary(piece))
                    {
                        if (GoRules.IsEye(board, pos, piece.Side))
                        {
                            eyes.Add(pos);
                        }
                    }
                }
                foreach (Piece piece in group)
                {
                    if (piece.Liberties != liberties || piece.EyeCount != eyes.Count || piece.ConnectedCells != cells)
                    {
                        throw new InvalidOperationException("优化前后规则不等价：" + piece.Id + "\n" + board.Dump());
                    }
                    checks++;
                }
            }
        }
    }
}
