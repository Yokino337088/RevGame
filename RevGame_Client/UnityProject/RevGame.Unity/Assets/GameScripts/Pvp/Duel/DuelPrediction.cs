using System;

namespace RevGame.Pvp.Duel
{
    // 每局本机一个预测副本，由 PvP 服务在 Unity 主线程持有。完整检查点只从已确认副本流向这个沙盒。
    internal sealed class DuelPrediction : IDisposable
    {
        // 50 毫秒步长下最多提前四帧。远端长期静默时不能让本机角色无限向前跑。
        internal const int MaxLead = 4;
        // 日志上限高于可靠 RPC 在途窗口，覆盖暂时积压的输入。溢出时等待权威确认后再打开预测。
        private const int Capacity = 24;
        // 输入日志与最近一次预测轨迹由本实例复用，不为每个渲染帧申请数组。
        private readonly PendingInput[] _pending = new PendingInput[Capacity];
        // 第 0 个槽是已确认锚点，其余是同一原生算法试算出的未来帧。
        private readonly DuelSnapshot[] _path = new DuelSnapshot[MaxLead + 1];
        // 锚点完整状态。只有前 _checkpointBytes 字节有效，显示快照无法替代它。
        private readonly byte[] _checkpoint = new byte[DuelCorePipe.MaxCheckpointBytes];
        // 沙盒原生句柄，和服务持有的权威回放句柄互不共享状态。
        private readonly DuelCorePipe _sandbox;
        // 本端控制的座位，决定从整帧的高五位还是低五位读取本地坐标。
        private readonly bool _black;
        // 当前日志中的元素数量，有效区间为 0 到 _count - 1。
        private int _count;
        // 最近被挤出日志的输入序号。回执覆盖它以前，无法证明重演材料齐备。
        private int _lostThrough;
        // 可恢复的检查点长度。0 表示正在等待开局、补帧或重连完成。
        private int _checkpointBytes;
        // 最近接受的权威锚点。暂停时也记住它的帧号，重复回执不能把旧轨迹重新激活。
        private DuelSnapshot _anchor;
        // 远端最后封口的方向作为短时猜测。远端的新跳跃和技能只从权威历史获知。
        private int _otherDirection;
        // 上次回执后仍在缓冲中的招式，与未来尚未确认的输入分开存。
        private DuelInputMailbox _confirmedMailbox;
        // 绝对播放时刻，整数为逻辑帧，小数供显示插值。回滚后不因新包重置到帧首。
        private double _cursor;
        // 路径起点，用于在回滚前找同一帧的预测样本。
        private int _pathStart = -1;
        // 上次预览的单席输入字，-1 表示还没有预览。重复采样相同输入无需重跑 C++。
        private int _preview = -1;
        // 尚未上行的意图在本地预计进入的帧，发送后记进日志，避免从头再播一次。
        private int _previewFrame;
        // 锚点、日志或预览变化才重演，平时直接从路径取样。
        private bool _dirty = true;

        // 日志中的一个已送出意图。预计帧只用于预测，真实落帧由权威时钟决定。
        private struct PendingInput
        {
            // 当前连接内的发送序号，用于权威回执后的裁剪与去重。
            public int Sequence;
            // 本地预览时估计它进入的帧，若锚点已超过则顺延到锚点下一帧。
            public int Frame;
            // 方向、跳跃、招式按 DuelCorePipe 的五位布局组合。
            public int Packed;
        }

        // 已确认帧与同号预测的完整指纹不一致次数，包含远端猜测不同造成的回滚。
        public int RollbackCount { get; private set; }
        // 本机角色的位置在同一帧出现差异的次数，显示端用版本变化触发限时纠偏。
        public int MotionCorrections { get; private set; }
        // 当前日志长度，供开发态对照回执是否真正裁剪了上行输入。
        public int PendingCount => _count;
        // 最近用于回滚的权威帧。未取得连续基线时返回 0。
        public int AnchorFrame => _checkpointBytes == 0 ? 0 : _anchor.Frame;

        // 用相同局号初始化独立原生世界。只有联机客户端创建，离开决斗时释放。
        public DuelPrediction(string id, bool black)
        {
            _sandbox = new DuelCorePipe(id);
            _black = black;
        }

