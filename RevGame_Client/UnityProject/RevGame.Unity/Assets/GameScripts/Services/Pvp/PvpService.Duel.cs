using System;
using RevGame.Pvp.Duel;
using UnityEngine;

namespace RevGame.Pvp
{
    // 围棋服务的决斗接线，负责接纳、收发、补帧与退出，伤害和移动由原生实例结算。
    public sealed partial class PvpService
    {
        // 资格检查复用的棋盘分析缓冲，避免界面刷新重复分配。
        private readonly GoBoardAnalysis _duelAnalysis = new GoBoardAnalysis();
        // 缓存上一次算过资格的棋盘引用。新棋面才重算。
        private GoCore _duelExaminedBoard;
        // 当前棋面下本座位是否达到劣势条件。
        private bool _duelEligible;
        // 对端已确认支持当前决斗指纹。旧客户端仍可围棋对弈。
        private bool _duelPeerCompatible;
        // 只有本机人机宿主或 LAN 黑方持有的封帧器。服务器模式由远端持有。
        private DuelAuthority _duelAuthority;
        // 本端显示副本，按收到的权威整帧重放并生成快照。
        private DuelCorePipe _duelPipe;
        // 本端独立决斗场景是否真正加载完成。
        private bool _duelSceneReady;
        // 来自权威方的就绪位，不用本端加载状态推测对手就绪。
        private int _duelReadyMask;
        // 尚未装入下一帧的按键招式编号，0 表示未排队。
        private int _duelQueuedAction;
        // 从按键时已确认的快照向后最多保留四个可执行帧。卡顿时逻辑帧不走，缓冲也不老化。
        private const int DuelAttackBufferFrames = 4;
        // 允许带上该招式的最后一个逻辑帧号，包含该帧。
        private int _duelActionLastFrame;
        // 最近一帧表现层采样到的方向，1 左、2 右、0 无。
        private int _duelDirection;
        // 锁存两次网络采样之间的跳跃点按，打包后清除。
        private bool _duelQueuedJump;
        // 已经锁定的一席意图。同一帧重试用完全相同的值。
        private int _duelIntent;
        // 当前锁定意图所属帧号，消费新帧后才换下一份。
        private int _duelIntentFrame;
#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
        // 开发验收读取待发送输入、最近锁定的招式及过期帧，区分缓冲丢失与封帧失败。
        internal int DuelQueuedAttackForCheck => _duelQueuedAction;
        internal int DuelLastIntentForCheck => _duelIntent;
        internal int DuelLastAttackIntentFrameForCheck { get; private set; }
        internal int DuelLastAttackIntentForCheck { get; private set; }
        internal int DuelAttackExpiredAtFrameForCheck { get; private set; }
#endif
        // 下次轮询的本地单调秒数，和原生逻辑帧号独立。
        private double _duelNextSend;
        // 挑战请求冷却截止的本地秒数，抑制连点。
        private double _duelChallengeUntil;
        // 当前决斗标识。空串表示仍在围棋或已经退场。
        public string DuelId { get; private set; } = "";
        // 切场、插件或指纹错误，场景 HUD 可直接展示。
        public string DuelError { get; private set; } = "";
        // 本端最近已执行的快照；未创建副本时返回空值。
        public DuelSnapshot DuelSnapshot => _duelPipe?.Snapshot ?? default;
        // 最近一次执行前的快照，供画面判断真实移动方向。补帧后也保留与当前帧相邻的样本。
        public DuelSnapshot DuelPreviousSnapshot { get; private set; }
        // 权威掩码中的黑白两位都已就绪才为真。
        public bool DuelBothReady => _duelReadyMask == 3;
        // 已有服务器传输且没有在处理其它连接操作时，允许发起重连。
        public bool DuelReconnectAvailable => _server != null && !_server.Busy;

        // 汇总棋局资格、连接兼容和当前操作状态，供 HUD 判断是否显示入口。
        public bool CanChallengeDuel
        {
            get
            {
                if (DuelId.Length != 0 || Core == null || _awaiting || _leaving ||
                    (!IsComputerGame && (Mode == PvpConnectionMode.Local || !Connected || !_duelPeerCompatible)))
                {
                    return false;
                }
                if (!ReferenceEquals(_duelExaminedBoard, Core))
                {
                    _duelExaminedBoard = Core;
                    _duelEligible = DuelEligibility.CanChallenge(Core, MySide, _duelAnalysis);
                }
                return _duelEligible;
            }
        }

