// 固定输入验证Boss风暴和治疗。造局可控制，倍率、目标、回血和伤害均调用生产代码。
#if UNITY_EDITOR || REVGAME_OFFLINE_TESTS
using System;
using RevGame.Battle;
using RevGame.Core;
using RevGame.Data;
using RevGame.Pieces;
namespace RevGame.Services
{
    public sealed partial class BattleService
    {
        /// <summary>六章只创建本章十技能、狂暴及三种生命周期状态；外国技能拒绝且不改变状态。</summary>
        public static void VerifyBossHealingRules(Action<bool,string> check)
        {
            for (int chapter=1;chapter<=Consts.ChapterCount;chapter++)
            {
                var b=new BattleService(false,1029);
                try
                {
                    check(b.StartRound(chapter,Balance.BossRoundIndex)&&b.LoadBossSkillPreview(chapter,1f),"建立Boss风暴章"+chapter);
                    b.Phase=GamePhase.Battle;
                    Piece boss=b._enemyPieces[0];
                    var owner=new BossOwner(b,boss);
                    using(var ai=new BossAI(owner))
                    {
                        check(ai.RegisteredStateCount==14,"章"+chapter+"仅注册14个本章状态，实际="+ai.RegisteredStateCount);
                        BossAIState old=ai.State;
                        foreach(BossSkillKind kind in Enum.GetValues(typeof(BossSkillKind)))
                        {
                            if(BossSkillCatalog.IsUnlocked(kind,chapter)){continue;}
                            check(!ai.TryCast(kind)&&ai.State==old,"外章技能拒绝且状态不变："+kind);
                        }
                    }
                    b.UpdateHelperStorm();
                    check(b.HelperStormActive&&b._playerPieces.Count>3,"多人Boss回合自动风暴，无需少子");
                    check(b.HelperStormAttackFactor==2f&&b.HelperStormSpeedFactor==2f,"Boss风暴攻击与速度各为两倍");
                    Piece first=b._playerPieces[0],second=b._playerPieces[1];
                    first.TakeDamage(first.MaxHp*9/10);second.TakeDamage(second.MaxHp*8/10);
                    check(b.HelperProtectionTargetId==first.Id,"受伤远程按最低生命比例锁定");
                    int hp=first.Hp;
                    long healed=b.HelperProtectionHealing;
                    b.TickHelperProtection(.5f);
                    int expected=(int)Math.Floor(first.MaxHp*(double)Balance.HelperBossHealPerSecond*.5+.000001d);
                    check(first.Hp-hp==expected&&b.HelperProtectionHealing-healed==expected,"半模拟秒精确恢复最大生命35%");
                    check(b.HelperProtectionTargetId==first.Id,"未满血保持同一条治疗线");
                    first.Heal(first.MaxHp);
                    check(b.HelperProtectionTargetId==second.Id&&!b.HasHelperProtection(first)&&b.HasHelperProtection(second),"满血切下一远程，旧目标增益撤销");
                    var melee=new Piece(b._nextPieceId++,PieceCatalog.Get(PieceKind.Shield,StarTier.One),GameSide.Player,1);
                    check(b._board.Place(melee,new BoardPos(4,4)),"加入近战优先级对照");b._playerPieces.Add(melee);melee.TakeDamage(melee.MaxHp-1);
                    b._bossHealingTargetId=0;
                    check(b.HelperProtectionTargetId==second.Id,"近战更残仍优先受伤远程");
                    foreach(Piece p in b._playerPieces){if(p.Def.IsRanged){p.Heal(p.MaxHp);}}
                    check(b.HelperProtectionTargetId==melee.Id,"远程都满血后治疗残血近战");
                    melee.Heal(melee.MaxHp);b.TickHelperProtection(8f);
                    check(b.HelperProtectionTargetId==0,"全部满血不显示治疗线，不蓄积治疗");
                    second.TakeDamage(second.MaxHp*8/10);b._bossHealingTargetId=0;
                    check(b.HelperProtectionTargetId==second.Id,"再次受伤可重新绑定");
                    var request=new DamageRequest(boss,DamageBasis.Attack,BattleDamageKind.Normal,second,fromSkill:false);
                    int buffed=b.EvaluateDamage(request).Damage;
                    second.ActionProgress=0;b.StepPlayerAttack(second,.01f);float boosted=second.ActionProgress;
                    second.Heal(second.MaxHp);
                    int normal=b.EvaluateDamage(request).Damage;
                    second.ActionProgress=0;b.StepPlayerAttack(second,.01f);float baseline=second.ActionProgress;
                    check(buffed>=normal*2&&buffed<=(normal+1)*2,"治疗攻击严格两倍（单次整数舍入边界）");
                    check(Math.Abs(boosted/baseline-2f)<.001f,"治疗攻速严格两倍，满血撤销");
                    b.ResetHelperProtection();b.Phase=GamePhase.Prepare;b.UpdateHelperStorm();
                    check(b.HelperProtectionTargetId==0&&!b.HelperStormActive,"准备与离场无治疗/风暴残留");
                }
                finally{b.StopEnemyAI();}
            }
        }
    }
}
#endif
