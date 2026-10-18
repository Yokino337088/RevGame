// ============================================================
// BattleService.Progression.cs —— 战斗服务的成长与回合推进部分。
// partial 只是把同一个服务按职责拆文件，不是另一份服务或单例。
// 奖励先生成后消费；只有「选择成功」才会开下一章，重复点击不能重复领奖。
// ============================================================
using System.Collections.Generic;
using RevGame.Board;
using RevGame.Core;
using RevGame.Data;
using RevGame.Pieces;
using Revolution;

namespace RevGame.Services
{
    public sealed partial class BattleService
    {
        private readonly List<RewardDef> _rewards = new List<RewardDef>();
        private readonly HashSet<PieceKind> _unlockedKinds = new HashSet<PieceKind>
        {
            PieceKind.Swordman,
            PieceKind.Shield,
            PieceKind.Archer,
            PieceKind.MageFire
        };
        private float _attackBonus;
        private float _speedBonus;
        private int _libertyBonus;
        private PieceKind? _rewardCard;
        private int _campaignsStarted;
        private bool _finished;
        public IReadOnlyList<RewardDef> Rewards
        {
            get
            {
                return _rewards.AsReadOnly();
            }
        }

        public int EyeStones { get; private set; }
        public bool EyeStoneSelected { get; private set; }
        public int UnlockedChapter { get; private set; } = 1;

        public float TeamAttackBonus
        {
            get
            {
                return _attackBonus + ClassBonus(GameSide.Player);
            }
        }

        /// <summary>从选关界面开一局；只保留局外解锁，清掉上局战斗增益，避免反复进出刷强化。</summary>
        public bool BeginCampaign(int chapter)
        {
            if (chapter < 1 || chapter > UnlockedChapter)
            {
                return Reject("这一章尚未解锁");
            }

            _playedPairs.Clear();
            _campaignsStarted++;
            _attackBonus = 0f;
            _speedBonus = 0f;
            _libertyBonus = 0;
            EyeStones = 0;
            EyeStoneSelected = false;
            SelectedPieceId = 0;
            _rewardCard = null;
            _rewards.Clear();
            _finished = false;
            _playerPieces.Clear();
            RevEvent.DispatchEvent(Events.ProgressChanged);
            return StartRound(chapter, 1);
        }

        /// <summary>由结算按钮或 GM 调用；完成最终 Boss 后只派发一次通关，不重新开最终回合。</summary>
        public bool ContinueRound()
        {
            if (Phase != GamePhase.Settle)
            {
                return Reject("请先完成当前战斗并查看结算");
            }

            if (_finished)
            {
                return Reject("本局已经结束，请返回主界面");
            }

            if (_lastWinner != GameSide.Player)
            {
                return StartRound(Chapter, 1);
            }

            if (!IsBossRound)
            {
                return StartRound(Chapter, Round + 1);
            }

            UnlockedChapter = System.Math.Min(Consts.ChapterCount, System.Math.Max(UnlockedChapter, Chapter + 1));
            RevEvent.DispatchEvent(Events.ProgressChanged);
            if (Chapter == Consts.ChapterCount)
            {
                _finished = true;
                RevEvent.DispatchEvent<bool>(Events.GameFinished, true);
                return true;
            }

            Phase = GamePhase.Reward;
            BuildRewards();
            RevEvent.DispatchEvent(Events.PhaseEnterReward);
            return true;
        }

        private void BuildRewards()
        {
            _rewards.Clear();
            var candidates = new List<RewardDef>();
            PieceKind duplicate = PickUnlockedKind();
            candidates.Add(new RewardDef(RewardKind.DuplicateCard, duplicate, BoostKind.Attack));
            candidates.Add(new RewardDef(RewardKind.TeamBoost, duplicate, (BoostKind)_random.Next(3)));
            var locked = new List<PieceKind>();
            foreach (PieceKind kind in System.Enum.GetValues(typeof(PieceKind)))
            {
                if (!_unlockedKinds.Contains(kind))
                {
                    locked.Add(kind);
                }
            }

            if (locked.Count > 0)
            {
                candidates.Add(new RewardDef(RewardKind.NewPiece, locked[_random.Next(locked.Count)], BoostKind.Attack));
            }

            if (_campaignsStarted >= 5)
            {
                candidates.Add(new RewardDef(RewardKind.EyeStone, PieceKind.Shield, BoostKind.Liberty));
            }

            // 解锁池耗尽而眼位石尚未开放时，用不同的强化内容补齐三项，不能显示一个无效的「新棋子」。
            if (candidates.Count < 3)
            {
                BoostKind other = (BoostKind)(((int)candidates[1].Boost + 1) % 3);
                candidates.Add(new RewardDef(RewardKind.TeamBoost, duplicate, other));
            }

            while (_rewards.Count < 3)
            {
                int total = 0;
                foreach (var option in candidates)
                {
                    total += RewardWeight(option.Kind);
                }

                int roll = _random.Next(total);
                int chosen = 0;
                for (int i = 0; i < candidates.Count; i++)
                {
                    roll -= RewardWeight(candidates[i].Kind);
                    if (roll < 0)
                    {
                        chosen = i;
                        break;
                    }
                }

                _rewards.Add(candidates[chosen]);
                candidates.RemoveAt(chosen);
            }
        }

