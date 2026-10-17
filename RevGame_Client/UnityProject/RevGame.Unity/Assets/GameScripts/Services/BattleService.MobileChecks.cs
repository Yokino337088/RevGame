// ============================================================
// BattleService.MobileChecks.cs —— 用户追加规则的真实业务验收，仅编辑器编译。
// 检查最终位置、实际扣血及失败事务，不复制 AI 算法，也不只检查常量。
// ============================================================
#if UNITY_EDITOR
using System;
using RevGame.Core;
using RevGame.Data;
using RevGame.Pieces;

namespace RevGame.Services
{
    public sealed partial class BattleService
    {
        private static void VerifyPlacementAndMovement(Action<bool, string> check)
        {
            for (int y = 0; y < Consts.BoardSize; y++)
            {
                for (int x = 0; x < Consts.BoardSize; x++)
                {
                    var service = new BattleService(false);
                    service._hand.Add(PieceKind.Shield, StarTier.One);
                    int id = service.Hand[0].Id;
                    check(service.TryPlayCard(id, new BoardPos(x, y)), "全盘任意空格可落子：" + x + "," + y);
                }
            }

            var battle = new BattleService(false);
            battle.AddCheckPiece(GameSide.Enemy, PieceKind.Shield, new BoardPos(0, 7));
            battle.AddCheckPiece(GameSide.Enemy, PieceKind.Archer, new BoardPos(1, 8));
            battle.RefreshGoState();
            battle._hand.Add(PieceKind.Shield, StarTier.One);
            int card = battle.Hand[0].Id;
            check(battle._board.GetCell(new BoardPos(0, 8)).IsEyeMarked && battle.TryPlayCard(card, new BoardPos(0, 8)), "准备期敌眼也是空格，可主动落子");
            Piece player = battle._playerPieces[0];
            int hand = battle.Hand.Count;
            int remaining = battle.CardsRemaining;
            check(battle.HandlePrepareClick(player.Pos) && battle.SelectedPieceId == player.Id, "点己方棋子选中");
            string before = battle._board.Dump();
            check(!battle.HandlePrepareClick(new BoardPos(0, 7)) && battle._board.Dump() == before, "已占格换位失败，不改棋盘");
            check(!battle.HandlePrepareClick(new BoardPos(-1, 0)) && player.Pos == new BoardPos(0, 8), "越界换位保留原位");
            check(battle.HandlePrepareClick(new BoardPos(4, 4)) && player.Pos == new BoardPos(4, 4), "已选棋子移动到全盘空格");
            check(battle.Hand.Count == hand && battle.CardsRemaining == remaining && battle.SelectedPieceId == 0, "换位不扣牌不扣额度，并清除选择");
            battle.HandlePrepareClick(player.Pos);
            battle._hand.Add(PieceKind.Archer, StarTier.One);
            int second = battle.Hand[0].Id;
            check(battle.SelectCard(second) && battle.SelectedPieceId == 0, "选手牌清除棋子选择");

            battle = new BattleService(false);
            Piece large = battle.AddCheckPiece(GameSide.Player, PieceKind.Shield, new BoardPos(3, 3), StarTier.Three);
            battle.HandlePrepareClick(large.Pos);
            before = battle._board.Dump();
            check(!battle.HandlePrepareClick(new BoardPos(8, 6)) && battle._board.Dump() == before, "2×2 身体越界失败，四格占用完整保留");
            check(battle.HandlePrepareClick(new BoardPos(6, 6)) && battle._board.GetPiece(new BoardPos(7, 7)) == large && battle._board.GetPiece(new BoardPos(3, 3)) == null, "大棋整体重摆，无旧占格残留");

            battle = new BattleService(false);
            player = battle.AddCheckPiece(GameSide.Player, PieceKind.Shield, new BoardPos(0, 0));
            Piece enemy = battle.AddCheckPiece(GameSide.Enemy, PieceKind.Archer, new BoardPos(8, 8));
            battle.RefreshGoState();
            battle.StartBattle();
            battle.Tick(3.75f);
            check(enemy.Pos == new BoardPos(8, 8), "敌人五秒冷却前不移动");
            battle.Tick(1.25f);
            BoardPos firstMove = enemy.Pos;
            check(Math.Abs(firstMove.X - 8) + Math.Abs(firstMove.Y - 8) == 1, "敌方滑步五秒时只靠近一格");
            battle.Tick(3.75f);
            check(enemy.Pos == firstMove && player.Pos == new BoardPos(0, 0), "冷却期间不重复挪动，我方始终守阵");

            battle = new BattleService(false);
            player = battle.AddCheckPiece(GameSide.Player, PieceKind.Archer, new BoardPos(0, 0));
            enemy = battle.AddCheckPiece(GameSide.Enemy, PieceKind.Shield, new BoardPos(8, 7));
            battle.RefreshGoState();
            battle.StartBattle();
            battle.Tick(5f);
            check(player.Pos == new BoardPos(0, 0) && enemy.Hp < enemy.MaxHp, "我方远程原地跨盘弹幕真实命中，打破直线两格限制");
            check(!battle.HandlePrepareClick(new BoardPos(3, 3)), "战斗中拒绝人工换位");

            battle = new BattleService(false);
            player = battle.AddCheckPiece(GameSide.Player, PieceKind.Shield, new BoardPos(3, 3));
            enemy = battle.AddCheckPiece(GameSide.Enemy, PieceKind.Archer, new BoardPos(3, 5));
            battle.RefreshGoState();
            battle.StartBattle();
            battle.Tick(5f);
            check(enemy.Pos == new BoardPos(3, 5) && player.Hp < player.MaxHp, "敌人有射程内目标只攻击，不移动");

            // 一眼、两眼均用真实连通组计算，不直接赋 EyeCount。
            for (int eyes = 1; eyes <= 2; eyes++)
            {
                battle = new BattleService(false);
                PieceKind[] kinds = { PieceKind.Shield, PieceKind.Swordman, PieceKind.HeavySword };
                int n = 0;
                int columns = eyes == 1 ? 2 : 4;
                for (int x = 0; x < columns; x++)
                {
                    battle.AddCheckPiece(GameSide.Player, kinds[n++ % 3], new BoardPos(x, 1));
                }
                battle.AddCheckPiece(GameSide.Player, kinds[n++ % 3], new BoardPos(1, 0));
                if (eyes == 2) { battle.AddCheckPiece(GameSide.Player, kinds[n % 3], new BoardPos(3, 0)); }
                battle.AddCheckPiece(GameSide.Enemy, PieceKind.Shield, new BoardPos(8, 8));
                battle.RefreshGoState();
                check(battle._playerPieces.TrueForAll(p => p.EyeCount == eyes), "真实形成眼阵：" + eyes);
                before = battle._board.Dump();
                battle.StartBattle();
                battle.Tick(3.75f);
                check(battle._board.Dump() == before, "成型眼阵保持原位：" + eyes);
            }
        }
    }
}
#endif
