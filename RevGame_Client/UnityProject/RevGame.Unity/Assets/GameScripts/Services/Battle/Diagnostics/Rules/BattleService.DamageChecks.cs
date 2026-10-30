#if UNITY_EDITOR
using System;
using RevGame.Core;
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
    }
}
#endif
