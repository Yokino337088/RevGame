#if UNITY_EDITOR || DEVELOPMENT_BUILD || REVGAME_OFFLINE_TESTS
using System;
using System.Collections.Generic;
using System.Diagnostics;
using RevGame.Battle;
using RevGame.Core;
using RevGame.Data;
using RevGame.Pieces;
using Revolution;

namespace RevGame.Services
{
    public sealed partial class BattleService
    {
        /// <summary>
        /// 独立服务固定造局，调用正式Attack/ResolveDamage与几何入口，不写玩家存档、不更改正在运行的棋盘。
        /// 每局停止AI、监听按owner移除；断言失败也必须清理，不能留下影响下一轮的状态。
        /// </summary>
        public static string VerifyPieceAttackRules(Action<bool, string> check)
        {
            int assertions = 0;
            void Verify(bool ok, string message)
            {
                assertions++;
                check(ok, message);
            }
            foreach (PieceKind kind in Enum.GetValues(typeof(PieceKind)))
            {
                var battle = new BattleService(false, 1031);
                try
                {
                    battle.Chapter = 1;
                    battle.Round = 1;
                    battle.Phase = GamePhase.Battle;
                    Piece actor = battle.AddPieceAttackCheck(kind, GameSide.Player, new BoardPos(3, 3));
                    Piece center = battle.AddPieceAttackCheck(PieceKind.Shield, GameSide.Enemy, new BoardPos(4, 3));
                    Piece nearby = battle.AddPieceAttackCheck(PieceKind.Shield, GameSide.Enemy, new BoardPos(5, 3));
                    Piece outside = battle.AddPieceAttackCheck(PieceKind.Shield, GameSide.Enemy, new BoardPos(8, 8));
                    battle.RefreshGoState();
                    var hits = new List<Piece>(81);
                    PieceAttackRules.CollectHits(battle._board, actor, center, hits);
                    Verify(hits.Count >= 1 && hits.Count <= Balance.AttackProfile(kind).MaxTargets, kind + " 命中数量符合配置上限");
                    Verify(!hits.Contains(outside), kind + " 范围外目标未被卷入");
                    int before = center.Hp;
                    int outsideBefore = outside.Hp;
                    int total = 0;
                    object owner = new object();
                    RevEvent.AddEventListener<BattleHitInfo>(Events.BattleHit, hit =>
                    {
                        if (hit.SourceId == actor.Id)
                        {
                            total += hit.Damage;
                        }
                    }, owner: owner);
                    int sumBefore = center.Hp + nearby.Hp + outside.Hp;
                    try
                    {
                        battle.Attack(actor, center);
                        Verify(center.Hp < before, kind + " 正式伤害管线真实扣血");
                        Verify(outside.Hp == outsideBefore, kind + " 范围外生命保持不变");
                        Verify(total == sumBefore - center.Hp - nearby.Hp - outside.Hp, kind + " 伤害事件总量与真实扣血一致");
                        Verify(battle._distinctHits.Count == 0 && !battle._resolvingDistinctAttack, kind + " 出手后缓冲与重入标记已清理");
                        if (kind == PieceKind.Archer || kind == PieceKind.Assassin || kind == PieceKind.Swordman || kind == PieceKind.Priest)
                        {
                            Verify(hits.Count == 1, kind + " 单点攻击没有误伤第二格");
                        }
                        if (kind == PieceKind.MageFire)
                        {
                            Verify(hits.Count == 2, "火法3×3覆盖邻格");
                            int primary = battle.EvaluateDamage(new DamageRequest(center, DamageBasis.Attack, BattleDamageKind.Normal, actor, DistinctAttackFactor(actor, center, true))).Damage;
                            int secondary = battle.EvaluateDamage(new DamageRequest(nearby, DamageBasis.Attack, BattleDamageKind.Normal, actor, DistinctAttackFactor(actor, nearby, false))).Damage;
                            Verify(primary > secondary, "火法中心伤害高于外围");
                        }
                    }
                    finally
                    {
                        RevEvent.RemoveAllByOwner(owner);
                    }
                }
                finally
                {
                    battle.StopEnemyAI();
                    battle.StopPlayerAI();
                    battle._board.Reset();
                }
            }
            VerifyPieceAttackSpecialCases(Verify);
            var edgeBattle = new BattleService(false, 1031);
            try
            {
                BattleService battle = edgeBattle;
                Piece mage = battle.AddPieceAttackCheck(PieceKind.MageFire, GameSide.Player, new BoardPos(0, 0));
                Piece big = new Piece(battle._nextPieceId++, PieceCatalog.Get(PieceKind.Shield, StarTier.Three), GameSide.Enemy, 2, false, false, 100f);
                Verify(battle._board.Place(big, new BoardPos(1, 1)), "大棋通过正式占格入口放置");
                var hits = new List<Piece>(81);
                PieceAttackRules.CollectHits(battle._board, mage, big, hits);
                Verify(hits.Count == 1, "3×3爆破覆盖大棋多个身体格仍只命中一次");
                var coverage = new List<BoardPos>(81);
                MoveRules.GetAttackCoverage(battle._board, mage, coverage);
                foreach (BoardPos pos in coverage)
                {
                    Verify(pos.X >= 0 && pos.Y >= 0 && pos.X < battle._board.Size && pos.Y < battle._board.Size, "边角射界不越界");
                }
                // 预热后固定查询次数，仅测局部纯规则CPU与当前线程分配，不代表Unity帧率或GPU。
                for (int i = 0; i < 100; i++)
                {
                    PieceAttackRules.CollectHits(battle._board, mage, big, hits);
                }
                var watch = Stopwatch.StartNew();
                for (int i = 0; i < 10000; i++)
                {
                    PieceAttackRules.CollectHits(battle._board, mage, big, hits);
                }
                watch.Stop();
                return "攻击差异化断言=" + assertions + "；固定10000次几何查询=" + watch.Elapsed.TotalMilliseconds.ToString("F3") + "ms；仅局部CPU";
            }
            finally
            {
                edgeBattle.StopEnemyAI();
                edgeBattle.StopPlayerAI();
                edgeBattle._board.Reset();
            }
        }

