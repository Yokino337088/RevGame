// ============================================================
// BattleService.Score.cs —— 积分先在真实命中时结算，HUD与飞星只显示。
// 高频倍率查询按实测人数阈值选择路径；缓存必须覆盖名单、生死、章回与配置，避免画面快了但分数错了。
// ============================================================
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
        // 名单/生死版本决定人数，章回和配置决定倍率。死亡但尚未Remove也会递增TargetVersion，不能漏扣人数。
        // 使用服务自有值缓存，不保存Piece引用；隐藏也可能让TargetVersion变化，保守重算不会漏规则。
        private int _scoreTopology = -1, _scoreTargets = -1, _scorePlayerCount = -1, _scoreAlive;
        // 章回不改变占格但改变积分成长，公式配置也作为收据，不能仅缓存“活棋数→倍率”。
        private int _scoreShownChapter = -1, _scoreShownRound = -1, _scoreRoundsPerChapter;
        // Cap为倍率上限，Growth无量纲；全从Excel读取，缓存只保存上次输入，不写死游戏成长参数。
        private float _scoreCap, _scoreRoundGrowth, _scorePieceGrowth, _scoreMultiplier;
        // 2026-10-30：相同生产属性、1～81棋、7轮中位数；.NET两轮及Unity自带Mono独立宿主两轮均从1棋获益。
        // 因而当前测得的交叉点为1（不是猜测8/16/32）。0棋直接计算，>=1棋核对版本后复用。
        // 注意这是稳定盘面重复查询的阈值，非目标笔记本/Editor实测；每次失效时缓存更贵，详见当天报告。
        // 缓存是服务自有值字段，没有字典。以后改公式/运行后端须复测，不能照搬此阈值到射界/战报。
        internal const int ScoreCacheThreshold = 1;
#if UNITY_EDITOR || REVGAME_OFFLINE_TESTS
        // 仅测试强制路径：0全缓存、82全扫描；默认-1按正式阈值，不在发行版本提供开关。
        internal int ScoreCacheThresholdOverride = -1;
#endif
        /// <summary>人数扫描累计次数；启用缓存时稳定查询不增加，直接路径每次扫描，死亡/复活/移除后重算。</summary>
        public long ScorePopulationScans { get; private set; }
        /// <summary>完整公式输入相同时复用倍率的累计次数，供Editor采样差值核对。</summary>
        public long ScoreMultiplierHits { get; private set; }
        /// <summary>依据当前存活棋数即时计算；后续死亡会降低棋数部分，章节进度部分继续递增。</summary>
        public float ScoreMultiplier
        {
            get
            {
                int threshold = ScoreCacheThreshold;
#if UNITY_EDITOR || REVGAME_OFFLINE_TESTS
                if (ScoreCacheThresholdOverride >= 0) { threshold = ScoreCacheThresholdOverride; }
#endif
                if (_playerPieces.Count < threshold) { return CalculateScoreMultiplierDirect(); }
                return ReadScoreMultiplierCached();
            }
        }

        /// <summary>小名单直接扫描，保持原公式。没有缓存收据检查的成本；不会沿用大名单时缓存的旧人数。</summary>
        private float CalculateScoreMultiplierDirect()
        {
            int alive = 0;
            foreach (Piece piece in _playerPieces) { if (piece.IsAlive) { alive++; } }
            ScorePopulationScans++;
            int progress = Math.Max(0, (Chapter - 1) * Balance.RoundsPerChapter + Round - 1);
            return Math.Min(Balance.ScoreMultiplierCap, (1f + progress * Balance.ScoreRoundGrowth)
                * (1f + Math.Max(0, alive - 1) * Balance.ScorePieceGrowth));
        }

        /// <summary>大名单复用版本收据。回到大名单时必须核对占格/生死/章回/配置，不能直接用切换前的结果。</summary>
        private float ReadScoreMultiplierCached()
        {
            bool populationChanged = _scoreTopology != _board.TopologyVersion || _scoreTargets != _board.TargetVersion || _scorePlayerCount != _playerPieces.Count;
            // 步骤1：任何成员/生死变化才扫描；普通非致死扣血不改变人数，反复得分不扫描整名单。
            if (populationChanged)
            {
                _scoreAlive = 0;
                foreach (Piece piece in _playerPieces) { if (piece.IsAlive) { _scoreAlive++; } }
                _scoreTopology = _board.TopologyVersion; _scoreTargets = _board.TargetVersion; _scorePlayerCount = _playerPieces.Count;
                ScorePopulationScans++;
            }
            // 配置仍从Excel读取。把影响公式的值也作为键，改表后不能命中旧倍率。
            int rounds = Balance.RoundsPerChapter;
            float cap = Balance.ScoreMultiplierCap, roundGrowth = Balance.ScoreRoundGrowth, pieceGrowth = Balance.ScorePieceGrowth;
            if (!populationChanged && _scoreShownChapter == Chapter && _scoreShownRound == Round && _scoreRoundsPerChapter == rounds
                && _scoreCap == cap && _scoreRoundGrowth == roundGrowth && _scorePieceGrowth == pieceGrowth)
            { ScoreMultiplierHits++; return _scoreMultiplier; }
            int progress = Math.Max(0, (Chapter - 1) * Balance.RoundsPerChapter + Round - 1);
            // 步骤2：只在公式输入变化时重算；先得完整人数，再乘章回与棋数成长，原浮点顺序保持。
            _scoreShownChapter = Chapter; _scoreShownRound = Round; _scoreRoundsPerChapter = rounds;
            _scoreCap = cap; _scoreRoundGrowth = roundGrowth; _scorePieceGrowth = pieceGrowth;
            _scoreMultiplier = Math.Min(cap, (1f + progress * roundGrowth) * (1f + Math.Max(0, _scoreAlive - 1) * pieceGrowth));
            return _scoreMultiplier;
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
