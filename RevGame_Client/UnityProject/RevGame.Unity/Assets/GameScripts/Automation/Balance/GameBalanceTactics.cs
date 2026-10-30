#if UNITY_EDITOR || DEVELOPMENT_BUILD || REVGAME_OFFLINE_TESTS
using System;
using RevGame.Core;
using RevGame.Data;
using RevGame.Pieces;
using RevGame.Services;

namespace RevGame.Automation
{
    /// <summary>正常策略测试的只读决策器：前排接敌、远程留后排、同伴连接但给眼位留空。只挑动作，不改模型。</summary>
    public static class GameBalanceTactics
    {
        /// <summary>从真实手牌和可用格选一张牌；算法权重只表示位置偏好，不是血攻/奖励数值。</summary>
        public static bool ChoosePlacement(IBattleService battle, bool careless, out int cardId, out BoardPos position, out string reason, int rangedPreference = 0)
        {
            cardId = 0;
            position = default;
            reason = "无合法落点";
            double best = double.NegativeInfinity;
            int liveFront = 0, liveRanged = 0;
            foreach (Piece own in battle.PlayerPieces)
            {
                if (own.IsAlive)
                {
                    if (own.Def.IsRanged)
                    {
                        liveRanged++;
                    }
                    else
                    {
                        liveFront++;
                    }
                }
            }
            foreach (var card in battle.Hand)
            {
                PieceDef def = PieceCatalog.Get(card.Kind, card.Star);
                if (def.IsRanged && liveRanged >= Balance.RangedLimit)
                {
                    continue;
                }
                int size = PieceCatalog.PlayerBodySize(card.Star);
                for (int y = 0; y < Consts.BoardSize; y++)
                {
                    for (int x = 0; x < Consts.BoardSize; x++)
                    {
                        var pos = new BoardPos(x, y);
                        if (!battle.Board.CanFit(pos, size, GameSide.Player, respectEnemyEyes: false))
                        {
                            continue;
                        }
                        float enemyDistance = Nearest(battle.EnemyPieces, pos, size);
                        int adjacent = Friends(battle, pos, size);
                        // 只基于当前盘面评分；不移动实战棋子试算，不窥探未来随机牌或敌AI选择。
                        double score;
                        if (careless)
                        {
                            score = -y * 12 - x * .7 + (def.IsRanged ? 0 : 2);
                        }
                        else
                        {
                            float targetDistance = def.IsRanged ? 4f : 1.5f;
                            score = -Math.Abs(enemyDistance - targetDistance) * 3 + adjacent * 2.4 - Math.Abs(x - 4) * .22;
                            // 脆皮近战在初始布阵与主坦同一条线，不贴着敌方后排出生；前排放己方半场等自动AI接敌。
                            if (!def.IsRanged && y > 3)
                            {
                                score -= (y - 3) * 8;
                            }
                            if (!def.IsRanged && def.MaxHp < 90 && enemyDistance < 3)
                            {
                                score -= 6;
                            }
                            if (def.IsRanged && enemyDistance < 3)
                            {
                                score -= 15;
                            }
                            if (!def.IsRanged && liveFront < 2)
                            {
                                score += 8;
                            }
                            if (def.IsRanged && liveRanged < liveFront + 2)
                            {
                                score += 4;
                            }
                            // Agent可以指定本轮补前排或补远程的策略；只改变合法候选排序，不制造手牌或棋子。
                            if (rangedPreference != 0 && def.IsRanged == (rangedPreference > 0))
                            {
                                score += 12;
                            }
                            // 给下半盘两处候选眼留呼吸空间，鼓励周围连接；不是直接制造眼或额外棋子。
                            if ((x == 2 || x == 6) && y == 1)
                            {
                                score -= 8;
                            }
                            if (y <= 2 && (Math.Abs(x - 2) + Math.Abs(y - 1) == 1 || Math.Abs(x - 6) + Math.Abs(y - 1) == 1))
                            {
                                score += 1.8;
                            }
                        }
                        if (score > best)
                        {
                            best = score;
                            cardId = card.Id;
                            position = pos;
                            reason = def.IsRanged ? "远程保持距离并与前排连接" : "近战迎敌并保护后排";
                        }
                    }
                }
            }
            return cardId != 0;
        }
        private static float Nearest(System.Collections.Generic.IReadOnlyList<Piece> enemies, BoardPos pos, int size)
        {
            float best = 99;
            foreach (Piece enemy in enemies)
            {
                if (!enemy.IsAlive)
                {
                    continue;
                }
                float dx = Math.Max(0, Math.Max(enemy.Pos.X - (pos.X + size - 1), pos.X - (enemy.Pos.X + enemy.Size - 1)));
                float dy = Math.Max(0, Math.Max(enemy.Pos.Y - (pos.Y + size - 1), pos.Y - (enemy.Pos.Y + enemy.Size - 1)));
                best = Math.Min(best, dx + dy);
            }
            return best;
        }
        private static int Friends(IBattleService battle, BoardPos pos, int size)
        {
            int count = 0;
            for (int n = 0; n < size; n++)
            {
                foreach (var p in new[]
                {
                    new BoardPos(pos.X - 1, pos.Y + n),
                    new BoardPos(pos.X + size, pos.Y + n),
                    new BoardPos(pos.X + n, pos.Y - 1),
                    new BoardPos(pos.X + n, pos.Y + size)
                }
                )
                {
                    if (battle.Board.GetPiece(p)?.Side == GameSide.Player)
                    {
                        count++;
                    }
                }
            }
            return count;
        }
        /// <summary>只在实际受伤且敌人逼近时撤一颗远程，使用正式换位入口；同一时刻最多一个决策。</summary>
        public static bool ChooseRetreat(IBattleService battle, out BoardPos source, out BoardPos destination)
        {
            source = destination = default;
            foreach (Piece own in battle.PlayerPieces)
            {
                if (!own.IsAlive || !own.Def.IsRanged || own.HpRatio > .55f || own.IsEyeGarrison)
                {
                    continue;
                }
                float near = Nearest(battle.EnemyPieces, own.Pos, own.Size);
                if (near > 2)
                {
                    continue;
                }
                double best = near;
                BoardPos chosen = own.Pos;
                for (int y = 0; y < Consts.BoardSize; y++)
                {
                    for (int x = 0; x < Consts.BoardSize; x++)
                    {
                        var pos = new BoardPos(x, y);
                        if (Math.Abs(x - own.Pos.X) + Math.Abs(y - own.Pos.Y) > 3 || !battle.Board.CanFit(pos, own.Size, GameSide.Player, own, respectEnemyEyes: false))
                        {
                            continue;
                        }
                        double score = Nearest(battle.EnemyPieces, pos, own.Size) + Friends(battle, pos, own.Size) * .15;
                        if (score > best + .5)
                        {
                            best = score;
                            chosen = pos;
                        }
                    }
                }
                if (chosen != own.Pos)
                {
                    source = own.Pos;
                    destination = chosen;
                    return true;
                }
            }
            return false;
        }
        /// <summary>奖励按当前解锁/伤势选择：优先新兵种，其次少量全队强化；不读取未来关卡随机结果。</summary>
        public static int ChooseReward(IBattleService battle)
        {
            int best = 0;
            double score = double.NegativeInfinity;
            for (int i = 0; i < battle.Rewards.Count; i++)
            {
                RewardDef reward = battle.Rewards[i];
                double value = reward.Kind == RewardKind.NewPiece ? 5 : reward.Kind == RewardKind.TeamBoost ? 3 : reward.Kind == RewardKind.EyeStone ? 2 : 1;
                if (value > score)
                {
                    best = i;
                    score = value;
                }
            }
            return best;
        }
    }
}
#endif