        // 可靠队列接纳后保存待确认意图。仅上行成功仍不能删日志，必须等已封帧的回执。
        public void RecordSent(int sequence, int packed)
        {
            if (_count == Capacity)
            {
                _lostThrough = _pending[0].Sequence;
                Array.Copy(_pending, 1, _pending, 0, --_count);
            }
            // 已预览的点按沿用显示时的预计帧。持续方向采样用当前播放头估计，避免所有心跳挤到旧帧。
            bool previewedButton = _preview == packed && (packed & ~3) != 0;
            int next = Math.Max(_anchor.Frame + 1, previewedButton ? _previewFrame : (int)Math.Ceiling(_cursor));
            if (_count > 0)
            {
                // 同一批意图可能进入同一个预计帧，仍按接纳顺序还原邮箱覆盖和点按语义。
                next = Math.Max(next, _pending[_count - 1].Frame);
            }
            _pending[_count++] = new PendingInput { Sequence = sequence, Frame = next, Packed = packed };
            _dirty = true;
        }

        // 将沙盒基线推进到已消费的权威回执帧。先比同号帧，再裁剪输入，最后保存完整状态供下一次重演。
        public bool Rebase(DuelCorePipe authoritative, DuelReply reply, int packed)
        {
            DuelSnapshot confirmed = authoritative.Snapshot;
            if (reply.inputAckFrame <= _anchor.Frame || reply.inputAckFrame != confirmed.Frame)
            {
                return false;
            }
            if (confirmed.Frame > int.MaxValue - MaxLead || reply.inputAckSequence < 0 ||
                reply.pendingActionSequence < 0 || reply.pendingActionSequence > reply.inputAckSequence ||
                (reply.pendingActionSequence != 0 && reply.pendingActionUntil < confirmed.Frame))
            {
                throw new InvalidOperationException("决斗预测回执的序号、到期帧或可预测范围无效。");
            }

            int previousIndex = confirmed.Frame - _pathStart;
            if (_pathStart >= 0 && previousIndex > 0 && previousIndex <= MaxLead)
            {
                DuelSnapshot predicted = _path[previousIndex];
                if (predicted.Hash != confirmed.Hash)
                {
                    RollbackCount++;
                    if (LocalX(predicted) != LocalX(confirmed) || LocalY(predicted) != LocalY(confirmed))
                    {
                        MotionCorrections++;
                    }
                }
            }

            // 已确认的方向和跳跃不再执行。只恢复权威还明确保留的那次招式，且保留原到期帧。
            _confirmedMailbox = default;
            int retained = 0;
            for (int i = 0; i < _count; i++)
            {
                PendingInput input = _pending[i];
                if (input.Sequence > reply.inputAckSequence)
                {
                    _pending[retained++] = input;
                }
                else if (input.Sequence == reply.pendingActionSequence)
                {
                    _confirmedMailbox.RestoreAction(input.Sequence, (input.Packed >> 2) & 3, reply.pendingActionUntil);
                    // 招式可能跨多次回执等待。保留该输入字供下一次恢复，只从待重演方向和跳跃里排除。
                    _pending[retained++] = input;
                }
            }
            _count = retained;
            if (_lostThrough != 0 && reply.inputAckSequence >= _lostThrough &&
                (reply.pendingActionSequence == 0 || _confirmedMailbox.ActionSequence == reply.pendingActionSequence))
            {
                _lostThrough = 0;
            }
            _confirmedMailbox.Sequence = reply.inputAckSequence;
            _confirmedMailbox.RestoreDirection((packed >> (_black ? 0 : DuelCorePipe.InputBits)) & 3, confirmed.Frame);
            _otherDirection = (packed >> (_black ? DuelCorePipe.InputBits : 0)) & 3;
            _anchor = confirmed;
            _checkpointBytes = authoritative.SaveCheckpoint(_checkpoint);
            _dirty = true;
            return true;
        }

        // 由渲染帧采样。预览包含还没上行的按钮，马上试算一帧，只返回本机坐标和跑动朝向。
        public bool TrySample(int preview, float deltaSeconds, out DuelPredictedMotion motion)
        {
            motion = default;
            if (_checkpointBytes == 0 || _lostThrough != 0 || _anchor.Result != 0)
            {
                return false;
            }
            int limit = _anchor.Frame + MaxLead;
            _cursor = Math.Min(limit, Math.Max(_anchor.Frame + 1, _cursor + Math.Max(0, deltaSeconds) * 1000.0 / DuelConfig.FrameMs));
            if (preview != _preview)
            {
                _preview = preview;
                // 边沿只放一次。将输入放到正在播放的下一整数帧，方向变化不必等旧包确认。
                _previewFrame = (int)Math.Ceiling(_cursor);
                _dirty = true;
            }
            if (_dirty)
            {
                Rebuild();
            }
            int index = (int)_cursor - _pathStart;
            DuelSnapshot from = _path[index];
            DuelSnapshot to = _path[Math.Min(index + 1, MaxLead)];
            float blend = (float)(_cursor - Math.Floor(_cursor));
            int movement = index == MaxLead
                ? LocalX(from).CompareTo(LocalX(_path[index - 1]))
                : LocalX(to).CompareTo(LocalX(from));
            motion = new DuelPredictedMotion(
                LocalX(from) + (LocalX(to) - LocalX(from)) * blend,
                LocalY(from) + (LocalY(to) - LocalY(from)) * blend,
                movement, MotionCorrections);
            return true;
        }

