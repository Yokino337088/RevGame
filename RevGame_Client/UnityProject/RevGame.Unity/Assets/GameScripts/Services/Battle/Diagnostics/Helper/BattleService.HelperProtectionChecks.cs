#if UNITY_EDITOR || REVGAME_OFFLINE_TESTS
using System;
using RevGame.Core;
using RevGame.Data;
using RevGame.Pieces;
using Revolution;

namespace RevGame.Services
{
    public sealed partial class BattleService
    {
        /// <summary>单棋保护边界：同伴存在/离开、真实回血与加成、满血不蓄治疗、真死不复活、换轮归零。</summary>
        public static void VerifyHelperProtectionRules(Action<bool, string> check)
        {
            var battle = new BattleService(false, 1029);
            object owner = new object();
            int hpEvents = 0;
            try
            {
                battle.Chapter = 1;
                battle.Round = 1;
                battle.Phase = GamePhase.Battle;
                // 高攻击仅用于倍率辨识的固定规则输入，不参与正常胜率/时长回放。
                // 普通攻击只有3点时取整容差占比太高；扩大同一原值后，更容易发现把50%接成5%/100%的错误。
                Piece last = new Piece(battle._nextPieceId++, PieceCatalog.Get(PieceKind.Shield, StarTier.One),
                    GameSide.Player, 1, attackScale: 100f);

                if (!battle._board.Place(last, new BoardPos(0, 0)))
                {
                    throw new InvalidOperationException("单棋保护倍率夹具落点冲突");
                }
                battle._playerPieces.Add(last);
                Piece ally = battle.AddHelperCheckPiece(GameSide.Player, PieceKind.Archer, new BoardPos(2, 0));
                Piece enemy = new Piece(battle._nextPieceId++, PieceCatalog.Get(PieceKind.Shield, StarTier.One), GameSide.Enemy, 1, hpScale: 1000);
                battle._board.Place(enemy, new BoardPos(8, 8));
                battle._enemyPieces.Add(enemy);
                battle.RefreshGoState();
                check(battle.HelperProtectionTargetId == 0, "两枚存活玩家棋不触发单棋保护");
                check(battle.RemovePlayerPiece(ally.Id), "通过正式移除入口变为最后一枚棋");
                check(battle.HelperProtectionTargetId == last.Id, "单棋保护立即绑定实际存活Id");
                RevEvent.AddEventListener<int>(Events.PieceHpChanged, id =>
                {
                    if (id == last.Id)
                    {
                        hpEvents++;
                    }
                }, owner: owner);
                last.TakeDamage(last.MaxHp - 1);
                int before = last.Hp;
                for (int i = 0; i < 4; i++)
                {
                    battle.TickHelperProtection(.25f);
                }
                int expected = (int)Math.Floor(last.MaxHp * (double)Balance.HelperProtectionHealPerSecond + 0.000001);
                check(last.Hp - before == expected && battle.HelperProtectionHealing == expected, "四个小步一秒恰好回复最大生命30%并记录实际量");
                check(hpEvents > 0, "回血发送实际HP变更事件供射线/血条观察");
                var request = new DamageRequest(enemy, DamageBasis.Attack, BattleDamageKind.Normal, last, fromSkill: false);
                int protectedDamage = battle.EvaluateDamage(request).Damage;
                last.ActionProgress = 0;
                battle.StepPlayerAttack(last, .01f);
                float protectedProgress = last.ActionProgress;
                ally = battle.AddHelperCheckPiece(GameSide.Player, PieceKind.Archer, new BoardPos(2, 0));
                battle.RefreshGoState();
                check(battle.HelperProtectionTargetId == 0, "同伴补入后立即撤销保护");
                int normalDamage = battle.EvaluateDamage(request).Damage;
                last.ActionProgress = 0;
                battle.StepPlayerAttack(last, .01f);
                float normalProgress = last.ActionProgress;
                // 两次计算使用同一攻击者、目标与既有增益，仅有单棋保护开关不同。
                // 伤害公式末尾向下取整：若普通伤害为n，则未取整原值位于[n,n+1)，
                // 乘保护倍率后的合法整数只可能位于下面这个闭区间，不能把“任意变大”误当准确+50%。
                // 夹具时间为0且目标是敌方，无加速/护盾/防御的二次取整，所以该误差区间只覆盖一次取整。
                double attackFactor = 1d + Balance.HelperProtectionAttackBonus;
                int minimumDamage = (int)Math.Floor(normalDamage * attackFactor);
                int maximumDamage = (int)Math.Ceiling((normalDamage + 1d) * attackFactor) - 1;
                check(protectedDamage >= minimumDamage && protectedDamage <= maximumDamage,
                    "单棋攻击倍率严格符合配置：normal=" + normalDamage + ",actual=" + protectedDamage
                    + ",factor=" + attackFactor.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)
                    + ",allowed=[" + minimumDamage + "," + maximumDamage + "]");
                check(Math.Abs(protectedProgress / normalProgress - (1 + Balance.HelperProtectionSpeedBonus)) < .001f, "保护攻速提升50%且未写回原定义");
                int lowHp = last.Hp;
                battle.TickHelperProtection(1f);
                check(last.Hp == lowHp, "两棋时不继续免费回血");
                battle.RemovePlayerPiece(ally.Id);
                last.Heal(last.MaxHp);
                battle.TickHelperProtection(8);
                last.TakeDamage(20);
                before = last.Hp;
                battle.TickHelperProtection(.01f);
                check(last.Hp - before <= 1, "满血时不储存数秒治疗来瞬间补满");
                battle.DamagePiece(last, int.MaxValue);
                check(!last.IsAlive && battle.HelperProtectionTargetId == 0, "致死伤害仍真实死亡，不因保护强制复活");
                battle.TickHelperProtection(5);
                check(!last.IsAlive, "死棋不会被治疗线复活");
                check(battle.StartRound(1, 1) && battle.HelperProtectionTargetId == 0 && battle.HelperProtectionHealing == 0, "换回合取消保护并清本轮治疗统计");
            }
            finally { battle.StopEnemyAI(); RevEvent.RemoveAllByOwner(owner); }
        }
    }
}
#endif
