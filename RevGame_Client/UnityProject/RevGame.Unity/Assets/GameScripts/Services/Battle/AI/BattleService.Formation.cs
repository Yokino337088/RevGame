// 整队归战斗服务拥有；规划、占格、规则刷新、事实事件按顺序提交，DOTween不参与规则。
using System;
using System.Collections.Generic;
using RevGame.Battle;
using RevGame.Core;
using RevGame.Data;
using RevGame.Pieces;
using Revolution;

namespace RevGame.Services
{
    public sealed partial class BattleService
    {
        // 下一次允许规划的战斗秒数；新回合设为首个整队窗口，使用模型时钟所以暂停不会消耗冷却。
        private float _nextFormationAt;
        // 服务自有结果缓冲，每次Plan会清空；不把这份可变列表暴露给表现层或事件监听器。
        private readonly List<FormationMove> _formationMoves = new List<FormationMove>(81);

        /// <summary>固定模拟步中执行；先完成全部模型变化再派发事实事件，失败显式抛出以暴露预检契约破坏。</summary>
        private void TryArrangeRangedFormation()
        {
            if (!Balance.RangedFormationEnabled || _battleElapsed < _nextFormationAt)
            {
                return;
            }
            _nextFormationAt = _battleElapsed + Balance.RangedFormationSeconds;
            RangedFormationPlanner.Plan(_board, _battleElapsed, Balance.RangedFormationMinCount, Balance.PlayerRetreatDistance, _formationMoves);
            foreach (FormationMove move in _formationMoves)
            {
                Piece piece = _board.FindPiece(move.PieceId);

                if (piece == null || !_board.Move(piece, move.Position))
                {
                    throw new InvalidOperationException("自动整队预检与提交不一致");
                }
                // 本轮整队后不立刻被常规后撤推翻；也给玩家手动覆盖留下清晰窗口。
                piece.NextMoveAt = _battleElapsed + Balance.PlayerMoveSeconds;
                RefreshGoState();
            }
            // 全部占格和气眼稳定后再通知显示，监听器不会看到一半旧阵型一半新阵型。
            foreach (FormationMove move in _formationMoves)
            {
                RevEvent.DispatchEvent<int>(Events.PieceFormationMoved, move.PieceId);
                RevEvent.DispatchEvent<int>(Events.PieceMoved, move.PieceId);
            }
        }
    }
}
