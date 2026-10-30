using System;
using RevGame.Battle;
using RevGame.Core;
using RevGame.Data;
using RevGame.Pieces;
using Revolution;

namespace RevGame.Services
{
    /// <summary>
    /// 帮手是非占格战斗演员，不在双方Piece名单内，所以敌方射程、技能与区域伤害无法选中它。
    /// 它也不计入玩家存活数；玩家全灭仍失败。积分钱包按章继承、换章归零，永久成绩只累计不扣减。
    /// </summary>
    public sealed partial class BattleService
    {
        /// <summary>本章可支付的能量余额，只由真实计分/消费修改，换章或重新挑战清零。</summary>
        public long CombatScore
        {
            get; private set;
        }
        /// <summary>服务拥有的唯一机器，结算/离域/新回合由StopEnemyAI释放，视图不能切状态。</summary>
        private HelperAI _helperAI;
        /// <summary>不占格的位置，格坐标允许外圈-1/9；仅由Crawl动作提交，不影响气眼。</summary>
        private BoardPos _helperPosition = new BoardPos(Consts.BoardSize, 4);
        /// <summary>本步目标与玩家指定目标的Id；不保存跨帧Piece引用，死亡后下一步重新选择。</summary>
        private int _helperTargetId, _helperExplicitTargetId;
        /// <summary>是否在棋盘外支援；拥挤或无合法空点时启用，仍然可以正常攻击。</summary>
        private bool _helperOutside = true;
        /// <summary>全部为战斗模拟秒的绝对时间，随回合重置。激光终点控制持续，nextBeam控制冷却。</summary>
        private float _helperNextMove, _helperNextShot, _helperBeamUntil, _helperNextBeam;
        /// <summary>最近动作的短反馈，视图只在内容改变时刷新台词，避免每帧重建文字网格。</summary>
        private string _helperSpeech = "攻击敌人，为我积攒能量！";
        /// <summary>累计成绩决定档位，可用余额决定能否攻击；两个概念分开避免花一次分就降级。</summary>
        private int HelperTier => (int)Math.Min(Balance.HelperMaxTier, ChapterScore / Balance.HelperScoreStep);
        /// <summary>实际弹幕间隔（模拟秒）；Boss加速只作用于帮手，不额外放大远程共鸣或激光每秒伤害。</summary>
        public float HelperShotInterval => Math.Max(Balance.HelperMinInterval,
            Balance.HelperBaseInterval / (1f + HelperTier * Balance.HelperDamageGrowth))
            / (IsBossRound ? 1f + Balance.HelperBossSpeedBonus : 1f);
        /// <summary>
        /// 帮手每档的攻击成长等比传给远程普攻，实时读当前章成绩。扣能源不降档，换章/重开才清零。
        /// 乘数独立作用于普攻，不能把炸弹/区域伤害也放大；既有阵容火力递减仍生效。
        /// </summary>
        public float HelperRangedAttackBonus => Math.Min(Balance.HelperRangedAttackCap,
            HelperTier * Balance.HelperDamageGrowth * Balance.HelperRangedAttackLink);
        /// <summary>攻速共鸣使用帮手实际速度提升比例，达到最快间隔后不再虚增共鸣。</summary>
        public float HelperRangedSpeedBonus
        {
            get
            {
                float helperInterval = Math.Max(Balance.HelperMinInterval, Balance.HelperBaseInterval / (1f + HelperTier * Balance.HelperDamageGrowth));
                float growth = Balance.HelperBaseInterval / helperInterval - 1f;
                return Math.Min(Balance.HelperRangedSpeedCap, growth * Balance.HelperRangedSpeedLink);
            }
        }
        public HelperSnapshot Helper => new HelperSnapshot(Chapter > 0 && Phase != GamePhase.Settle,
            _helperOutside, _helperPosition, _helperTargetId, HelperTier, _helperAI == null ? "Rest" : _helperAI.State.ToString(), _helperSpeech, HelperStormActive);

