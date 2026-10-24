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
        /// <summary>通过真实扣血出口验证归属、去重、清零和永久累计；存储替身仅在内存，不污染玩家存档。</summary>
        public static void VerifyScoreRules(Action<bool, string> check)
        {
            var memory = new ScoreMemory();
            var service = new BattleService(false, 381, memory);
            check(service.StartRound(1, 1), "积分夹具开局");
            service.StopEnemyAI(); service._board.Reset(); service._playerPieces.Clear(); service._enemyPieces.Clear();
            var player = new Piece(9001, PieceCatalog.Get(PieceKind.Archer, StarTier.One), GameSide.Player);
            var enemy = new Piece(9002, PieceCatalog.Get(PieceKind.Shield, StarTier.One), GameSide.Enemy);
            service._board.Place(player, new BoardPos(0, 0)); service._playerPieces.Add(player);
            service._board.Place(enemy, new BoardPos(5, 5)); service._enemyPieces.Add(enemy);
            service.Phase = GamePhase.Battle;
            object owner = new object(); int awards = 0; BattleHitInfo observed = default(BattleHitInfo);
            RevEvent.AddEventListener<ScoreGrantedInfo>(Events.ScoreGranted, info => awards++, owner: owner);
            RevEvent.AddEventListener<BattleHitInfo>(Events.BattleHit, hit => observed = hit, owner: owner);
            try
            {
                long before = service.LifetimeScore;
                service.DamagePiece(enemy, 0, player, damageKind: BattleDamageKind.Critical);
                check(service.LifetimeScore == before && awards == 0, "0伤害暴击不给分");
                service.DamagePiece(enemy, 1, player, damageKind: BattleDamageKind.Critical);
                check(service.RoundScore == Balance.ScoreCriticalPoints && awards == 1, "真实暴击只发一次暴击分");
                check(observed.DamageKind == BattleDamageKind.Critical && observed.Damage == 1, "暴击快照携带真实扣血及来源");
                service.DamagePiece(enemy, int.MaxValue, player, damageKind: BattleDamageKind.Critical);
                long expected = Balance.ScoreKillPoints + Balance.ScoreCriticalPoints * 2L;
                check(service.RoundScore == expected && awards == 2, "致死暴击同时给击杀和暴击分但只发一组飞星");
                service.DamagePiece(enemy, int.MaxValue, player, damageKind: BattleDamageKind.Critical);
                check(service.RoundScore == expected && awards == 2, "多段技能重复命中死亡棋不重复计分");
                service.FlushScore();
                check(memory.Total == expected && memory.FlushCount > 0, "总分存储收到真实入账与刷盘");
                var resumed = new BattleService(false, 381, memory);
                check(resumed.LifetimeScore == expected && resumed.RoundScore == 0, "重建服务恢复永久积分，回合积分归零");
                check(service.StartRound(1, 2) && service.RoundScore == 0 && service.LifetimeScore == expected, "换回合只清本回合积分");
                check(service.ScoreMultiplier > 1f, "后续回合倍率递增");
                service.StopEnemyAI(); service.Phase = GamePhase.Battle;
                Piece victim = service._enemyPieces[0];
                long old = service.LifetimeScore;
                service.DamagePiece(victim, int.MaxValue, damageKind: BattleDamageKind.RegionPulse, fromSkill: true);
                check(service.LifetimeScore == old && observed.DamageKind == BattleDamageKind.RegionPulse, "中立波动不算玩家击杀，伤害有独立来源");
                victim = service._enemyPieces.Find(p => p.IsAlive);
                service.DamagePiece(victim, int.MaxValue, damageKind: BattleDamageKind.EnergyBomb, fromSkill: true);
                check(service.LifetimeScore > old && observed.DamageKind == BattleDamageKind.EnergyBomb, "友方能量炸弹击杀计分且来源独立");
                check(AddScore(long.MaxValue - 1, 100) == long.MaxValue, "永久积分饱和相加不回绕");
                using (service.BeginScorePreview())
                {
                    long stored = memory.Total;
                    victim = service._enemyPieces.Find(p => p.IsAlive);
                    service.DamagePiece(victim, int.MaxValue, player);
                    check(memory.Total == stored, "视觉验收隔离不写永久存档");
                }
                // 真实普攻抽样测试，不给随机逻辑偷偷开100%暴击；固定种子保证可重复。
                check(service.StartRound(1, 1), "重开挑战仍保留永久积分");
                service.StopEnemyAI(); service.Phase = GamePhase.Battle;
                int critical = 0, normal = 0;
                RevEvent.AddEventListener<BattleHitInfo>(Events.BattleHit, hit => { if (hit.DamageKind == BattleDamageKind.Critical) { critical++; } else { normal++; } }, owner: owner);
                victim = service._enemyPieces[0];
                for (int i = 0; i < 100; i++) { victim.Heal(victim.MaxHp); service.Attack(player, victim); }
                check(critical > 0 && normal > 0, "固定种子真实普攻既有暴击又有普通命中");
            }
            finally { RevEvent.RemoveAllByOwner(owner); service.StopEnemyAI(); }
        }

        private sealed class ScoreMemory : IScoreProgress
        {
            public long Total { get; private set; }
            public int FlushCount;
            public void Record(long total) { Total = total; }
            public void Flush() { FlushCount++; }
        }

        /// <summary>仅用于本次Play视觉夹具：调用真实统一扣血，目标仍来自当前占格。不得给正式包开放任意加分。</summary>
        public void PreviewScoreHit(BattleDamageKind kind, bool lethal)
        {
            if (!_scorePreview) { throw new InvalidOperationException("必须在积分预览隔离区调用"); }
            Piece player = _playerPieces.Find(p => p.IsAlive), enemy = _enemyPieces.Find(p => p.IsAlive);
            if (player == null || enemy == null) { throw new InvalidOperationException("预览需要双方存活棋"); }
            DamagePiece(enemy, lethal ? int.MaxValue : 7, player, damageKind: kind, fromSkill: kind != BattleDamageKind.Critical);
            RefreshGoState();
        }
    }
}
#endif
