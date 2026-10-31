// 固定种子比较真实伤害与敌方FSM；临时基线只用于独立规则测试，finally恢复配置，不修改存档。
#if UNITY_EDITOR || REVGAME_OFFLINE_TESTS
using System;
using RevGame.Battle;
using RevGame.Core;
using RevGame.Data;
using RevGame.Pieces;
using Revolution;

namespace RevGame.Services
{
    public sealed partial class BattleService
    {
        /// <summary>仅离线或非Play编辑态执行；覆盖全职业/星级/阵营/大小棋，检查预测与实际扣血一致。</summary>
        public static void VerifyEnemyRangedRules(Action<bool, string> check)
        {
            BalanceValues values = GameConfig.Current.Balance;
            float attack = values.EnemyRangedAttackMultiplier;
            float speed = values.EnemyRangedSpeedMultiplier;
            check(Math.Abs(attack - 1.5f) < 0.00001f && Math.Abs(speed - 1.5f) < 0.00001f, "Excel敌方远程攻击和频率均为原来的1.5倍");
            try
            {
                foreach (PieceKind kind in Enum.GetValues(typeof(PieceKind)))
                {
                    for (int tier = 1; tier <= 3; tier++)
                    {
                        foreach (GameSide side in new[] { GameSide.Player, GameSide.Enemy })
                        {
                            foreach (bool boss in new[] { false, true })
                            {
                                var service = new BattleService(false, 1031) { Chapter = 1, Round = 1, Phase = GamePhase.Battle };
                                var actor = new Piece(1, PieceCatalog.Get(kind, (StarTier)tier), side, 1, boss, false, 1000f, 100f);
                                var target = new Piece(2, PieceCatalog.Get(PieceKind.Swordman, StarTier.One), side == GameSide.Player ? GameSide.Enemy : GameSide.Player, 1, hpScale: 10000f);
                                service._board.Place(actor, new BoardPos(3, 3));
                                service._board.Place(target, new BoardPos(3, 4));
                                (side == GameSide.Player ? service._playerPieces : service._enemyPieces).Add(actor);
                                (side == GameSide.Player ? service._enemyPieces : service._playerPieces).Add(target);
                                service.RefreshGoState();
                                var request = new DamageRequest(target, DamageBasis.Attack, BattleDamageKind.Normal, actor, fromSkill: false);
                                values.EnemyRangedAttackMultiplier = 1f;
                                int before = service.EvaluateDamage(request).Damage;
                                int skillBefore = service.EvaluateDamage(new DamageRequest(target, DamageBasis.BossSkill, BattleDamageKind.Normal, actor)).Damage;
                                values.EnemyRangedAttackMultiplier = attack;
                                int after = service.EvaluateDamage(request).Damage;
                                bool affected = side == GameSide.Enemy && !boss && actor.Def.IsRanged;
                                // 实际伤害按整数结算，前后取整最多有2点偏差；大攻击夹具避免最低伤害掩盖倍率。
                                check(affected ? Math.Abs(after - before * 1.5) <= 2 : after == before, kind + "/" + tier + "/" + side + "/boss=" + boss + " 伤害 " + before + "→" + after);
                                int hp = target.Hp;
                                service.ResolveDamage(request);
                                check(hp - target.Hp == after, "预测与真实扣血一致");
                                check(service.EvaluateDamage(new DamageRequest(target, DamageBasis.BossSkill, BattleDamageKind.Normal, actor)).Damage == skillBefore, "技能伤害不受远程普攻倍率影响");
                                check(actor.EnemyRangedSpeedMultiplier == (affected ? speed : 1f), "频率倍率仅覆盖普通敌方远程");
                            }
                        }
                    }
                }
                foreach (PieceKind kind in new[] { PieceKind.Crossbow, PieceKind.Swordman })
                {
                    values.EnemyRangedSpeedMultiplier = 1f;
                    float baseline = MeasureRangedActions(kind, check);
                    values.EnemyRangedSpeedMultiplier = speed;
                    float changed = MeasureRangedActions(kind, check);
                    float factor = kind == PieceKind.Crossbow ? 1.5f : 1f;
                    check(Math.Abs(changed - baseline * factor) < 0.001f, kind + "真实FSM攻击次数+余量 " + baseline + "→" + changed);
                }
            }
            finally
            {
                values.EnemyRangedAttackMultiplier = attack;
                values.EnemyRangedSpeedMultiplier = speed;
            }
            check(Balance.EnemyRangedAttackMultiplier == attack && Balance.EnemyRangedSpeedMultiplier == speed, "临时基线配置已恢复");
        }

        /// <summary>固定20模拟秒，只推进真实敌棋FSM；高生命靶子承受真实伤害，事件owner与机器在finally释放。</summary>
        private static float MeasureRangedActions(PieceKind kind, Action<bool, string> check)
        {
            var service = new BattleService(false, 1031) { Chapter = 1, Round = 1, Phase = GamePhase.Battle };
            var actor = new Piece(1, PieceCatalog.Get(kind, StarTier.One), GameSide.Enemy, 1, hpScale: 10000f);
            var target = new Piece(2, PieceCatalog.Get(PieceKind.Swordman, StarTier.One), GameSide.Player, 1, hpScale: 10000f);
            service._board.Place(actor, new BoardPos(3, 3));
            service._board.Place(target, new BoardPos(3, 4));
            service._enemyPieces.Add(actor);
            service._playerPieces.Add(target);
            service.RefreshGoState();
            int count = 0;
            object owner = new object();
            RevEvent.AddEventListener<PieceAttackInfo>(Events.PieceAttacked, info =>
            {
                if (info.PieceId == actor.Id)
                {
                    count++;
                }
            }, owner: owner);
            try
            {
                int hp = target.Hp;
                EnemyAI ai = service.EnsureEnemyAI(actor);
                for (int step = 0; step < 160; step++)
                {
                    ai.Tick(0.125f);
                }
                check(count > 0 && target.Hp < hp, kind + "真实出手" + count + "次且造成扣血");
                return count + actor.ActionProgress;
            }
            finally
            {
                RevEvent.RemoveAllByOwner(owner);
                service.StopEnemyAI();
            }
        }
    }
}
#endif
