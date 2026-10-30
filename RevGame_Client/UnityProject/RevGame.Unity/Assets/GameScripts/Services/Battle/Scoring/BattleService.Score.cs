using System;
using RevGame.Core;
using RevGame.Data;
using RevGame.Pieces;
using Revolution;

namespace RevGame.Services
{
    /// <summary>积分属于战斗服务；HUD只读，飞行特效只是反馈，退出面板不会吞分或重复加分。</summary>
    public sealed partial class BattleService
    {
        /// <summary>启动宿主拥有的永久存储；服务不释放它。纯规则测试允许为null，杜绝改写真实存档。</summary>
        private readonly IScoreProgress _scoreProgress;
        /// <summary>本回合分数，成功进入新回合时清零；准备失败时保留旧值。</summary>
        public long RoundScore { get; private set; }
        /// <summary>当前章累计积分，同章回合继承；换章、失败重开和新战役清零，不等于永久总积分。</summary>
        public long ChapterScore { get; private set; }
        /// <summary>本设备永久累计总量，重新挑战/退出大厅均保留；取值0～long.MaxValue。</summary>
        public long LifetimeScore { get; private set; }
        /// <summary>依据当前存活棋数即时计算；后续死亡会降低棋数部分，章节进度部分继续递增。</summary>
        public float ScoreMultiplier
        {
            get
            {
                int alive = 0;
                foreach (Piece piece in _playerPieces) { if (piece.IsAlive) { alive++; } }
                int progress = Math.Max(0, (Chapter - 1) * Balance.RoundsPerChapter + Round - 1);
                return Math.Min(Balance.ScoreMultiplierCap, (1f + progress * Balance.ScoreRoundGrowth)
                    * (1f + Math.Max(0, alive - 1) * Balance.ScorePieceGrowth));
            }
        }

        /// <summary>
        /// 唯一积分入口：必须已有真实扣血，敌方目标只在本次由活变死时给击杀分。
        /// 区域波动是中立伤害，不能冒充玩家击杀；能量炸弹/炮火/断链是友方支援，可以计击杀分。
        /// 基础积分与倍率均来自Excel。long饱和相加，长时间游玩不会溢出成负数。
        /// </summary>
        private void AwardHitScore(Piece target, Piece attacker, BattleDamageKind kind, int real)
        {
            if (Phase != GamePhase.Battle || real <= 0 || target.Side != GameSide.Enemy) { return; }
            bool friendly = attacker != null && attacker.Side == GameSide.Player;
            bool critical = friendly && kind == BattleDamageKind.Critical;
            bool kill = !target.IsAlive && (friendly || kind == BattleDamageKind.EnergyBomb
                || kind == BattleDamageKind.Artillery || kind == BattleDamageKind.Execution);
            // 普攻、暴击与击杀合并一组飞星，避免同一击重复广播入账。
            bool basic = friendly && (kind == BattleDamageKind.Normal || kind == BattleDamageKind.Critical);
            int basis = (kill ? Balance.ScoreKillPoints : 0) + (critical ? Balance.ScoreCriticalPoints : 0)
                + (basic ? Balance.HelperPlayerHitScore : 0);
            if (basis <= 0) { return; }
            float multiplier = ScoreMultiplier;
            long added = Math.Max(1L, (long)Math.Round(basis * (double)multiplier, MidpointRounding.AwayFromZero));
            RoundScore = AddScore(RoundScore, added);
            ChapterScore = AddScore(ChapterScore, added);
            LifetimeScore = AddScore(LifetimeScore, added);
            CombatScore = AddScore(CombatScore, added);
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (!_scorePreview)
#endif
            {
                _scoreProgress?.Record(LifetimeScore);
            }
            RevEvent.DispatchEvent<ScoreGrantedInfo>(Events.ScoreGranted,
                new ScoreGrantedInfo(target.Pos, target.Size, added, RoundScore, LifetimeScore, multiplier, kill, critical));
        }

        private static long AddScore(long total, long added) { return total > long.MaxValue - added ? long.MaxValue : total + added; }

        /// <summary>回合结束、退战斗域立即刷盘；实现可为空，使离线规则测试完全不碰玩家存档。</summary>
        public void FlushScore() { _scoreProgress?.Flush(); }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        // 开发测试积分隔离开关，真实规则仍计算分数，但预览期间不写玩家存档。
        // 只能由取得ScorePreview租约的所有者还原；嵌套租约会同时恢复两份不同积分，故明确拒绝。
        private bool _scorePreview;
        /// <summary>开发态只读隔离标记，测试复用已有租约，不允许嵌套或自行撤掉启动流程拥有的隔离。</summary>
        // 测试夹具先检查启动流程是否已拥有隔离，已有时借其保护但不Dispose它，避免误解除别人的租约。
        public bool IsScorePreviewActive => _scorePreview;
        /// <summary>自动化视觉预览的隔离区：保存内存积分并停止写存档，Dispose必须放在finally里。</summary>
        public IDisposable BeginScorePreview()
        {
            if (_scorePreview) { throw new InvalidOperationException("积分预览不可嵌套"); }
            return new ScorePreview(this);
        }
        private sealed class ScorePreview : IDisposable
        {
            private BattleService _service;
            private readonly long _round, _total, _chapter, _wallet;
            public ScorePreview(BattleService service)
            {
                _service = service; _round = service.RoundScore; _total = service.LifetimeScore; _chapter = service.ChapterScore; _wallet = service.CombatScore; service._scorePreview = true;
            }
            public void Dispose()
            {
                if (_service == null) { return; }
                _service.RoundScore = _round; _service.LifetimeScore = _total; _service.ChapterScore = _chapter; _service.CombatScore = _wallet; _service._scorePreview = false; _service = null;
            }
        }
#endif
    }
}
