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
        private readonly System.Random _random = new System.Random();
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

        /// <summary>游戏默认开启轻松模式；编辑器可传 false 验证无辅助的基础规则。</summary>
        public BattleService() : this(true)
        {
        }

        public BattleService(bool victoryAssist)
        {
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
            // ★ 必须先为整队找好位置。否则后三章的 3★ 敌人占 2×2，单格空着不代表放得下；
            //   先清旧棋盘再尝试，会出现「名单里有敌人，画面上却没有」或 Boss 主将缺失。
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
            _roundSpeedBonus = 0f;
            _roundLibertyBonus = 0;
            _battleEventCount = 0;
            _nextEventAt = Consts.BattleEventDelay;
            _decisiveSupportUsed = false;
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
            TryMergePieces();
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

            SelectedCardId = 0;
            SelectedPieceId = 0;
            EyeStoneSelected = false;
            Phase = GamePhase.Battle;
            _battleElapsed = 0f;
            _tickAccumulator = 0f;
            LastError = string.Empty;
            RevEvent.DispatchEvent<int>(Events.PhaseEnterBattle, Round);
            RevLog.Info("战斗开始（限时 " + BattleDuration + " 秒）", Tag);
            return true;
        }

        public void Tick(float deltaSeconds)
        {
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
            // 而不是每 1.25 秒把两发挤在同一帧。普通棋仍需攒满整个周期，敌人移动仍是五秒一次。
            // 固定步长和进度累加共同保证：不同帧率下攻击次数相同。
            float stepSeconds = Consts.BattleSimulationStepSeconds;
            while (_tickAccumulator >= stepSeconds && Phase == GamePhase.Battle)
            {
                _tickAccumulator -= stepSeconds;
                _battleElapsed += stepSeconds;
                ResolveOneStep(stepSeconds);
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
            // 上次因 2×2 空间不足而暂缓的合成，在移动或死亡腾出位置后重新尝试。
            // 不能在 StepSide 遍历名单的途中合成，否则删除材料会让下一颗棋子漏行动。
            TryMergePieces();
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
            GameSide foe = side == GameSide.Player ? GameSide.Enemy : GameSide.Player;
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
                        Attack(piece, _board.GetPiece(targets[0]));
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

                BoardPos best = ChooseMoveTowardNearestFoe(piece, moves, foe);
                // 只接受确实更接近目标的落点；被阻挡时原地等待，不能来回横跳。
                if (NearestFoeDistance(best, foe) < NearestFoeDistance(piece.Pos, foe) && _board.Move(piece, best))
                {
                    RevEvent.DispatchEvent<int>(Events.PieceMoved, piece.Id);
                }
            }
        }

        /// <summary>在候选格里挑一个"离最近的敌人最近"的（同距离时挑 Y 更靠近敌方的，保证有推进感）。</summary>
        private BoardPos ChooseMoveTowardNearestFoe(Piece piece, List<BoardPos> moves, GameSide foe)
        {
            BoardPos best = moves[0];
            int bestScore = int.MaxValue;
            for (int i = 0; i < moves.Count; i++)
            {
                int nearest = NearestFoeDistance(moves[i], foe);
                // 平局时优先往敌方方向推进：玩家往上、敌人往下。
                int push = piece.Side == GameSide.Player ? -moves[i].Y : moves[i].Y;
                int score = nearest * 100 + push;
                if (score < bestScore)
                {
                    bestScore = score;
                    best = moves[i];
                }
            }

            return best;
        }

        private int NearestFoeDistance(BoardPos from, GameSide foe)
        {
            List<Piece> list = foe == GameSide.Player ? _playerPieces : _enemyPieces;
            int min = int.MaxValue;
            for (int i = 0; i < list.Count; i++)
            {
                if (!list[i].IsAlive)
                {
                    continue;
                }

                int d = System.Math.Abs(list[i].Pos.X - from.X) + System.Math.Abs(list[i].Pos.Y - from.Y);
                if (d < min)
                {
                    min = d;
                }
            }

            return min == int.MaxValue ? 999 : min;
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
            if (target.ConnectedCells <= 1)
            {
                multiplier *= Consts.IsolatedDamageMultiplier;
            }

            float linked = System.Math.Min(Consts.MaxLinkAttackBonus, (attacker.ConnectedCells - 1) * Consts.LinkAttackPerCell);
            multiplier *= 1f + System.Math.Max(0f, linked) + ClassBonus(attacker.Side) + (attacker.Side == GameSide.Player ? _attackBonus : 0f);
            if (IsBossRound)
            {
                multiplier *= 1f + (int)(_battleElapsed / Consts.BossAccelerationSeconds) * Consts.BossAccelerationBonus;
            }

            var attack = new PieceAttackInfo(attacker.Id, attacker.Def.Kind, attacker.Pos, attacker.Size, attacker.Side);
            RevEvent.DispatchEvent<PieceAttackInfo>(Events.PieceAttacked, attack);
            int raw = (int)(attacker.Def.Attack * multiplier);
            int real = DamagePiece(target, raw, attacker);
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
        /// 为下一回合预留所有敌棋占格。这里的集合只记录本次预检占用，不保存另一份棋盘；
        /// 失败返回时，原棋盘、手牌、阶段、编号均未改变，玩家仍可查看原因并返回主界面。
        /// </summary>
        private bool TryPlanEnemies(RoundDef def, bool newBoard, out List<Piece> plan)
        {
            plan = new List<Piece>();
            var occupied = new HashSet<BoardPos>();
            if (!newBoard)
            {
                foreach (Piece piece in _playerPieces)
                {
                    if (!piece.IsAlive)
                    {
                        continue;
                    }

                    for (int y = 0; y < piece.Size; y++)
                    {
                        for (int x = 0; x < piece.Size; x++)
                        {
                            occupied.Add(new BoardPos(piece.Pos.X + x, piece.Pos.Y + y));
                        }
                    }
                }
            }

            // 上一回合敌棋移除之后可能空出新的眼。只从保留的玩家占格计算保护点，
            // 不沿用上一回合缓存的 EyeSide，避免新敌人出生在玩家已围好的眼里。
            var reserved = new HashSet<BoardPos>(occupied);
            for (int y = 0; y < Consts.BoardSize; y++)
            {
                for (int x = 0; x < Consts.BoardSize; x++)
                {
                    var pos = new BoardPos(x, y);
                    if (occupied.Contains(pos))
                    {
                        continue;
                    }

                    bool eye = true;
                    foreach (BoardPos next in pos.GetOrthogonalNeighbors())
                    {
                        if (next.IsInside(Consts.BoardSize) && !occupied.Contains(next))
                        {
                            eye = false;
                            break;
                        }
                    }

                    if (eye)
                    {
                        reserved.Add(pos);
                    }
                }
            }

            if (def.IsBoss)
            {
                int[] sizes = def.Chapter <= 2 ? new[]
                {
                    2
                }

                : (def.Chapter <= 4 ? new[]
                {
                    2,
                    2
                }

                : (def.Chapter == 5 ? new[]
                {
                    3,
                    2,
                    2
                }

                : new[]
                {
                    3,
                    3
                }

                ));
                for (int i = 0; i < sizes.Length; i++)
                {
                    float hpScale = (1f + (def.Chapter - 1) * 0.25f) * (def.Chapter == 6 ? 2f : 1f);
                    var boss = new Piece(_nextPieceId + plan.Count, PieceCatalog.Get(PieceKind.HeavySword, StarTier.Three), GameSide.Enemy, sizes[i], true, i == 0, hpScale);
                    if (!ReserveEnemy(boss, reserved, plan))
                    {
                        return false;
                    }
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

                var piece = new Piece(_nextPieceId + plan.Count, pieceDef, GameSide.Enemy);
                if (!ReserveEnemy(piece, reserved, plan))
                {
                    return false;
                }
            }

            return true;
        }

        private bool ReserveEnemy(Piece piece, HashSet<BoardPos> reserved, List<Piece> plan)
        {
            for (int y = Consts.BoardSize - piece.Size; y >= 0; y--)
            {
                for (int x = Consts.BoardSize - piece.Size; x >= 0; x--)
                {
                    bool free = true;
                    for (int dy = 0; dy < piece.Size && free; dy++)
                    {
                        for (int dx = 0; dx < piece.Size; dx++)
                        {
                            if (reserved.Contains(new BoardPos(x + dx, y + dy)))
                            {
                                free = false;
                                break;
                            }
                        }
                    }

                    if (!free)
                    {
                        continue;
                    }

                    piece.Pos = new BoardPos(x, y);
                    for (int dy = 0; dy < piece.Size; dy++)
                    {
                        for (int dx = 0; dx < piece.Size; dx++)
                        {
                            reserved.Add(new BoardPos(x + dx, y + dy));
                        }
                    }

                    plan.Add(piece);
                    return true;
                }
            }

            return Reject("棋盘没有 " + piece.Size + "×" + piece.Size + " 的敌方出生空间；本回合保持不变，可返回主界面重新布阵");
        }
    }
}