        // 经正式业务入口发起决斗；服务器 RPC、LAN 转发或本地创建按模式选择。
        public bool ChallengeDuel()
        {
            if (!CanChallengeDuel || _clock.Elapsed.TotalSeconds < _duelChallengeUntil)
            {
                return false;
            }
            _duelChallengeUntil = _clock.Elapsed.TotalSeconds + 1;
            if (Mode == PvpConnectionMode.Server)
            {
                return _server.ExchangeDuel(DuelRequestFor(DuelAuthority.Challenge), AcceptDuelReply);
            }
            if (Mode == PvpConnectionMode.Lan && MySide == GoSide.White)
            {
                SendLan(new PvpLanMessage { type = "duel-challenge", name = Core.StateHash.ToString(),
                    duelRequest = DuelRequestFor(DuelAuthority.Challenge) });
                return true;
            }
            return OpenOwnedDuel(Mode == PvpConnectionMode.Lan);
        }

        // 为权威位创建封帧器，必要时通知 LAN 对端；创建失败后报告错误。
        private bool OpenOwnedDuel(bool notifyPeer)
        {
            string id = Guid.NewGuid().ToString("N");
            try
            {
                _duelAuthority = new DuelAuthority(id);
                ActivateDuel(id);
                if (DuelId != id)
                {
                    _duelAuthority.Dispose();
                    _duelAuthority = null;
                    return false;
                }
                if (notifyPeer)
                {
                    SendLan(new PvpLanMessage { type = "duel-start", name = id, rules = Core.StateHash.ToString(),
                        duelRequest = DuelRequestFor(DuelAuthority.Challenge) });
                }
                return true;
            }
            catch (Exception error)
            {
                DuelError = error.Message;
                Status = "决斗启动失败：" + error.Message;
                Revision++;
                return false;
            }
        }

        // 接受当前局标识并创建本端副本。重复通知不会重新创建。
        private void ActivateDuel(string id)
        {
            if (string.IsNullOrEmpty(id) || DuelId.Length != 0)
            {
                return;
            }
            try
            {
                _duelPipe = new DuelCorePipe(id);
                DuelId = id;
                CancelAgent();
                CancelHint();
                _pendingSequence = 0;
                _awaiting = false;
                DuelError = "";
                Revision++;
            }
            catch (Exception error)
            {
                DuelError = error.Message;
                Status = "决斗内核加载失败：" + error.Message;
                Revision++;
            }
        }

        // 场景加载成功后登记本端就绪，后续 Poll 才会发送 Ready。
        public void MarkDuelSceneReady()
        {
            _duelSceneReady = DuelId.Length != 0;
        }

        // 接收键盘或 HUD 采样，只缓存意图，不立即修改原生世界。
        public void SetDuelInput(int direction, int action, bool jump = false)
        {
            if (Mode != PvpConnectionMode.Local && !Connected)
            {
                ClearDuelUnsealedInput();
                return;
            }
            _duelDirection = direction < 0 ? 1 : direction > 0 ? 2 : 0;
            // 点按可能发生在两个网络帧之间。锁存跳跃，直到它被打包进下一帧意图。
            _duelQueuedJump |= jump;
            if (DuelBothReady && DuelSnapshot.Result == 0 && action >= 1 && action <= 3)
            {
                _duelQueuedAction = action;
                _duelActionLastFrame = DuelSnapshot.Frame + DuelAttackBufferFrames;
#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
                DuelAttackExpiredAtFrameForCheck = 0;
#endif
            }
        }

        // 失焦、掉线或退出时丢掉尚未锁定的按键。已编号的意图仍必须原样重发。
        internal void ClearDuelUnsealedInput()
        {
            _duelDirection = 0;
            _duelQueuedAction = 0;
            _duelQueuedJump = false;
            _duelActionLastFrame = 0;
        }

        // 下一帧若会结束恢复期，就允许带上缓存的攻击意图。是否成功出招仍由原生内核决定。
        private int TakeDuelAction(int nextFrame)
        {
            if (_duelQueuedAction == 0)
            {
                return 0;
            }
            if (nextFrame > _duelActionLastFrame)
            {
#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
                DuelAttackExpiredAtFrameForCheck = nextFrame;
#endif
                _duelQueuedAction = 0;
                return 0;
            }
            DuelSnapshot snapshot = DuelSnapshot;
            bool black = MySide == GoSide.Black;
            int action = black ? snapshot.FirstAction : snapshot.SecondAction;
            int age = black ? snapshot.FirstAge : snapshot.SecondAge;
            if (age < DuelConfig.ActionDuration(action))
            {
                return 0;
            }
            int queued = _duelQueuedAction;
            _duelQueuedAction = 0;
            return queued;
        }