        // 恢复完整状态，再按预计帧和上行顺序重演有限步。沙盒的伤害、命中和终局不会交给表现消费者。
        private void Rebuild()
        {
            if (!_sandbox.TryRestoreCheckpoint(_checkpoint, _checkpointBytes))
            {
                throw new InvalidOperationException("决斗预测无法恢复完整原生检查点。");
            }
            _pathStart = _anchor.Frame;
            _path[0] = _anchor;
            DuelInputMailbox local = _confirmedMailbox;
            int pendingIndex = 0;
            int limit = _anchor.Frame + MaxLead;
            // 预览排在已送意图之后。最迟只在预测窗最后一帧试算，绝不占用权威时钟。
            int previewFrame = Math.Max(_anchor.Frame + 1, Math.Min(limit, _previewFrame));
            if (_count > 0)
            {
                previewFrame = Math.Max(previewFrame, Math.Min(limit, _pending[_count - 1].Frame));
            }
            for (int step = 1; step <= MaxLead; step++)
            {
                int frame = _anchor.Frame + step;
                while (pendingIndex < _count && Math.Min(limit, _pending[pendingIndex].Frame) <= frame)
                {
                    PendingInput input = _pending[pendingIndex++];
                    local.Stage(input.Sequence, input.Packed, frame - 1);
                }
                if (frame == previewFrame)
                {
                    // 预览没有真实序号，用只在本次重演有效的最大值。它不会进入日志或网络。
                    local.Stage(int.MaxValue, _preview, frame - 1);
                }
                // 对手只猜方向，两席五位字仍按真实封帧顺序进入同一个 C++ 入口。
                int own = local.Take(_sandbox.Snapshot, _black);
                int packed = _black ? own | (_otherDirection << DuelCorePipe.InputBits) : _otherDirection | (own << DuelCorePipe.InputBits);
                _sandbox.Append(frame, packed);
                _path[step] = _sandbox.Snapshot;
            }
            _dirty = false;
        }

        // 读取本席横向坐标，单位千分格，仅用于比较预测轨迹。
        private int LocalX(DuelSnapshot snapshot)
        {
            return _black ? snapshot.FirstX : snapshot.SecondX;
        }

        // 读取本席离地高度，单位千分格，用于发现预跳与权威跳跃的差异。
        private int LocalY(DuelSnapshot snapshot)
        {
            return _black ? snapshot.FirstY : snapshot.SecondY;
        }

        // 失焦、掉线、追帧或退场时丢弃临时轨迹和输入，等下一份新基线再开启。
        public void Suspend()
        {
            _count = 0;
            _lostThrough = 0;
            _checkpointBytes = 0;
            _pathStart = -1;
            _preview = -1;
            _previewFrame = 0;
            _cursor = 0;
            _dirty = true;
        }

        // 和决斗服务的本地副本一起退出，不能遗留原生句柄。
        public void Dispose()
        {
            _sandbox.Dispose();
        }
    }

    // 给场景的预测结果只有本机运动数据。显示端无法从这里读到沙盒的伤害、血量和胜负。
    internal readonly struct DuelPredictedMotion
    {
        // 屏幕换算前的预测横坐标，单位千分格。
        public readonly float X;
        // 插值后的离地高度，单位千分格。
        public readonly float Y;
        // 当前播放区间的移动方向，-1 左、0 静止、1 右。
        public readonly int Movement;
        // 同帧位置校正版本。显示端只在它变化时平滑消化纠偏差值。
        public readonly int CorrectionVersion;

        // 构建一次只读输出，离开本次渲染即失效。
        public DuelPredictedMotion(float x, float y, int movement, int correctionVersion)
        {
            X = x;
            Y = y;
            Movement = movement;
            CorrectionVersion = correctionVersion;
        }
    }
}
