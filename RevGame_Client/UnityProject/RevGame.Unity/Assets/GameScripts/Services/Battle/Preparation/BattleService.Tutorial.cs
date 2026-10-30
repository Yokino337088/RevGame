using RevGame.Core;
using RevGame.Data;
using RevGame.Flow;
using RevGame.Pieces;
using Revolution;

namespace RevGame.Services
{
    public sealed partial class BattleService
    {
        /// <summary>
        /// 首次第一章的教学开局：沿用BeginCampaign的阶段/增益复位，仅把初始阵型换成一口气的可读布局。
        /// 所有棋子属于正式名单和BoardModel，血攻来自PieceCatalog表；之后落子、战斗和死亡沿用正式规则。
        /// 只能在场景交付之前调用，视图尚未生成，避免玩家看见两次开局。不会直接发奖、设胜利或写完成标记。
        /// </summary>
        public void ConfigureTutorialOpening()
        {
            if (Phase != GamePhase.Prepare || Chapter != 1 || Round != 1)
            {
                throw new System.InvalidOperationException("教学布局只能用于第一章首轮准备期");
            }
            StopEnemyAI();
            _board.Reset();
            _playerPieces.Clear();
            _enemyPieces.Clear();
            AddTutorialPiece(GameSide.Enemy, TutorialLogic.EnemyPosition);
            AddTutorialPiece(GameSide.Player, TutorialLogic.AllyPosition);
            AddTutorialPiece(GameSide.Player, new BoardPos(5, 4));
            AddTutorialPiece(GameSide.Player, new BoardPos(4, 5));
            _hand.Clear();
            // 六张真实一星手牌保留正式手牌槽；第一张必为剑士，避免随机大体型牌放不进教学气口。
            for (int i = 0; i < Consts.MaxHandSize; i++)
            {
                _hand.Add(PieceKind.Swordman, StarTier.One);
            }
            RefreshGoState();
        }
        private void AddTutorialPiece(GameSide side, BoardPos pos)
        {
            var piece = new Piece(_nextPieceId++, PieceCatalog.Get(PieceKind.Swordman, StarTier.One), side, size: 1);

            if (!_board.Place(piece, pos))
            {
                throw new System.InvalidOperationException("教学棋子落点冲突：" + pos);
            }
            if (side == GameSide.Player)
            {
                _playerPieces.Add(piece);
            }
            else
            {
                _enemyPieces.Add(piece);
            }
        }
    }
}
