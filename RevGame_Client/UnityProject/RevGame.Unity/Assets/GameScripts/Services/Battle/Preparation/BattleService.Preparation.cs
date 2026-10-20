// ============================================================
// BattleService.Preparation.cs —— 准备增益与本轮持续效果。
// 只由战斗域固定时钟推进，不创建跨界面的延时回调；退出战斗域后不会在大厅突然加棋或弹提示。
// 所有百分比、次数、阈值都来自 Excel（增益与事件配置.xlsx 的 PrepBuffConfig），领取时缓存进下面的 _prepXxx 字段，
// 战斗热路径只读字段，不再按名称反复查表。
// ============================================================
using System.Collections.Generic;
using RevGame.Core;
using RevGame.Data;
using RevGame.Pieces;
using Revolution;

namespace RevGame.Services
{
    // partial 片段：与其他 BattleService.*.cs 共享同一实例，完整类说明见 Simulation/BattleService.cs。
    public sealed partial class BattleService
    {
        // 这一轮已经领取的增益清单。UI 只读取它排队播报，不能从动画回调再次发放奖励。
        private readonly List<PreparationBuffDef> _preparationBuffs = new List<PreparationBuffDef>();
        // 只累计准备阶段的模拟时间；切回大厅后域时钟停止，因此不会出现大厅里迟到发奖。
        private float _preparationElapsed;
        // 一轮只领取一次；发事件之前置为 true，防止事件回调重入造成重复抽取。
        private bool _preparationGranted;
        // 抽中升星却没有合适目标时保存机会，后续新棋入场再消费，不让空棋盘白领奖。
        private int _pendingUpgrade;
        private float _prepAttack; // 全队攻击加成，0.25 表示在攻击倍率上增加 25%。
        private float _prepRanged; // 只给远程棋的额外攻击加成。
        private float _prepExecution; // 目标生命低于 _prepExecutionThreshold 时才启用的额外攻击加成。
        private float _prepExecutionThreshold; // 破阵追击的生命百分比线（Excel 的 Threshold）。
        private float _prepLifeSteal; // 按实际扣血计算吸血，不能用未扣到的理论伤害回血。
        private float _prepDamageReduction; // 统一扣血出口使用的减伤比例，0.2 表示减伤 20%。
        // 最大生命倍率不是加成百分比：1 表示原值，1.3 表示原值的 130%。
        private float _prepHealthScale = 1f;
        private bool _prepImmune; // 只免疫围困惩罚，不篡改实际气数或眼数。
        private float _prepRegenRatio; // 全队周期回复最大生命的比例；0 表示未领取生生不息。
        private float _prepEyeAttack; // 成眼棋的额外攻击加成；0 表示未领取慧眼共鸣。
        private float _prepEyeRegenRatio; // 成眼棋额外的周期回复比例（与慧眼共鸣同时领取）。
        private int _prepShieldCharges; // 每颗棋的护盾次数；0 表示未领取三重护盾。后续新棋继承，老棋剩余次数存在棋子自身。
        private float _prepRecoveryRatio; // 首次实际受伤后消费的一次全队回复储备比例；0 表示没有可消费的储备。
        // 团队规则而非逐棋修改：每次攻击读此倍率，后落子/援军自然继承，换轮一次清零。
        private float _prepArrowRate = 1f;
        // 回复脉冲的本轮累计时间；每个周期减掉一个周期，保留不足一个周期的余量。
        private float _prepRegenClock;
        /// <summary>
        /// 本轮已发放的增益清单（只读）。HUD 只负责排队播报，不能借它重复发奖。
        /// </summary>
        public IReadOnlyList<PreparationBuffDef> PreparationBuffs => _preparationBuffs;
        /// <summary>
        /// 准备阶段已过的模拟秒数；开始按钮用它显示“增益抵达 N 秒”。
        /// </summary>
        public float PreparationElapsed => _preparationElapsed;
        /// <summary>
        /// 本轮增益是否已统一生效；开启辅助时，未生效不允许开战。
        /// </summary>
        public bool PreparationGranted => _preparationGranted;

