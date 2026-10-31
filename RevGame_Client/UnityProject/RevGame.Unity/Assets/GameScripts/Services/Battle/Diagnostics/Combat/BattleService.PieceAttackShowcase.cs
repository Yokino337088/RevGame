#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
using RevGame.Core;
using RevGame.Pieces;
using Revolution;

namespace RevGame.Services
{
    public sealed partial class BattleService
    {
        /// <summary>
        /// 开发态单棋种演示输入：一名攻击者、九格高生命靶阵和一名受伤友军。
        /// 只设置初始条件，之后由真实FSM/Attack/ResolveDamage推进；不注入命中或表现事件伪造攻击。
        /// 必须由隔离测试调用，结束通过正式GameFlow回大厅清理，不计入普通关卡胜率。
        /// </summary>
        public bool LoadPieceAttackShowcase(PieceKind kind)
        {
            if (!StartRound(1, 1))
            {
                return false;
            }
            StopEnemyAI();
            _board.Reset();
            _playerPieces.Clear();
            _enemyPieces.Clear();
            Piece actor = AddImpactPiece(GameSide.Player, kind, new BoardPos(3, 4));
            actor.TakeDamage(actor.Hp / 2);
            Piece ally = AddImpactPiece(GameSide.Player, PieceKind.Shield, new BoardPos(2, 4));
            ally.TakeDamage(ally.Hp * 3 / 4);
            for (int y = 3; y <= 5; y++)
            {
                for (int x = 4; x <= 6; x++)
                {
                    Piece target = AddImpactPiece(GameSide.Enemy, PieceKind.Shield, new BoardPos(x, y));
                    if (kind == PieceKind.Assassin && x == 4 && y == 3)
                    {
                        target.TakeDamage(target.Hp * 9 / 10);
                    }
                }
            }
            RefreshGoState();
            RevEvent.DispatchEvent<int>(Events.PhaseEnterPrepare, Round);
            return true;
        }
    }
}
#endif
