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
    /// 职责边界（刻意划死，避免又长成一个 900 行的万能管理器）：
    /// - **只管规则与数据**：棋盘、回合、伤害、胜负；
    /// - **不管表现**：不碰 GameObject、不写 UI —— 只通过 <c>RevEvent</c> 发广播，表现层自己去听；
    /// - **不管节奏**：什么时候 tick 由 <c>BattleLogic</c> 决定（它用 RevTimer 驱动）。
    ///
    /// 这样"改表现不用碰规则、改规则不用碰表现"，两边都能单独验证。
    /// </summary>
    public sealed partial class BattleService : IBattleService
    {
        private const string Tag = "RevGame";
        private readonly BoardModel _board = new BoardModel(Consts.BoardSize);
        private readonly List<Piece> _playerPieces = new List<Piece>();
        private readonly List<Piece> _enemyPieces = new List<Piece>();
        private readonly HandModel _hand = new HandModel();
        // 正式游玩使用随机种子；验收可在构造时传固定种子，复现同一手牌和事件序列。
        private readonly System.Random _random;
        public IReadOnlyList<HandCard> Hand
        {
            get
            {
                return _hand.Cards;
            }
        }

        public int SelectedCardId { get; private set; }

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

        private int _nextPieceId = 1;
        private int _cardsPlayedThisRound;
        private float _battleElapsed;
        private float _tickAccumulator;
        private GameSide? _lastWinner;
        public GamePhase Phase { get; private set; }
        public int Chapter { get; private set; }
        public int Round { get; private set; }
        public bool IsBossRound { get; private set; }

        public BoardModel Board
        {
            get
            {
                return _board;
            }
        }

        public float BattleElapsed
        {
            get
            {
                return _battleElapsed;
            }
        }

        public float BattleDuration
        {
            get
            {
                return IsBossRound ? float.PositiveInfinity : Consts.BattleDurationSeconds;
            }
        }

        public IReadOnlyList<Piece> PlayerPieces
        {
            get
            {
                return _playerPieces;
            }
        }

        public IReadOnlyList<Piece> EnemyPieces
        {
            get
            {
                return _enemyPieces;
            }
        }

        public GameSide? LastWinner
        {
            get
            {
                return _lastWinner;
            }
        }

        public string LastError { get; private set; }
        /// <summary>本回合实际通过规则验证的敌阵名称；空间受限时包含降级原因，供 HUD/GM 查询。</summary>
        public string EnemyFormation { get; private set; } = string.Empty;
        /// <summary>只读档位供界面/GM核对；真正策略由 EnemyTactics 使用。</summary>
        public int EnemyIntelligence => LevelCatalog.GetRound(Chapter, Round)?.Intelligence ?? 1;

        /// <summary>游戏默认开启轻松模式；编辑器可传 false 验证无辅助的基础规则。</summary>
        public BattleService() : this(true)
        {
        }

        public BattleService(bool victoryAssist) : this(victoryAssist, null)
        {
        }

        /// <summary>固定种子只控制手牌/事件等随机输入，方便重放同一局；正式入口省略种子即可。</summary>
        public BattleService(bool victoryAssist, int? seed)
        {
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
            List<Piece> plannedEnemies;
            if (!TryPlanEnemies(def, newBoard, out plannedEnemies))
            {
                return false;
            }

            if (newBoard)
            {
                _playerPieces.Clear();
            }

            Chapter = chapter;
            Round = round;
            IsBossRound = def.IsBoss;
            Phase = GamePhase.Prepare;
            _battleElapsed = 0f;
            _tickAccumulator = 0f;
            _cardsPlayedThisRound = 0;
            _lastWinner = null;
            EyeStoneSelected = false;
            SelectedPieceId = 0;
            ResetPreparation();
            _roundSpeedBonus = 0f;
            _roundLibertyBonus = 0;
            _battleEventCount = 0;
            _nextEventAt = Consts.BattleEventDelay;
            _decisiveSupportUsed = false;
            _supportStage = 0;
            _lastEnemyDamageAt = 0f;
            _nextSupportPulseAt = 0f;
            _lastStandShown = false;
            _eyeHealElapsed = 0f;
            _eventScheduled = VictoryAssistEnabled || IsBossRound || _random.NextDouble() < Consts.BattleEventChance;
            LastError = string.Empty;
            // 1) 清掉上一回合的敌人；我方棋子保留（策划案：布局与状态全部保留）。
            ClearEnemies();
            // 2) 我方棋子重新落回棋盘（上一回合结束时它们可能被移出过棋盘）。
            RebuildBoardFromRoster();
            // 3) 生成敌方阵容（Boss 回合额外加一个"大棋"，由表现层用 Boss 立绘）。
            foreach (Piece enemy in plannedEnemies)
            {
                if (!_board.Place(enemy, enemy.Pos))
                {
                    throw new System.InvalidOperationException("敌方占格预检与实际落子不一致");
                }

                _enemyPieces.Add(enemy);
            }

            _nextPieceId += plannedEnemies.Count;
            DealHand();
            RefreshGoState();
            RevLog.Info("开始 " + def + "，敌方 " + _enemyPieces.Count + " 颗", Tag);
            RevEvent.DispatchEvent<int>(Events.PhaseEnterPrepare, Round);
            return true;
        }

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

        public bool CanPlaySelectedAt(BoardPos pos)
        {
            if (Phase != GamePhase.Prepare)
            {
                return false;
            }

            if (EyeStoneSelected)
            {
                return EyeStones > 0 && CanCompleteEye(pos);
            }

            Piece selected = FindSelectedPiece();
            if (selected != null)
            {
                return _board.CanFit(pos, selected.Size, GameSide.Player, selected, respectEnemyEyes: false);
            }

            HandCard card = _hand.Find(SelectedCardId);
            if (card == null || CardsRemaining <= 0)
            {
                return false;
            }

            int size = card.Star == StarTier.Three ? 2 : 1;
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

        private bool Reject(string reason)
        {
            LastError = reason;
            RevEvent.DispatchEvent(Events.SelectionChanged);
            return false;
        }

        private void DealHand()
        {
            _hand.Clear();
            SelectedCardId = 0;
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

            int size = star == StarTier.Three ? 2 : 1;
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

            if (VictoryAssistEnabled && !_preparationGranted) { return Reject("开战前请等待准备增益，约 " + System.Math.Max(1, (int)System.Math.Ceiling(3f - _preparationElapsed)) + " 秒"); }
            SelectedCardId = 0;
            SelectedPieceId = 0;
            EyeStoneSelected = false;
            Phase = GamePhase.Battle;
            _battleElapsed = 0f;
            _tickAccumulator = 0f;
            LastError = string.Empty;
            RevEvent.DispatchEvent<int>(Events.PhaseEnterBattle, Round);
            RevEvent.DispatchEvent<string>(Events.BattleLog, "敌阵：" + EnemyFormation + "；先破连接，再压制远程");
            RevLog.Info("战斗开始（限时 " + BattleDuration + " 秒）", Tag);
            return true;
        }

        public void Tick(float deltaSeconds)
        {
            if (Phase == GamePhase.Prepare)
            {
                TickPreparation(deltaSeconds);
                return;
            }
            if (Phase != GamePhase.Battle) { return; }
            if (float.IsNaN(deltaSeconds) || float.IsInfinity(deltaSeconds) || deltaSeconds <= 0f)
            {
                return;
            }

            _tickAccumulator += deltaSeconds;
            // 每个标准攻击周期分四小步，远程两倍攻速就能每 0.625 秒打一次，
            // 而不是每 1.25 秒把两发挤在同一帧。普通棋仍需攒满整个周期，敌人移动仍是五秒一次。
            // 固定步长和进度累加共同保证：不同帧率下攻击次数相同。
            float stepSeconds = Consts.BattleSimulationStepSeconds;
            while (_tickAccumulator >= stepSeconds && Phase == GamePhase.Battle)
            {
                _tickAccumulator -= stepSeconds;
                _battleElapsed += stepSeconds;
                ResolveOneStep(stepSeconds);
                TickPreparationCombat(stepSeconds);
                TryAutomaticEvent();
                ApplyVictorySupport();
                bool timeout = !IsBossRound && _battleElapsed >= BattleDuration;
                bool captured = IsKingCaptured();
                if (timeout || captured || _board.IsSideWiped(GameSide.Enemy) || _board.IsSideWiped(GameSide.Player))
                {
                    EndBattle(timeout);
                }
            }
        }

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
        /// 一个战斗步：双方交替行动 —— 先攻击射程内的目标；我方守阵，敌方仅在射程不足且移动冷却结束时接近。
        /// 规则简单但**完全确定**（同样的棋盘 + 同样的顺序 ⇒ 同样的结果），方便复现 bug。
        /// </summary>
        private void ResolveOneStep(float stepSeconds)
        {
            RefreshGoState();
            if (IsKingCaptured())
            {
                return;
            }

            StepSide(GameSide.Player, stepSeconds / Consts.BattleTickSeconds);
            RefreshGoState();
            if (IsKingCaptured())
            {
                return;
            }

            StepSide(GameSide.Enemy, stepSeconds / Consts.BattleTickSeconds);
            // 升级只由成功打牌/明确增益触发。战斗轮询绝不自动吃掉守眼棋子。
            RefreshGoState();
            _eyeHealElapsed += stepSeconds;
            if (_eyeHealElapsed >= Consts.EyeHealSeconds)
            {
                _eyeHealElapsed -= Consts.EyeHealSeconds;
                foreach (Piece p in _board.GetAllPieces())
                {
                    if (p.EyeCount >= 2 && p.Heal(System.Math.Max(1, (int)(p.MaxHp * Consts.EyeHealRatio))) > 0)
                    {
                        RevEvent.DispatchEvent<int>(Events.PieceHpChanged, p.Id);
                    }
                }
            }
        }

        private void StepSide(GameSide side, float stepFraction = 1f)
        {
            List<Piece> pieces = side == GameSide.Player ? _playerPieces : _enemyPieces;
            for (int i = 0; i < pieces.Count; i++)
            {
                Piece piece = pieces[i];
                if (!piece.IsAlive)
                {
                    continue;
                }

                if (piece.IsLocked)
                {
                    continue;
                }

                float rate = side == GameSide.Player && piece.Def.IsRanged ? RangedActionsPerTick : 1f;
                piece.ActionProgress += stepFraction * (side == GameSide.Player ? rate * (1f + _speedBonus + _roundSpeedBonus) : 1f);
                int actions = (int)piece.ActionProgress;
                piece.ActionProgress -= actions;
                if (actions == 0) { continue; }
                // 1) 能打就打。
                List<BoardPos> targets = MoveRules.GetAttackTargets(_board, piece);
                if (targets.Count > 0)
                {
                    for (int action = 0; action < actions; action++)
                    {
                        // 连击的上一发可能已经击败原目标；每发重查，才能继续射击下一名敌人。
                        targets = MoveRules.GetAttackTargets(_board, piece);
                        if (targets.Count == 0) { break; }
                        Piece target = side == GameSide.Enemy ? EnemyTactics.ChooseTarget(_board, piece, targets, EnemyIntelligence) : _board.GetPiece(targets[0]);
                        Attack(piece, target);
                        if (IsKingCaptured())
                        {
                            return;
                        }
                    }

                    continue;
                }

                // 2) 我方始终守住玩家布置的阵位，成一眼/两眼后也不会被自动 AI 拆散。
                // 敌方只有「没有攻击目标」才走到这里；每四个逻辑步才允许靠近一次。
                // 攻速增益只增加攻击次数，不增加移动次数，避免连击事件让整盘棋乱跳。
                int tick = (int)System.Math.Round(_battleElapsed / Consts.BattleTickSeconds);
                if (side == GameSide.Player || tick % Consts.EnemyMoveEveryTicks != 0)
                {
                    continue;
                }

                // 保留每种走法，但滑步不再一口气穿过整盘；马步仍保留原有蹩马腿规则。
                List<BoardPos> moves = MoveRules.GetMoveTargets(_board, piece);
                moves.RemoveAll(pos => piece.Def.Pattern != MovePattern.Knight &&
                    System.Math.Abs(pos.X - piece.Pos.X) + System.Math.Abs(pos.Y - piece.Pos.Y) > 1);
                if (moves.Count == 0)
                {
                    continue;
                }

                BoardPos best = EnemyTactics.ChooseSafeMove(_board, piece, moves, EnemyIntelligence);
                // 只接受确实更接近目标的落点；被阻挡时原地等待，不能来回横跳。
                if (best != piece.Pos && _board.Move(piece, best))
                {
                    RefreshGoState();
                    RevEvent.DispatchEvent<int>(Events.PieceMoved, piece.Id);
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

            if (!target.IsAlive)
            {
                return;
            }

            float multiplier = target.IsLocked ? Consts.LockedDamageMultiplier : Consts.BaseDamageMultiplier;
            if (!target.IgnoreEncirclement && target.ConnectedCells <= 1)
            {
                multiplier *= Consts.IsolatedDamageMultiplier;
            }

            float linked = System.Math.Min(Consts.MaxLinkAttackBonus, (attacker.ConnectedCells - 1) * Consts.LinkAttackPerCell);
            multiplier *= 1f + System.Math.Max(0f, linked) + ClassBonus(attacker.Side) + (attacker.Side == GameSide.Player ? _attackBonus + PreparationAttackBonus(attacker, target) : 0f);
            if (IsBossRound)
            {
                multiplier *= 1f + (int)(_battleElapsed / Consts.BossAccelerationSeconds) * Consts.BossAccelerationBonus;
            }

            var attack = new PieceAttackInfo(attacker.Id, attacker.Def.Kind, attacker.Pos, attacker.Size, attacker.Side);
            RevEvent.DispatchEvent<PieceAttackInfo>(Events.PieceAttacked, attack);
            // 双方一起降低单发伤害，保留远程较高的出手频率，让玩家能看见互相消耗和支援反击。
            int raw = System.Math.Max(1, (int)(attacker.Def.Attack * attacker.AttackScale * multiplier * Consts.CombatDamageScale));
            int real = DamagePiece(target, raw, attacker);
            if (attacker.Side == GameSide.Player && _prepLifeSteal > 0f && attacker.Heal((int)(real * _prepLifeSteal)) > 0)
            {
                RevEvent.DispatchEvent<int>(Events.PieceHpChanged, attacker.Id);
            }
            RevEvent.DispatchEvent<string>(Events.BattleLog, attacker.Def.DisplayName + " → " + target.Def.DisplayName + " -" + real + (target.IsLocked ? "（锁死×2）" : ""));
            RefreshGoState();
        }

        private void EndBattle(bool timeout)
        {
            GameSide winner;
            if (IsKingCaptured())
            {
                winner = GameSide.Player;
            }
            else if (_board.IsSideWiped(GameSide.Player))
            {
                winner = GameSide.Enemy;
            }
            else if (timeout)
            {
                // 只有普通回合会走到超时判定；Boss 不限时，胜负由擒王或我方全灭决定。
                winner = TotalHp(GameSide.Player) >= TotalHp(GameSide.Enemy) ? GameSide.Player : GameSide.Enemy;
                RevEvent.DispatchEvent<string>(Events.BattleLog, "战斗超时，按剩余血量判定");
            }
            else if (_board.IsSideWiped(GameSide.Enemy))
            {
                winner = GameSide.Player;
            }
            else
            {
                winner = GameSide.Enemy;
            }

            _lastWinner = winner;
            _hand.Clear();
            SelectedCardId = 0;
            RevEvent.DispatchEvent(Events.HandChanged);
            Phase = GamePhase.Settle;
            RevEvent.DispatchEvent<GameSide>(Events.PhaseEnterSettle, winner);
            RevEvent.DispatchEvent<GameSide>(Events.BattleEnded, winner);
            RevLog.Info("战斗结束：" + (winner == GameSide.Player ? "胜" : "负"), Tag);
        }

        private int TotalHp(GameSide side)
        {
            List<Piece> list = side == GameSide.Player ? _playerPieces : _enemyPieces;
            int sum = 0;
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].IsAlive)
                {
                    sum += list[i].Hp;
                }
            }

            return sum;
        }

        // =====================================================================
        //  生成 / 重建
        // =====================================================================
        private void ClearEnemies()
        {
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

            // 清掉已经死掉的棋子。
            for (int i = _playerPieces.Count - 1; i >= 0; i--)
            {
                if (!_playerPieces[i].IsAlive)
                {
                    _playerPieces.RemoveAt(i);
                }
            }
        }

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

        private bool IsKingCaptured()
        {
            if (!IsBossRound)
            {
                return false;
            }

            Piece king = _enemyPieces.Find(p => p.IsKing);
            return king != null && (!king.IsAlive || king.IsLocked);
        }

        /// <summary>
        /// 先创建完整名单，再在独立棋盘验证眼阵和全部身体占格。成功后才交给 StartRound 一次性提交。
        /// 玩家残局太满时允许眼阵降级；完整名单仍放不下则明确拒绝，不能少生主将或清空旧回合。
        /// </summary>
        private bool TryPlanEnemies(RoundDef def, bool newBoard, out List<Piece> plan)
        {
            plan = new List<Piece>();
            // ★ 双牌升级不再消耗棋子，存活阵容会逐轮变大。按继承阵容的星级总量补充敌方承伤与输出，
            // 让“保留十几颗远程”仍有交战时间，避免几秒清场和一路堆满导致下一轮无法出生。
            // 这只是当前原型的动态压力，基础成长仍由关卡进度决定；最终收尾保障不受它影响。
            float strength = 0f;
            if (!newBoard)
            {
                foreach (Piece player in _playerPieces) { if (player.IsAlive) { strength += (int)player.Def.Star; } }
            }
            float pressure = System.Math.Max(1f, strength / 6f);
            float hpScale = def.HpScale * System.Math.Min(16f, pressure * pressure);
            float attackScale = def.AttackScale * System.Math.Min(4f, (float)System.Math.Sqrt(pressure));
            if (def.IsBoss)
            {
                int[] sizes = def.Chapter <= 2 ? new[] { 2 } : (def.Chapter <= 4 ? new[] { 2, 2 } : (def.Chapter == 5 ? new[] { 3, 2, 2 } : new[] { 3, 3 }));
                for (int i = 0; i < sizes.Length; i++)
                {
                    float scale = hpScale * (1f + (def.Chapter - 1) * 0.25f);
                    plan.Add(new Piece(_nextPieceId + plan.Count, PieceCatalog.Get(PieceKind.HeavySword, StarTier.Three), GameSide.Enemy, sizes[i], true, i == 0, scale, attackScale));
                }
            }
            StarTier star = LevelCatalog.GetEnemyStar(def.Chapter, def.IsBoss);
            foreach (PieceKind kind in def.EnemyKinds)
            {
                PieceDef pieceDef = PieceCatalog.Get(kind, star);
                if (pieceDef == null) { return Reject("敌方图鉴配置缺失：" + kind); }
                plan.Add(new Piece(_nextPieceId + plan.Count, pieceDef, GameSide.Enemy, hpScale: hpScale, attackScale: attackScale));
            }
            string report;
            if (!EnemyFormationPlanner.TryArrange(plan, _playerPieces, newBoard, out report, def.Intelligence)) { return Reject(report); }
            EnemyFormation = report;
            return true;
        }
    }
}