        /// <summary>每轮先清临时属性，保留玩家棋子的位置、伤势和永久星级，防止增益跨轮叠加失控。</summary>
        private void ResetPreparation()
        {
            _preparationBuffs.Clear();
            _preparationElapsed = 0f;
            _preparationGranted = false;
            _pendingUpgrade = 0;
            _prepAttack = _prepRanged = _prepExecution = _prepExecutionThreshold = _prepLifeSteal = _prepDamageReduction = 0f;
            _prepHealthScale = 1f;
            _prepImmune = false;
            _prepRegenRatio = _prepEyeAttack = _prepEyeRegenRatio = _prepRecoveryRatio = 0f;
            _prepShieldCharges = 0;
            _prepRegenClock = 0f;
            _prepArrowRate = 1f;
            foreach (Piece piece in _playerPieces)
            {
                piece.IgnoreEncirclement = false;
                piece.ShieldCharges = 0;
                piece.SetRoundHealthScale(1f);
            }
        }

        /// <summary>等待 Prep.DelaySeconds 后洗牌取出若干种不重复增益。业务先生效，随后通知UI播放动画。</summary>
        private void TickPreparation(float deltaSeconds)
        {
            if (_preparationGranted || float.IsNaN(deltaSeconds) || float.IsInfinity(deltaSeconds) || deltaSeconds <= 0f)
            {
                return;
            }
            _preparationElapsed += deltaSeconds;
            if (_preparationElapsed < Balance.PrepDelaySeconds)
            {
                return;
            }
            // 基础规则验收显式关闭通关辅助时，不赠送增益，便于隔离比较同一攻击/走法的结果。
            if (!VictoryAssistEnabled)
            {
                _preparationGranted = true; return;
            }
            // ★ 先标记再派事件。界面刷新/GM重入都不能让同一轮重复抽取一批奖励。
            _preparationGranted = true;
            // 保底不是“加大概率”：先把万箭齐发取出并实际应用，再从剩余增益里抽取。
            // 它占用本轮总名额；先移除再洗牌，保证不会出现同一轮重复领取两次。
            PreparationBuffDef volley = PreparationBuffCatalog.Get(PreparationBuffKind.ArrowVolley);
            _preparationBuffs.Add(volley);
            ApplyPreparationBuff(volley);
            var shuffled = new List<PreparationBuffDef>(PreparationBuffCatalog.All);
            shuffled.Remove(volley);
            // Fisher-Yates：每次把当前位置与前面一个随机位置交换，最后取前几项即可保证本轮不重复。
            // 先复制目录再洗牌，不能打乱全局目录，否则下一轮或验收读取会受上一轮影响。
            for (int i = shuffled.Count - 1; i > 0; i--)
            {
                int j = _random.Next(i + 1);
                PreparationBuffDef swap = shuffled[i];
                shuffled[i] = shuffled[j];
                shuffled[j] = swap;
            }
            // 数量上下限来自 Prep.BuffCountMin/Max（含上限）；Next 的上界是开区间，所以 +1。
            int count = _random.Next(Balance.PrepBuffCountMin, Balance.PrepBuffCountMax + 1);
            for (int i = 0; i < count - 1; i++)
            {
                _preparationBuffs.Add(shuffled[i]);
                ApplyPreparationBuff(shuffled[i]);
            }
            RefreshGoState();
            RevEvent.DispatchEvent(Events.PreparationBuffsGranted);
            RevEvent.DispatchEvent(Events.HandChanged);
        }

