// ============================================================
// BattleService.Preparation.cs —— 准备增益与本轮持续效果。
// 只由战斗域固定时钟推进，不创建跨界面的延时回调；退出战斗域后不会在大厅突然加棋或弹提示。
// ============================================================
using System.Collections.Generic;
using RevGame.Core;
using RevGame.Data;
using RevGame.Pieces;
using Revolution;

namespace RevGame.Services
{
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
        private float _prepExecution; // 目标低于 35% 生命时才启用的额外攻击加成。
        private float _prepLifeSteal; // 按实际扣血计算吸血，不能用未扣到的理论伤害回血。
        private float _prepDamageReduction; // 统一扣血出口使用的减伤比例，0.2 表示减伤 20%。
        // 最大生命倍率不是加成百分比：1 表示原值，1.3 表示原值的 130%。
        private float _prepHealthScale = 1f;
        private bool _prepImmune; // 只免疫围困惩罚，不篡改实际气数或眼数。
        private bool _prepRegen; // 是否开启全队每四秒回复。
        private bool _prepEyePower; // 是否开启成眼棋的攻击加成和额外回复。
        private bool _prepShields; // 后续新棋是否继承三次护盾；老棋剩余次数存在棋子自身。
        private bool _prepRecoveryBank; // 首次实际受伤后消费的一次全队回复储备。
        // 团队规则而非逐棋修改：每次攻击读此开关，后落子/援军自然继承，换轮一次清零。
        private bool _prepArrowVolley;
        // 回复脉冲的本轮累计时间；每四秒减掉一个周期，保留不足四秒的余量。
        private float _prepRegenClock;
        public IReadOnlyList<PreparationBuffDef> PreparationBuffs => _preparationBuffs;
        public float PreparationElapsed => _preparationElapsed;
        public bool PreparationGranted => _preparationGranted;

        /// <summary>每轮先清临时属性，保留玩家棋子的位置、伤势和永久星级，防止增益跨轮叠加失控。</summary>
        private void ResetPreparation()
        {
            _preparationBuffs.Clear();
            _preparationElapsed = 0f;
            _preparationGranted = false;
            _pendingUpgrade = 0;
            _prepAttack = _prepRanged = _prepExecution = _prepLifeSteal = _prepDamageReduction = 0f;
            _prepHealthScale = 1f;
            _prepImmune = _prepRegen = _prepEyePower = _prepShields = _prepRecoveryBank = false;
            _prepRegenClock = 0f;
            _prepArrowVolley = false;
            foreach (Piece piece in _playerPieces)
            {
                piece.IgnoreEncirclement = false;
                piece.ShieldCharges = 0;
                piece.SetRoundHealthScale(1f);
            }
        }

        /// <summary>等待三秒后洗牌取出3～5种不重复增益。业务先生效，随后通知UI播放动画。</summary>
        private void TickPreparation(float deltaSeconds)
        {
            if (_preparationGranted || float.IsNaN(deltaSeconds) || float.IsInfinity(deltaSeconds) || deltaSeconds <= 0f) { return; }
            _preparationElapsed += deltaSeconds;
            if (_preparationElapsed < 3f) { return; }
            // 基础规则验收显式关闭通关辅助时，不赠送增益，便于隔离比较同一攻击/走法的结果。
            if (!VictoryAssistEnabled) { _preparationGranted = true; return; }
            // ★ 先标记再派事件。界面刷新/GM重入都不能让同一轮重复抽取一批奖励。
            _preparationGranted = true;
            var shuffled = new List<PreparationBuffDef>(PreparationBuffCatalog.All);
            // Fisher-Yates：每次把当前位置与前面一个随机位置交换，最后取前几项即可保证本轮不重复。
            // 先复制目录再洗牌，不能打乱全局目录，否则下一轮或验收读取会受上一轮影响。
            for (int i = shuffled.Count - 1; i > 0; i--)
            {
                int j = _random.Next(i + 1);
                PreparationBuffDef swap = shuffled[i];
                shuffled[i] = shuffled[j];
                shuffled[j] = swap;
            }
            int count = _random.Next(3, 6);
            for (int i = 0; i < count; i++)
            {
                _preparationBuffs.Add(shuffled[i]);
                ApplyPreparationBuff(shuffled[i].Kind);
            }
            RefreshGoState();
            RevEvent.DispatchEvent(Events.PreparationBuffsGranted);
            RevEvent.DispatchEvent(Events.HandChanged);
        }