        // 记录可展示的决斗失败原因，使轮询停止。
        public void SetDuelError(string error)
        {
            DuelError = error;
        }

        // 用本端已消费帧号和共享配置指纹创建请求。
        private DuelRequest DuelRequestFor(int operation)
        {
            return new DuelRequest { operation = operation, fingerprint = DuelConfig.Fingerprint, afterFrame = DuelSnapshot.Frame };
        }

        // 按本地单调时钟发送 Ready 或锁定输入，并按对局模式选择传输边界。
        private void PollDuel()
        {
            if (!_duelSceneReady || DuelSnapshot.Result != 0 || DuelError.Length != 0 ||
                _clock.Elapsed.TotalSeconds < _duelNextSend)
            {
                return;
            }
            _duelNextSend = _clock.Elapsed.TotalSeconds + DuelConfig.FrameMs / 1000.0;
            var request = DuelRequestFor(DuelBothReady ? DuelAuthority.Input : DuelAuthority.Ready);
            if (request.operation == DuelAuthority.Input)
            {
                // 按本端连续执行的上界生成下一帧。没收到封口结果前重复请求仍使用旧意图。
                int next = DuelSnapshot.Frame + 1;
                if (_duelIntentFrame != next)
                {
                    _duelIntentFrame = next;
                    int action = TakeDuelAction(next);
                    _duelIntent = _duelDirection | (action << 2) | (_duelQueuedJump ? DuelCorePipe.Jump : 0);
#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
                    if (action != 0)
                    {
                        DuelLastAttackIntentForCheck = action;
                        DuelLastAttackIntentFrameForCheck = next;
                    }
#endif
                    _duelQueuedJump = false;
                }
                request.inputFrame = _duelIntentFrame;
                request.input = _duelIntent;
            }
            if (Mode == PvpConnectionMode.Server)
            {
                _server?.ExchangeDuel(request, AcceptDuelReply);
            }
            else if (Mode == PvpConnectionMode.Lan && _duelAuthority == null)
            {
                SendLan(new PvpLanMessage { type = "duel-input", name = DuelId, duelRequest = request });
            }
            else if (_duelAuthority != null)
            {
                long now = _clock.ElapsedMilliseconds;
                if (IsComputerGame)
                {
                    var bot = DuelRequestFor(request.operation);
                    bot.inputFrame = request.inputFrame;
                    bot.input = ComputerDuelInput();
                    _duelAuthority.Exchange(2, bot, now);
                }
                AcceptDuelReply(_duelAuthority.Exchange((int)MySide, request, now));
            }
        }

        // 由权威位从快照生成电脑意图，跟真人一起进入下一完整帧。
        private int ComputerDuelInput()
        {
            // AI 只生成一个座位的意图。它与真人一样经过权威帧，伤害和距离判定仍由 C++ 负责。
            var view = _duelAuthority.Snapshot;
            int distance = view.FirstX - view.SecondX;
            if (Math.Abs(distance) > DuelConfig.JabRange)
            {
                return distance < 0 ? 1 : 2;
            }
            return view.SecondEnergy >= DuelConfig.UltimateCost ? 12 : view.SecondEnergy >= DuelConfig.SkillCost ? 8 : 4;
        }

        // 核对房间，按连续游标补帧并消费结果。缺口保留到下一次从现有上界重取。
        private void AcceptDuelReply(DuelReply reply)
        {
            if (reply == null)
            {
                return;
            }
            if (!string.IsNullOrEmpty(reply.error))
            {
                Status = reply.error;
                Revision++;
                return;
            }
            ActivateDuel(reply.id);
            if (_duelPipe == null || reply.id != DuelId)
            {
                return;
            }
            _duelReadyMask = reply.readyMask;
            int current = DuelSnapshot.Frame;
            if (reply.afterFrame > current || reply.frames == null)
            {
                // 留在现有连续上界，下一次请求会从本地已消费的帧重新取补帧。
                return;
            }
            try
            {
                // 回复可能和本端历史重叠。从第一条未消费的位置继续，不重复调用 C++。
                for (int i = current - reply.afterFrame; i < reply.frames.Length; i++)
                {
                    if ((reply.frames[i] & DuelCorePipe.NoDamageFrame) != 0 && (Mode != PvpConnectionMode.Local || !IsComputerGame))
                    {
                        throw new InvalidOperationException("联机决斗收到本地测试帧。");
                    }
                    DuelSnapshot before = _duelPipe.Snapshot;
                    _duelPipe.Append(reply.afterFrame + i + 1, reply.frames[i]);
                    DuelPreviousSnapshot = before;
                }
                // 满包还可能有历史，等补齐后对账。终局会停止轮询，所以最后一包恰好满包也必须检查。
                if (DuelSnapshot.Frame > 0 && (reply.frames.Length < DuelAuthority.MaxBatch || DuelSnapshot.Result != 0) &&
                    DuelSnapshot.Frame == reply.afterFrame + reply.frames.Length &&
                    (DuelSnapshot.Result != reply.result || DuelSnapshot.Hash != reply.finalHash))
                {
                    DuelError = "决斗同帧指纹不一致。";
                }
            }
            catch (Exception error)
            {
                DuelError = error.Message;
            }
        }