        /// <summary>所有效果在模型生效，弹幕只是告知。空棋盘也保存团队加成和待升级次数，不会白领。</summary>
        private void ApplyPreparationBuff(PreparationBuffDef buff)
        {
            switch (buff.Kind)
            {
                case PreparationBuffKind.Upgrade:
                    var candidates = _playerPieces.FindAll(p => p.IsAlive && p.Def.Star < StarTier.Three);
                    if (candidates.Count > 0)
                    {
                        UpgradePiece(candidates[_random.Next(candidates.Count)]);
                    }
                    else
                    {
                        _pendingUpgrade++;
                    }
                    break;
                case PreparationBuffKind.Elite: SpawnPreparationElite(buff); break;
                case PreparationBuffKind.Attack: _prepAttack += buff.Amount; break;
                case PreparationBuffKind.Recovery: HealTeam(buff.Amount); _prepRecoveryRatio = buff.Extra; break;
                case PreparationBuffKind.EncirclementImmunity: _prepImmune = true; break;
                case PreparationBuffKind.Haste: _roundSpeedBonus += buff.Amount; break;
                case PreparationBuffKind.Vitality: _prepHealthScale = 1f + buff.Amount; break;
                case PreparationBuffKind.Armor: _prepDamageReduction = buff.Amount; break;
                case PreparationBuffKind.LifeSteal: _prepLifeSteal = buff.Amount; break;
                case PreparationBuffKind.Regeneration: _prepRegenRatio = buff.Amount; break;
                case PreparationBuffKind.Shields: _prepShieldCharges = (int)buff.Amount; break;
                case PreparationBuffKind.Ranged: _prepRanged = buff.Amount; break;
                case PreparationBuffKind.Execution: _prepExecution = buff.Amount; _prepExecutionThreshold = buff.Threshold; break;
                case PreparationBuffKind.EyePower: _prepEyeAttack = buff.Amount; _prepEyeRegenRatio = buff.Extra; break;
                case PreparationBuffKind.Liberties: _roundLibertyBonus += (int)buff.Amount; break;
                case PreparationBuffKind.ArrowVolley: _prepArrowRate = 1f + buff.Amount; break;
            }
            foreach (Piece piece in _playerPieces)
            {
                if (!piece.IsAlive)
                {
                    continue;
                }
                piece.SetRoundHealthScale(_prepHealthScale);
                piece.IgnoreEncirclement = _prepImmune;
                // 此处仍在准备期，没有受击，统一赋满护盾；战斗中新增棋走单独入口，绝不重置老棋护盾。
                piece.ShieldCharges = _prepShieldCharges;
            }
        }

        /// <summary>让手牌和援军继承已领取的效果；只初始化这颗新棋，避免把老棋消耗过的护盾补满。</summary>
        private void ApplyPreparationToNewPiece(Piece piece)
        {
            piece.SetRoundHealthScale(_prepHealthScale);
            piece.IgnoreEncirclement = _prepImmune;
            piece.ShieldCharges = _prepShieldCharges;
            if (_pendingUpgrade > 0 && piece.Def.Star < StarTier.Three)
            {
                _pendingUpgrade--;
                UpgradePiece(piece);
            }
        }

        /// <summary>寻找不破坏现有眼阵的安全空格加入强援（外形与星级来自 Excel）；全盘无安全格时转换为攻击加成。</summary>
        private void SpawnPreparationElite(PreparationBuffDef buff)
        {
            if (TryDeployReinforcement(buff.Piece, (StarTier)buff.Star))
            {
                return;
            }
            _prepAttack += buff.Amount;
            // 日志里的比例与上面实际增加的是同一个数，不再手写“+10%”。
            RevEvent.DispatchEvent<string>(Events.BattleLog, buff.Title + "：无安全空格，改为本轮全队攻击加成+" + ConfigText.Percent(buff.Amount));
        }

        /// <summary>按这一次攻击的攻击者和目标计算附加比例；不修改共享图鉴，也不会让敌人继承玩家增益。</summary>
        private float PreparationAttackBonus(Piece attacker, Piece target)
        {
            return _prepAttack + (attacker.Def.IsRanged ? _prepRanged : 0f)
                + (target.HpRatio < _prepExecutionThreshold ? _prepExecution : 0f) + (_prepEyeAttack > 0f && attacker.EyeCount > 0 ? _prepEyeAttack : 0f);
        }

        /// <summary>万箭齐发在已有攻速上乘倍率（1 + Amount，默认翻倍）；重弩、法师和敌方弓箭手不受影响。</summary>
        private float PreparationAttackRate(Piece piece)
        {
            return _prepArrowRate > 1f && piece.Side == GameSide.Player && piece.Def.Kind == PieceKind.Archer ? _prepArrowRate : 1f;
        }

        /// <summary>只在战斗阶段驱动准备增益的持续回复；没领取对应效果时，回复比例为零。</summary>
        private void TickPreparationCombat(float seconds)
        {
            _prepRegenClock += seconds;
            if (_prepRegenClock < Balance.PrepRegenIntervalSeconds)
            {
                return;
            }
            _prepRegenClock -= Balance.PrepRegenIntervalSeconds;
            foreach (Piece piece in _playerPieces)
            {
                float ratio = _prepRegenRatio + (_prepEyeAttack > 0f && piece.EyeCount > 0 ? _prepEyeRegenRatio : 0f);
                if (ratio > 0f && piece.Heal(System.Math.Max(1, (int)(piece.MaxHp * ratio))) > 0)
                {
                    RevEvent.DispatchEvent<int>(Events.PieceHpChanged, piece.Id);
                }
            }
        }
    }
}
