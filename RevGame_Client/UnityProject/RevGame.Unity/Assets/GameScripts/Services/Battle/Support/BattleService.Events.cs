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
        /// <summary>保留既有 HUD/GM 契约：这里仅描述正常节奏，绝不保证通关。</summary>
        public string VictorySupportStatus => "真实胜负 · 不限时 · 区域波动与能量炸弹";
        /// <summary>兼容旧构造参数：只决定自动事件轮换方式，不再提供免死或清敌。</summary>
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
                case BattleEventKind.Attack:
                case BattleEventKind.Defense:
                case BattleEventKind.Haste:
                case BattleEventKind.Vitality:
                    ApplyCombatBuff(kind, eventDef.Amount * Balance.EventEffectMultiplier);
                    break;
                case BattleEventKind.Reinforcement:
                    applied = false;
                    for (int n = 0; n < (int)Balance.EventEffectMultiplier; n++) { applied |= ReinforceFromHand(); }
                    break;
                case BattleEventKind.Liberty:
                    _roundLibertyBonus += (int)(eventDef.Amount * Balance.EventEffectMultiplier);
                    break;
                case BattleEventKind.Heal:
                    HealTeam(System.Math.Min(1f, eventDef.Amount * Balance.EventEffectMultiplier));
                    break;
                case BattleEventKind.Combo:
                    _roundSpeedBonus = System.Math.Min(eventDef.Cap * Balance.EventEffectMultiplier, _roundSpeedBonus + eventDef.Amount * Balance.EventEffectMultiplier);
                    break;
                case BattleEventKind.Eye:
                    applied = false;
                    for (int n = 0; n < (int)Balance.EventEffectMultiplier; n++) { applied |= CompleteOneEye(); }
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
            _roundLibertyBonus += (int)(BattleEventCatalog.DefOf(BattleEventKind.Teleport).Amount * Balance.EventEffectMultiplier);
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
            float hpRatio = BattleEventCatalog.DefOf(BattleEventKind.Artillery).Amount * Balance.EventEffectMultiplier;
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
                    DamagePiece(p, System.Math.Max(Balance.SupportDamage, (int)(p.MaxHp * hpRatio)), fromSkill: true);
                }
            }

            return true;
        }

        /// <summary>普通攻击、炮火、区域波动、炸弹与Boss技能共用真实扣血出口，保证飘字与实际伤害一致。</summary>
        /// <param name="fromSkill">true = Boss 技能伤害：表现层只飘数字，不追加普通弹道。</param>
        private int DamagePiece(Piece target, int amount, Piece attacker = null, bool fromSkill = false)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            // 仅开发验收：GM 可让我方全队免伤，专门观察 Boss 技能；
            // 条件编译保证正式包既没有这个字段，也不会走这条分支。
            if (PlayerInvincible && target.Side == GameSide.Player)
            {
                amount = 0;
            }
#endif
            if (target.Side == GameSide.Player)
            {
                // 护盾消费属于这颗棋，只有真实受击才扣次数；耗尽后可正常阵亡。
                if (amount > 0 && target.ShieldCharges > 0)
                {
                    target.ShieldCharges--; amount = 0;
                }
                else
                {
                    // 用double做中间值：float会把int.MaxValue向上舍入，Mono转回int可能溢出成负数。
                    // 大额GM/回归伤害也必须是正常伤害，不能因为数值转换变成0而留下“假免死”棋子。
                    amount = (int)System.Math.Max(0d, System.Math.Min(int.MaxValue, amount * (1d - _prepDamageReduction)));
                }
            }
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            // 测试防御位于统一伤害出口，Boss技能也生效；不开夹具时不改变原规则，护盾挡住的0伤害保持0。
            amount = ScaleAutomationIncomingDamage(target, amount);
#endif
            // 防御用等效生命倍率折算：+100%防御=同样伤害减半；仍可正常死亡，不设最后一子保命。
            if (target.Side == GameSide.Player && amount > 0)
            {
                amount = System.Math.Max(1, (int)(amount / (1d + _combatDefenseBonus)));
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
                // 保存攻击者Id供表现公平排队；只传值，不让延迟弹道持有已死亡或换位的Piece对象。
                int sourceId = attacker == null ? 0 : attacker.Id;
                var hit = new BattleHitInfo(target.Id, target.Pos, target.Size, target.Side, real, attacker != null, attacker == null ? target.Pos : attacker.Pos, sourceId, fromSkill, attacker == null ? PieceKind.Swordman : attacker.Def.Kind, attacker == null ? 1 : attacker.Size, attacker != null && attacker.Def.IsRanged);
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

    }
}
