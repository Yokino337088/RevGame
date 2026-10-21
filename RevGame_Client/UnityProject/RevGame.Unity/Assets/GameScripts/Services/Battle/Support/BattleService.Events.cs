// ============================================================
// BattleService.Events.cs —— 战斗事件与眼位石的具体效果。
// 本文件是 BattleService 的 partial 片段；事件同样走统一伤害出口，不另算一套血。
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
    // partial 片段：与其他 BattleService.*.cs 共享同一实例，完整类说明见 Simulation/BattleService.cs。
    public sealed partial class BattleService
    {
        /// <summary>本回合是否允许自动战斗事件；关闭辅助且未抽中概率时为 false。</summary>
        private bool _eventScheduled;
        /// <summary>本回合已触发的事件次数；同时充当轮换下标和上限判断依据。</summary>
        private int _battleEventCount;
        /// <summary>下一次事件最早发生的战斗时刻；防止同一秒连发多条大提示。</summary>
        private float _nextEventAt;
        /// <summary>决胜支援是否已使用；只做诊断统计，不参与伤害计算。</summary>
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
                if (_supportStage >= 2)
                {
                    return "决胜收尾 · 逐个击破";
                }
                if (_supportStage == 1)
                {
                    return "僵持支援 · 集火破阵";
                }
                return "守阵交战 · " + Balance.VictoryAssistFinishSeconds + "秒最后兜底";
            }
        }
        /// <summary>默认轻松模式含最后一子守护与决胜支援；界面会公开说明。</summary>
        public bool VictoryAssistEnabled { get; }
        /// <summary>
        /// 本轮已触发的战斗事件次数；同时用于上限判断与轮换下标。
        /// </summary>
        public int BattleEventCount => _battleEventCount;
        /// <summary>
        /// 本轮攻击提速加成（疾风连击等事件）；换轮清零，不影响移动冷却。
        /// </summary>
        private float _roundSpeedBonus;
        /// <summary>
        /// 本轮额外气数（气脉畅通等）；会改变围困判定，修改后必须刷新气眼。
        /// </summary>
        private int _roundLibertyBonus;
        /// <summary>
        /// 守眼回血的累计时钟：攒够一个间隔才回一次，避免每小步零散回血。
        /// </summary>
        private float _eyeHealElapsed;
        /// <summary>上次已发布的棋盘占格版本；-1保证第一次一定计算。</summary>
        private int _evaluatedTopology = -1;
        /// <summary>气数加成同样会改变围困判定，不能只检查棋子是否移动。</summary>
        private int _evaluatedLibertyBonus = int.MinValue;
        /// <summary>免疫切换不改真实气数，但会改变锁死标识，因此也需要通知视图。</summary>
        private bool _evaluatedImmunity;
        /// <summary>
        /// 规则刷新入口。占格/加气/免疫都没变时立即返回：掉血事件已更新血条，没必要重画81格。
        /// 必须在真正落子、移位、死亡之后调用；BoardModel的版本号保证这些变化不会漏算。
        /// </summary>
        private void RefreshGoState()
        {
            int bonus = _libertyBonus + _roundLibertyBonus - (IsBossRound && Chapter == 3 ? 1 : 0);
            if (_evaluatedTopology == _board.TopologyVersion && _evaluatedLibertyBonus == bonus && _evaluatedImmunity == _prepImmune)
            {
                return;
            }
            // 先写缓存再广播，防止界面同步回调重入时重复发布相同结果。
            _evaluatedTopology = _board.TopologyVersion;
            _evaluatedLibertyBonus = bonus;
            _evaluatedImmunity = _prepImmune;
            foreach (Piece p in _board.Pieces)
            {
                p.BonusLiberties = p.Side == GameSide.Player ? bonus : 0;
            }

            GoRules.Evaluate(_board);
            RevEvent.DispatchEvent(Events.GoStateRefreshed);
        }

        /// <summary>
        /// 切换“补眼模式”：仅准备阶段且持有眼位石时可用。
        /// 切换时清掉手牌与棋子选择，保证一次点击只会走一条操作路径。
        /// </summary>
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

        /// <summary>
        /// 在合法空格补一颗支援棋并扣一枚眼位石。
        /// 不合法（不能补成眼）时拒绝且不扣石头；没有亮格时可以保留到以后用。
        /// </summary>
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

        /// <summary>
        /// 受控触发一次战斗事件（自动事件与 GM 共用）。
        /// 先校验阶段、每回合上限与冷却，再执行效果；没有合法目标时转全队回复，
        /// 避免出现“日志说生效、棋盘什么也没变”。
        /// </summary>
        public bool TriggerBattleEvent(BattleEventKind kind)
        {
            if (Phase != GamePhase.Battle)
            {
                return Reject("战斗事件只能在战斗阶段触发");
            }

            if (_battleEventCount >= Balance.MaxBattleEventsPerRound)
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
            _nextEventAt = _battleElapsed + Balance.BattleEventInterval;
            bool applied = true;
            // 每个事件的效果量来自 BattleEventConfig（Amount/Cap），不在代码里写死。
            BattleEventDef eventDef = BattleEventCatalog.DefOf(kind);
            switch (kind)
            {
                case BattleEventKind.Reinforcement:
                    applied = ReinforceFromHand();
                    break;
                case BattleEventKind.Liberty:
                    _roundLibertyBonus += (int)eventDef.Amount;
                    break;
                case BattleEventKind.Heal:
                    HealTeam(eventDef.Amount);
                    break;
                case BattleEventKind.Combo:
                    _roundSpeedBonus = System.Math.Min(eventDef.Cap, _roundSpeedBonus + eventDef.Amount);
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
                HealTeam(Balance.EventFallbackHealRatio);
            }

            RefreshGoState();
            string title = BattleEventCatalog.NameOf(kind);
            LastError = string.Empty;
            RevEvent.DispatchEvent<string>(Events.BattleEventTriggered, title);
            // 回退回复的比例写进日志时也取自参数，不手写“10%”。
            RevEvent.DispatchEvent<string>(Events.BattleLog, title + (applied ? "：已生效" : "：没有合法目标，转为全队回复 " + ConfigText.Percent(Balance.EventFallbackHealRatio) + " 生命"));
            return true;
        }

        /// <summary>
        /// 自动事件的调度：开启辅助时按配置顺序轮换，关闭时按概率随机抽组。
        /// 只在冷却与上限都满足时触发，不在每个模拟小步里重复尝试。
        /// </summary>
        private void TryAutomaticEvent()
        {
            if (!_eventScheduled || _battleEventCount >= Balance.MaxBattleEventsPerRound || _battleElapsed < _nextEventAt)
            {
                return;
            }

            if (VictoryAssistEnabled)
            {
                // 顺序轮换，保证援军、治疗和炮火都会出现，避免连续随机到没有目标的事件。
                // 轮换顺序来自 EventRotationConfig（同一事件出现几次，就是它的权重）。
                IReadOnlyList<BattleEventKind> support = BattleEventCatalog.Rotation;
                TriggerBattleEvent(support[_battleEventCount % support.Count]);
                return;
            }

            // 先按稀有概率决定抽哪一组，再在组内等概率随机；分组来自 BattleEventConfig.IsRare。
            IReadOnlyList<BattleEventKind> pool = BattleEventCatalog.Pool(_random.NextDouble() < Balance.RareEventChance);
            TriggerBattleEvent(pool[_random.Next(pool.Count)]);
        }

        /// <summary>
        /// 全队按最大生命比例回复，并逐颗广播血条变化。
        /// 实际回复量为 0 的棋子不发事件，避免无意义的界面刷新。
        /// </summary>
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

        /// <summary>
        /// 生成我方支援棋：占格后加入名单并继承本轮准备增益。
        /// 调用前必须已经预检过空格，失败直接抛错而不是悄悄少一颗援军。
        /// </summary>
        private void SpawnSupport(BoardPos pos, PieceKind kind = PieceKind.Shield)
        {
            var piece = new Piece(_nextPieceId++, PieceCatalog.Get(kind, StarTier.One), GameSide.Player);
            if (!_board.Place(piece, pos))
            {
                throw new System.InvalidOperationException("已预检的援兵落点不可用：" + pos);
            }

            _playerPieces.Add(piece);
            ApplyPreparationToNewPiece(piece);
            RevEvent.DispatchEvent<int>(Events.PiecePlaced, piece.Id);
        }

        /// <summary>
        /// 扫描全盘，找到第一个可以补成眼的位置并生成支援棋。
        /// 找不到返回 false，由调用方转为回复兜底，不能强行塞进不合法位置。
        /// </summary>
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

        /// <summary>
        /// 统计四邻空格数量，用于选点/评估出入口；不代表该点可以落子。
        /// </summary>
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

        /// <summary>自动援军不能填己方眼；远程也不闯敌眼，近战试放可开放敌眼，但仍须通过气眼与攻击预检。</summary>
        private bool CanSupportFit(BoardPos pos, int size, bool allowEnemyEyes = false)
        {
            if (!_board.CanFit(pos, size, GameSide.Player, respectEnemyEyes: !allowEnemyEyes))
            {
                return false;
            }
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    Cell cell = _board.GetCell(new BoardPos(pos.X + x, pos.Y + y));
                    if (cell.IsEyeMarked && (!allowEnemyEyes || cell.EyeSide == GameSide.Player))
                    {
                        return false;
                    }
                }
            }
            return true;
        }

        /// <summary>保留旧事件入口，但用回气替代自动传送；玩家布阵不被任何增益事件搬走。</summary>
        private bool TeleportToMoreLiberties()
        {
            _roundLibertyBonus += (int)BattleEventCatalog.DefOf(BattleEventKind.Teleport).Amount;
            RefreshGoState();
            return true;
        }

        /// <summary>
        /// 切断敌方最大连通块：在临时图里试删一颗，确认会分成两块才算断点。
        /// 只在真实存在割点时生效；不能为了“有伤害”随便击杀一颗普通棋。
        /// </summary>
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

            // 连块至少多大才能断链（BattleEventConfig[BreakChain].Amount）。
            if (largest == null || largest.Count < (int)BattleEventCatalog.DefOf(BattleEventKind.BreakChain).Amount)
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

        /// <summary>
        /// 炮火支援：以敌人最密集的位置为中心，对范围内敌人造成比例伤害。
        /// 半径与比例都来自配置表；没有敌人时返回 false，由调用方转为回复。
        /// </summary>
        private bool FireSupport()
        {
            var enemies = _board.GetPiecesOf(GameSide.Enemy);
            if (enemies.Count == 0)
            {
                return false;
            }

            // 炮击范围半径与伤害比例都来自参数/事件表：伤害 = max(固定下限, 目标最大生命 × 比例)。
            int radius = Balance.EventArtilleryRadius;
            float hpRatio = BattleEventCatalog.DefOf(BattleEventKind.Artillery).Amount;
            Piece center = enemies[0];
            int density = -1;
            foreach (Piece candidate in enemies)
            {
                int n = enemies.FindAll(p => System.Math.Abs(p.Pos.X - candidate.Pos.X) <= radius && System.Math.Abs(p.Pos.Y - candidate.Pos.Y) <= radius).Count;
                if (n > density)
                {
                    center = candidate;
                    density = n;
                }
            }

            foreach (Piece p in enemies)
            {
                if (System.Math.Abs(p.Pos.X - center.Pos.X) <= radius && System.Math.Abs(p.Pos.Y - center.Pos.Y) <= radius)
                {
                    DamagePiece(p, System.Math.Max(Balance.SupportDamage, (int)(p.MaxHp * hpRatio)));
                }
            }

            return true;
        }

        /// <summary>普通攻击、炮火、决胜支援、Boss 技能共用真实扣血出口，保证飘字与实际伤害一致。</summary>
        /// <param name="fromSkill">true = Boss 技能伤害：表现层只飘数字，不追加普通弹道。</param>
        private int DamagePiece(Piece target, int amount, Piece attacker = null, bool fromSkill = false)
        {
            if (target.Side == GameSide.Player)
            {
                // 护盾消费属于这颗棋，只有真实受击才扣次数；最后一子守护在减伤之后兜底。
                if (amount > 0 && target.ShieldCharges > 0)
                {
                    target.ShieldCharges--; amount = 0;
                }
                else
                {
                    amount = System.Math.Max(0, (int)(amount * (1f - _prepDamageReduction)));
                }
            }
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            // 测试防御位于统一伤害出口，Boss技能也生效；不开夹具时不改变原规则，护盾挡住的0伤害保持0。
            amount = ScaleAutomationIncomingDamage(target, amount);
#endif
            if (VictoryAssistEnabled && target.Side == GameSide.Player && _playerPieces.FindAll(p => p.IsAlive).Count == 1)
            {
                // ★ 只保护最后一颗我方棋子至少留 1 血，其余棋子的受伤与阵亡仍正常发生。
                // 配合后段渐进支援保证真实取胜。只保护最后一子，其他阵亡与阵型破坏仍正常发生。
                amount = System.Math.Min(amount, System.Math.Max(0, target.Hp - 1));
                if (amount == 0 && !_lastStandShown)
                {
                    _lastStandShown = true;
                    RevEvent.DispatchEvent<string>(Events.BattleLog, "最后一子守护：阵线危急，援军将逐步接管火力");
                }
            }

            int real = target.TakeDamage(amount);
            if (real > 0 && target.Side == GameSide.Player && _prepRecoveryRatio > 0f)
            {
                // 复苏储备：首次实际受伤后回复一次，用完清零（比例来自 PrepBuffConfig[Recovery].Extra）。
                float bank = _prepRecoveryRatio;
                _prepRecoveryRatio = 0f;
                HealTeam(bank);
            }
            if (real > 0)
            {
                if (target.Side == GameSide.Enemy)
                {
                    _lastEnemyDamageAt = _battleElapsed;
                }
                // 保存攻击者Id供表现公平排队；只传值，不让延迟弹道持有已死亡或换位的Piece对象。
                int sourceId = attacker == null ? 0 : attacker.Id;
                var hit = new BattleHitInfo(target.Id, target.Pos, target.Size, target.Side, real, attacker != null, attacker == null ? target.Pos : attacker.Pos, sourceId, fromSkill);
                RevEvent.DispatchEvent<BattleHitInfo>(Events.BattleHit, hit);
            }
            RevEvent.DispatchEvent<int>(Events.DamageResolved, target.Id);
            RevEvent.DispatchEvent<int>(Events.PieceHpChanged, target.Id);
            if (!target.IsAlive)
            {
                _board.Remove(target);
                MarkEnemyDead(target);
                // 玩家可能刚选中这颗棋子准备换位；死亡后清选择，不能留下一个指向已移除棋子的编号。
                if (SelectedPieceId == target.Id)
                {
                    SelectedPieceId = 0;
                    RevEvent.DispatchEvent(Events.SelectionChanged);
                }
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
            bool stalled = _battleElapsed - _lastEnemyDamageAt >= Balance.VictoryAssistStallSeconds;
            bool critical = _lastStandShown || _playerPieces.FindAll(p => p.IsAlive).TrueForAll(p => p.HpRatio < Balance.VictoryAssistCriticalHpRatio);
            if (_supportStage == 0 && _battleElapsed >= Balance.VictoryAssistStartSeconds && (stalled || critical))
            {
                // 阶段说明里的间隔秒数取自参数，避免“每三秒”写在文字里、实际间隔在表里改了却对不上。
                BeginSupportStage(1, "僵持支援：每" + ConfigText.Number(Balance.VictoryAssistPulseSeconds) + "秒集中火力压制一名敌人，正常攻防继续");
            }
            // 双方守眼、近战隔盘和 Boss 都必须能结束；后段改成逐个击破，让玩家看见破阵过程。
            if (_supportStage < 2 && _battleElapsed >= Balance.VictoryAssistFinalSeconds)
            {
                BeginSupportStage(2, "决胜收尾：援军逐个击破残部，保留最后通关保障");
            }
            if (_supportStage == 0 || _battleElapsed < _nextSupportPulseAt)
            {
                return;
            }
            _nextSupportPulseAt += _supportStage == 2 ? Balance.VictoryAssistFinalPulseSeconds : Balance.VictoryAssistPulseSeconds;
            Piece target = _enemyPieces.Find(p => p.IsAlive && p.IsKing);
            if (target == null)
            {
                target = _enemyPieces.Find(p => p.IsAlive && p.Def.IsRanged);
            }
            if (target == null)
            {
                target = _enemyPieces.Find(p => p.IsAlive);
            }
            if (target == null)
            {
                return;
            }
            _decisiveSupportUsed = true;
            int damage = _supportStage == 2 ? target.Hp : System.Math.Max(Balance.SupportDamage, (int)(target.MaxHp * Balance.VictoryAssistPulseHpRatio));
            DamagePiece(target, damage);
            // 非标准开发夹具也有绝对上限；常规敌阵先经过前段逐个击破，最后才清理异常残敌。
            if (_battleElapsed >= Balance.VictoryAssistFinishSeconds)
            {
                foreach (Piece enemy in new List<Piece>(_enemyPieces))
                {
                    if (enemy.IsAlive)
                    {
                        DamagePiece(enemy, enemy.Hp);
                    }
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
            _nextEventAt = System.Math.Max(_nextEventAt, _battleElapsed + Balance.EventStageDelaySeconds);
        }
    }
}
