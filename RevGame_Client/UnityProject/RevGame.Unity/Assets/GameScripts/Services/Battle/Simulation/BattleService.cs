// ============================================================
// BattleService.cs
// 所有玩家动作先校验再修改数据；表现层仅接收事件，不能自己扣牌、改血或判断胜负。
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
    /// <summary>
    /// 战斗服务实现（一局战斗的唯一业务入口）。
    ///
    /// 【它在整个项目里的位置】
    /// 界面（UI/Panels）只把玩家点击转发进来、把服务状态读出去；流程（Flow）负责何时开始/结束回合；
    /// 表现（View）只监听事件画动画。所有“落子是否合法、谁掉血、谁赢、发什么奖励”都在这里决定，
    /// 因此它既是规则唯一入口，也是唯一可信的数据源。
    ///
    /// 【为什么不是单例】
    /// 调用方只认 IBattleService，取实例统一走 RevServices.GetRequired&lt;IBattleService&gt;()；
    /// 注册只有启动入口一行。这样测试可以注册固定随机种子的实例，将来换实现也不用改界面；
    /// 单例的 Xxx.Instance 会把实现类型焊死在每个调用点，这正是本目录要避免的写法。
    ///
    /// 【为什么用 partial 拆成多个文件】
    /// 一个文件 2000 行以上没人愿意读。这里按“职责”拆：Simulation（回合与战斗推进）、
    /// Preparation（落牌/刷新/换位）、Progression（成长与奖励）、Support（事件与伤害）、
    /// AI（敌方状态机与增援）、Diagnostics（仅编辑器验收入口）。
    /// partial 只是同一个类的不同片段，**不是**多个服务、更不是多个实例。
    ///
    /// 【用到的框架概念】
    /// · RevServices：本类型由启动入口注册，全局取同一个实例；
    /// · RevScope/RevLogic：BattleLogic 属于战斗域，域关闭即停止驱动，服务自己不建 Timer；
    /// · RevTimer：由 BattleLogic.Every(固定小步) 调 Tick，保证不同帧率结果一致；
    /// · RevEvent：状态变化只广播事实，界面自己订阅，服务不引用任何 UI 类型；
    /// · RevHeavyFSM：敌方 AI 的状态机由本服务持有并逐步驱动；
    /// · RevLog/LastError：失败必须带中文原因，不能静默 return；
    /// · Excel 导表：血量、增益、节奏、奖励等策划数值全部读 Balance/各配置目录，不写死在这里。
    ///
    /// 职责边界（刻意划死，避免又长成一个 900 行的万能管理器）：
    /// - **只管规则与数据**：棋盘、回合、伤害、胜负；
    /// - **不管表现**：不碰 GameObject、不写 UI —— 只通过 <c>RevEvent</c> 发广播，表现层自己去听；
    /// - **不管节奏**：什么时候 tick 由 <c>BattleLogic</c> 决定（它用 RevTimer 驱动）。
    ///
    /// 这样"改表现不用碰规则、改规则不用碰表现"，两边都能单独验证。
    /// </summary>
    public sealed partial class BattleService : IBattleService, IAttackPrediction
    {
        /// <summary>日志标签：RevLog 按它分组过滤，排查时先看 "RevGame"。</summary>
        private const string Tag = "RevGame";
        /// <summary>棋盘数据模型：只管占格、连通、气眼，不知道自己被谁画出来。</summary>
        private readonly BoardModel _board = new BoardModel(Consts.BoardSize);
        /// <summary>我方棋子名单。回合之间保留（残局继承），死亡后从这里移除。</summary>
        private readonly List<Piece> _playerPieces = new List<Piece>();
        /// <summary>敌方棋子名单。每回合重新生成，换回合时连同 AI 状态机一起回收。</summary>
        private readonly List<Piece> _enemyPieces = new List<Piece>();
        // 规则查询同步完成后立即消费；由服务独占并复用，绝不借给事件监听器保存。
        // 为什么复用：每 0.3125 秒的固定小步都会查询，每次都 new List 会造成持续 GC。
        private readonly List<BoardPos> _attackTargets = new List<BoardPos>(81);
        private readonly List<BoardPos> _moveTargets = new List<BoardPos>(81);
        /// <summary>手牌模型：负责“有哪些牌、怎么抽”，额度与扣牌仍由本服务决定。</summary>
        private readonly HandModel _hand = new HandModel();
        // 正式游玩使用随机种子；验收可在构造时传固定种子，复现同一手牌和事件序列。
        // 随机控制抽牌、事件和普攻暴击；伤害公式仍读Excel，落子是否合法仍由棋盘规则判定。
        private System.Random _random;
        /// <summary>手牌只读视图：界面只能看，增删必须走 TryPlayCard / RefreshHand。</summary>
        public IReadOnlyList<HandCard> Hand
        {
            get
            {
                return _hand.Cards;
            }
        }
        /// <summary>当前选中的手牌编号；0 = 未选。只影响落点提示，扣牌发生在真正落子成功之后。</summary>
        public int SelectedCardId
        {
            get; private set;
        }
        /// <summary>本回合剩余落子额度 = 上限 − 已落张数；刷新手牌不会把额度刷回来。</summary>
        public int CardsRemaining
        {
            get
            {
                return System.Math.Max(0, RoundCardLimit - _cardsPlayedThisRound);
            }
        }
        // 用户把各阶段的不同张数统一改为六张；实际发牌、消费额度与 UI 槽位必须一致。
        private int RoundCardLimit
        {
            get
            {
                return Consts.MaxCardsPerRound;
            }
        }
        /// <summary>棋子编号发号器：全服务唯一，事件与表现层都靠它区分是哪颗棋。</summary>
        private int _nextPieceId = 1;
        /// <summary>本回合已成功落下的手牌张数；额度判定只看它，刷新手牌不清零。</summary>
        private int _cardsPlayedThisRound;
        /// <summary>战斗累计模拟秒数（准备阶段恒为 0）；所有冷却/事件时刻都以它为准。</summary>
        private float _battleElapsed;
        /// <summary>外部传入的帧时间累加器：攒够一个固定小步才推进，保证不同帧率结果一致。</summary>
        private float _tickAccumulator;
        /// <summary>最近一次胜负结果；未结算为 null，界面据此决定是否显示结算。</summary>
        private GameSide? _lastWinner;
        /// <summary>当前阶段：准备 / 战斗 / 结算。所有写操作都必须先检查它。</summary>
        public GamePhase Phase
        {
            get; private set;
        }
        /// <summary>当前章节（1~6）。</summary>
        public int Chapter
        {
            get; private set;
        }
        /// <summary>当前回合（1~10，最后一个是 Boss）。</summary>
        public int Round
        {
            get; private set;
        }
        /// <summary>本回合是否 Boss 回合；Boss 回合不限时，靠擒王或全灭结束。</summary>
        public bool IsBossRound
        {
            get; private set;
        }
        /// <summary>棋盘模型（只读用途）。外部直接占用格子会绕过气眼刷新和事件，禁止。</summary>
        public BoardModel Board
        {
            get
            {
                return _board;
            }
        }
        /// <summary>战斗累计秒数；准备阶段为 0，HUD 进度条和冷却都读它。</summary>
        public float BattleElapsed
        {
            get
            {
                return _battleElapsed;
            }
        }
        /// <summary>所有回合均不限时，返回正无穷；时间仅用于驱动冷却，不参与胜负比较。</summary>
        public float BattleDuration
        {
            get
            {
                return float.PositiveInfinity;
            }
        }
        /// <summary>我方棋子只读视图；增删仍由服务内部完成，表现层不要 Remove。</summary>
        public IReadOnlyList<Piece> PlayerPieces
        {
            get
            {
                return _playerPieces;
            }
        }
        /// <summary>敌方棋子只读视图；换回合时整批回收并重建。</summary>
        public IReadOnlyList<Piece> EnemyPieces
        {
            get
            {
                return _enemyPieces;
            }
        }
        /// <summary>最近一次结算的胜方；null 表示还没结算。</summary>
        public GameSide? LastWinner
        {
            get
            {
                return _lastWinner;
            }
        }
        /// <summary>最近一次拒绝/失败的中文原因；空字符串表示当前没有错误。</summary>
        public string LastError
        {
            get; private set;
        }
        /// <summary>本回合实际通过规则验证的敌阵名称；空间受限时包含降级原因，供 HUD/GM 查询。</summary>
        public string EnemyFormation { get; private set; } = string.Empty;
        /// <summary>只读档位供界面/GM核对；真正策略由 EnemyTactics 使用。</summary>
        public int EnemyIntelligence => LevelCatalog.GetIntelligence(Chapter, Round);

        /// <summary>游戏默认开启随机战斗事件；该开关不提供免死或保胜。</summary>
        public BattleService() : this(true, null, RevServices.Get<IScoreProgress>())
        {
        }
        /// <summary>
        /// 战斗事件开关的重载：不传种子时使用系统随机，正式游玩走这个入口。
        /// </summary>
        public BattleService(bool victoryAssist) : this(victoryAssist, null)
        {
        }
        /// <summary>固定种子只控制手牌/事件等随机输入，方便重放同一局；正式入口省略种子即可。</summary>
        public BattleService(bool victoryAssist, int? seed, IScoreProgress scoreProgress = null)
        {
            _scoreProgress = scoreProgress;
            LifetimeScore = scoreProgress == null ? 0 : scoreProgress.Total;
            _random = seed.HasValue ? new System.Random(seed.Value) : new System.Random();
            VictoryAssistEnabled = victoryAssist;
            LastError = string.Empty;
            Phase = GamePhase.Prepare;
        }
        // =====================================================================
        //  回合生命周期
        // =====================================================================
        public bool StartRound(int chapter, int round)
        {
            RoundDef def = LevelCatalog.GetRound(chapter, round);

            if (def == null)
            {
                LastError = "关卡配置不存在：第 " + chapter + " 章第 " + round + " 回合";
                RevLog.Warn(LastError, Tag);
                return false;
            }
            // ★ 章间和失败重开要重置棋盘；只有同一章内的连续回合继承残局。
            bool newBoard = Chapter != chapter || round == 1;
            // 必须先验证整队。普通敌棋为 1×1，Boss 为 2×2/3×3，空出一格并不代表整块能落下。
            // 吞噬先在独立名单上规划，敌阵预检失败不会改真实棋盘或玩家残局。
            List<Piece> swallowed;
            List<Piece> survivors = PlanBossSwallow(def, newBoard, out swallowed);
            List<Piece> plannedEnemies;

            if (!TryPlanEnemies(def, newBoard, out plannedEnemies, survivors))
            {
                // 空格总数不保证存在连续Boss身体区；只在原吞噬预算内重选，不额外删棋。
                if (!TryPlanBossSpace(def, newBoard, swallowed.Count, out survivors, out swallowed, out plannedEnemies))
                {
                    return false;
                }
            }

            foreach (Piece swallowedPiece in swallowed)
            {
                RemovePlayerPieceCore(swallowedPiece);
            }
            LastBossSwallowedCount = swallowed.Count;

            if (newBoard)
            {
                CombatScore = 0;
                _playerPieces.Clear();
                ChapterScore = 0;
            }
            Chapter = chapter;
            Round = round;
            RoundScore = 0; // 只清本回合分，永久积分不因失败、换回合或返回大厅清零。
            HelperProtectionHealing = 0;
            IsBossRound = def.IsBoss;
            Phase = GamePhase.Prepare;
            _battleElapsed = 0f;
            _tickAccumulator = 0f;
            // 战斗时间每回合从零开始，存活棋子的绝对移动冷却也必须重置，不能带着上回合的时间戳进入新回合。
            foreach (Piece piece in _board.GetAllPieces())
            {
                piece.NextMoveAt = 0f;
            }
            _cardsPlayedThisRound = 0;
            _lastWinner = null;
            EyeStoneSelected = false;
            SelectedPieceId = 0;
            // 先归零本轮生命增益，再恢复准备属性；反过来会把上一轮的生命倍率重新套回残局。
            ResetCombatPacing();
            _nextFormationAt = Balance.RangedFormationSeconds;
            _formationMoves.Clear();
            ResetPreparation();
            _roundSpeedBonus = 0f;
            _roundLibertyBonus = 0;
            _battleEventCount = 0;
            _nextEventAt = Balance.BattleEventDelay;
            _eyeHealElapsed = 0f;
            ResetEnemyDeployment();
            _eventScheduled = VictoryAssistEnabled || IsBossRound || _random.NextDouble() < Balance.BattleEventChance;
            LastError = string.Empty;
            // 1) 清掉上一回合的敌人；我方棋子保留（策划案：布局与状态全部保留）。
            ClearEnemies();
            // 2) 我方棋子重新落回棋盘（上一回合结束时它们可能被移出过棋盘）。
            RebuildBoardFromRoster();
            // 3) 生成敌方阵容（Boss 回合额外加一个"大棋"，由表现层用 Boss 立绘）。
            CommitEnemyFormation(plannedEnemies);

            _nextPieceId += plannedEnemies.Count;
            DealHand();
            RefreshGoState();
            DeployInitialEyeGarrison();
            RevLog.Info("开始 " + def + "，敌方 " + _enemyPieces.Count + " 颗", Tag);
            ResetHelperRound();
            RevEvent.DispatchEvent<int>(Events.PhaseEnterPrepare, Round);

            if (swallowed.Count > 0)
            {
                RevEvent.DispatchEvent<string>(Events.BoardRuleNotice, "不好，boss发动了技能，强制吞噬了你的棋子");
            }
            return true;
        }
        /// <summary>
        /// 按棋子种类+星级落一张牌（GM/教学入口）。
        /// 内部仍要找到手中真实存在的同种牌，不能凭种类凭空生成棋子。
        /// </summary>
        public bool TryPlayCard(PieceKind kind, StarTier star, BoardPos pos)
        {
            // 保留原有 GM 的入口，但也必须真的拥有这张牌，不能凭空生成棋子。
            for (int i = 0; i < Hand.Count; i++)
            {
                if (Hand[i].Kind == kind && Hand[i].Star == star)
                {
                    return TryPlayCard(Hand[i].Id, pos);
                }
            }

            return Reject("手中没有这张牌，请先查看手牌");
        }
        /// <summary>
        /// 选中/取消一张手牌：只改“选择”，不扣牌也不落子。
        /// 同时清掉棋子选择与眼位石选择，保证一次点击只走一条路径。
        /// </summary>
        public bool SelectCard(int cardId)
        {
            if (Phase != GamePhase.Prepare)
            {
                return Reject("只有准备阶段可以选牌");
            }
            if (cardId != 0 && _hand.Find(cardId) == null)
            {
                return Reject("这张牌已经不在手中了");
            }
            SelectedCardId = cardId == SelectedCardId ? 0 : cardId;
            EyeStoneSelected = false;
            SelectedPieceId = 0;
            LastError = string.Empty;
            RevEvent.DispatchEvent(Events.SelectionChanged);
            return true;
        }
        /// <summary>
        /// 只做落点预检，供棋盘高亮反复调用：不占格、不扣牌、不发事件。
        /// 真正落子仍要再走一次校验，预检和提交分成两步是为了让失败不产生中间状态。
        /// </summary>
        public bool CanPlaySelectedAt(BoardPos pos)
        {
            if (Phase != GamePhase.Prepare && Phase != GamePhase.Battle)
            {
                return false;
            }
            Piece selected = FindSelectedPiece();

            if (selected != null)
            {
                return _board.CanFit(pos, selected.Size, GameSide.Player, selected, respectEnemyEyes: false);
            }
            // 战斗中高亮仅来自已选己方棋子的换位范围，不能把剩余手牌当作可落牌状态。
            if (Phase != GamePhase.Prepare)
            {
                return false;
            }
            if (EyeStoneSelected)
            {
                return EyeStones > 0 && CanCompleteEye(pos);
            }
            HandCard card = _hand.Find(SelectedCardId);

            if (card == null || CardsRemaining <= 0)
            {
                return false;
            }
            if (!CanAddPlayerRanged(PieceCatalog.Get(card.Kind, card.Star), false))
            {
                return false;
            }
            int size = PieceCatalog.PlayerBodySize(card.Star);
            return _board.CanFit(pos, size, GameSide.Player, respectEnemyEyes: false);
        }
        /// <summary>先完整校验，再落子和扣牌；失败时手牌、额度、棋盘都保持原样。</summary>
        public bool TryPlayCard(int cardId, BoardPos pos)
        {
            HandCard card = _hand.Find(cardId);

            if (card == null)
            {
                return Reject("请先选择一张仍在手中的牌");
            }
            bool placed = PlaceCard(card.Kind, card.Star, pos);

            if (!placed)
            {
                RevEvent.DispatchEvent(Events.SelectionChanged);
                return false;
            }
            _hand.Remove(cardId);
            SelectedCardId = 0;
            RegisterPlayedPair(card.Kind, _playerPieces[_playerPieces.Count - 1]);
            RevEvent.DispatchEvent(Events.HandChanged);
            RevEvent.DispatchEvent(Events.SelectionChanged);
            return true;
        }
        /// <summary>
        /// 统一的失败出口：记录中文原因并广播选择变化，让界面立即显示提示。
        /// 禁止只 return false 不写原因——那样玩家只能看到“点了没反应”。
        /// </summary>
        private bool Reject(string reason)
        {
            LastError = reason;
            RevEvent.DispatchEvent(Events.SelectionChanged);
            return false;
        }
        /// <summary>
        /// 每回合发满六张手牌；奖励牌先占一个槽位，再抽齐其余牌。
        /// 先占位是为了避免奖励牌在“抽满”之后追加、因超限被悄悄丢掉。
        /// </summary>
        private void DealHand()
        {
            _hand.Clear();
            SelectedCardId = 0;
            SelectedPieceId = 0;
            EyeStoneSelected = false;

            // ★ 每回合重新发满六张，与上轮剩余一张/零张无关。
            // 奖励牌先占一个名额，再抽齐其余牌，不能追加后因超限被悄悄丢掉。
            if (_rewardCard.HasValue)
            {
                _hand.Add(_rewardCard.Value, StarTier.One);
                _rewardCard = null;
            }
            while (_hand.Cards.Count < RoundCardLimit)
            {
                PieceKind kind = PickUnlockedKind();
                _hand.Add(kind, StarTier.One);
            }
            RevEvent.DispatchEvent(Events.HandChanged);
        }
        /// <summary>
        /// 落牌核心：校验阶段、额度、图鉴、整块棋子空间，再占格并继承本轮增益。
        /// 失败时手牌、额度、棋盘都保持原样；调用方只在返回 true 后才扣牌。
        /// </summary>
        private bool PlaceCard(PieceKind kind, StarTier star, BoardPos pos)
        {
            if (Phase != GamePhase.Prepare)
            {
                LastError = "当前不是准备阶段，不能落子";
                return false;
            }
            if (_cardsPlayedThisRound >= RoundCardLimit)
            {
                LastError = "本回合最多落 " + RoundCardLimit + " 张牌";
                return false;
            }
            PieceDef def = PieceCatalog.Get(kind, star);

            if (def == null)
            {
                LastError = "图鉴里没有这个棋子：" + kind + " " + star;
                return false;
            }
            if (!CanAddPlayerRanged(def, true))
            {
                return false;
            }
            int size = PieceCatalog.PlayerBodySize(star);

            if (!_board.CanFit(pos, size, GameSide.Player, respectEnemyEyes: false))
            {
                LastError = "这个格子不能落子：" + pos;
                return false;
            }
            Piece piece = new Piece(_nextPieceId++, def, GameSide.Player);

            if (!_board.Place(piece, pos, respectEnemyEyes: false))
            {
                LastError = "落子失败：" + pos;
                return false;
            }
            _playerPieces.Add(piece);
            ApplyPreparationToNewPiece(piece);
            _cardsPlayedThisRound++;
            LastError = string.Empty;
            RefreshGoState();
            RevEvent.DispatchEvent<int>(Events.PiecePlaced, piece.Id);
            RevLog.Info("落子 " + def.DisplayName + " → " + pos + "（本回合第 " + _cardsPlayedThisRound + " 张）", Tag);
            return true;
        }
        /// <summary>
        /// 从准备进入战斗：先检查至少一颗我方棋子和增益已发放，再清空选择并广播 PhaseEnterBattle。
        /// 开战后不再接受新手牌落子，只能移动已在场棋子（由 HandleBoardClick 判定）。
        /// </summary>
        public bool StartBattle()
        {
            if (Phase != GamePhase.Prepare)
            {
                LastError = "只有准备阶段能开始战斗（当前：" + Phase + "）";
                return false;
            }
            if (_board.IsSideWiped(GameSide.Player))
            {
                return Reject("请先放下至少一颗棋子再准备战斗");
            }
            if (VictoryAssistEnabled && !_preparationGranted)
            {
                return Reject("开战前请等待准备增益，约 " + System.Math.Max(1, (int)System.Math.Ceiling(Balance.PrepDelaySeconds - _preparationElapsed)) + " 秒");
            }
            SelectedCardId = 0;
            SelectedPieceId = 0;
            EyeStoneSelected = false;
            Phase = GamePhase.Battle;
            _battleElapsed = 0f;
            _tickAccumulator = 0f;
            LastError = string.Empty;
            RevEvent.DispatchEvent<int>(Events.PhaseEnterBattle, Round);
            RevEvent.DispatchEvent<string>(Events.BattleLog, "敌阵：" + EnemyFormation + "；先破连接，再压制远程");
            RevLog.Info("战斗开始（不限时，胜负由真实战斗决定）", Tag);
            return true;
        }
        /// <summary>
        /// 外部推进入口，由 BattleLogic 用固定小步调用。
        /// 准备期只推进增益发放；战斗期把时间攒成固定小步循环结算，
        /// 保证不同帧率下攻击次数、冷却与事件顺序一致。
        /// </summary>
        public void Tick(float deltaSeconds)
        {
            if (Phase == GamePhase.Prepare)
            {
                TickPreparation(deltaSeconds);
                return;
            }
            if (Phase != GamePhase.Battle)
            {
                return;
            }
            if (float.IsNaN(deltaSeconds) || float.IsInfinity(deltaSeconds) || deltaSeconds <= 0f)
            {
                return;
            }
            _tickAccumulator += deltaSeconds;
            // 每个标准攻击周期分四小步，远程两倍攻速就能每 0.625 秒打一次，
            // 而不是每 1.25 秒把两发挤在同一帧。普通棋仍需攒满整个周期，敌人移动按配置里的独立冷却执行。
            // 固定步长和进度累加共同保证：不同帧率下攻击次数相同。
            float stepSeconds = Balance.BattleSimulationStepSeconds;
            while (_tickAccumulator >= stepSeconds && Phase == GamePhase.Battle)
            {
                _tickAccumulator -= stepSeconds;
                _battleElapsed += stepSeconds;
                TickHelperProtection(stepSeconds);
                ResolveOneStep(stepSeconds);

                // 全灭是已经发生的结局，必须先结算；不能让同一步的自动援军复活已全灭的一方。
                if (HasBattleEnded())
                {
                    EndBattle();
                    break;
                }
                // 攻击结算完再补子：已被全灭或擒王的敌人不能靠同一帧的增援“复活”。
                TickEnemyDeployment();
                TickPreparationCombat(stepSeconds);
                TickEyeSupport();
                TryAutomaticEvent();
                // 全部战斗不限时；加速由真实增益、区域波动和炸弹伤害完成，不删除残敌。
                TickCombatPacing();

                if (!HasBattleEnded())
                {
                    TickHelper(stepSeconds);
                }
                if (HasBattleEnded())
                {
                    EndBattle();
                }
            }
        }
        /// <summary>
        /// 结算读完战报后调用：进入下一回合，或结束整局。
        /// </summary>
        public bool CompleteSettle()
        {
            if (Phase != GamePhase.Settle)
            {
                LastError = "当前不是结算阶段";
                return false;
            }
            return ContinueRound();
        }
        // =====================================================================
        //  战斗推进
        // =====================================================================
        /// <summary>
        /// 一个战斗步：双方交替行动；玩家按独立冷却执行贴敌/后撤，再结算攻击，敌人由各自状态机驱动。
        /// 规则简单但**完全确定**（同样的棋盘 + 同样的顺序 ⇒ 同样的结果），方便复现 bug。
        /// </summary>
        private void ResolveOneStep(float stepSeconds)
        {
            RefreshGoState();

            if (IsKingCaptured())
            {
                return;
            }
            TickBossEffects();
            TryArrangeRangedFormation();
            StepSide(GameSide.Player, stepSeconds / Balance.BattleTickSeconds);
            RefreshGoState();

            if (IsKingCaptured())
            {
                return;
            }
            StepSide(GameSide.Enemy, stepSeconds / Balance.BattleTickSeconds);
            // 升级只由成功打牌/明确增益触发。战斗轮询绝不自动吃掉守眼棋子。
            RefreshGoState();
            TickEyeHealing(stepSeconds);
        }
        /// <summary>
        /// 推进一方的行动。先按统一资格跳过死亡棋/锁死敌棋，再积攒攻击进度：满1才出手，不按显示帧数出手。
        /// 我方零气仍参与守阵反击；气眼状态及其原有受击惩罚继续保留，不能为出手偷偷清除锁死标记。
        /// 玩家按独立冷却决定接敌或后撤，有合法目标就攻击；敌人由EnemyTactics安全试走。
        /// 攻击和移动共用真实规则，但使用两套时钟，所以“万箭齐发”不会把棋子移动速度也翻倍。
        /// 缓冲列表归本服务持有，查询完成立即消费，不能交给异步视图保存；视图接收的是不可变事件快照。
        /// </summary>
        /// <param name="side">本小步处理的阵营；先玩家后敌人保证相同输入有相同结算顺序。</param>
        /// <param name="stepFraction">本小步占标准攻击周期的比例，通常是0.3125/1.25，即四分之一。</param>
        private void StepSide(GameSide side, float stepFraction = 1f)
        {
            if (side == GameSide.Enemy)
            {
                StepEnemyAI(stepFraction * Balance.BattleTickSeconds);
                return;
            }
            foreach (Piece piece in _playerPieces)
            {
                if (!piece.IsAlive || !MoveRules.CanAttack(piece))
                {
                    continue;
                }
                EnsurePlayerAI(piece).Tick(stepFraction * Balance.BattleTickSeconds);

                if (IsKingCaptured())
                {
                    return;
                }
            }
        }
        /// <summary>玩家状态的原子攻击入口。驻眼倍率只乘攻击进度；每次出手重新查询，致死后不会攻击旧目标。</summary>
        private void StepPlayerAttack(Piece piece, float stepFraction)
        {
            float rate = piece.Def.IsRanged ? RangedActionsPerTick : 1f;

            if (HasHelperProtection(piece))
            {
                rate *= 1f + (IsBossRound ? Balance.HelperBossHealingSpeedBonus : Balance.HelperProtectionSpeedBonus);
            }
            if (piece.Def.IsRanged)
            {
                rate *= 1f + HelperRangedSpeedBonus;
            }
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            // 测试计划显式申请的临时攻速；普通游玩没有租约时原样返回，发行包根本不包含此钩子。
            rate = ScaleAutomationAttackRate(rate);
#endif
            piece.ActionProgress += stepFraction * Balance.AttackSpeedScale * PreparationAttackRate(piece)
                * rate * piece.BossSpeedFactor * (1f + _speedBonus + _roundSpeedBonus + _combatHasteBonus) * piece.EyeSpeedMultiplier;
            int actions = piece.ConsumeCompletedActions();

            for (int action = 0; action < actions; action++)
            {
                MoveRules.GetAttackTargets(_board, piece, _attackTargets);

                if (_attackTargets.Count == 0)
                {
                    break;
                }
                Attack(piece, _board.GetPiece(_attackTargets[0]));

                if (IsKingCaptured())
                {
                    return;
                }
            }
        }
        /// <summary>
        /// 结算一次攻击。伤害公式放在这里（不要散到 UI）：
        /// 攻击力 ×（目标被锁死 ? 2 : 1）。策划案 §06：锁死挨双倍伤害。
        /// </summary>
        private void Attack(Piece attacker, Piece target)
        {
            if (attacker == null || target == null)
            {
                return;
            }
            if (!target.IsAlive || target.BossHidden || attacker.BossHidden || attacker.BossFrozen)
            {
                return;
            }
            // 攻击表现先发事实；暴击只在真正出手时抽样，AI预判绝不消耗随机序列。
            var attack = new PieceAttackInfo(attacker.Id, attacker.Def.Kind, attacker.Pos, attacker.Size, attacker.Side);
            RevEvent.DispatchEvent<PieceAttackInfo>(Events.PieceAttacked, attack);
            bool critical = attacker.Side == GameSide.Player && _random.NextDouble() < Balance.CriticalChance;
            int real = ResolveDamage(new DamageRequest(target, DamageBasis.Attack, critical ? BattleDamageKind.Critical : BattleDamageKind.Normal, attacker, fromSkill: false));
            // ★ 倍数从参数取，不写死“×2”：策划把锁死倍率改成 3 时，战报要跟着变。
            RevEvent.DispatchEvent<string>(Events.BattleLog, FormatAttackLog(attacker, target, real));
            RefreshGoState();
        }
        /// <summary>
        /// 结束战斗：判定胜方、停掉敌方状态机、清牌并广播结算事件。
        /// 先停 AI 再广播，避免界面在结算阶段还能看到旧回合的棋继续行动。
        /// </summary>
        private void EndBattle()
        {
            GameSide winner;

            if (IsKingCaptured())
            {
                winner = GameSide.Player;
            }
            else

                if (_board.IsSideWiped(GameSide.Player))
                {
                    winner = GameSide.Enemy;
                }
                else

                    if (_board.IsSideWiped(GameSide.Enemy))
                    {
                        winner = GameSide.Player;
                    }
                    else
                    {
                        winner = GameSide.Enemy;
                    }
            _lastWinner = winner;
            FlushScore();
            _hand.Clear();
            SelectedCardId = 0;
            SelectedPieceId = 0;
            EyeStoneSelected = false;
            RevEvent.DispatchEvent(Events.HandChanged);
            Phase = GamePhase.Settle;
            StopEnemyAI();

            if (IsBossRound && winner == GameSide.Player)
            {
                // Phase先落定，演出不能再次造成伤害；事件先于结算，流程可立即锁住提前弹窗/继续请求。
                Piece king = _enemyPieces.Find(piece => piece.IsKing);

                if (king != null)
                {
                    RevEvent.DispatchEvent<BossDefeatedInfo>(Events.BossDefeated,
                        new BossDefeatedInfo(Chapter, Round, king.Id, king.Pos, king.Size, ChapterScore, king.IsAlive));
                }
            }

            RevEvent.DispatchEvent<GameSide>(Events.PhaseEnterSettle, winner);
            RevEvent.DispatchEvent<GameSide>(Events.BattleEnded, winner);
            RevLog.Info("战斗结束：" + (winner == GameSide.Player ? "胜" : "负"), Tag);
        }
        /// <summary>唯一结束条件：真实全灭或满足残血门槛的擒王。时间不参与胜负。</summary>
        private bool HasBattleEnded()
        {
            return IsKingCaptured() || _board.IsSideWiped(GameSide.Player) || _board.IsSideWiped(GameSide.Enemy);
        }
        /// <summary>仅供固定输入验收比较血量变化；绝不参与胜负，避免按剩余血量把超时战斗判赢。</summary>
        private int TotalHp(GameSide side)
        {
            int sum = 0;
            foreach (Piece piece in side == GameSide.Player ? _playerPieces : _enemyPieces)
            {
                if (piece.IsAlive)
                {
                    sum += piece.Hp;
                }
            }
            return sum;
        }
        // =====================================================================
        //  生成 / 重建
        // =====================================================================
        /// <summary>
        /// 清空敌方棋子：必须先停 AI 再移除占格。
        /// 反过来的话，状态机在下一步仍可能拿到已经不在棋盘上的棋子，做出无效攻击或移动。
        /// </summary>
        private void ClearEnemies()
        {
            StopEnemyAI();
            for (int i = 0; i < _enemyPieces.Count; i++)
            {
                if (_enemyPieces[i].IsAlive)
                {
                    _board.Remove(_enemyPieces[i]);
                }
            }

            _enemyPieces.Clear();
        }
        /// <summary>
        /// 把"我方棋子名单"重新摆回棋盘：保留坐标与血量，只重摆那些还活着但不在棋盘上的。
        /// （回合之间棋盘会被清空重建，但玩家不该发现自己的棋子消失了。）
        /// </summary>
        private void RebuildBoardFromRoster()
        {
            // 原有驻眼身份随残局保留，但只还给同一格的同一颗棋；重建顺序不能决定谁得到增益。
            var eyeGarrisons = new List<KeyValuePair<Piece, BoardPos>>();
            foreach (Piece piece in _playerPieces)
            {
                if (piece.IsAlive && _board.GetCell(piece.Pos)?.EyeGarrisonId == piece.Id)
                {
                    eyeGarrisons.Add(new KeyValuePair<Piece, BoardPos>(piece, piece.Pos));
                }
            }
            _board.Reset();
            // 先把玩家棋子按原坐标放回去；坐标被占就找最近的空格。
            for (int i = 0; i < _playerPieces.Count; i++)
            {
                Piece piece = _playerPieces[i];

                if (!piece.IsAlive)
                {
                    continue;
                }
                if (_board.CanPlace(piece.Pos) && _board.Place(piece, piece.Pos))
                {
                    continue;
                }
                BoardPos free = FindFreeCellInOwnHalf(GameSide.Player);

                if (free.X >= 0)
                {
                    _board.Place(piece, free);
                }
            }

            foreach (KeyValuePair<Piece, BoardPos> saved in eyeGarrisons)
            {
                Cell cell = _board.GetCell(saved.Value);

                if (cell != null && cell.Occupant == saved.Key)
                {
                    cell.EyeGarrisonId = saved.Key.Id;
                }
            }
            // 清掉已经死掉的棋子。
            for (int i = _playerPieces.Count - 1; i >= 0; i--)
            {
                if (!_playerPieces[i].IsAlive)
                {
                    _playerPieces.RemoveAt(i);
                }
            }
        }
        /// <summary>
        /// 在我方半场找一个能放下的空格，作为原坐标被占时的兜底落点。
        /// 返回 (-1,-1) 表示完全没有空间，调用方必须保留原状而不是硬塞。
        /// </summary>
        private BoardPos FindFreeCellInOwnHalf(GameSide side)
        {
            int half = Consts.BoardSize / 2;
            for (int y = 0; y < half; y++)
            {
                int row = side == GameSide.Player ? y : Consts.BoardSize - 1 - y;
                for (int x = 0; x < Consts.BoardSize; x++)
                {
                    BoardPos pos = new BoardPos(x, row);

                    if (_board.CanPlace(pos))
                    {
                        return pos;
                    }
                }
            }
            return new BoardPos(-1, -1);
        }
        /// <summary>
        /// 敌方主将是否已被击败；没有主将的普通回合一律返回 false。
        /// 这是“擒王即胜”的唯一判断点，表现层不要自己再判断一次。
        /// </summary>
        private bool IsKingCaptured()
        {
            if (!IsBossRound)
            {
                return false;
            }
            Piece king = _enemyPieces.Find(p => p.IsKing);
            // 血线与包围同时满足才擒王。满血时即使零气，也必须继续打掉血量；真实击杀始终获胜。
            return king != null && (!king.IsAlive || (king.HpRatio <= Balance.BossCaptureHpRatio && king.IsLocked && !king.BossInvulnerable));
        }
        /// <summary>
        /// 先创建完整名单，再在独立棋盘验证眼阵和全部身体占格。成功后才交给 StartRound 一次性提交。
        /// 玩家残局太满时允许眼阵降级；完整名单仍放不下则明确拒绝，不能少生主将或清空旧回合。
        /// </summary>
        private bool TryPlanEnemies(RoundDef def, bool newBoard, out List<Piece> plan, IReadOnlyList<Piece> survivors = null)
        {
            plan = new List<Piece>();
            IReadOnlyList<Piece> retainedPlayers = survivors ?? _playerPieces;
            // ★ 双牌升级不再消耗棋子，存活阵容会逐轮变大。按继承阵容的星级总量补充敌方承伤与输出，
            // 让“保留十几颗远程”仍有交战时间，避免几秒清场和一路堆满导致下一轮无法出生。
            // 这只是当前原型的动态压力，基础成长仍由关卡进度决定，胜负始终由真实战斗规则判断。
            float strength = 0f;

            if (!newBoard)
            {
                foreach (Piece player in retainedPlayers)
                {
                    if (player.IsAlive)
                    {
                        strength += (int)player.Def.Star;
                    }
                }
            }
            float pressure = System.Math.Max(1f, strength / Balance.EnemyPressureStarDivisor);
            float hpScale = def.HpScale * System.Math.Min(Balance.EnemyPressureHpMax, pressure * pressure);
            float attackScale = def.AttackScale * System.Math.Min(Balance.EnemyPressureAttackMax, (float)System.Math.Sqrt(pressure));

            if (def.IsBoss)
            {
                // 每章只允许一颗Boss。配置加载先校验单体，此处再拒绝异常数据，避免多主将进入占格计划。
                BossLineup boss = LevelCatalog.BossOf(def.Chapter);
                PieceDef bossDef = PieceCatalog.Get(boss.Kind, boss.Star);

                if (bossDef == null)
                {
                    return Reject("Boss 图鉴配置缺失：" + boss.Kind + " " + boss.Star);
                }
                if (boss.BodySizes.Count != 1)
                {
                    return Reject("Boss阵容必须且只能有一颗主将");
                }
                {
                    // 主将独立耐久：普通敌兵调参不再决定全部Boss节奏；只在出生时计算，不修改共享图鉴。
                    float scale = hpScale * Balance.BossHpMultiplier * (1f + (def.Chapter - 1) * Balance.BossHpGrowthPerChapter);
                    // 主将厚血与低单发伤害独立配置；沿用实例AttackScale，普攻/技能/AI预测自然使用同一份数值。
                    plan.Add(new Piece(_nextPieceId + plan.Count, bossDef, GameSide.Enemy, boss.BodySizes[0], true, true, scale, attackScale * Balance.BossAttackMultiplier));
                }
            }

            StarTier star = LevelCatalog.GetEnemyStar(def.Chapter, def.IsBoss);
            foreach (PieceKind kind in def.EnemyKinds)
            {
                PieceDef pieceDef = PieceCatalog.Get(kind, star);

                if (pieceDef == null)
                {
                    return Reject("敌方图鉴配置缺失：" + kind);
                }
                plan.Add(new Piece(_nextPieceId + plan.Count, pieceDef, GameSide.Enemy, hpScale: hpScale, attackScale: attackScale));
            }
            string report;

            if (!EnemyFormationPlanner.TryArrange(plan, retainedPlayers, newBoard, out report, def.Intelligence, def.Progress))
            {
                return Reject(report);
            }
            EnemyFormation = report;
            return true;
        }
    }
}
