using System;
using RevGame.Pvp.Duel;

// Windows 离线回归。使用真实权威、回放副本和预测沙盒，显式调度延迟输入与补帧，便于复现每一步。
internal static class Program
{
    // 整帧确认前后的位移差、隐藏状态恢复与回执边界都要通过，任意失败使进程非零退出。
    private static void Main()
    {
        CheckLateJumpAndRollback();
        CheckPendingAttackReceipt();
        CheckLongCatchUpAndReconnect();
        CheckJournalOverflow();
        Console.WriteLine("DUEL_PREDICTION_PASS lateJump, rollback, anchorHash, bufferedAttack, duplicate, catchUp, staleAnchor, reconnect, journalOverflow, blackAndWhite");
    }

    // 跳跃在本机预览时尚未到权威。恢复至实际空帧，再重演仍未确认的同一个点按。
    private static void CheckLateJumpAndRollback()
    {
        const string id = "prediction-late-jump";
        using var authority = new DuelAuthority(id);
        using var replica = new DuelCorePipe(id);
        using var prediction = new DuelPrediction(id, black: true);
        ReadyBoth(authority);
        authority.AdvanceNetwork(50);
        Apply(replica, prediction, Exchange(authority, 1, DuelAuthority.Read, 50));
        int firstHash = replica.Snapshot.Hash;

        int jumpRight = DuelCorePipe.Jump | 2;
        Check(prediction.TrySample(jumpRight, 0, out var early) && early.Y > 0 && early.X > replica.Snapshot.FirstX,
            "本机在跳跃发出前立即使用原生试算位移");
        prediction.RecordSent(1, jumpRight);
        Check(replica.Snapshot.Frame == 1 && replica.Snapshot.Hash == firstHash && replica.Snapshot.FirstY == 0,
            "预测没有污染已确认原生副本");

        authority.AdvanceNetwork(100);
        Apply(replica, prediction, Exchange(authority, 1, DuelAuthority.Read, 100, after: 1));
        Check(prediction.RollbackCount == 1 && prediction.MotionCorrections == 1 && prediction.PendingCount == 1,
            "同号空帧触发回滚，未确认点按保留");
        Check(prediction.TrySample(2, 0, out var replayed) && replayed.Y > 0,
            "回滚从完整检查点重演尚未确认的跳跃");

        // 权威刚收包尚未封口。旧帧回执和重复请求都不能让这个跳跃执行第二次。
        var staged = Exchange(authority, 1, DuelAuthority.Input, 125, after: 2, sequence: 1, packed: jumpRight);
        Check(staged.inputAckFrame == 2 && staged.inputAckSequence == 0 && staged.frames.Length == 0,
            "收包不等于权威确认");
        authority.AdvanceNetwork(150);
        var confirmed = Exchange(authority, 1, DuelAuthority.Input, 160, after: 2, sequence: 1, packed: jumpRight);
        Apply(replica, prediction, confirmed);
        Check(confirmed.inputAckSequence == 1 && prediction.PendingCount == 0 && replica.Snapshot.FirstY > 0,
            "仅封口回执裁剪输入");
        Check(!prediction.Rebase(replica, staged, 0) && prediction.AnchorFrame == 3, "旧回执不可把预测基线拉回");
        Check(prediction.TrySample(2, 0, out var flying) && flying.Y > replica.Snapshot.FirstY,
            "检查点保留垂直速度，可在空中按原生物理续算");

        authority.AdvanceNetwork(200);
        var next = Exchange(authority, 1, DuelAuthority.Read, 200, after: 3);
        Apply(replica, prediction, next);
        Check((next.frames[0] & DuelCorePipe.Jump) == 0 && replica.Snapshot.Hash == authority.Snapshot.Hash &&
            prediction.RollbackCount == 1, "重复包不重跳，空中重演与下一权威整帧一致");
    }

    // 攻击等恢复期时，更新的方向回执不能误清旧招式。超期帧由权威决定。
    private static void CheckPendingAttackReceipt()
    {
        const string id = "prediction-action-mailbox";
        using var authority = new DuelAuthority(id);
        using var replica = new DuelCorePipe(id);
        using var prediction = new DuelPrediction(id, black: false);
        ReadyBoth(authority);
        Exchange(authority, 2, DuelAuthority.Input, 10, sequence: 1, packed: 4);
        authority.AdvanceNetwork(50);
        Apply(replica, prediction, Exchange(authority, 2, DuelAuthority.Read, 50));
        Check(replica.Snapshot.SecondAction == 1, "白方第一记普攻实际执行");

        prediction.RecordSent(2, 4);
        prediction.RecordSent(3, 0);
        Exchange(authority, 2, DuelAuthority.Input, 60, sequence: 2, packed: 4);
        Exchange(authority, 2, DuelAuthority.Input, 70, sequence: 3);
        authority.AdvanceNetwork(100);
        var buffered = Exchange(authority, 2, DuelAuthority.Read, 100, after: 1);
        Apply(replica, prediction, buffered);
        Check(buffered.inputAckSequence == 3 && buffered.pendingActionSequence == 2 &&
            buffered.pendingActionUntil == 5 && prediction.PendingCount == 1,
            "新方向已确认，保留权威仍在等待的旧攻击序号");
        for (int frame = 3; frame <= 6; frame++)
        {
            authority.AdvanceNetwork(frame * DuelConfig.FrameMs);
        }
        var expired = Exchange(authority, 2, DuelAuthority.Read, 300, after: 2);
        Apply(replica, prediction, expired);
        Check(expired.pendingActionSequence == 0 && prediction.PendingCount == 0,
            "过早的攻击由权威过期，预测不能延长寿命");
    }

