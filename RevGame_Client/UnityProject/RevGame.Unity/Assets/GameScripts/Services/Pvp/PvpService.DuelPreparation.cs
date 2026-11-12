using System;
using RevGame.Pvp.Duel;

namespace RevGame.Pvp
{
    // 准备流程沿用每局的决斗 RPC/可靠 LAN 消息，由服务保存本席意图与权威回执。
    public sealed partial class PvpService
    {
        // 选角状态低频轮询，既能及时看到对手，又不占用战斗的输入频率。
        private const int PrepPollMs = 120;
        // 当前准备阶段的权威值，网络线程不直接操作界面。
        public DuelPrepState DuelPrep { get; private set; }
        // 该端已经真正显示选角面板，重发直到权威开启选择。
        private bool _prepVisible;
        // 本席最近一次点选；确认直接携带最后选中的角色，避免快点时用到旧回执。
        private DuelRole _prepPick;
        private bool _prepLock;
        private bool _prepAbort;
        // Unity 已完成的本地进度，只有场景预加载门回调可以报告 100。
        private int _prepLoad;
        // 单调时钟用于发送限速、倒计时插值和等待失败兜底。
        private long _prepNextSend;
        private long _prepStageStarted;
        private long _prepPickDeadline;
        // 取消请求的发送窗口，连接异常时也能从本机收尾。
        private long _prepAbortAt;

        // 面板与实际决斗渲染统一使用这份权威的黑白角色；尚无回执时展示原默认阵容。
        public DuelRole DuelRoleFor(int side)
        {
            return DuelPrep?.Role(side) ?? (side == 1 ? DuelRole.Ember : DuelRole.Yaya);
        }

        // 本机本席可以先看到自己的鼠标选择；对端只从权威回包读到它。
        public DuelRole DuelPreviewFor(int side)
        {
            return side == (int)MySide && DuelPrep?.phase == DuelPrepPhase.Choosing &&
                (DuelPrep.locked & (1 << (side - 1))) == 0 && _prepPick != 0 ? _prepPick : DuelRoleFor(side);
        }

        // 显示用整数秒。截止点由权威决定，归零时面板禁止新的输入并等待回包。
        public int DuelPickSeconds => DuelPrep?.phase == DuelPrepPhase.Choosing
            ? (int)Math.Max(0, (_prepPickDeadline - _clock.ElapsedMilliseconds + 999) / 1000)
            : DuelPrep == null || DuelPrep.phase == DuelPrepPhase.Waiting ? (DuelConfig.PickDurationMs + 999) / 1000 : 0;

        // 同一次点击发出的锁角在回包前也禁用按钮，防止新意图覆盖它。
        public bool DuelCanPick => DuelPrep?.phase == DuelPrepPhase.Choosing && DuelPickSeconds > 0 &&
            MySide is GoSide.Black or GoSide.White && !_prepLock && (DuelPrep.locked & (1 << ((int)MySide - 1))) == 0;

        // 正式流程拿到面板后才调用；离场或旧局回调不会重新打开准备。
        internal void ShowDuelSelection()
        {
            if (_prepVisible || DuelId.Length == 0)
            {
                return;
            }
            _prepVisible = DuelId.Length != 0;
            _prepNextSend = 0;
        }

        // UI、GM 与真实联机测试共用同一提交入口；提交成功仅表示本端收下这次操作。
        public bool SelectDuelRole(DuelRole role, bool confirm)
        {
            if (!DuelCanPick || !DuelPreparation.ValidRole(role))
            {
                return false;
            }
            _prepPick = role;
            _prepLock = confirm;
            return true;
        }

        // 离开时通知权威取消；已经放行的决斗仍由正式返回大厅处理。
        public void AbortDuelPreparation()
        {
            if (DuelPrep?.phase == DuelPrepPhase.Released)
            {
                return;
            }
            _prepAbort = true;
            _prepAbortAt = _clock.ElapsedMilliseconds;
            _prepNextSend = 0;
        }

        // 只有真加载的单调回调可以推进，动画补间不写回业务。
        internal void ReportDuelSceneProgress(int percent)
        {
            if (DuelPrep?.phase == DuelPrepPhase.Loading && percent > _prepLoad && percent <= 100)
            {
                _prepLoad = percent;
            }
        }