        // 识别 P2P 决斗消息，区分宿主授权、客端请求与回执。
        private bool ReceiveLanDuel(PvpLanMessage message)
        {
            switch (message.type)
            {
                case "duel-challenge":
                    if (MySide == GoSide.Black && DuelId.Length == 0 && message.name == Core.StateHash.ToString() &&
                        message.duelRequest?.fingerprint == DuelConfig.Fingerprint &&
                        DuelEligibility.CanChallenge(Core, GoSide.White, _duelAnalysis))
                    {
                        OpenOwnedDuel(true);
                    }
                    return true;
                case "duel-start":
                    if (MySide == GoSide.White && message.rules == Core.StateHash.ToString() &&
                        message.duelRequest?.fingerprint == DuelConfig.Fingerprint)
                    {
                        ActivateDuel(message.name);
                    }
                    return true;
                case "duel-input":
                    if (_duelAuthority != null && message.name == DuelId)
                    {
                        var reply = _duelAuthority.Exchange(2, message.duelRequest, _clock.ElapsedMilliseconds);
                        SendLan(new PvpLanMessage { type = "duel-frame", duelReply = reply });
                    }
                    return true;
                case "duel-frame":
                    if (MySide == GoSide.White && DuelId.Length != 0)
                    {
                        AcceptDuelReply(message.duelReply);
                    }
                    return true;
                default:
                    return false;
            }
        }

        // 离开或换连接时清空决斗状态，成对释放权威和本端副本。
        private void ResetDuel()
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
            DuelLastAttackIntentForCheck = 0;
            DuelLastAttackIntentFrameForCheck = 0;
            DuelAttackExpiredAtFrameForCheck = 0;
#endif
            DuelPreviousSnapshot = default;
            _duelAuthority?.Dispose();
            _duelPipe?.Dispose();
            _duelAuthority = null;
            _duelPipe = null;
            _duelExaminedBoard = null;
            _duelPeerCompatible = false;
            DuelId = "";
            DuelError = "";
            _duelSceneReady = false;
            _duelReadyMask = 0;
            ClearDuelUnsealedInput();
            _duelIntent = 0;
            _duelIntentFrame = 0;
            _duelNextSend = 0;
            _duelChallengeUntil = 0;
        }

#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
        // 仅本机人机且处于决斗时才修改当前权威的封帧选项。离场销毁权威后自动复位。
        internal bool TrySetDuelNoDamage(bool enabled)
        {
            if (Mode != PvpConnectionMode.Local || !IsComputerGame || !_duelSceneReady ||
                _duelAuthority == null || DuelSnapshot.Result != 0)
            {
                return false;
            }
            _duelAuthority.DebugNoDamage = enabled;
            return true;
        }

        // 开发态用合法棋步构造明显劣势，再进入本机人机控制，供实际入口回归。
        public bool PrepareDuelFixture()
        {
            // 不改存档或直接伪造棋盘。黑方停手，白方每轮下一个合法点，最后接回正式人机控制。
            StartLocal();
            int rounds = (DuelConfig.MinimumMove + 1) / 2;
            for (int i = 0; i < rounds; i++)
            {
                if (!Submit(GoAction.Pass) || !Submit(GoAction.Play, i % 9, i / 9))
                {
                    return false;
                }
            }
            IsComputerGame = true;
            MySide = GoSide.Black;
            BlackName = "你 · 黑方";
            WhiteName = "棋盘助手 · 白方";
            Revision++;
            return CanChallengeDuel;
        }
#endif
    }
}
