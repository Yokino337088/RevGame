#if UNITY_EDITOR
using System;
using RevGame.Core;
using RevGame.Data;
using RevGame.Pieces;
using Revolution;

namespace RevGame.Services
{
    public sealed partial class BattleService
    {
        /// <summary>验证预测无副作用、预测/实扣同公式、护盾只消费一次、百分比来源不套攻击增益、暴击随机序列不被AI查询推进。</summary>
        public static void VerifyDamageContracts(Action<bool, string> check)
        {
            var service = new BattleService(false, 1025);
            service.Chapter = 1; service.Round = 1; service.Phase = GamePhase.Battle;
            Piece player = service.AddImpactPiece(GameSide.Player, PieceKind.Archer, new BoardPos(1, 1));
            Piece enemy = service.AddImpactPiece(GameSide.Enemy, PieceKind.Shield, new BoardPos(1, 2));
            service.RefreshGoState();
            player.ShieldCharges = 2;
            int hp = player.Hp;
            int events = 0;
            object owner = new object();
            RevEvent.AddEventListener<int>(Events.DamageResolved, id => events++, owner: owner);
            try
            {
                var normal = new DamageRequest(player, DamageBasis.Attack, BattleDamageKind.Normal, enemy, fromSkill: false);
                for (int i = 0; i < 20; i++) { check(service.EvaluateDamage(normal).Damage == 0, "带盾预测为零"); }
                check(player.ShieldCharges == 2 && player.Hp == hp && events == 0, "预测不消耗盾/生命/事件");
                check(service.ResolveDamage(normal) == 0 && player.ShieldCharges == 1 && events == 1, "一次提交恰好消费一层盾与一次伤害事实");
                player.ShieldCharges = 0;
                service._prepDamageReduction = .25f; service._combatDefenseBonus = .5f;
                foreach (DamageBasis basis in new[] { DamageBasis.Attack, DamageBasis.BossSkill, DamageBasis.MaxHp, DamageBasis.CurrentHp, DamageBasis.Fixed })
                {
                    player.Heal(player.MaxHp);
                    var request = new DamageRequest(player, basis, BattleDamageKind.Normal, enemy, factor: .2f, floor: 40);
                    int predicted = service.EvaluateDamage(request).Damage;
                    hp = player.Hp;
                    int actual = service.ResolveDamage(request);
                    check(actual == Math.Min(hp, predicted) && hp - player.Hp == actual, "计算与实扣完全一致：" + basis);
                }
                var percent = new DamageRequest(enemy, DamageBasis.MaxHp, BattleDamageKind.RegionPulse, factor: .15f);
                int before = service.EvaluateDamage(percent).Damage;
                service._combatAttackBonus = 100f;
                check(service.EvaluateDamage(percent).Damage == before, "百分比技能不继承普攻攻击加成");
                service._combatAttackBonus = 0f;
                CheckCampaignEnemyDefense(service, enemy, check);
                var reference = new Random(1025);
                for (int i = 0; i < 20; i++)
                {
                    service.PredictAttackDefeat(enemy, player);
                    check(service._random.NextDouble() == reference.NextDouble(), "反复AI预判不推进真实暴击随机序列");
                }
                service.DamagePiece(enemy, int.MaxValue);
                int resolved = events;
                check(service.DamagePiece(enemy, int.MaxValue) == 0 && events == resolved, "重复命中死棋不产生第二次结算");
            }
            finally { RevEvent.RemoveAllByOwner(owner); service.StopEnemyAI(); }
        }

        /// <summary>按正式开局进入第一个 Boss 回合，对主将和护卫分别核对防御与实扣。</summary>
        public static void VerifyCampaignEnemyDefense(Action<bool, string> check)
        {
            var service = new BattleService(false, 1025);
            try
            {
                check(service.StartRound(1, Balance.BossRoundIndex), "敌方防御夹具生成真实 Boss 阵容");
                Piece boss = service._enemyPieces.Find(piece => piece.IsBoss);
                Piece guard = service._enemyPieces.Find(piece => !piece.IsBoss);
                check(boss != null && guard != null, "敌方防御覆盖主将和普通护卫");
                CheckCampaignEnemyDefense(service, boss, check);
                CheckCampaignEnemyDefense(service, guard, check);
            }
            finally
            {
                service.StopEnemyAI();
            }
        }

        /// <summary>固定伤害没有攻击增益，可直接核对防御除数与零伤害、最低一点两个边界。</summary>
        private static void CheckCampaignEnemyDefense(BattleService service, Piece enemy, Action<bool, string> check)
        {
            int enemyHp = enemy.Hp;
            int expected = Math.Max(1, (int)(60d / Balance.EnemyDefenseMultiplier));
            var request = new DamageRequest(enemy, DamageBasis.Fixed, BattleDamageKind.Normal, floor: 60);
            check(service.EvaluateDamage(request).Damage == expected, "敌方防御应用于统一伤害预估");
            check(service.ResolveDamage(request) == expected && enemy.Hp == enemyHp - expected, "敌方防御应用于实际扣血");
            check(service.EvaluateDamage(new DamageRequest(enemy, DamageBasis.Fixed, BattleDamageKind.Normal, floor: 0)).Damage == 0, "敌方防御保留零伤害");
            check(service.EvaluateDamage(new DamageRequest(enemy, DamageBasis.Fixed, BattleDamageKind.Normal, floor: 1)).Damage == 1, "敌方防御保留正伤害最低一点");
        }
    }
}
#endif
