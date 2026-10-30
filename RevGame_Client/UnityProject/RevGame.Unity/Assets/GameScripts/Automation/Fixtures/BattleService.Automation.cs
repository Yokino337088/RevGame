// 开发态测试夹具。物理文件留在用户指定的 Animation/Fixtures，partial 仍是原来的 BattleService，绝不是第二套战斗服务。
#if UNITY_EDITOR || DEVELOPMENT_BUILD
using RevGame.Automation;
using RevGame.Core;
using RevGame.Pieces;

namespace RevGame.Services
{
    public sealed partial class BattleService
    {
        /// <summary>当前测试独占的增益租约；null 表示普通游玩。所有权使旧测试无法清掉新测试的配置。</summary>
        private object _automationBoostOwner;
        /// <summary>仅本次流程的测试参数，不写Excel/存档，不改只读PieceDef，换回合和后续援军都读取同一份倍率。</summary>
        private GameAutomationBoost _automationBoost;
        /// <summary>只读开发诊断：当前是否仍持有强化配置。供退出验收查询，不能从外部赋值或改数值。</summary>
        public bool IsAutomationBoostActive
        {
            get
            {
                return _automationBoostOwner != null && _automationBoost != null;
            }
        }
        /// <summary>只能在已验证的测试计划开始时取得租约；增益本身不生成棋子、不改胜负、不跳过战斗。</summary>
        internal void SetAutomationBoost(object owner, GameAutomationBoost boost)
        {
            GameAutomationBoost.Validate(boost);
            if (_automationBoostOwner != null && _automationBoostOwner != owner && _automationBoostOwner != this)
            {
                throw new System.InvalidOperationException("另一份自动化测试仍持有战斗增益");
            }
            _automationBoostOwner = owner;
            // 手动GM以服务自身为owner；启动自动化可接管它，并显式采用计划配置，不继承未声明的旧强化。
            _automationBoost = boost?.Copy();
            RefreshAutomationHealth();
        }
        /// <summary>完成、失败、取消和退出Play必须归还租约；反复调用安全，普通玩家不会继承测试强化。</summary>
        internal void ClearAutomationBoost(object owner)
        {
            if (_automationBoostOwner == owner)
            {
                _automationBoostOwner = null;
                _automationBoost = null;
                RefreshAutomationHealth();
            }
        }
        /// <summary>返回独立参数快照，供GM读取/修改后整体提交；不暴露正在使用的可变配置。</summary>
        internal GameAutomationBoost CopyAutomationBoost()
        {
            return _automationBoost?.Copy() ?? new GameAutomationBoost();
        }
        /// <summary>手动GM的租约不会覆盖运行中的自动化；运行中修改必须由执行器持同一owner接续。</summary>
        internal void SetManualDevelopmentBoost(GameAutomationBoost boost)
        {
            if (boost == null)
            {
                ClearAutomationBoost(this);
            }
            else
            {
                SetAutomationBoost(this, boost);
            }
        }
        /// <summary>组合原有本轮生命增益，不改只读图鉴；比例重算让反复设置/清零都不会乘上旧倍率。</summary>
        private float ScaleAutomationHealth(float normalScale)
        {
            return normalScale * (1f + (_automationBoost == null ? 0 : _automationBoost.healthBonusPercent) / 100f);
        }
        private void RefreshAutomationHealth()
        {
            foreach (Piece piece in _playerPieces)
            {
                // SetRoundHealthScale保留生命比例且保留0血；加生命不是复活或免费回满的旁路。
                ApplyPreparationHealth(piece, _prepHealthScale);
                if (piece.IsAlive)
                {
                    Revolution.RevEvent.DispatchEvent<int>(Events.PieceHpChanged, piece.Id);
                }
            }
        }
        /// <summary>只提高我方的攻击进度积累，不改Time.timeScale；所以准备三秒、敌人AI和界面动画仍按正常时钟运行。</summary>
        private float ScaleAutomationAttackRate(float rate)
        {
            return _automationBoost == null ? rate : rate * (1f + _automationBoost.attackSpeedBonusPercent / 100f);
        }
        /// <summary>百分比是额外加成：+500%=原来的6倍。先保留原伤害公式，再在统一出手出口乘测试倍率。</summary>
        private int ScaleAutomationAttackDamage(Piece attacker, int damage)
        {
            if (_automationBoost == null || attacker.Side != GameSide.Player)
            {
                return damage;
            }
            double scaled = damage * (1d + _automationBoost.attackBonusPercent / 100d);
            return (int)System.Math.Min(int.MaxValue, scaled);
        }
        /// <summary>
        /// 当前游戏没有独立的“防御力”属性，因此测试防御+500%明确采用承受伤害÷6。
        /// 该钩子位于统一DamagePiece出口：普通攻击、Boss技能都覆盖；护盾已抵消的0伤害仍为0。
        /// 正伤害最少1，不把减伤当成无敌；关闭夹具时原值原样返回，不改变普通游戏的取整规则。
        /// </summary>
        private int ScaleAutomationIncomingDamage(Piece target, int damage)
        {
            if (_automationBoost == null || target.Side != GameSide.Player || damage <= 0)
            {
                return damage;
            }
            return System.Math.Max(1, (int)(damage / (1d + _automationBoost.defenseBonusPercent / 100d)));
        }
    }
}
#endif