        /// <summary>回合是机器的生命周期边界。钱包同章保留，目标与全部绝对冷却重置，避免旧回合时钟污染。</summary>
        private void ResetHelperRound()
        {
            _helperAI?.Dispose();
            _helperAI = new HelperAI(new HelperOwner(this));
            ResetHelperStorm();
            _helperTargetId = _helperExplicitTargetId = 0;
            _helperPosition = new BoardPos(Consts.BoardSize, 4);
            _helperOutside = true;
            _helperNextMove = _helperNextShot = _helperBeamUntil = _helperNextBeam = 0;
            _helperSpeech = "攻击敌人，为我积攒能量！";
        }
        /// <summary>点击敌棋/GM都走同一校验。Boss存活时仍先打Boss，指定目标保留为Boss消失后的下一目标。</summary>
        public bool SetHelperTarget(int id)
        {
            if (Phase != GamePhase.Prepare && Phase != GamePhase.Battle)
            {
                return Reject("结算时帮手不接受攻击指令");
            }
            Piece target = _enemyPieces.Find(p => p.Id == id && p.IsAlive);

            if (id != 0 && (target == null || _board.FindPiece(id) != target))
            {
                return Reject("指定目标必须是棋盘上的存活敌棋");
            }
            _helperExplicitTargetId = id;

            if (id != 0)
            {
                // 指定敌人后清除旧落牌/换位选择，避免下一次点空格误移动之前选中的己方棋。
                SelectedPieceId = SelectedCardId = 0;
                EyeStoneSelected = false;
                RevEvent.DispatchEvent(Events.SelectionChanged);
            }
            _helperSpeech = id == 0 ? "我会优先压制敌人的远程！" : "收到！我来盯住这颗棋子。";
            LastError = string.Empty;
            return true;
        }
        /// <summary>每步一次目标感知，缓存只存Id。延迟表现不引用Piece，死亡或换回合后不会打旧对象。</summary>
        private void TickHelper(float seconds)
        {
            if (_helperAI == null)
            {
                ResetHelperRound();
            }
            Piece best = null;
            int bestPriority = int.MinValue;
            foreach (Piece enemy in _enemyPieces)
            {
                if (!enemy.IsAlive || _board.FindPiece(enemy.Id) != enemy)
                {
                    continue;
                }
                int priority = enemy.IsKing && IsBossRound ? 100000
                    : enemy.Id == _helperExplicitTargetId ? 50000 : enemy.Def.IsRanged ? 10000 : 0;
                priority -= Math.Abs(enemy.Pos.X - _helperPosition.X) + Math.Abs(enemy.Pos.Y - _helperPosition.Y);

                if (priority > bestPriority || (priority == bestPriority && enemy.Id < best.Id))
                {
                    best = enemy;
                    bestPriority = priority;
                }
            }
            _helperTargetId = best == null ? 0 : best.Id;
            UpdateHelperStorm();
            _helperAI.Tick(seconds);
        }
        /// <summary>实际玩家攻击才赚基础命中分；能量炸弹、环境、帮手都不能冒充玩家普攻。</summary>
        private void AwardCombatHit(Piece target, Piece attacker, BattleDamageKind kind, int real)
        {
            if (Phase != GamePhase.Battle || real <= 0 || target.Side != GameSide.Enemy)
            {
                return;
            }
            // 玩家普攻已在AwardHitScore合并；这里只返帮手的小额能源。
            int points = kind == BattleDamageKind.Helper ? Balance.HelperHitScore : 0;

            if (points <= 0)
            {
                return;
            }
            GrantBattlePoints(target, points, false, false);
        }
        /// <summary>入账只发生在服务，视觉飞星只是反馈。long饱和加避免超长对局溢出负分。</summary>
        private void GrantBattlePoints(Piece source, long points, bool kill, bool critical)
        {
            RoundScore = AddScore(RoundScore, points);
            ChapterScore = AddScore(ChapterScore, points);
            LifetimeScore = AddScore(LifetimeScore, points);
            CombatScore = AddScore(CombatScore, points);

#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
            if (!_scorePreview)
#endif
            {
                _scoreProgress?.Record(LifetimeScore);
            }
            RevEvent.DispatchEvent<ScoreGrantedInfo>(Events.ScoreGranted,
                new ScoreGrantedInfo(source.Pos, source.Size, points, RoundScore, LifetimeScore, ScoreMultiplier, kill, critical));
        }
        /// <summary>仅接受正成本，检查与扣款在同一次同步动作内完成，不存在飞星尚未抵达时重复花钱的窗口。</summary>
        private bool SpendCombatScore(int amount)
        {
            if (amount <= 0 || CombatScore < amount)
            {
                return false;
            }
            CombatScore -= amount;
            return true;
        }
        /// <summary>宿主适配器提供原子动作，FSM只控制时序。所有伤害仍进入ResolveDamage。</summary>
        private sealed class HelperOwner : IHelperAIOwner
        {
            private readonly BattleService _service;
            public HelperOwner(BattleService service)
            {
                _service = service;
            }
            private Piece Target => _service._board.FindPiece(_service._helperTargetId);
            public bool CanFight => _service.Phase == GamePhase.Battle && !_service.HasBattleEnded();
            public bool HasTarget => Target != null && Target.IsAlive;
            public bool BeamRunning => _service._helperBeamUntil > _service._battleElapsed;
            // 成本跟随章节积分倍率增长，防后章一击赚几千分而激光只消耗个位数。
            private int ShotCost => (int)Math.Min(int.MaxValue, Math.Ceiling(Balance.HelperShotCost * (double)_service.ScoreMultiplier));
            private int BeamCost => (int)Math.Min(int.MaxValue, Math.Ceiling(Balance.HelperBeamTickCost * (double)_service.ScoreMultiplier));
            public bool CanBeam => _service.ChapterScore >= Balance.HelperBeamScore
                && _service.CombatScore >= Math.Max(Balance.HelperBeamReserve, BeamCost) && _service._battleElapsed >= _service._helperNextBeam;
            public bool StormActive => _service.HelperStormActive;
            public void Storm(float seconds)
            {
                _service.StepHelperStorm(seconds, this);
            }
            public bool MoveDue => _service._battleElapsed >= _service._helperNextMove;
            public bool ShotDue => _service._battleElapsed >= _service._helperNextShot && _service.CombatScore >= ShotCost;