        /// <summary>开发夹具通过Place登记占格和身份；放大生命只为了隔离一次攻击，生产图鉴保持不变。</summary>
        private Piece AddPieceAttackCheck(PieceKind kind, GameSide side, BoardPos position)
        {
            var piece = new Piece(_nextPieceId++, PieceCatalog.Get(kind, StarTier.One), side, 1, false, false, 100f);
            if (!_board.Place(piece, position))
            {
                throw new InvalidOperationException("差异化造局占格失败：" + position);
            }
            (side == GameSide.Player ? _playerPieces : _enemyPieces).Add(piece);
            return piece;
        }

        /// <summary>针对易出错的形状、治疗、收割及非法出手做独立后置断言，避免只验证“命令没有抛异常”。</summary>
        private static void VerifyPieceAttackSpecialCases(Action<bool, string> check)
        {
            var battle = new BattleService(false, 1031);
            try
            {
                Piece fire = battle.AddPieceAttackCheck(PieceKind.MageFire, GameSide.Player, new BoardPos(1, 4));
                Piece center = null;
                for (int y = 3; y <= 5; y++)
                {
                    for (int x = 3; x <= 5; x++)
                    {
                        Piece enemy = battle.AddPieceAttackCheck(PieceKind.Shield, GameSide.Enemy, new BoardPos(x, y));
                        if (x == 4 && y == 4)
                        {
                            center = enemy;
                        }
                    }
                }
                var hits = new List<Piece>(81);
                PieceAttackRules.CollectHits(battle._board, fire, center, hits);
                check(hits.Count == 9, "火法完整3×3九格均纳入且中心首结算");
                check(hits[0] == center, "爆破名单首位保持中心目标");
                check(PieceAttackRules.SelectPrimary(battle._board, fire, center) == center, "火法实时密度选敌选中九格阵中心");
                int hp = center.Hp;
                fire.TakeDamage(fire.Hp);
                battle.Attack(fire, center);
                check(center.Hp == hp, "死亡攻击者不会产生范围伤害");
                battle._board.Reset();
                battle._playerPieces.Clear();
                battle._enemyPieces.Clear();

                Piece crossbow = battle.AddPieceAttackCheck(PieceKind.Crossbow, GameSide.Player, new BoardPos(1, 1));
                Piece first = battle.AddPieceAttackCheck(PieceKind.Shield, GameSide.Enemy, new BoardPos(3, 1));
                Piece next = battle.AddPieceAttackCheck(PieceKind.Shield, GameSide.Enemy, new BoardPos(4, 1));
                Piece offLine = battle.AddPieceAttackCheck(PieceKind.Shield, GameSide.Enemy, new BoardPos(3, 2));
                PieceAttackRules.CollectHits(battle._board, crossbow, first, hits);
                check(hits.Count == 2 && hits.Contains(next) && !hits.Contains(offLine), "重弩沿一条直线穿透，不在首目标处拐弯");
                battle._board.Reset();
                battle._playerPieces.Clear();
                battle._enemyPieces.Clear();

                Piece priest = battle.AddPieceAttackCheck(PieceKind.Priest, GameSide.Player, new BoardPos(2, 2));
                Piece ally = battle.AddPieceAttackCheck(PieceKind.Shield, GameSide.Player, new BoardPos(3, 2));
                ally.TakeDamage(ally.Hp / 2);
                hp = ally.Hp;
                battle.ApplyPieceSupport(priest);
                check(ally.Hp > hp && ally.Hp <= ally.MaxHp, "牧师恢复受伤友军且不超过生命上限");
                ally.TakeDamage(ally.Hp);
                battle.ApplyPieceSupport(priest);
                check(!ally.IsAlive, "牧师不会复活死棋");
                battle._board.Reset();
                battle._playerPieces.Clear();
                battle._enemyPieces.Clear();

                Piece assassin = battle.AddPieceAttackCheck(PieceKind.Assassin, GameSide.Player, new BoardPos(2, 2));
                Piece healthy = battle.AddPieceAttackCheck(PieceKind.Shield, GameSide.Enemy, new BoardPos(3, 2));
                Piece wounded = battle.AddPieceAttackCheck(PieceKind.Shield, GameSide.Enemy, new BoardPos(4, 2));
                wounded.TakeDamage(wounded.Hp * 9 / 10);
                check(PieceAttackRules.SelectPrimary(battle._board, assassin, healthy) == wounded, "刺客优先低生命比例目标而非只选最近");
                check(DistinctAttackFactor(assassin, wounded, true) > DistinctAttackFactor(assassin, healthy, true), "刺客收割倍率在配置血线下生效");
                hp = wounded.Hp;
                battle.PredictAttackDefeat(assassin, wounded);
                check(wounded.Hp == hp, "AI伤害预测不提前扣血");
            }
            finally
            {
                battle.StopEnemyAI();
                battle.StopPlayerAI();
                battle._board.Reset();
            }
        }
    }
}
#endif