    // 在超过单包 64 帧的历史上，只有最后一包携带回执。断线后不继续外推，重连后可以用新序号。
    private static void CheckLongCatchUpAndReconnect()
    {
        const string id = "prediction-catchup";
        using var authority = new DuelAuthority(id);
        using var replica = new DuelCorePipe(id);
        using var prediction = new DuelPrediction(id, black: false);
        ReadyBoth(authority);
        for (int frame = 1; frame <= 70; frame++)
        {
            authority.AdvanceNetwork(frame * DuelConfig.FrameMs);
        }
        var older = Exchange(authority, 2, DuelAuthority.Read, 3500);
        Apply(replica, prediction, older);
        Check(older.frames.Length == 64 && older.inputAckFrame == 0 &&
            !prediction.TrySample(1, 0, out _), "补帧落后时不打开预测");
        Apply(replica, prediction, Exchange(authority, 2, DuelAuthority.Read, 3500, after: 64));
        Check(prediction.TrySample(1, 0, out var white) && white.X < replica.Snapshot.SecondX,
            "追到连续上界后只预测本机白方");

        authority.DisconnectNetworkSeat(2);
        prediction.Suspend();
        authority.AdvanceNetwork(3800);
        Check(authority.Frame == 70 && !prediction.TrySample(2, .25f, out _), "断线暂停并清除旧轨迹");
        var stale = Exchange(authority, 2, DuelAuthority.Read, 3800, after: 70);
        Check(!prediction.Rebase(replica, stale, 0) && !prediction.TrySample(2, 0, out _),
            "同帧旧回执不能让暂停的预测重新运行");
        Exchange(authority, 2, DuelAuthority.Ready, 3800, after: 70);
        authority.AdvanceNetwork(3850);
        Apply(replica, prediction, Exchange(authority, 2, DuelAuthority.Read, 3850, after: 70));
        Check(prediction.TrySample(2, 0, out white) && white.X > replica.Snapshot.SecondX &&
            replica.Snapshot.Hash == authority.Snapshot.Hash, "重连从新锚点恢复白方预测");
    }

    // 可靠队列积压超过日志容量时暂停预测，直到权威水位覆盖丢失部分才恢复。
    private static void CheckJournalOverflow()
    {
        const string id = "prediction-journal-cap";
        using var authority = new DuelAuthority(id);
        using var replica = new DuelCorePipe(id);
        using var prediction = new DuelPrediction(id, black: true);
        ReadyBoth(authority);
        authority.AdvanceNetwork(50);
        Apply(replica, prediction, Exchange(authority, 1, DuelAuthority.Read, 50));
        const int queued = 30;
        for (int sequence = 1; sequence <= queued; sequence++)
        {
            prediction.RecordSent(sequence, 2);
        }
        Check(!prediction.TrySample(2, 0, out _) && prediction.PendingCount < queued, "超限不靠丢失的输入猜未来");
        Exchange(authority, 1, DuelAuthority.Input, 75, after: 1, sequence: queued, packed: 2);
        authority.AdvanceNetwork(100);
        Apply(replica, prediction, Exchange(authority, 1, DuelAuthority.Read, 100, after: 1));
        Check(prediction.PendingCount == 0 && prediction.TrySample(2, 0, out _), "权威确认覆盖积压后恢复预测");
    }

    // 复用服务的顺序：先执行完整连续历史，再提交与当前快照同号的回执。
    private static void Apply(DuelCorePipe replica, DuelPrediction prediction, DuelReply reply)
    {
        for (int i = 0; i < reply.frames.Length; i++)
        {
            replica.Append(reply.afterFrame + i + 1, reply.frames[i]);
        }
        prediction.Rebase(replica, reply, reply.frames.Length > 0 ? reply.frames[^1] : 0);
    }

    // 权威只有在两个座位都就绪后才从第 1 帧开始定时封口。
    private static void ReadyBoth(DuelAuthority authority)
    {
        Exchange(authority, 1, DuelAuthority.Ready, 0);
        Exchange(authority, 2, DuelAuthority.Ready, 0);
    }

    // 用同一套正式协议构造请求，时间为测试指定的单调毫秒，不依赖电脑调度。
    private static DuelReply Exchange(DuelAuthority authority, int side, int operation, long now, int after = 0, int sequence = 0, int packed = 0)
    {
        DuelReply reply = authority.Exchange(side, new DuelRequest
        {
            operation = operation, fingerprint = DuelAuthority.NetworkFingerprint, afterFrame = after, inputFrame = sequence, input = packed
        }, now);
        Check(string.IsNullOrEmpty(reply.error), "权威拒绝了验收请求：" + reply.error);
        return reply;
    }

    // 失败保留具体契约说明，方便从首个错误回溯到对应的时间与帧号。
    private static void Check(bool condition, string detail)
    {
        if (!condition)
        {
            throw new InvalidOperationException(detail);
        }
    }
}
