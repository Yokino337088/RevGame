// ============================================================
// BattleService.Events.cs —— 战斗事件与眼位石的具体效果。
// 自动事件和 GM 共用同一条受控入口；事件有间隔和次数上限，不能在同一秒无限叠增益。
// 对没有合法目标的事件明确说明替代效果，避免出现「日志说生效，棋盘什么也没变」。
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
        private bool _eventScheduled;
        private int _battleEventCount;
        private float _nextEventAt;
        private bool _decisiveSupportUsed;
        // 支援是同一回合的状态，不是独立计时器；换轮统一清零，不会有上一轮的迟到炮火。
        /// <summary>0=正常交战，1=僵持集火，2=决胜收尾；只向后推进，下一回合重新从零开始。</summary>
        private int _supportStage;
        /// <summary>最近一次敌人真正掉血的战斗时间；用它判断久攻无进展，不靠是否播过攻击动画。</summary>
        private float _lastEnemyDamageAt;
        /// <summary>下一次支援扣血的战斗时间，随固定模拟推进，不另开跨回合计时器。</summary>
        private float _nextSupportPulseAt;
        /// <summary>最后一子守护是否已经提示过，避免每次零伤害都刷同一条日志。</summary>
        private bool _lastStandShown;
        /// <summary>供 HUD/GM 读取的保障阶段说明；界面不能反过来修改支援阶段。</summary>
        public string VictorySupportStatus
        {
            get
            {
                if (_supportStage >= 2) { return "决胜收尾 · 逐个击破"; }
                if (_supportStage == 1) { return "僵持支援 · 集火破阵"; }
                return "守阵交战 · " + Consts.VictoryAssistFinishSeconds + "秒最后兜底";
            }
        }
        /// <summary>默认轻松模式含最后一子守护与决胜支援；界面会公开说明。</summary>
        public bool VictoryAssistEnabled { get; }
        public int BattleEventCount => _battleEventCount;
        private float _roundSpeedBonus;
        private int _roundLibertyBonus;
        private float _eyeHealElapsed;
        private void RefreshGoState()
        {
            foreach (Piece p in _board.GetAllPieces())
            {
                p.BonusLiberties = p.Side == GameSide.Player ? _libertyBonus + _roundLibertyBonus : 0;
                if (IsBossRound && Chapter == 3 && p.Side == GameSide.Player)
                {
                    p.BonusLiberties--;
                }
            }

            GoRules.Evaluate(_board);
            RevEvent.DispatchEvent(Events.GoStateRefreshed);
        }

        public bool SelectEyeStone()
        {
            if (Phase != GamePhase.Prepare || EyeStones <= 0)
            {
                return Reject("准备阶段持有眼位石才能使用");
            }

            SelectedPieceId = 0;
            EyeStoneSelected = !EyeStoneSelected;
            SelectedCardId = 0;
            LastError = string.Empty;
            RevEvent.DispatchEvent(Events.SelectionChanged);
            return true;
        }

        /// <summary>候选落点必须恰好补齐邻近空点的最后一个邻居；空点本身必须留空才叫眼。</summary>
        private bool CanCompleteEye(BoardPos placement)
        {
            if (!_board.CanFit(placement, 1, GameSide.Player))
            {
                return false;
            }

            foreach (BoardPos center in placement.GetOrthogonalNeighbors())
            {
                if (!_board.CanPlace(center))
                {
                    continue;
                }

                bool valid = true;
                int count = 0;
                foreach (BoardPos neighbor in center.GetOrthogonalNeighbors())
                {
                    Cell cell = _board.GetCell(neighbor);
                    if (cell == null)
                    {
                        continue;
                    }

                    count++;
                    if (neighbor == placement)
                    {
                        continue;
                    }

                    if (cell.IsEmpty || cell.Occupant.Side != GameSide.Player)
                    {
                        valid = false;
                        break;
                    }
                }

                if (valid && count >= 2)
                {
                    return true;
                }
            }

            return false;
        }

        public bool TryPlaceEyeStone(BoardPos pos)
        {
            if (Phase != GamePhase.Prepare || !EyeStoneSelected || EyeStones <= 0)
            {
                return Reject("请先选中一颗眼位石");
            }

            if (!CanCompleteEye(pos))
            {
                return Reject("这个位置不能补成眼，请点金色高亮格；没有亮格时可以先保留眼位石");
            }

            SpawnSupport(pos);
            EyeStones--;
            EyeStoneSelected = false;
            LastError = string.Empty;
            RefreshGoState();
            RevEvent.DispatchEvent(Events.HandChanged);
            RevEvent.DispatchEvent(Events.SelectionChanged);
            return true;
        }

        public bool TriggerBattleEvent(BattleEventKind kind)
        {
            if (Phase != GamePhase.Battle)
            {
                return Reject("战斗事件只能在战斗阶段触发");
            }

            if (_battleEventCount >= Consts.MaxBattleEventsPerRound)
            {
                return Reject("本回合支援次数已达上限");
            }

            if (_battleEventCount > 0 && _battleElapsed < _nextEventAt)
            {
                return Reject("支援尚未冷却，请等待下一次战斗事件");
            }

            if (!System.Enum.IsDefined(typeof(BattleEventKind), kind))
            {
                return Reject("未知战斗事件");
            }

            _battleEventCount++;
            _nextEventAt = _battleElapsed + Consts.BattleEventInterval;
            bool applied = true;
            switch (kind)
            {
                case BattleEventKind.Reinforcement:
                    applied = ReinforceFromHand();
                    break;
                case BattleEventKind.Liberty:
                    _roundLibertyBonus++;
                    break;
                case BattleEventKind.Heal:
                    HealTeam(Consts.HealRatio);
                    break;
                case BattleEventKind.Combo:
                    _roundSpeedBonus = System.Math.Min(0.5f, _roundSpeedBonus + Consts.ComboSpeedBonus);
                    break;
                case BattleEventKind.Eye:
                    applied = CompleteOneEye();
                    break;
                case BattleEventKind.BreakChain:
                    applied = BreakEnemyChain();
                    break;
                case BattleEventKind.Teleport:
                    applied = TeleportToMoreLiberties();
                    break;
                case BattleEventKind.Artillery:
                    applied = FireSupport();
                    break;
            }

            if (!applied)
            {
                HealTeam(Consts.HealRatio);
            }

            RefreshGoState();
            string title = BattleEventCatalog.NameOf(kind);
            LastError = string.Empty;
            RevEvent.DispatchEvent<string>(Events.BattleEventTriggered, title);
            RevEvent.DispatchEvent<string>(Events.BattleLog, title + (applied ? "：已生效" : "：没有合法目标，转为全队回复 10% 生命"));
            return true;
        }

        private void TryAutomaticEvent()
        {
            if (!_eventScheduled || _battleEventCount >= Consts.MaxBattleEventsPerRound || _battleElapsed < _nextEventAt)
            {
                return;
            }

            if (VictoryAssistEnabled)
            {
                // 顺序轮换，保证援军、治疗和炮火都会出现，避免连续随机到没有目标的事件。
                BattleEventKind[] support = { BattleEventKind.Reinforcement, BattleEventKind.Liberty, BattleEventKind.Heal, BattleEventKind.Eye, BattleEventKind.Combo, BattleEventKind.Artillery, BattleEventKind.Heal, BattleEventKind.BreakChain };
                TriggerBattleEvent(support[_battleEventCount % support.Length]);
                return;
            }

            int first = _random.NextDouble() < Consts.RareEventChance ? 4 : 0;
            TriggerBattleEvent((BattleEventKind)(first + _random.Next(4)));
        }

        private void HealTeam(float ratio)
        {
            foreach (Piece piece in _playerPieces)
            {
                if (piece.Heal(System.Math.Max(1, (int)(piece.MaxHp * ratio))) > 0)
                {
                    RevEvent.DispatchEvent<int>(Events.PieceHpChanged, piece.Id);
                }
            }
        }

        private void SpawnSupport(BoardPos pos, PieceKind kind = PieceKind.Shield)
        {
            var piece = new Piece(_nextPieceId++, PieceCatalog.Get(kind, StarTier.One), GameSide.Player);
            if (!_board.Place(piece, pos))
            {
                throw new System.InvalidOperationException("已预检的援兵落点不可用：" + pos);
            }

            _playerPieces.Add(piece);
            RevEvent.DispatchEvent<int>(Events.PiecePlaced, piece.Id);
        }

        private bool CompleteOneEye()
        {
            for (int y = 0; y < Consts.BoardSize; y++)
            {
                for (int x = 0; x < Consts.BoardSize; x++)
                {
                    var pos = new BoardPos(x, y);
                    if (CanCompleteEye(pos))
                    {
                        SpawnSupport(pos);
                        return true;
                    }
                }
            }

            return false;
        }

        private int FreeNeighbors(BoardPos pos)
        {
            int n = 0;
            foreach (BoardPos next in pos.GetOrthogonalNeighbors())
            {
                if (_board.CanPlace(next))
                {
                    n++;
                }
            }

            return n;
        }

        /// <summary>自动援军不能填进现有眼位；手动落子可主动拆眼，但自动增益必须保护阵型。</summary>
        private bool CanSupportFit(BoardPos pos, int size)
        {
            if (!_board.CanFit(pos, size, GameSide.Player)) { return false; }
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    if (_board.GetCell(new BoardPos(pos.X + x, pos.Y + y)).IsEyeMarked) { return false; }
                }
            }
            return true;
        }

        private bool ReinforceFromHand()
        {
            if (Hand.Count == 0)
            {
                if (VictoryAssistEnabled)
                {
                    // 六张牌都落完仍能获得援军；自动支援不占玩家的六次落子额度。
                    for (int y = 0; y < Consts.BoardSize / 2; y++)
                    {
                        for (int x = 0; x < Consts.BoardSize; x++)
                        {
                            var pos = new BoardPos(x, y);
                            if (CanSupportFit(pos, 1))
                            {
                                // 全部六牌已落完也有援军。己方远程全灭时补弓箭，而不是再加一个够不着敌人的盾战。
                                bool hasRanged = _playerPieces.Exists(p => p.IsAlive && p.Def.IsRanged);
                                SpawnSupport(pos, hasRanged ? PieceKind.Shield : PieceKind.Archer);
                                return true;
                            }
                        }
                    }
                }

                return false;
            }

            HandCard card = Hand[0];
            int size = card.Star == StarTier.Three ? 2 : 1;
            BoardPos best = new BoardPos(-1, -1);
            int score = -1;
            for (int y = 0; y < Consts.BoardSize; y++)
            {
                for (int x = 0; x < Consts.BoardSize; x++)
                {
                    var pos = new BoardPos(x, y);
                    if (!CanSupportFit(pos, size))
                    {
                        continue;
                    }

                    int candidate = FreeNeighbors(pos);
                    if (candidate > score)
                    {
                        best = pos;
                        score = candidate;
                    }
                }
            }

            if (score < 0)
            {
                return false;
            }

            var piece = new Piece(_nextPieceId++, PieceCatalog.Get(card.Kind, card.Star), GameSide.Player);
            _board.Place(piece, best);
            _playerPieces.Add(piece);
            _hand.Remove(card.Id);
            RevEvent.DispatchEvent<int>(Events.PiecePlaced, piece.Id);
            RevEvent.DispatchEvent(Events.HandChanged);
            TryMergePieces();
            return true;
        }

        /// <summary>保留旧事件入口，但用回气替代自动传送；玩家布阵不被任何增益事件搬走。</summary>
        private bool TeleportToMoreLiberties()
        {
            _roundLibertyBonus++;
            RefreshGoState();
            return true;
        }

        private bool BreakEnemyChain()
        {
            List<Piece> largest = null;
            foreach (var group in GoRules.BuildGroups(_board))
            {
                if (group[0].Side == GameSide.Enemy && (largest == null || group.Count > largest.Count))
                {
                    largest = group;
                }
            }

            if (largest == null || largest.Count < 3)
            {
                return false;
            }

            foreach (Piece cut in largest)
            {
                if (cut.IsKing)
                {
                    continue;
                }

                // 只在临时图上忽略候选连接点，不移动真实棋盘试算，事件观察者不会看到中间状态。
                var visited = new HashSet<Piece>
                {
                    cut
                };
                var queue = new Queue<Piece>();
                Piece start = largest.Find(p => p != cut);
                visited.Add(start);
                queue.Enqueue(start);
                while (queue.Count > 0)
                {
                    foreach (BoardPos next in BoardModel.Boundary(queue.Dequeue()))
                    {
                        Piece p = _board.GetPiece(next);
                        if (p != null && p.Side == GameSide.Enemy && visited.Add(p))
                        {
                            queue.Enqueue(p);
                        }
                    }
                }

                if (visited.Count < largest.Count)
                {
                    DamagePiece(cut, cut.Hp);
                    return true;
                }
            }

            return false;
        }

        private bool FireSupport()
        {
            var enemies = _board.GetPiecesOf(GameSide.Enemy);
            if (enemies.Count == 0)
            {
                return false;
            }

            Piece center = enemies[0];
            int density = -1;
            foreach (Piece candidate in enemies)
            {
                int n = enemies.FindAll(p => System.Math.Abs(p.Pos.X - candidate.Pos.X) <= 1 && System.Math.Abs(p.Pos.Y - candidate.Pos.Y) <= 1).Count;
                if (n > density)
                {
                    center = candidate;
                    density = n;
                }
            }

            foreach (Piece p in enemies)
            {
                if (System.Math.Abs(p.Pos.X - center.Pos.X) <= 1 && System.Math.Abs(p.Pos.Y - center.Pos.Y) <= 1)
                {
                    DamagePiece(p, System.Math.Max(Consts.SupportDamage, (int)(p.MaxHp * Consts.ArtilleryHpRatio)));
                }
            }

            return true;
        }

        /// <summary>普通攻击、炮火、决胜支援共用真实扣血出口，保证飘字与实际伤害一致。</summary>
        private int DamagePiece(Piece target, int amount, Piece attacker = null)
        {
            if (VictoryAssistEnabled && target.Side == GameSide.Player && _playerPieces.FindAll(p => p.IsAlive).Count == 1)
            {
                // ★ 只保护最后一颗我方棋子至少留 1 血，前面的阵亡与合成仍正常发生。
                // 配合后段渐进支援保证真实取胜。只保护最后一子，其他阵亡与阵型破坏仍正常发生。
                amount = System.Math.Min(amount, System.Math.Max(0, target.Hp - 1));
                if (amount == 0 && !_lastStandShown)
                {
                    _lastStandShown = true;
                    RevEvent.DispatchEvent<string>(Events.BattleLog, "最后一子守护：阵线危急，援军将逐步接管火力");
                }
            }

            int real = target.TakeDamage(amount);
            if (real > 0)
            {
                if (target.Side == GameSide.Enemy) { _lastEnemyDamageAt = _battleElapsed; }
                var hit = new BattleHitInfo(target.Id, target.Pos, target.Size, target.Side, real, attacker != null, attacker == null ? target.Pos : attacker.Pos);
                RevEvent.DispatchEvent<BattleHitInfo>(Events.BattleHit, hit);
            }
            RevEvent.DispatchEvent<int>(Events.DamageResolved, target.Id);
            RevEvent.DispatchEvent<int>(Events.PieceHpChanged, target.Id);
            if (!target.IsAlive)
            {
                _board.Remove(target);
                RevEvent.DispatchEvent<int>(Events.PieceDied, target.Id);
            }

            return real;
        }

        /// <summary>每个模拟小步检查一次：先让正常交战展开，僵持时集火，长局最后逐个击破。</summary>
        private void ApplyVictorySupport()
        {
            if (!VictoryAssistEnabled || _board.IsSideWiped(GameSide.Enemy))
            {
                return;
            }
            // 第一段只在久攻无进展或己方已经危急时介入。正常交战即使超过旧的45秒也可以继续。
            bool stalled = _battleElapsed - _lastEnemyDamageAt >= Consts.VictoryAssistStallSeconds;
            bool critical = _lastStandShown || _playerPieces.FindAll(p => p.IsAlive).TrueForAll(p => p.HpRatio < 0.3f);
            if (_supportStage == 0 && _battleElapsed >= Consts.VictoryAssistStartSeconds && (stalled || critical))
            {
                BeginSupportStage(1, "僵持支援：每三秒集中火力压制一名敌人，正常攻防继续");
            }
            // 双方守眼、近战隔盘和 Boss 都必须能结束；后段改成逐个击破，让玩家看见破阵过程。
            if (_supportStage < 2 && _battleElapsed >= Consts.VictoryAssistFinalSeconds)
            {
                BeginSupportStage(2, "决胜收尾：援军逐个击破残部，保留最后通关保障");
            }
            if (_supportStage == 0 || _battleElapsed < _nextSupportPulseAt) { return; }
            _nextSupportPulseAt += _supportStage == 2 ? 1f : Consts.VictoryAssistPulseSeconds;
            Piece target = _enemyPieces.Find(p => p.IsAlive && p.IsKing);
            if (target == null) { target = _enemyPieces.Find(p => p.IsAlive && p.Def.IsRanged); }
            if (target == null) { target = _enemyPieces.Find(p => p.IsAlive); }
            if (target == null) { return; }
            _decisiveSupportUsed = true;
            int damage = _supportStage == 2 ? target.Hp : System.Math.Max(Consts.SupportDamage, (int)(target.MaxHp * Consts.VictoryAssistPulseHpRatio));
            DamagePiece(target, damage);
            // 非标准开发夹具也有绝对上限；正常6~10名普通敌棋已在前段逐个击破，不会默认整盘秒杀。
            if (_battleElapsed >= Consts.VictoryAssistFinishSeconds)
            {
                foreach (Piece enemy in new List<Piece>(_enemyPieces))
                {
                    if (enemy.IsAlive) { DamagePiece(enemy, enemy.Hp); }
                }
            }
            RefreshGoState();
        }

        /// <summary>切换阶段并通知现有池化提示；推迟普通事件，避免两条大标题同时争抢阅读时间。</summary>
        private void BeginSupportStage(int stage, string description)
        {
            _supportStage = stage;
            _nextSupportPulseAt = _battleElapsed;
            // 沿用 Excel 的决胜支援表现配置，阶段细节由持续可读的 HUD 与战斗日志说明。
            RevEvent.DispatchEvent<string>(Events.BattleEventTriggered, "决胜支援");
            RevEvent.DispatchEvent<string>(Events.BattleLog, description);
            _nextEventAt = System.Math.Max(_nextEventAt, _battleElapsed + 4f);
        }
    }
}
