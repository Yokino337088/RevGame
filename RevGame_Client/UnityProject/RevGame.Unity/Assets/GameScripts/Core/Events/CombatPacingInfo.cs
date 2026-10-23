// 战斗节奏只传不可变快照。DOTween可以中途取消，伤害始终由固定模拟时钟结算，不能在动画回调里扣血。
namespace RevGame.Core
{
    /// <summary>区域波动一次施放的固定矩形；左下角、边长、预警秒数和逐格间隔由服务锁定。</summary>
    public readonly struct RegionPulseInfo
    {
        public readonly BoardPos Origin;
        public readonly int Size;
        public readonly float WarningSeconds;
        public readonly float CellDelaySeconds;
        public RegionPulseInfo(BoardPos origin, int size, float warningSeconds, float cellDelaySeconds)
        {
            Origin = origin; Size = size; WarningSeconds = warningSeconds; CellDelaySeconds = cellDelaySeconds;
        }
        /// <summary>按自下而上、从左到右逐格传播。范围外返回-1，禁止对不存在的格子排动画。</summary>
        public int CellIndex(BoardPos pos)
        {
            int x = pos.X - Origin.X, y = pos.Y - Origin.Y;
            return x < 0 || y < 0 || x >= Size || y >= Size ? -1 : y * Size + x;
        }
    }

    /// <summary>能量炸弹落点和蓄力秒数；命中事件单独广播，震屏/声音/手机振动只在命中时触发。</summary>
    public readonly struct EnergyBombInfo
    {
        public readonly BoardPos Position;
        public readonly float WarningSeconds;
        public EnergyBombInfo(BoardPos position, float warningSeconds)
        {
            Position = position; WarningSeconds = warningSeconds;
        }
    }
}