        private int RewardWeight(RewardKind kind)
        {
            int weight = kind == RewardKind.DuplicateCard ? 30 : (kind == RewardKind.EyeStone ? 20 : 25);
            if ((kind == RewardKind.DuplicateCard || kind == RewardKind.EyeStone) && _board.GetPiecesOf(GameSide.Player).Count * 2 < _board.GetPiecesOf(GameSide.Enemy).Count)
            {
                weight += 20;
            }

            return weight;
        }

        public bool ChooseReward(int index)
        {
            if (Phase != GamePhase.Reward)
            {
                return Reject("当前没有待选择的章节奖励");
            }

            if (index < 0 || index >= _rewards.Count)
            {
                return Reject("奖励编号超出范围");
            }

            RewardDef reward = _rewards[index];
            switch (reward.Kind)
            {
                case RewardKind.TeamBoost:
                    if (reward.Boost == BoostKind.Attack)
                    {
                        _attackBonus += Consts.RewardAttackBonus;
                    }
                    else if (reward.Boost == BoostKind.Speed)
                    {
                        _speedBonus += Consts.RewardAttackBonus;
                    }
                    else
                    {
                        _libertyBonus++;
                    }

                    break;
                case RewardKind.NewPiece:
                    _unlockedKinds.Add(reward.PieceKind);
                    break;
                case RewardKind.DuplicateCard:
                    _rewardCard = reward.PieceKind;
                    break;
                case RewardKind.EyeStone:
                    EyeStones++;
                    break;
            }

            _rewards.Clear();
            RevEvent.DispatchEvent(Events.ProgressChanged);
            return StartRound(Chapter + 1, 1);
        }

        public bool SkipRound()
        {
            if (Phase != GamePhase.Prepare || Round != Consts.NormalRoundsPerChapter)
            {
                return Reject("只能在第 10 回合准备阶段跳过");
            }

            // 已经落牌就不能拿满收益又跳过，以免绕过「放弃本回合落子」的代价。
            if (_cardsPlayedThisRound != 0)
            {
                return Reject("本回合已落子，不能再跳过换强化");
            }

            if (!StartRound(Chapter, Consts.BossRoundIndex))
            {
                return false;
            }

            _attackBonus += Consts.RewardAttackBonus;
            return true;
        }

        private PieceKind PickUnlockedKind()
        {
            var pool = new List<PieceKind>();
            foreach (PieceKind kind in System.Enum.GetValues(typeof(PieceKind)))
            {
                if (!_unlockedKinds.Contains(kind))
                {
                    continue;
                }

                pool.Add(kind);
                pool.Add(kind);
                pool.Add(kind);
                PieceClass job = PieceCatalog.Get(kind, StarTier.One).Class;
                if (_playerPieces.Exists(p => p.IsAlive && p.Def.Class == job))
                {
                    pool.Add(kind);
                }
            }

            return pool[_random.Next(pool.Count)];
        }

        private float ClassBonus(GameSide side)
        {
            int[] counts = new int[4];
            foreach (Piece piece in _board.GetPiecesOf(side))
            {
                counts[(int)piece.Def.Class]++;
            }

            float bonus = 0f;
            foreach (int count in counts)
            {
                if (count >= 3)
                {
                    bonus += Consts.ClassAttackBonus;
                }

                if (count >= 5)
                {
                    bonus += Consts.ClassAttackBonus;
                }
            }

            return bonus;
        }

        // 每种棋的成功打牌数跨轮累计，重开一局才清零；刷新、落子失败、自动援军均不计数。
        private readonly Dictionary<PieceKind, int> _playedPairs = new Dictionary<PieceKind, int>();

        /// <summary>每成功打出第二张同类牌，给场上已有同类棋升一级；新牌照常留在指定格。</summary>
        private void RegisterPlayedPair(PieceKind kind, Piece placed)
        {
            int count;
            _playedPairs.TryGetValue(kind, out count);
            _playedPairs[kind] = ++count;
            if (count % 2 != 0) { return; }
            // 优先最早入场的棋，四次同类牌可把第一颗逐步养到三星；满星后再培养下一颗。
            Piece original = _playerPieces.Find(p => p != placed && p.IsAlive && p.Def.Kind == kind && p.Def.Star < StarTier.Three);
            if (original == null) { return; }
            UpgradePiece(original);
        }

        private void UpgradePiece(Piece piece)
        {
            if (!piece.UpgradeInPlace()) { return; }
            RefreshGoState();
            RevEvent.DispatchEvent<int>(Events.PieceMerged, piece.Id);
            RevEvent.DispatchEvent<int>(Events.PieceHpChanged, piece.Id);
        }
    }
}
