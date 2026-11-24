using System;

namespace RevGame.Pvp.Duel
{
    /// <summary>决斗角色编号供 LAN、Fantasy 与录像使用。可上场的阵容由准备流程校验。</summary>
    public enum DuelRole
    {
        // 旧录像及原生模拟仍用这个编号还原历史规则。
        Ember = 1,
        // 新决斗两席共用的角色。
        Yaya = 2
    }

    /// <summary>权威准备阶段。仅 Released 允许激活场景，随后仍要等原有场景 Ready 屏障。</summary>
    public enum DuelPrepPhase
    {
        Waiting = 1,
        Choosing,
        Loading,
        Released,
        Cancelled
    }

    /// <summary>由权威复制的准备画面数据。毫秒只供本机插值显示，不用于本机裁决。</summary>
    [Serializable]
    public sealed class DuelPrepState
    {
        // 变更序号，防止旧 RPC 回复覆盖选择、锁定或进度。
        public int revision;
        // 当前阶段，客户端不得自行推进。
        public DuelPrepPhase phase;
        // 黑方的权威角色编号，新局固定为鸦鸦。
        public DuelRole black;
        // 白方的权威角色编号，新局固定为鸦鸦。
        public DuelRole white;
        // 低两位按黑白顺序表示已经锁定。
        public int locked;
        // 两席已完成的真实加载百分比，范围 0 到 100。
        public int blackLoad;
        public int whiteLoad;
        // 选角剩余毫秒，其他阶段为 0。
        public int remainingMs;

        // 以棋局既有的黑 1、白 2 座位编号取角色。
        public DuelRole Role(int side)
        {
            return side == 1 ? black : white;
        }

        // 以棋局座位编号取对应该客户端的加载进度。
        public int Progress(int side)
        {
            return side == 1 ? blackLoad : whiteLoad;
        }
    }

    /// <summary>与封帧器同线程的准备规则。仅保存本房间两席的意图、锁定、进度和单调时间。</summary>
    internal sealed class DuelPreparation
    {
        // 连接、面板或磁盘故障的操作超时，保证退出时不会长期占住 Unity 场景队列。
        internal const int StageTimeoutMs = 45000;
        // 低两位为两席标记；固定协议结构，不随策划配置变化。
        private const int BothSeats = 3;
        // 这局的可见状态。返回快照时复制，网络不得持有可变原件。
        private readonly DuelPrepState _state = new DuelPrepState
        {
            revision = 1, phase = DuelPrepPhase.Waiting, black = DuelRole.Yaya, white = DuelRole.Yaya
        };
        // 本地电脑席为 1 或 2；网络局没有虚拟席。
        private readonly int _computer;
        // 选角面板已真正显示的两席掩码。
        private int _present;
        // 当前阶段的单调毫秒截止点。
        private long _deadline;

        // 本机电脑使用默认角色，并代替不存在的第二份 Unity UI 报告就绪。
        internal DuelPreparation(long now, int computerSide)
        {
            _computer = computerSide;
            _deadline = now + StageTimeoutMs;
            if (computerSide is 1 or 2)
            {
                _present = _state.locked = 1 << (computerSide - 1);
            }
        }

        // 两份面板就绪后开始确认倒计时，超时由权威以鸦鸦锁定仍未确认的席位。
        internal void Tick(long now)
        {
            if (_state.phase == DuelPrepPhase.Waiting && _present == BothSeats)
            {
                Change(DuelPrepPhase.Choosing, now + DuelConfig.PickDurationMs);
            }
            if (_state.phase == DuelPrepPhase.Choosing && (_state.locked == BothSeats || now >= _deadline))
            {
                _state.locked = BothSeats;
                if (_computer == 1)
                {
                    _state.blackLoad = 100;
                }
                if (_computer == 2)
                {
                    _state.whiteLoad = 100;
                }
                Change(DuelPrepPhase.Loading, now + StageTimeoutMs);
            }
            if (_state.phase == DuelPrepPhase.Loading && _state.blackLoad == 100 && _state.whiteLoad == 100)
            {
                Change(DuelPrepPhase.Released, 0);
            }
            if ((_state.phase == DuelPrepPhase.Waiting || _state.phase == DuelPrepPhase.Loading) && now >= _deadline)
            {
                Change(DuelPrepPhase.Cancelled, 0);
            }
        }

        // 接收认证席位的操作。只有确认鸦鸦可以锁席，重复确认和退步的加载进度均不写入。
        internal void Apply(int side, int operation, int value, long now)
        {
            Tick(now);
            int bit = 1 << (side - 1);
            if (operation == DuelAuthority.PrepAbort && _state.phase < DuelPrepPhase.Released)
            {
                Change(DuelPrepPhase.Cancelled, 0);
            }
            else if (operation == DuelAuthority.PrepVisible && _state.phase == DuelPrepPhase.Waiting)
            {
                _present |= bit;
            }
            else if (operation == DuelAuthority.PrepLock && _state.phase == DuelPrepPhase.Choosing &&
                (_state.locked & bit) == 0 && CanSelectRole((DuelRole)value))
            {
                _state.locked |= bit;
                _state.revision++;
            }
            else if (operation == DuelAuthority.PrepLoad && _state.phase == DuelPrepPhase.Loading &&
                value <= 100 && value > _state.Progress(side))
            {
                if (side == 1)
                {
                    _state.blackLoad = value;
                }
                else
                {
                    _state.whiteLoad = value;
                }
                _state.revision++;
            }
            Tick(now);
        }

        // 向客户端交付不可串改的值副本；只在选角期间发送剩余时间。
        internal DuelPrepState Read(long now)
        {
            return new DuelPrepState
            {
                revision = _state.revision, phase = _state.phase, black = _state.black, white = _state.white,
                locked = _state.locked, blackLoad = _state.blackLoad, whiteLoad = _state.whiteLoad,
                remainingMs = _state.phase == DuelPrepPhase.Choosing ? (int)Math.Max(0, _deadline - now) : 0
            };
        }

        // 席位推进和进度修改都递增同一个序号，便于可靠传输与 RPC 使用同一契约。
        private void Change(DuelPrepPhase phase, long deadline)
        {
            _state.phase = phase;
            _state.revision++;
            _deadline = deadline;
        }

        // 当前阵容只允许鸦鸦确认参战。旧客户端发送的其它编号不会锁席。
        internal static bool CanSelectRole(DuelRole role)
        {
            return role == DuelRole.Yaya;
        }

        // 录像解码和原生模拟仍识别历史角色编号，以原有规则重放旧数据。
        internal static bool ValidRole(DuelRole role)
        {
            return role == DuelRole.Ember || role == DuelRole.Yaya;
        }
    }
}
