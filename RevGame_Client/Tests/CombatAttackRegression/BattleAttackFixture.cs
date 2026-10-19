// 夹具只接线和造局：以下方法均不计算目标、不扣伤害、不计算气眼。
// 它和链接进来的 BattleService 属于同一 partial 类型，因而可调用真实私有方法。
// 此文件在 Assets 外，不会进入 Unity 或游戏包，也不向正式游戏暴露作弊入口。
using RevGame.Core;
using RevGame.Data;
using RevGame.Pieces;

namespace RevGame.Services
{
    public sealed partial class BattleService
    {
        /// <summary>关闭保胜辅助、随机事件和敌人出手，仅隔离验证这颗棋子的真实攻击链。</summary>
        public static BattleService CreateAttackRegressionFixture()
        {
            return new BattleService(false, 20261019)
            {
                Chapter = 1,
                Round = 1,
                Phase = GamePhase.Battle
            };
        }

        /// <summary>借助正式占格入口添加高血量靶子；高血量避免连击第一发杀死目标后无法继续测量。</summary>
        public Piece AddRegressionPiece(PieceKind kind, StarTier star, GameSide side, BoardPos pos, float hpScale = 1f)
        {
            Piece piece = new Piece(_nextPieceId++, PieceCatalog.Get(kind, star), side, hpScale: hpScale);
            if (!_board.Place(piece, pos))
            {
                throw new System.InvalidOperationException("测试造局占格失败：" + kind + " @" + pos);
            }
            (side == GameSide.Player ? _playerPieces : _enemyPieces).Add(piece);
            return piece;
        }

        /// <summary>删除一个包围者后必须走正式 Remove 和刷新入口，验证拓扑版本缓存确实失效。</summary>
        public void RemoveRegressionPiece(Piece piece)
        {
            _board.Remove(piece);
            (piece.Side == GameSide.Player ? _playerPieces : _enemyPieces).Remove(piece);
            RefreshGoState();
        }

        public void RefreshRegressionGoState()
        {
            RefreshGoState();
        }

        /// <summary>处理真实行动进度、目标查询、Attack、DamagePiece 及事件；不能直接调 TakeDamage 伪造结果。</summary>
        public void RunRegressionPlayerStep(float fraction = 1f)
        {
            StepSide(GameSide.Player, fraction);
        }

        /// <summary>单独推进敌人，核对玩家守阵援击的改动没有放开敌方零气禁攻约束。</summary>
        public void RunRegressionEnemyStep(float fraction = 1f)
        {
            StepSide(GameSide.Enemy, fraction);
        }
    }
}
