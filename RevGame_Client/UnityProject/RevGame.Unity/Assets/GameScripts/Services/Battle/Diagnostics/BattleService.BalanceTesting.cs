#if UNITY_EDITOR
using System;
using RevGame.Flow;
using Revolution;

namespace RevGame.Services
{
    public sealed partial class BattleService
    {
        /// <summary>测试只在隔离首页替换随机源；不修改伤害或棋子，也不把固定种子写进正式存档。</summary>
        public void SetBalanceTestSeed(int seed)
        {
            var flow=RevServices.GetRequired<GameFlow>();
            if(!RevServices.GetRequired<StoryProgress>().IsIsolated||flow.Screen!=RevGame.Core.GameScreen.Home||flow.IsGameplayBlocked)
            {throw new InvalidOperationException("固定种子需要隔离测试首页");}
            _random=new Random(seed);
            ClearAutomationBoost(this);
        }
        /// <summary>基线失败后的独立回合夹具：按真实关卡表开目标回合，不制造胜利或继承虚假的已通关阵容。</summary>
        public bool BeginBalanceRound(int chapter,int round)
        {
            if(!RevServices.GetRequired<StoryProgress>().IsIsolated){throw new InvalidOperationException("仅隔离数值测试可独立造局");}
            return BeginCampaignAtRound(chapter,round);
        }
    }
}
#endif
