// 本文件只提供开发态可控输入；实际治疗、风暴与技能均由正常战斗Tick推进。
#if UNITY_EDITOR
using System;
using RevGame.Core;
using RevGame.Data;
using Revolution;
namespace RevGame.Services
{
    public sealed partial class BattleService
    {
        /// <summary>仅隔离Boss准备态可建立高生命输入；保留正常伤害和回合规则，不改生产存档。</summary>
        public bool PrepareBossHealingPreview(int chapter)
        {
            if (!RevServices.GetRequired<StoryProgress>().IsIsolated) { return Reject("需要隔离测试进度"); }
            return LoadBossSkillPreview(chapter,1f);
        }
        /// <summary>有限测试伤口：经唯一DamagePiece结算，保留至少一血；不能指定状态或治疗结果。</summary>
        public int WoundBossHealingPreview(int id)
        {
            if (!RevServices.GetRequired<StoryProgress>().IsIsolated || Phase!=GamePhase.Battle || !IsBossRound)
            { throw new InvalidOperationException("需要隔离Boss战斗态"); }
            var p=_board.FindPiece(id);
            if(p==null||!p.IsAlive||p.Side!=GameSide.Player){throw new InvalidOperationException("需要存活玩家目标");}
            int total=0;
            // 有界多次真实伤害克服护甲，绝不直接把血量赋成期望值。
            for(int i=0;i<8&&p.HpRatio>.05f;i++){total+=DamagePiece(p,p.Hp-1,fromSkill:true);}
            return total;
        }
        /// <summary>读取真实机器登记计数；对象归当前战斗服务拥有，不暴露内核或任意反射。</summary>
        public int BossRegisteredStates
        {
            get
            {
                foreach(var ai in _bossAI.Values){return ai.RegisteredStateCount;}
                return 0;
            }
        }
    }
}
#endif
