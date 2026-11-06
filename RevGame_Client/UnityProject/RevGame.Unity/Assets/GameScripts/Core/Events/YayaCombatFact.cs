// 只携带已经结算的战斗事实，观察者不能改变伤害、胜负或随机源；手动拆子与合并不发送死亡事实。
namespace RevGame.Core
{
    /// <summary>模式1六章、2无尽；damage为实际受伤数，敌方死亡kills=1，己方死亡losses=1。</summary>
    public readonly struct YayaCombatFact
    {
        /// <summary>模式、伤害单位生命点、击杀/己方死亡次数；只保存值，不持有死亡棋子。</summary>
        public readonly int Mode, Damage, Kills, Losses;
        /// <summary>真实目标阵营，决定受伤属于己方损失还是敌方交战事实。</summary>
        public readonly GameSide TargetSide;
        /// <summary>战斗服务提交后构造，演出调用方不能重算伤害。</summary>
        public YayaCombatFact(int mode, GameSide side, int damage, int kills = 0, int losses = 0)
        {
            Mode = mode;
            TargetSide = side;
            Damage = damage;
            Kills = kills;
            Losses = losses;
        }
    }
}
