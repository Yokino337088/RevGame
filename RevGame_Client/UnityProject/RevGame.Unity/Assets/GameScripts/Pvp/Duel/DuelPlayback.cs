using System;

namespace RevGame.Pvp.Duel
{
    // 只保存连续执行后的只读快照。服务写入，场景按自己的播放时钟采样，不影响原生或输入。
    internal sealed class DuelPlayback
    {
        // 保留最近 32 帧供插值和取表现事件，旧内容可由新快照覆盖。
        private const int Capacity = 32;
        // 联机在连续上界后约一帧半绘制远端，给下一张快照留到达时间。
        private const double NetworkDelayFrames = 1.5;
        // 落后超过六帧时跳过过期画面。权威事件和逻辑历史已在服务中逐帧消费。
        private const double MaxDisplayLagFrames = 6;
        // 按逻辑帧号取模访问。有效窗口由最新连续帧决定，退出时只需复位游标。
        private readonly DuelSnapshot[] _snapshots = new DuelSnapshot[Capacity];
        // 已记录的最后一个逻辑帧。-1 表示未创建决斗。
        private int _latest = -1;
        // 当前播放时刻，小数部分用于画面插值，与网络补帧游标独立。
        private double _cursor;
        // 第一次采样直接对准最近可显示位置，避免进场或重连重播积压。
        private bool _started;

        // 原生成功执行一整帧后写入其值副本；开局也记录帧零以支持首帧插值。
        public void Record(DuelSnapshot snapshot)
        {
            _snapshots[snapshot.Frame % Capacity] = snapshot;
            _latest = snapshot.Frame;
        }

        // 丢弃上一场的播放窗口，原生规则和回放记录有独立的所有者。
        public void Reset()
        {
            _latest = -1;
            _cursor = 0;
            _started = false;
        }

        // 取得仍在环内的表现帧。调用者必须保证帧号在最近 32 帧内，过期槽位已被新帧覆盖。
        public DuelSnapshot Get(int frame)
        {
            return _snapshots[frame % Capacity];
        }

        // 根据本地秒数推进小数帧，在两张已执行快照之间采样。缺新帧时最多走到最后一张。
        public bool Sample(float deltaSeconds, bool networked, out DuelSnapshot current, out DuelSnapshot next, out float fraction)
        {
            current = next = default;
            fraction = 0;
            if (_latest < 0)
            {
                return false;
            }

            double delay = networked ? NetworkDelayFrames : 1;
            int earliest = Math.Max(0, _latest - Capacity + 1);
            if (!_started || _latest - _cursor > MaxDisplayLagFrames)
            {
                _cursor = Math.Max(earliest, _latest - delay);
                _started = true;
            }
            else
            {
                // 小幅加减速吸收网络抖动。终局时恢复正常速度，播完最后一段再显示结果。
                double rate = Get(_latest).Result == 0
                    ? 1 + Math.Max(-.10, Math.Min(.20, (_latest - _cursor - delay) * .12))
                    : 1;
                _cursor = Math.Min(_latest, _cursor + Math.Max(0, deltaSeconds) * 1000 / DuelConfig.FrameMs * rate);
            }
            int frame = (int)_cursor;
            // 对手使用两张已执行的快照。到连续上界时 next 等于 current，不用当前方向猜未来位置。
            current = Get(frame);
            next = Get(Math.Min(frame + 1, _latest));
            fraction = (float)(_cursor - frame);
            return true;
        }
    }
}
