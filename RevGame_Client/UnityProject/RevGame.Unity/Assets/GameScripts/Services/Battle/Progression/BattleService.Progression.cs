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
    // partial 片段：与其他 BattleService.*.cs 共享同一实例，完整类说明见 Simulation/BattleService.cs。
    public sealed partial class BattleService
    {
        /// <summary>本章 Boss 后的三选一候选；生成后只能消费一次，重复点击会被拒绝。</summary>
        private readonly List<RewardDef> _rewards = new List<RewardDef>();
        // ★ 开局解锁的棋种来自 PieceConfig.UnlockedAtStart，而战斗服务在启动入口注册时配置表还没加载完，
        // 所以不能在字段初始化里读配置——第一次真正用到时才从配置创建，之后随运行保留解锁进度。
        private HashSet<PieceKind> _unlockedKindsStore;
        /// <summary>
        /// 已解锁棋种，首次使用时才从配置创建。
        /// 为什么延迟：服务在启动入口注册时配置表还没加载完，字段初始化里读表会拿到空集合。
        /// </summary>
        private HashSet<PieceKind> _unlockedKinds => _unlockedKindsStore ?? (_unlockedKindsStore = PieceCatalog.CreateInitialUnlockedSet());
        /// <summary>跨回合保留的全队攻击加成（跳过强化、奖励等）；准备增益另算，不混在一起。</summary>
        private float _attackBonus;
        /// <summary>跨回合保留的攻击提速加成；只影响出手频率，不影响移动。</summary>
        private float _speedBonus;
        /// <summary>跨回合保留的额外气数；会改变围困判定，改动后必须 RefreshGoState。</summary>
        private int _libertyBonus;
        /// <summary>下一回合额外塞进手牌的奖励棋种；发牌时先占一个槽位，避免超限被丢。</summary>
        private PieceKind? _rewardCard;
        /// <summary>已开过的战役数；奖励池据此解锁更高档选项，退出重进不会清零。</summary>
        private int _campaignsStarted;
        /// <summary>整局是否已结束；防止结算按钮重复触发通关事件。</summary>
        private bool _finished;
        /// <summary>奖励候选只读视图；真实领取必须走 ChooseReward，不能由界面自己加属性。</summary>
        public IReadOnlyList<RewardDef> Rewards
        {
            get
            {
                return _rewards.AsReadOnly();
            }
        }
        /// <summary>持有眼位石数量；每局开始归零，只有选中补眼奖励才会增加。</summary>
        public int EyeStones
        {
            get; private set;
        }
        /// <summary>
        /// 是否处于“点金色空格补眼”的模式；与手牌、棋子选择互斥。
        /// </summary>
        public bool EyeStoneSelected
        {
            get; private set;
        }
        /// <summary>
        /// 已解锁到第几章；选关界面据此决定按钮是否可点，战斗服务据此拒绝越级开局。
        /// </summary>
        public int UnlockedChapter { get; private set; } = 1;

#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
        /// <summary>GM解锁本次运行的全部章节。服务拥有解锁值；不伪造通关、发奖励或改当前战役，退出程序后不保存。</summary>
        public int UnlockAllChaptersForDevelopment()
        {
            // 重复执行不重复广播；使用项目章节总数，避免GM与正式选关范围不一致。
            if (UnlockedChapter < Consts.ChapterCount)
            {
                UnlockedChapter = Consts.ChapterCount;
                RevEvent.DispatchEvent(Events.ProgressChanged);
            }
            return UnlockedChapter;
        }
#endif

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
            return BeginCampaignAtRound(chapter, 1);
        }
        /// <summary>普通入口与开发Boss直达共享战役重置；回合号来自关卡配置，不另造测试敌阵或胜负规则。</summary>
        private bool BeginCampaignAtRound(int chapter, int round)
        {
            if (LevelCatalog.GetRound(chapter, round) == null)
            {
                return Reject("目标关卡配置不存在");
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
            ChapterScore = 0;
            CombatScore = 0;
            RevEvent.DispatchEvent(Events.ProgressChanged);
            return StartRound(chapter, round);
        }
#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
        /// <summary>开发专用：不要求前章解锁，按该章真实Boss回合开新战役；不写通关/奖励存档。</summary>
        public bool BeginBossCampaignForDevelopment(int chapter)
        {
            // 先校验章号，拒绝时不能清掉当前棋盘、增益或手牌。
            if (chapter < 1 || chapter > Consts.ChapterCount)
            {
                return Reject("Boss章节只允许1～" + Consts.ChapterCount);
            }
            var target = LevelCatalog.GetRound(chapter, Balance.BossRoundIndex);

            if (target == null || !target.IsBoss)
            {
                return Reject("目标章节缺少Boss回合配置");
            }
            if (_automationBoostOwner != null && _automationBoostOwner != this)
            {
                return Reject("另一份自动化仍持有战斗增益，请先结束该测试");
            }
            ClearAutomationBoost(this);
            return BeginCampaignAtRound(chapter, Balance.BossRoundIndex);
        }
#endif

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
        /// <summary>
        /// 按奖励表生成三选一候选：先按权重抽不重复的项，再写入 _rewards 并广播。
        /// 失败时宁可给出保底项，也不能让结算界面出现空白选项。
        /// </summary>
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
            // 眼位石要开过足够多局战役才出现（RewardConfig.UnlockAfterCampaigns）。
            if (_campaignsStarted >= RewardCatalog.UnlockAfterCampaigns(RewardKind.EyeStone))
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
        /// <summary>
        /// 计算某类奖励的抽取权重：人数落后等局势会提高对应奖励的权重。
        /// 权重值来自配置表，本方法只负责把局势换算成倍率。
        /// </summary>
        private int RewardWeight(RewardKind kind)
        {
            // 基础权重与“人数落后时的额外权重”都来自 RewardConfig；落后判定的倍数来自 Reward.OutnumberedFactor。
            bool outnumbered = _board.GetPiecesOf(GameSide.Player).Count * Balance.RewardOutnumberedFactor < _board.GetPiecesOf(GameSide.Enemy).Count;
            return RewardCatalog.WeightOf(kind, outnumbered);
        }
        /// <summary>
        /// 领取第 index 个奖励并推进下一章。
        /// 必须校验阶段、索引和“是否已领过”，重复点击不能重复加属性。
        /// </summary>
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

                    // 加成量来自 RewardBoostConfig；奖励卡上的说明文字用的是同一个数，所以“看到的”和“拿到的”一致。
                    if (reward.Boost == BoostKind.Attack)
                    {
                        _attackBonus += RewardCatalog.BoostAmount(BoostKind.Attack);
                    }
                    else

                        if (reward.Boost == BoostKind.Speed)
                        {
                            _speedBonus += RewardCatalog.BoostAmount(BoostKind.Speed);
                        }
                        else
                        {
                            _libertyBonus += (int)RewardCatalog.BoostAmount(BoostKind.Liberty);
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
        /// <summary>
        /// 最后普通回合未落子时跳过，直接挑战 Boss 并换取永久攻击加成。
        /// 已落子会被拒绝，防止“拿满本回合收益再跳过”绕过代价。
        /// </summary>
        public bool SkipRound()
        {
            if (Phase != GamePhase.Prepare || Round != Balance.NormalRoundsPerChapter)
            {
                return Reject("只能在第 " + Balance.NormalRoundsPerChapter + " 回合准备阶段跳过");
            }
            // 已经落牌就不能拿满收益又跳过，以免绕过「放弃本回合落子」的代价。
            if (_cardsPlayedThisRound != 0)
            {
                return Reject("本回合已落子，不能再跳过换强化");
            }
            if (!StartRound(Chapter, Balance.BossRoundIndex))
            {
                return false;
            }
            _attackBonus += Balance.RoundSkipAttackBonus;
            return true;
        }
        /// <summary>
        /// 从已解锁棋种按权重抽一个手牌种类；场上已有同职业时提高其权重。
        /// 只决定“发什么牌”，不扣额度、不落子。
        /// </summary>
        private PieceKind PickUnlockedKind()
        {
            var pool = new List<PieceKind>();
            foreach (PieceKind kind in System.Enum.GetValues(typeof(PieceKind)))
            {
                if (!_unlockedKinds.Contains(kind))
                {
                    continue;
                }
                // 每个已解锁棋种先放基础权重份；场上已有同职业棋子时再多放额外权重份（Hand.KindBaseWeight / Hand.ClassBonusWeight）。
                for (int copy = 0; copy < Balance.HandKindBaseWeight; copy++)
                {
                    pool.Add(kind);
                }
                PieceClass job = PieceCatalog.Get(kind, StarTier.One).Class;

                if (_playerPieces.Exists(p => p.IsAlive && p.Def.Class == job))
                {
                    for (int copy = 0; copy < Balance.HandClassBonusWeight; copy++)
                    {
                        pool.Add(kind);
                    }
                }
            }
            return pool[_random.Next(pool.Count)];
        }
        /// <summary>
        /// 同职业在场数量达到门槛后的攻击加成（按配置档位计算）。
        /// 只作数值加成，不改变棋子本身属性，因此换职业/死亡后会自动回落。
        /// </summary>
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
                // 同职业在场数量每达到一档门槛，加一次职业加成（门槛与加成比例都在“战斗数值参数.xlsx”）。
                if (count >= Balance.ClassThresholdLow)
                {
                    bonus += Balance.ClassAttackBonus;
                }
                if (count >= Balance.ClassThresholdHigh)
                {
                    bonus += Balance.ClassAttackBonus;
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

            if (count % Balance.UpgradePairsPerStar != 0)
            {
                return;
            }
            // 优先最早入场的棋，四次同类牌可把第一颗逐步养到三星；满星后再培养下一颗。
            Piece original = _playerPieces.Find(p => p != placed && p.IsAlive && p.Def.Kind == kind && p.Def.Star < StarTier.Three);

            if (original == null)
            {
                return;
            }
            UpgradePiece(original);
        }
        /// <summary>
        /// 原位升星：替换定义、刷新最大生命与攻击，并广播升级事件。
        /// 不新建棋子，避免棋盘上出现“旧棋消失、新棋出生”的假移动。
        /// </summary>
        private void UpgradePiece(Piece piece)
        {
            if (!piece.UpgradeInPlace())
            {
                return;
            }
            RefreshGoState();
            RevEvent.DispatchEvent<int>(Events.PieceMerged, piece.Id);
            RevEvent.DispatchEvent<int>(Events.PieceHpChanged, piece.Id);
        }
    }
}
