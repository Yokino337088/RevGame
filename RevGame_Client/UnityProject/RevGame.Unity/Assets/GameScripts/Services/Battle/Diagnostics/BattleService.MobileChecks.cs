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
    // partial 片段：与其他 BattleService.*.cs 共享同一实例，完整类说明见 Simulation/BattleService.cs。
    public sealed partial class BattleService
    {
        /// <summary>
        /// 验收手机单指操作的全部真实边界：全盘落子、己方换位、大棋整块搬移、
        /// 敌方独立移动冷却、零气仍可反击、结算期拒绝操作。
        /// </summary>
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
            check(battle.HandleBoardClick(player.Pos) && battle.SelectedPieceId == player.Id, "点己方棋子选中");
            string before = battle._board.Dump();
            check(!battle.HandleBoardClick(new BoardPos(0, 7)) && battle._board.Dump() == before, "已占格换位失败，不改棋盘");
            check(!battle.HandleBoardClick(new BoardPos(-1, 0)) && player.Pos == new BoardPos(0, 8), "越界换位保留原位");
            check(battle.HandleBoardClick(new BoardPos(4, 4)) && player.Pos == new BoardPos(4, 4), "已选棋子移动到全盘空格");
            check(battle.Hand.Count == hand && battle.CardsRemaining == remaining && battle.SelectedPieceId == 0, "换位不扣牌不扣额度，并清除选择");
            battle.HandleBoardClick(player.Pos);
            battle._hand.Add(PieceKind.Archer, StarTier.One);
            int second = battle.Hand[0].Id;
            check(battle.SelectCard(second) && battle.SelectedPieceId == 0, "选手牌清除棋子选择");

            battle = new BattleService(false);
            Piece large = battle.AddCheckPiece(GameSide.Player, PieceKind.Shield, new BoardPos(3, 3), StarTier.Three);
            battle.HandleBoardClick(large.Pos);
            before = battle._board.Dump();
            check(!battle.HandleBoardClick(new BoardPos(8, 6)) && battle._board.Dump() == before, "2×2 身体越界失败，四格占用完整保留");
            check(battle.HandleBoardClick(new BoardPos(6, 6)) && battle._board.GetPiece(new BoardPos(7, 7)) == large && battle._board.GetPiece(new BoardPos(3, 3)) == null, "大棋整体重摆，无旧占格残留");

            battle = new BattleService(false);
            player = battle.AddCheckPiece(GameSide.Player, PieceKind.Shield, new BoardPos(0, 0));
            Piece enemy = battle.AddCheckPiece(GameSide.Enemy, PieceKind.Archer, new BoardPos(8, 8));
            battle.RefreshGoState();
            battle.StartBattle();
            float movementCooldown = Balance.BattleTickSeconds * Balance.EnemyMoveEveryTicks;
            float simulationStep = Balance.BattleSimulationStepSeconds;
            battle.Tick(movementCooldown - simulationStep);
            check(enemy.Pos == new BoardPos(8, 8), "敌人配置冷却到期前不移动");
            battle.Tick(simulationStep);
            BoardPos firstMove = enemy.Pos;
            check(Math.Abs(firstMove.X - 8) + Math.Abs(firstMove.Y - 8) == 1, "敌方滑步冷却到期只靠近一格");
            battle.Tick(movementCooldown - simulationStep);
            check(enemy.Pos == firstMove && player.Pos == new BoardPos(0, 0), "冷却期间不重复挪动，我方始终守阵");

            battle = new BattleService(false);
            player = battle.AddCheckPiece(GameSide.Player, PieceKind.Archer, new BoardPos(0, 0));
            enemy = battle.AddCheckPiece(GameSide.Enemy, PieceKind.Shield, new BoardPos(8, 7));
            battle.RefreshGoState();
            battle.StartBattle();
            battle.Tick(5f);
            check(player.Pos == new BoardPos(0, 0) && enemy.Hp < enemy.MaxHp, "我方远程原地跨盘弹幕真实命中，打破直线两格限制");
            check(!battle.HandleBoardClick(new BoardPos(3, 3)), "战斗未选择棋子时不能凭空换位或落手牌");
            int oldHp = player.Hp;
            float oldProgress = player.ActionProgress;
            check(battle.HandleBoardClick(player.Pos) && battle.CanPlaySelectedAt(new BoardPos(3, 3)), "战斗中点己方棋可选中并高亮空格");
            check(battle.HandleBoardClick(new BoardPos(3, 3)) && player.Pos == new BoardPos(3, 3), "战斗中真实手动换位");
            check(player.Hp == oldHp && player.ActionProgress == oldProgress, "战斗换位不回血、不重置攻击进度");
            check(!battle.HandleBoardClick(enemy.Pos) && battle.SelectedPieceId == 0, "不能选择敌棋换位");
            battle.HandleBoardClick(player.Pos);
            battle.DamagePiece(player, player.MaxHp);
            check(battle.SelectedPieceId == 0 && battle.Board.GetPiece(new BoardPos(3, 3)) == null, "选中棋子战死后清除选择和旧占格");

            battle = new BattleService(false);
            large = battle.AddCheckPiece(GameSide.Player, PieceKind.Shield, new BoardPos(3, 3), StarTier.Three);
            battle.AddCheckPiece(GameSide.Enemy, PieceKind.Archer, new BoardPos(8, 8));
            battle._hand.Add(PieceKind.Swordman, StarTier.One);
            battle.StartBattle();
            large.TakeDamage(30);
            large.ShieldCharges = 2;
            check(battle.HandleBoardClick(new BoardPos(4, 4)) && battle.SelectedPieceId == large.Id, "战斗中点击大棋任何身体格均选中同一颗棋");
            before = battle._board.Dump();
            check(!battle.HandleBoardClick(new BoardPos(8, 5)) && battle._board.Dump() == before, "战斗大棋越界预检失败不留半颗棋");
            check(battle.HandleBoardClick(new BoardPos(6, 4)) && battle._board.GetPiece(new BoardPos(7, 5)) == large, "战斗大棋完整搬移四格");
            check(large.Hp == large.MaxHp - 30 && large.ShieldCharges == 2 && battle.Hand.Count == 1, "换位保持伤势护盾与剩余手牌");
            battle.Phase = GamePhase.Settle;
            check(!battle.HandleBoardClick(large.Pos), "结算期间拒绝棋盘操作");

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
                if (eyes == 2)
                {
                    battle.AddCheckPiece(GameSide.Player, kinds[n % 3], new BoardPos(3, 0));
                }
                // 这里验收的是“守眼时有没有自动挪位”，不能让敌人提前死亡改变棋盘字符画。
                // 新规则下六颗近战也会原位援击，普通一星盾靶可能在3.75秒内被打死；那是火力变化，不是玩家移动。
                // 保留同种一星盾战及同一落点，只在夹具里给耐打血量，避免用禁攻/跳过战斗来掩盖真正的移动错误。
                var eyeTarget = new Piece(battle._nextPieceId++, PieceCatalog.Get(PieceKind.Shield, StarTier.One), GameSide.Enemy, hpScale: 100f);
                check(battle._board.Place(eyeTarget, new BoardPos(8, 8)), "守眼移动夹具的耐打敌靶完整落位：" + eyes);
                battle._enemyPieces.Add(eyeTarget);
                battle.RefreshGoState();
                check(battle._playerPieces.TrueForAll(p => p.EyeCount == eyes), "真实形成眼阵：" + eyes);
                var eyePositions = new BoardPos[battle._playerPieces.Count];
                for (int i = 0; i < eyePositions.Length; i++) { eyePositions[i] = battle._playerPieces[i].Pos; }
                before = battle._board.Dump();
                battle.StartBattle();
                battle.Tick(3.75f);
                check(battle.Phase == GamePhase.Battle && eyeTarget.IsAlive && eyeTarget.Hp < eyeTarget.MaxHp, "真实守阵攻击继续且敌靶未被提前击败：" + eyes);
                // 字符画只区分P/E，不能识别两颗我方互换；再按每颗棋子的编号/对象确认原占格，防止漏报换位。
                for (int i = 0; i < eyePositions.Length; i++)
                {
                    Piece defender = battle._playerPieces[i];
                    check(defender.Pos == eyePositions[i] && battle._board.GetPiece(eyePositions[i]) == defender, "守眼棋子原编号原位不动：" + eyes + "眼 #" + defender.Id);
                }
                check(battle._board.Dump() == before, "成型眼阵保持原位：" + eyes);
            }
        }
    }
}
#endif
