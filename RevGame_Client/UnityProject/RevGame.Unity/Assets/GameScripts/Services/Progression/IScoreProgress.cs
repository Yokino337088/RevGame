namespace RevGame.Services
{
    /// <summary>积分持久化契约。战斗模型只提交递增总量；内存测试可用替身，不依赖Unity磁盘或玩家存档。</summary>
    public interface IScoreProgress
    {
        /// <summary>已记录的永久累计分数，范围0～long.MaxValue，花费战斗能源不降低它。</summary>
        long Total { get; }
        /// <summary>记录新的累计总量；生产存储可先标脏，再按自身节流策略刷盘。</summary>
        void Record(long total);
        /// <summary>结算、退场或切后台时请求持久化；内存测试替身无需写文件。</summary>
        void Flush();
    }
}