        // 等不到权威也可收尾：不无限占住正在预加载的引擎队列。
        internal bool DuelPreparationFailed =>
            DuelError.Length != 0 || DuelPrep?.phase == DuelPrepPhase.Cancelled ||
            (_prepAbort && _clock.ElapsedMilliseconds - _prepAbortAt > 1200) ||
            ((DuelPrep == null || DuelPrep.phase != DuelPrepPhase.Released) &&
                _clock.ElapsedMilliseconds - _prepStageStarted > DuelPreparation.StageTimeoutMs + DuelConfig.PickDurationMs);

        // 只有一个本席命令需要重发：可见、最后选角、最高进度与取消都天然幂等。
        private void PollDuelPreparation()
        {
            long now = _clock.ElapsedMilliseconds;
            if (now < _prepNextSend || (Mode != PvpConnectionMode.Local && !Connected) || MySide == GoSide.Empty)
            {
                return;
            }
            _prepNextSend = now + PrepPollMs;
            var request = DuelRequestFor(DuelAuthority.Read);
            DuelPrepState state = DuelPrep;
            if (_prepAbort)
            {
                request.operation = DuelAuthority.PrepAbort;
            }
            else if (_prepVisible && (state == null || state.phase == DuelPrepPhase.Waiting))
            {
                request.operation = DuelAuthority.PrepVisible;
            }
            else if (state?.phase == DuelPrepPhase.Choosing && _prepPick != 0 &&
                (state.locked & (1 << ((int)MySide - 1))) == 0 && (_prepLock || state.Role((int)MySide) != _prepPick))
            {
                request.operation = _prepLock ? DuelAuthority.PrepLock : DuelAuthority.PrepPick;
                request.input = (int)_prepPick;
            }
            else if (state?.phase == DuelPrepPhase.Loading && _prepLoad > state.Progress((int)MySide))
            {
                request.operation = DuelAuthority.PrepLoad;
                request.input = _prepLoad;
            }
            if (_duelAuthority != null)
            {
                AcceptDuelReply(_duelAuthority.Exchange((int)MySide, request, now));
            }
            else if (Mode == PvpConnectionMode.Server)
            {
                _server?.ExchangeDuel(request, AcceptDuelReply);
            }
            else
            {
                SendLan(new PvpLanMessage { type = "duel-input", name = DuelId, duelRequest = request });
            }
        }

        // 只消费本局单调版本；锁角时重建第 0 帧本端副本，使预测与权威拿到相同的角色数据。
        private void ApplyDuelPreparation(DuelPrepState state)
        {
            if (state == null || (DuelPrep != null && state.revision < DuelPrep.revision))
            {
                return;
            }
            long now = _clock.ElapsedMilliseconds;
            if (DuelPrep?.phase != state.phase)
            {
                _prepStageStarted = now;
            }
            if (state.phase == DuelPrepPhase.Choosing)
            {
                long deadline = now + state.remainingMs;
                _prepPickDeadline = DuelPrep?.phase == state.phase ? Math.Min(_prepPickDeadline, deadline) : deadline;
            }
            DuelPrep = state;
            if ((state.phase is DuelPrepPhase.Loading or DuelPrepPhase.Released) &&
                _duelPipe.Snapshot.Frame == 0 && (_duelPipe.FirstRole != state.black || _duelPipe.SecondRole != state.white))
            {
                try
                {
                    var replacement = new DuelCorePipe(DuelId, state.black, state.white);
                    _duelPipe.Dispose();
                    _duelPipe = replacement;
                    DuelDisplay.Reset();
                    DuelDisplay.Record(replacement.Snapshot);
                }
                catch (Exception error)
                {
                    SetDuelError("角色初始化失败：" + error.Message);
                    AbortDuelPreparation();
                }
            }
        }

        // 房间退出时清掉所有准备数据，不让上一局的确认或进度进入下一局。
        private void ResetDuelPreparation()
        {
            DuelPrep = null;
            _prepVisible = _prepLock = _prepAbort = false;
            _prepPick = 0;
            _prepLoad = 0;
            _prepNextSend = _prepStageStarted = _prepPickDeadline = _prepAbortAt = 0;
        }
    }
}