        /// <summary>所有效果在模型生效，弹幕只是告知。空棋盘也保存团队加成和待升级次数，不会白领。</summary>
        private void ApplyPreparationBuff(PreparationBuffKind kind)
        {
            switch (kind)
            {
                case PreparationBuffKind.Upgrade:
                    var candidates = _playerPieces.FindAll(p => p.IsAlive && p.Def.Star < StarTier.Three);
                    if (candidates.Count > 0) { UpgradePiece(candidates[_random.Next(candidates.Count)]); }
                    else { _pendingUpgrade++; }
                    break;
                case PreparationBuffKind.Elite: SpawnPreparationElite(); break;
                case PreparationBuffKind.Attack: _prepAttack += 0.25f; break;
                case PreparationBuffKind.Recovery: HealTeam(0.4f); _prepRecoveryBank = true; break;
                case PreparationBuffKind.EncirclementImmunity: _prepImmune = true; break;
                case PreparationBuffKind.Haste: _roundSpeedBonus += 0.25f; break;
                case PreparationBuffKind.Vitality: _prepHealthScale = 1.3f; break;
                case PreparationBuffKind.Armor: _prepDamageReduction = 0.2f; break;
                case PreparationBuffKind.LifeSteal: _prepLifeSteal = 0.2f; break;
                case PreparationBuffKind.Regeneration: _prepRegen = true; break;
                case PreparationBuffKind.Shields: _prepShields = true; break;
                case PreparationBuffKind.Ranged: _prepRanged = 0.35f; break;
                case PreparationBuffKind.Execution: _prepExecution = 0.5f; break;
                case PreparationBuffKind.EyePower: _prepEyePower = true; break;
                case PreparationBuffKind.Liberties: _roundLibertyBonus += 2; break;
                case PreparationBuffKind.ArrowVolley: _prepArrowVolley = true; break;
            }
            foreach (Piece piece in _playerPieces)
            {
                if (!piece.IsAlive) { continue; }
                piece.SetRoundHealthScale(_prepHealthScale);
                piece.IgnoreEncirclement = _prepImmune;
                // 此处仍在准备期，没有受击，统一赋三次安全；战斗中新增棋走单独入口，绝不重置老棋护盾。
                piece.ShieldCharges = _prepShields ? 3 : 0;
            }
        }

        /// <summary>让手牌和援军继承已领取的效果；只初始化这颗新棋，避免把老棋消耗过的护盾补满。</summary>
        private void ApplyPreparationToNewPiece(Piece piece)
        {
            piece.SetRoundHealthScale(_prepHealthScale);
            piece.IgnoreEncirclement = _prepImmune;
            piece.ShieldCharges = _prepShields ? 3 : 0;
            if (_pendingUpgrade > 0 && piece.Def.Star < StarTier.Three)
            {
                _pendingUpgrade--;
                UpgradePiece(piece);
            }
        }

        /// <summary>寻找不破坏现有眼阵的安全空格加入强援；全盘无安全格时转换为攻击加成。</summary>
        private void SpawnPreparationElite()
        {
            if (TryDeployReinforcement(PieceKind.Crossbow, StarTier.Two)) { return; }
            _prepAttack += 0.1f;
            RevEvent.DispatchEvent<string>(Events.BattleLog, "重弩援军：无安全空格，改为本轮全队攻击加成+10%");
        }

        /// <summary>按这一次攻击的攻击者和目标计算附加比例；不修改共享图鉴，也不会让敌人继承玩家增益。</summary>
        private float PreparationAttackBonus(Piece attacker, Piece target)
        {
            return _prepAttack + (attacker.Def.IsRanged ? _prepRanged : 0f)
                + (target.HpRatio < 0.35f ? _prepExecution : 0f) + (_prepEyePower && attacker.EyeCount > 0 ? 0.3f : 0f);
        }

        /// <summary>万箭齐发在已有攻速上乘2，保证“频率+100%”确实翻倍；重弩、法师和敌方弓箭手不受影响。</summary>
        private float PreparationAttackRate(Piece piece)
        {
            return _prepArrowVolley && piece.Side == GameSide.Player && piece.Def.Kind == PieceKind.Archer ? 2f : 1f;
        }

        /// <summary>只在战斗阶段驱动准备增益的持续回复；没领取对应效果时，回复比例为零。</summary>
        private void TickPreparationCombat(float seconds)
        {
            _prepRegenClock += seconds;
            if (_prepRegenClock < 4f) { return; }
            _prepRegenClock -= 4f;
            foreach (Piece piece in _playerPieces)
            {
                float ratio = (_prepRegen ? 0.04f : 0f) + (_prepEyePower && piece.EyeCount > 0 ? 0.06f : 0f);
                if (ratio > 0f && piece.Heal(System.Math.Max(1, (int)(piece.MaxHp * ratio))) > 0)
                {
                    RevEvent.DispatchEvent<int>(Events.PieceHpChanged, piece.Id);
                }
            }
        }
    }
}
