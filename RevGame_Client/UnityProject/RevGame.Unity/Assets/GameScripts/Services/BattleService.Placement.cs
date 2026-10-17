// ============================================================
// BattleService.Placement.cs —— 准备期全盘落子与免费重摆。
// 点击只改变「选择」，确认目标合法后才一次性搬完整颗棋子；失败不扣牌、不丢棋子。
// ============================================================
using RevGame.Core;
using RevGame.Pieces;
using Revolution;

namespace RevGame.Services
{
    public sealed partial class BattleService
    {
        /// <summary>当前选中的己方棋子编号。0 是「没有选择」，不是有效棋子编号。</summary>
        public int SelectedPieceId { get; private set; }

        private Piece FindSelectedPiece()
        {
            return _playerPieces.Find(piece => piece.Id == SelectedPieceId && piece.IsAlive);
        }

        /// <summary>
        /// 手机单指操作：点己方棋子选中/取消；点空格落牌或换位。
        /// 手牌、已落棋子、眼位石三种选择互斥，避免一次点击同时消耗牌和移动棋子。
        /// </summary>
        public bool HandlePrepareClick(BoardPos pos)
        {
            if (Phase != GamePhase.Prepare)
            {
                return Reject("战斗中保持阵型，请等准备阶段再调整位置");
            }

            if (!pos.IsInside(Consts.BoardSize))
            {
                return Reject("这个位置在棋盘之外");
            }

            Piece occupant = _board.GetPiece(pos);
            if (occupant != null)
            {
                if (occupant.Side != GameSide.Player)
                {
                    return Reject("这个格子已有敌方棋子，请选择空格");
                }

                SelectedPieceId = SelectedPieceId == occupant.Id ? 0 : occupant.Id;
                SelectedCardId = 0;
                EyeStoneSelected = false;
                LastError = string.Empty;
                RevEvent.DispatchEvent(Events.SelectionChanged);
                return true;
            }

            Piece selected = FindSelectedPiece();
            if (selected != null)
            {
                // 重摆允许玩家主动拆眼；只要求完整身体不越界、不覆盖别的棋子。
                // Board.Move 先预检所有身体格再搬移，2×2 棋子失败时仍完整留在原位。
                if (!_board.Move(selected, pos, respectEnemyEyes: false))
                {
                    return Reject("目标区域放不下整颗棋子：请检查占格和棋盘边界");
                }

                SelectedPieceId = 0;
                LastError = string.Empty;
                RefreshGoState();
                RevEvent.DispatchEvent<int>(Events.PieceMoved, selected.Id);
                RevEvent.DispatchEvent(Events.SelectionChanged);
                return true;
            }

            return EyeStoneSelected ? TryPlaceEyeStone(pos) : TryPlayCard(SelectedCardId, pos);
        }
    }
}
