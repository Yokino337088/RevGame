// ============================================================
// BattleService.Reinforcement.cs —— 援军的选兵和落点预检。
// 远程守住主体阵营，近战落下就应有攻击目标；所有候选在深快照里试放，不能污染实战气眼。
// ============================================================
using System.Collections.Generic;
using RevGame.Battle;
using RevGame.Board;
using RevGame.Core;
using RevGame.Data;
using RevGame.Pieces;
using Revolution;

namespace RevGame.Services
{
    public sealed partial class BattleService
    {
        /// <summary>先选最强远程手牌，近战随后；实际部署至少二星。手牌为空时，辅助模式免费补二星重弩。</summary>
        private bool ReinforceFromHand()
        {
            var candidates = new List<HandCard>(Hand);
            candidates.Sort((a, b) =>
            {
                PieceDef first = PieceCatalog.Get(a.Kind, a.Star);
                PieceDef second = PieceCatalog.Get(b.Kind, b.Star);
                int ranged = second.IsRanged.CompareTo(first.IsRanged);
                if (ranged != 0) { return ranged; }
                int power = second.Attack.CompareTo(first.Attack);
                return power != 0 ? power : a.Id.CompareTo(b.Id);
            });
            // 援军至少二星：这是“援军比普通手牌强”的规则底线，数值上的星级倍率仍来自 StarConfig。
            foreach (HandCard card in candidates)
            {
                StarTier star = card.Star < StarTier.Two ? StarTier.Two : card.Star;
                if (!TryDeployReinforcement(card.Kind, star)) { continue; }
                // 先确定完整身体真的能落下，再消费这张牌；失败候选不能把手牌或落子额度扣掉。
                _hand.Remove(card.Id);
                RevEvent.DispatchEvent(Events.HandChanged);
                return true;
            }
            // 手牌为空时的免费兜底援军，沿用“重弩援军”增益的外形与星级配置，而不是再写死一份。
            PreparationBuffDef fallback = PreparationBuffCatalog.Get(PreparationBuffKind.Elite);
            return VictoryAssistEnabled && TryDeployReinforcement(fallback.Piece, (StarTier)fallback.Star);
        }

        /// <summary>寻找安全落点并一次性提交援军；不占手动落子额度，不参与双牌升级计数。</summary>
        private bool TryDeployReinforcement(PieceKind kind, StarTier star)
        {
            PieceDef def = PieceCatalog.Get(kind, star);
            int size = PieceCatalog.PlayerBodySize(star);
            BoardPos pos;
            if (!TryFindSupportPosition(def, size, out pos)) { return false; }
            var piece = new Piece(_nextPieceId++, def, GameSide.Player, size);
            // 近战可以突入敌方空眼，前面的试放已确认可攻击且不拆己方眼；仍然不能覆盖已有棋子。
            if (!_board.Place(piece, pos, respectEnemyEyes: false))
            {
                throw new System.InvalidOperationException("援军预检与提交不一致：" + pos);
            }
            _playerPieces.Add(piece);
            ApplyPreparationToNewPiece(piece);
            RefreshGoState();
            RevEvent.DispatchEvent<int>(Events.PiecePlaced, piece.Id);
            return true;
        }

