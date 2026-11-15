#if UNITY_EDITOR
using System;
using Revolution;

namespace RevGame.Services
{
    public sealed partial class BattleService
    {
        /// <summary>由 GameFlow 核对隔离首页后调用，只替换本局随机源并清空旧测试增益。</summary>
        internal void SetBalanceTestSeed(int seed)
        {
            _random = new Random(seed);
            ClearAutomationBoost(this);
        }
        /// <summary>基线失败后的独立回合夹具：按真实关卡表开目标回合，不制造胜利或继承虚假的已通关阵容。</summary>
        public bool BeginBalanceRound(int chapter, int round)
        {
            if (!RevServices.GetRequired<StoryProgress>().IsIsolated)
            {
                throw new InvalidOperationException("仅隔离数值测试可独立造局");
            }
            return BeginCampaignAtRound(chapter, round);
        }
    }
}
#endif