            /// <summary>拥挤时沿最近外圈爬行；空闲时选择敌旁空点。它不占格、不堵气，玩家仍可在脚下落子。</summary>
            public void Crawl()
            {
                Piece target = Target;
                _service._helperNextMove = _service._battleElapsed + Balance.HelperMoveSeconds;

                if (target == null)
                {
                    return;
                }
                int alive = 0;
                foreach (Piece player in _service._playerPieces)
                {
                    if (player.IsAlive)
                    {
                        alive++;
                    }
                }
                // 右栏中段是项目布局预留的空区；左栏六张手牌会遮住放大后的帮手，避免爬进UI背后。
                BoardPos next = new BoardPos(Consts.BoardSize, Math.Max(3, Math.Min(5, target.Pos.Y)));
                bool outside = true;

                if (alive < Balance.HelperCrowdedCount)
                {
                    int best = int.MaxValue;
                    for (int y = 0; y < Consts.BoardSize; y++)
                    {
                        for (int x = 0; x < Consts.BoardSize; x++)
                        {
                            var pos = new BoardPos(x, y);

                            if (!_service._board.GetCell(pos).IsEmpty)
                            {
                                continue;
                            }
                            // 保持三格左右的射击距离。放大的帮手贴敌一格会把激光完全盖在双方身体下。
                            // 三格是棋盘可视间距的结构选择，不增加射程限制或伤害。
                            int distance = Math.Abs(x - target.Pos.X) + Math.Abs(y - target.Pos.Y);
                            int score = Math.Abs(distance - 3) * 10
                                + Math.Abs(x - _service._helperPosition.X) + Math.Abs(y - _service._helperPosition.Y);

                            if (score < best)
                            {
                                best = score;
                                next = pos;
                                outside = false;
                            }
                        }
                    }
                }

                _service._helperPosition = next;
                _service._helperOutside = outside;

                if (outside)
                {
                    _service._helperSpeech = "棋盘有点挤，我去外面支援！";
                }
            }

            /// <summary>档位增加粒子数与频率，但每发都花积分。先算可支付数量，不能欠账或靠返分无限发射。</summary>
            public void Barrage()
            {
                Piece target = Target;

                if (target == null)
                {
                    return;
                }
                int tier = _service.HelperTier;
                int count = (int)Math.Min(1 + tier, _service.CombatScore / ShotCost);
                int cost = count * ShotCost;

                if (!_service.SpendCombatScore(cost))
                {
                    return;
                }
                // 保留固定步的微小超时，避免“每次从当前时刻重新计时”吃掉高攻速；缺分时不积攒无限追发。
                _service._helperNextShot = Math.Max(_service._helperNextShot,
                    _service._battleElapsed - Balance.BattleSimulationStepSeconds) + _service.HelperShotInterval;
                BoardPos position = target.Pos;
                for (int i = 0; i < count && target.IsAlive; i++)
                {
                    float damageScale = _service.IsBossRound ? Balance.HelperBossDamageScale : 1f;
                    _service.DamagePiece(target, ClampDamage(Balance.HelperBaseDamage * (1f + tier * Balance.HelperDamageGrowth) * damageScale, 1), damageKind: BattleDamageKind.Helper);
                }
                _service._helperSpeech = tier >= 2 ? "能量充足，弹幕掩护！" : "我来帮你压制远程！";
                RevEvent.DispatchEvent<HelperShotInfo>(Events.HelperShot, new HelperShotInfo(_service._helperPosition, position, target.Id, _service.Chapter, count, cost, false));
            }
            /// <summary>激光持续按固定步扣分与伤害，不返分；全灭、目标消失、余额不足或持续到期立即停。</summary>
            public void Beam(float seconds)
            {
                Piece target = Target;

                if (target == null)
                {
                    return;
                }
                if (!BeamRunning)
                {
                    _service._helperBeamUntil = _service._battleElapsed + Balance.HelperBeamSeconds;
                    _service._helperNextBeam = _service._battleElapsed + Balance.HelperBeamCooldown;
                }
                if (!_service.SpendCombatScore(BeamCost))
                {
                    _service._helperBeamUntil = 0;
                    _service._helperSpeech = "能量不足，继续攻击为我充能！";
                    return;
                }
                BoardPos position = target.Pos;
                _service.DamagePiece(target, ClampDamage(Balance.HelperBeamDamage * seconds / Balance.BattleSimulationStepSeconds, 1), damageKind: BattleDamageKind.HelperBeam);
                _service._helperSpeech = "蓝色能量束——全力压制！";
                RevEvent.DispatchEvent<HelperShotInfo>(Events.HelperShot, new HelperShotInfo(_service._helperPosition, position, target.Id, _service.Chapter, 1, BeamCost, true));
            }
        }
    }
}