        /// <summary>
        /// 远程优先靠近最大己方连通阵营，并远离敌人；近战必须落下就能攻击，优先可用的敌眼。
        /// 深快照内的气眼会被试算覆盖，最后只提交坐标；实战棋子不会被试算改血量、护盾或阵型。
        /// </summary>
        private bool TryFindSupportPosition(PieceDef def, int size, out BoardPos best)
        {
            best = new BoardPos(-1, -1);
            int bestScore = int.MinValue;
            List<Piece> mainGroup = new List<Piece>();
            foreach (List<Piece> group in GoRules.BuildGroups(_board))
            {
                if (group[0].Side == GameSide.Player && group.Count > mainGroup.Count) { mainGroup = group; }
            }
            // 同一份快照逐格试放、移除，避免每个候选都复制整个棋盘；实战数据始终只读。
            BoardModel trial = _board.CreateSnapshot();
            var reinforcement = new Piece(_nextPieceId, def, GameSide.Player, size);
            reinforcement.IgnoreEncirclement = _prepImmune;
            reinforcement.BonusLiberties = _libertyBonus + _roundLibertyBonus;
            for (int y = 0; y < Consts.BoardSize; y++)
            {
                for (int x = 0; x < Consts.BoardSize; x++)
                {
                    var pos = new BoardPos(x, y);
                    if (!CanSupportFit(pos, size, allowEnemyEyes: !def.IsRanged)) { continue; }
                    if (!trial.Place(reinforcement, pos, respectEnemyEyes: false)) { continue; }
                    GoRules.Evaluate(trial);
                    // 援军落点评分需要的是“身边贴着几颗敌人”，不能复用现在允许全盘援击的攻击列表。
                    // 否则后排格子会因全盘敌人多而拿高分，违背近战援军贴敌/破眼的原有要求。
                    // 直接数身体边缘相邻的存活敌棋，不在每一个候选格创建临时目标List。
                    int attacks = 0;
                    if (!def.IsRanged)
                    {
                        foreach (Piece enemy in _enemyPieces)
                        {
                            if (enemy.IsAlive && BodyDistance(pos, size, enemy) == 1) { attacks++; }
                        }
                    }
                    bool safe = !reinforcement.IsLocked && PreservesPlayerFormation(trial);
                    // 没有敌人时准备期仍能发远程援军；近战战斗支援仍需贴敌，避免援击新规则改变落点偏好。
                    if (safe && (def.IsRanged || attacks > 0))
                    {
                        int enemyDistance = Consts.BoardSize * 2;
                        foreach (Piece enemy in _enemyPieces)
                        {
                            if (enemy.IsAlive) { enemyDistance = System.Math.Min(enemyDistance, BodyDistance(pos, size, enemy)); }
                        }
                        int mainDistance = Consts.BoardSize;
                        int nearbyAllies = 0;
                        foreach (Piece ally in mainGroup)
                        {
                            int distance = BodyDistance(pos, size, ally);
                            mainDistance = System.Math.Min(mainDistance, distance);
                            if (distance <= 2) { nearbyAllies++; }
                        }
                        int score;
                        if (def.IsRanged)
                        {
                            // 距敌太近的格子受重罚；主体阵营附近的后排空格比孤零零的远角更优。
                            score = enemyDistance * 24 + nearbyAllies * 35 - mainDistance * 22 + FreeNeighbors(pos);
                            if (enemyDistance <= 2) { score -= 500; }
                        }
                        else
                        {
                            bool enemyEye = _board.GetCell(pos).EyeSide == GameSide.Enemy;
                            score = (enemyEye ? 600 : 0) + attacks * 90 - enemyDistance * 25 + reinforcement.Liberties * 4;
                        }
                        if (score > bestScore) { best = pos; bestScore = score; }
                    }
                    trial.Remove(reinforcement);
                }
            }
            return bestScore != int.MinValue;
        }

        /// <summary>比较旧棋的眼数和气，防止自动加棋堵死同伴；玩家手动换位则由玩家自行决定是否拆眼。</summary>
        private bool PreservesPlayerFormation(BoardModel trial)
        {
            foreach (Piece original in _playerPieces)
            {
                if (!original.IsAlive) { continue; }
                Piece copy = EnemyFormationPlanner.Find(trial, original.Id);
                if (copy == null || copy.EyeCount < original.EyeCount || (original.Liberties > 0 && copy.Liberties <= 0)) { return false; }
            }
            return true;
        }

        /// <summary>两块身体最近边缘的曼哈顿距离；大棋不能只拿左下角算距离。</summary>
        private static int BodyDistance(BoardPos pos, int size, Piece other)
        {
            int dx = System.Math.Max(0, System.Math.Max(other.Pos.X - (pos.X + size - 1), pos.X - (other.Pos.X + other.Size - 1)));
            int dy = System.Math.Max(0, System.Math.Max(other.Pos.Y - (pos.Y + size - 1), pos.Y - (other.Pos.Y + other.Size - 1)));
            return dx + dy;
        }
    }
}
