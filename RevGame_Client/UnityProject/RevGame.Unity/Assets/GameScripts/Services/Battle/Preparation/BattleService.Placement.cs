// ============================================================
// BattleService.Placement.cs —— 准备期落牌，以及准备/战斗两阶段的主动换位。
// 本文件是 BattleService 的 partial 片段；玩家意图统一从这里进入，再改棋盘数据。
// 点击只改变「选择」，确认目标合法后才一次性搬完整颗棋子；失败不扣牌、不丢棋子。
// ============================================================
using RevGame.Core;
using RevGame.Pieces;
using RevGame.Data;
using Revolution;

namespace RevGame.Services
{
    // partial 片段：与其他 BattleService.*.cs 共享同一实例，完整类说明见 Simulation/BattleService.cs。
    public sealed partial class BattleService
    {
        /// <summary>当前选中的己方棋子编号。0 是「没有选择」，不是有效棋子编号。</summary>
        public int SelectedPieceId
        {
            get; private set;
        }
        /// <summary>
        /// 按编号找仍在存活名单里的选中棋子；已死亡则视为未选中。
        /// 返回 null 时调用方按“没选棋子”处理，不能继续对旧坐标换位。
        /// </summary>
        private Piece FindSelectedPiece()
        {
            return _playerPieces.Find(piece => piece.Id == SelectedPieceId && piece.IsAlive && !piece.BossHidden && !piece.BossFrozen);
        }
        /// <summary>
        /// 手机单指操作：点己方棋子选中/取消；点空格落牌或换位。
        /// 准备期可以落手牌；战斗期只允许移动已在场的己方棋，不能偷打剩余手牌或眼位石。
        /// 手牌、已落棋子、眼位石三种选择互斥，避免一次点击同时消耗牌和移动棋子。
        /// </summary>
        public bool HandleBoardClick(BoardPos pos)
        {
            if (Phase != GamePhase.Prepare && Phase != GamePhase.Battle)
            {
                return Reject("结算期间不能调整棋子，请先进入下一回合");
            }
            if (!pos.IsInside(Consts.BoardSize))
            {
                return Reject("这个位置在棋盘之外");
            }
            Piece occupant = _board.GetPiece(pos);

            if (occupant != null)
            {
                if (occupant.BossHidden || occupant.BossFrozen)
                {
                    return Reject("棋子正在黑洞或冰封中，状态结束后才能调整");
                }
                if (occupant.Side != GameSide.Player)
                {
                    return SetHelperTarget(occupant.Id);
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
                // 自动AI仍遵守保眼和移动冷却；玩家主动操作允许拆眼，不拿自动移动限制来拒绝手动换位。
                // 移动不重置生命、护盾或攻击进度，否则反复点击就会变成无限回血/立即攻击的漏洞。
                // Board.Move 先预检所有身体格再搬移，2×2 棋子失败时仍完整留在原位。
                if (!_board.Move(selected, pos, respectEnemyEyes: false))
                {
                    return Reject("目标区域放不下整颗棋子：请检查占格和棋盘边界");
                }
                SelectedPieceId = 0;
                LastError = string.Empty;

                // 手动指令优先：暂缓下一次自动移动/整队，避免刚拖开的棋当步又被AI拉回去。
                if (Phase == GamePhase.Battle)
                {
                    selected.NextMoveAt = _battleElapsed + System.Math.Max(Balance.PlayerMoveSeconds, Balance.RangedFormationSeconds);
                }
                RefreshGoState();
                RevEvent.DispatchEvent<int>(Events.PieceMoved, selected.Id);
                RevEvent.DispatchEvent(Events.SelectionChanged);
                return true;
            }
            if (Phase == GamePhase.Battle)
            {
                return Reject("请先点击一颗存活的己方棋子，再点击空格换位");
            }
            return EyeStoneSelected ? TryPlaceEyeStone(pos) : TryPlayCard(SelectedCardId, pos);
        }
    }
}
