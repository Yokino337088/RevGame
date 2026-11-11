using System;
using System.Collections.Generic;
using RevGame.Pvp.Duel;
using Revolution;
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
        // 为场景保留一小段连续只读快照，画面时钟不会改变原生执行与上行补帧游标。
        internal DuelPlayback DuelDisplay { get; } = new DuelPlayback();
        // 独立原生预测沙盒，仅联机本机座位使用，结束决斗时释放。
        private DuelPrediction _duelPrediction;
        // 预测发生不可恢复的原生错误后，本局退回权威插值，避免每帧重复创建句柄。
        private bool _duelPredictionFailed;
        // 最近一次收到有效预测锚点的单调秒数，与 Unity TimeScale 无关。
        private double _duelPredictionAnchorAt;
        // 超过五个逻辑帧没有新锚点时暂停预测，避免弱网下停留在过期的虚拟位置。
        private const double DuelPredictionStaleSeconds = 5 * DuelConfig.FrameMs / 1000.0;
        // 本端已连续执行的最后一个整帧输入字，用于为预测提供双方最近确认的方向。
        private int _duelLastPacked;
        // 本端独立决斗场景是否真正加载完成。
        private bool _duelSceneReady;
        // 来自权威方的就绪位，不用本端加载状态推测对手就绪。
        private int _duelReadyMask;
        // 尚未装入下一帧的按键招式编号，0 表示未排队。
        private int _duelQueuedAction;
        // 从按键时已确认的快照向后最多保留四个可执行帧。卡顿时逻辑帧不走，缓冲也不老化。
        private const int DuelAttackBufferFrames = 4;
        // 本机人机遇到硬直或出招恢复时，最多保留一次动作 800 毫秒。
        private const int LocalMaxInputBufferFrames = 16;
        // 允许带上该招式的最后一个逻辑帧号，包含该帧。
        private int _duelActionLastFrame;
        // 本机跳跃等待落地或招式结束的最后一个合法逻辑帧。
        private int _duelJumpLastFrame;
        // 最近一帧表现层采样到的方向，1 左、2 右、0 无。
        private int _duelDirection;
        // 锁存两次网络采样之间的跳跃点按，打包后清除。
        private bool _duelQueuedJump;
        // 已经锁定的一席意图。同一帧重试用完全相同的值。
        private int _duelIntent;
        // 当前锁定意图所属帧号，消费新帧后才换下一份。
        private int _duelIntentFrame;
        // 当前连接已经提交的采样序号，与本机执行或显示到哪一帧无关。
        private int _duelInputSequence;
#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
        // 开发验收读取待发送输入、最近锁定的招式及过期帧，区分缓冲丢失与封帧失败。
        internal int DuelQueuedAttackForCheck => _duelQueuedAction;
        internal int DuelLastIntentForCheck => _duelIntent;
        internal int DuelLastAttackIntentFrameForCheck { get; private set; }
        internal int DuelLastAttackIntentForCheck { get; private set; }
        internal int DuelAttackExpiredAtFrameForCheck { get; private set; }
        // 当前人机决斗的暂停开关，随本局释放。暂停后电脑在新帧提交空输入。
        internal bool DuelComputerAiPaused { get; private set; }
        // 最近一帧已确认的白方六位输入，0 表示没有方向、招式或跳跃。
        internal int DuelComputerInputForCheck => (_duelLastPacked >> DuelCorePipe.InputBits) & DuelCorePipe.InputMask;
#endif
        // 下次轮询的本地单调秒数，和原生逻辑帧号独立。
        private double _duelNextSend;
        // 对账重试最短间隔，单位为本地单调秒数。采样频率由逻辑帧配置决定。
        private const double DuelHashRetrySeconds = .5;
        // 允许积压样本先对账，再确认终局。到期后停止终局查询，保留已收到的共同上界。
        private const double DuelTerminalHashTimeoutSeconds = 20;
        // 按已执行帧顺序保存待确认指纹，收到权威确认才移除队首。
        private readonly Queue<(int Frame, int Hash)> _duelHashPending = new Queue<(int, int)>();
        // 最早允许送下一份样本的单调秒数，避免每个方向包附上同一个样本。
        private double _duelHashNextSend;
        // 终局后低频对账的单调秒数截止时间，随本局待发样本积压长度计算。
        private double _duelTerminalHashUntil;
        // 两个客户端都已与同一权威帧指纹核对的最新帧。
        public int DuelHashMatchedFrame { get; private set; }
        // 权威报告的首次分歧帧，0 表示双方尚未发现分歧。
        public int DuelHashMismatchFrame { get; private set; }
#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
        // 当前客户端收到的不同样本确认总数，测试用来发现重复扣队列或漏报。
        internal int DuelHashSamplesAcknowledgedForCheck { get; private set; }
        // 验收只篡改指定帧的上行样本，原生世界与现场快照仍原样运行。
        internal int DuelHashCorruptFrameForCheck { get; set; }
        // 仅拥有封帧器的一端有此统计。座位按棋局约定的 1/2 传入。
        internal int DuelHashReportsForCheck(int side) => _duelAuthority?.HashReportsForCheck(side) ?? 0;
#endif
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

        // 测试按钮只在开发客户端编译。正式 LAN 客端也据此拒绝测试启动通知。
#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
        private const bool DuelTestEnabled = true;
#else
        private const bool DuelTestEnabled = false;
#endif

        // 测试跳过劣势门槛，仍需要已入局、有效座位、同步完成且双方协议兼容。
        internal bool CanTestDuel => DuelTestEnabled && Mode != PvpConnectionMode.Local && CanAct &&
            _duelPeerCompatible && (MySide == GoSide.Black || MySide == GoSide.White) && Core.Phase == GoPhase.Playing;

        // 正常挑战使用原围棋劣势资格。
        public bool ChallengeDuel()
        {
            return RequestDuel(DuelAuthority.Challenge);
        }

        // 明确标记为开发测试，服务端与局域网宿主仍要重新校验。
        internal bool TestDuel()
        {
            return RequestDuel(DuelAuthority.TestChallenge);
        }

        // 两种入口共用服务器 RPC、LAN 转发、就绪屏障和原生开局。
        private bool RequestDuel(int operation)
        {
            bool allowed = operation == DuelAuthority.TestChallenge ? CanTestDuel : CanChallengeDuel;
            if (!allowed || _clock.Elapsed.TotalSeconds < _duelChallengeUntil)
            {
                return false;
            }
            _duelChallengeUntil = _clock.Elapsed.TotalSeconds + 1;
            if (Mode == PvpConnectionMode.Server)
            {
                return _server.ExchangeDuel(DuelRequestFor(operation), AcceptDuelReply);
            }
            if (Mode == PvpConnectionMode.Lan && MySide == GoSide.White)
            {
                SendLan(new PvpLanMessage { type = "duel-challenge", name = Core.StateHash.ToString(),
                    duelRequest = DuelRequestFor(operation) });
                return true;
            }
            return OpenOwnedDuel(Mode == PvpConnectionMode.Lan, operation);
        }

        // 为权威位创建封帧器，必要时通知 LAN 对端；创建失败后报告错误。
        private bool OpenOwnedDuel(bool notifyPeer, int operation)
        {
            string id = Guid.NewGuid().ToString("N");
            try
            {
                _duelAuthority = new DuelAuthority(id, networked: notifyPeer);
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
                        duelRequest = DuelRequestFor(operation) });
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
                DuelDisplay.Record(_duelPipe.Snapshot);
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
            DuelSnapshot snapshot = DuelSnapshot;
            if (!DuelBothReady || snapshot.Result != 0)
            {
                return;
            }
            if (jump)
            {
                // 触点可能位于两帧之间。本机可以等到允许起跳，联机由宿主按现有规则消费。
                _duelQueuedJump = true;
                _duelJumpLastFrame = snapshot.Frame + DuelBufferFrames(snapshot, true);
            }
            if (action >= 1 && action <= DuelCorePipe.MaxActionInput)
            {
                _duelQueuedAction = action;
                _duelActionLastFrame = snapshot.Frame + DuelBufferFrames(snapshot, false);
#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
                DuelAttackExpiredAtFrameForCheck = 0;
#endif
            }
        }

        // 按键记录由本机服务持有，只为暂时不可用的动作延长单个缓存槽。
        private int DuelBufferFrames(DuelSnapshot snapshot, bool jump)
        {
            if (Mode != PvpConnectionMode.Local)
            {
                return DuelAttackBufferFrames;
            }
            bool black = MySide == GoSide.Black;
            if (jump && (black ? snapshot.FirstY : snapshot.SecondY) > 0)
            {
                return LocalMaxInputBufferFrames;
            }
            int active = black ? snapshot.FirstAction : snapshot.SecondAction;
            int age = black ? snapshot.FirstAge : snapshot.SecondAge;
            int stun = black ? snapshot.FirstStun : snapshot.SecondStun;
            int recovery = active == 0 ? 0 : DuelConfig.ActionDuration(active) - age + 1;
            return Math.Max(DuelAttackBufferFrames, Math.Min(LocalMaxInputBufferFrames, Math.Max(stun, recovery) + 1));
        }

        // 失焦、掉线或退出时丢掉尚未锁定的按键和预测。已经上行的输入仍归权威决定，不会被本端撤销。
        internal void ClearDuelUnsealedInput()
        {
            _duelDirection = 0;
            _duelQueuedAction = 0;
            _duelQueuedJump = false;
            _duelActionLastFrame = 0;
            _duelJumpLastFrame = 0;
            _duelPrediction?.Suspend();
        }

        // 由场景在绘制前读取预测运动。失去有效锚点时返回 false，画面直接退回权威播放。
        internal bool TrySampleDuelMotion(float deltaSeconds, out DuelPredictedMotion motion)
        {
            motion = default;
            if (_duelPrediction == null || !Connected || !DuelBothReady || DuelSnapshot.Result != 0 ||
                DuelError.Length != 0 || _duelPrediction.AnchorFrame == 0)
            {
                return false;
            }
            if (_clock.Elapsed.TotalSeconds - _duelPredictionAnchorAt > DuelPredictionStaleSeconds)
            {
                _duelPrediction.Suspend();
                return false;
            }
            try
            {
                return _duelPrediction.TrySample(PackDuelInput(_duelQueuedAction), deltaSeconds, out motion);
            }
            catch (Exception error)
            {
                DisableDuelPrediction(error);
                return false;
            }
        }

        // 把本机方向和仍未送出的点按组成一个输入字。发包和即刻预测使用同一格式。
        private int PackDuelInput(int action)
        {
            return _duelDirection | (action << 2) | (_duelQueuedJump ? DuelCorePipe.Jump : 0);
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
            if (!snapshot.CanTakeAction(black, _duelQueuedAction))
            {
                return 0;
            }
            int queued = _duelQueuedAction;
            _duelQueuedAction = 0;
            return queued;
        }

        // 本地跳跃在空中或受击时等待合法窗口，过期和成功消费都会清除。
        private bool TakeLocalDuelJump(DuelSnapshot snapshot, int nextFrame)
        {
            if (!_duelQueuedJump)
            {
                return false;
            }
            if (nextFrame > _duelJumpLastFrame)
            {
                _duelQueuedJump = false;
                return false;
            }
            bool black = MySide == GoSide.Black;
            bool grounded = black
                ? snapshot.FirstY == 0 && snapshot.FirstAction == 0 && snapshot.FirstStun == 0
                : snapshot.SecondY == 0 && snapshot.SecondAction == 0 && snapshot.SecondStun == 0;
            _duelQueuedJump = !grounded;
            return grounded;
        }

        // 记录可展示的决斗失败原因，使轮询停止。
        public void SetDuelError(string error)
        {
            DuelError = error;
        }

        // 用本端已消费帧号和共享配置指纹创建请求。
        private DuelRequest DuelRequestFor(int operation)
        {
            return new DuelRequest { operation = operation,
                fingerprint = Mode == PvpConnectionMode.Local ? DuelConfig.Fingerprint : DuelAuthority.NetworkFingerprint,
                afterFrame = DuelSnapshot.Frame };
        }

        // 由 PvP 业务宿主每帧轮询。联机走独立权威时钟，本地人机仍锁定两席下一帧的输入。
        private void PollDuel()
        {
            if (Mode != PvpConnectionMode.Local)
            {
                PollNetworkDuel();
                return;
            }
            DuelSnapshot snapshot = DuelSnapshot;
            long now = _clock.ElapsedMilliseconds;
            if (!_duelSceneReady || DuelError.Length != 0 || snapshot.Result != 0 ||
                _duelAuthority == null || !_duelAuthority.LocalFrameDue(now))
            {
                return;
            }
            var request = DuelRequestFor(DuelBothReady ? DuelAuthority.Input : DuelAuthority.Ready);
            if (request.operation == DuelAuthority.Input)
            {
                // 按本端连续执行的上界生成下一帧。没收到封口结果前重复请求仍使用旧意图。
                int next = DuelSnapshot.Frame + 1;
                if (_duelIntentFrame != next)
                {
                    _duelIntentFrame = next;
                    int action = TakeDuelAction(next);
                    _duelIntent = _duelDirection | (action << 2) | (TakeLocalDuelJump(snapshot, next) ? DuelCorePipe.Jump : 0);
#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
                    if (action != 0)
                    {
                        DuelLastAttackIntentForCheck = action;
                        DuelLastAttackIntentFrameForCheck = next;
                    }
#endif
                }
                request.inputFrame = _duelIntentFrame;
                request.input = _duelIntent;
            }
            if (IsComputerGame)
            {
                var bot = DuelRequestFor(request.operation);
                bot.inputFrame = request.inputFrame;
                bot.input = ComputerDuelInput();
                _duelAuthority.Exchange(2, bot, now);
            }
            AcceptDuelReply(_duelAuthority.Exchange((int)MySide, request, now));
        }

        // 先推进本机持有的联机权威，再按独立时钟送方向和点按。补帧游标落后也可以继续送输入。
        private void PollNetworkDuel()
        {
            if (!_duelSceneReady || !Connected || DuelError.Length != 0)
            {
                return;
            }
            long now = _clock.ElapsedMilliseconds;
            _duelAuthority?.AdvanceNetwork(now);
            double seconds = _clock.Elapsed.TotalSeconds;
            DuelSnapshot snapshot = DuelSnapshot;
            bool finished = snapshot.Result != 0;
            if (seconds < _duelNextSend || (finished &&
                (DuelHashMatchedFrame >= snapshot.Frame || seconds >= _duelTerminalHashUntil)))
            {
                return;
            }

            var request = DuelRequestFor(finished ? DuelAuthority.Read : DuelBothReady ? DuelAuthority.Input : DuelAuthority.Ready);
            if (request.operation == DuelAuthority.Input)
            {
                // 保留 int.MaxValue 给沙盒内部的未发送预览序号。真实序号不能回绕成负数。
                if (_duelInputSequence == int.MaxValue - 1)
                {
                    SetDuelError("本次连接的决斗输入序号已耗尽。");
                    return;
                }
                if (snapshot.Frame > _duelActionLastFrame)
                {
                    _duelQueuedAction = 0;
                }
                request.inputFrame = _duelInputSequence + 1;
                request.input = PackDuelInput(_duelQueuedAction);
            }
            AttachDuelHashSample(request, seconds);
            bool sent;
            if (Mode == PvpConnectionMode.Server)
            {
                sent = _server != null && _server.ExchangeDuel(request, AcceptDuelReply);
            }
            else if (_duelAuthority == null)
            {
                sent = SendLan(new PvpLanMessage { type = "duel-input", name = DuelId, duelRequest = request });
            }
            else
            {
                AcceptDuelReply(_duelAuthority.Exchange((int)MySide, request, now));
                sent = true;
            }
            if (!sent)
            {
                // 可靠通道暂时没有空间时仍保留点按和待送样本，下一次轮询可继续送。
                return;
            }
            double interval = finished ? DuelHashRetrySeconds : DuelConfig.FrameMs / 1000.0;
            // 保留正常采样相位。跨过一个以上周期时，从现在重新计时，避免短时间补发旧方向。
            _duelNextSend = _duelNextSend + interval > seconds ? _duelNextSend + interval : seconds + interval;
            if (request.hashFrame > 0)
            {
                _duelHashNextSend = seconds + DuelHashRetrySeconds;
            }
            if (request.operation == DuelAuthority.Input)
            {
                _duelInputSequence = request.inputFrame;
                _duelPrediction?.RecordSent(request.inputFrame, request.input);
                _duelQueuedAction = 0;
                _duelQueuedJump = false;
            }
        }

        // 始终重发最早未确认的样本。普通输入包不携带已经确认的样本。
        private void AttachDuelHashSample(DuelRequest request, double seconds)
        {
            if (_duelHashPending.Count == 0 || seconds < _duelHashNextSend)
            {
                return;
            }
            var sample = _duelHashPending.Peek();
            request.hashFrame = sample.Frame;
            request.hash = sample.Hash;
#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
            if (sample.Frame == DuelHashCorruptFrameForCheck)
            {
                request.hash ^= 1;
            }
#endif
        }

        // 在消费每条完整帧时取样。补帧一包包含多个采样点时，按顺序保留每一个。
        private void RecordDuelHashSample(DuelSnapshot before)
        {
            DuelSnapshot frame = DuelSnapshot;
            if (Mode == PvpConnectionMode.Local || before.Result != 0 ||
                (frame.Frame % DuelConfig.HashSampleFrames != 0 && frame.Result == 0))
            {
                return;
            }
            _duelHashPending.Enqueue((frame.Frame, frame.Hash));
            if (frame.Result != 0)
            {
                // 无时限决斗可能积压更多样本，按当前队列长度预留低频补送时间。
                double wait = Math.Max(DuelTerminalHashTimeoutSeconds, _duelHashPending.Count * DuelHashRetrySeconds + 5);
                _duelTerminalHashUntil = _clock.Elapsed.TotalSeconds + wait;
            }
        }

        // 回包属于当前对局才消费。P2P 宿主也观察转发给客端的共同结论，但不能替自己确认样本。
        private void ObserveDuelHashReceipt(DuelReply reply, bool localAcknowledgement)
        {
            if (reply.id != DuelId)
            {
                return;
            }
            if (reply.hashMismatchFrame > 0)
            {
                DuelHashMismatchFrame = reply.hashMismatchFrame;
                DuelError = "决斗在第 " + DuelHashMismatchFrame + " 帧的指纹不一致。";
                return;
            }
            DuelHashMatchedFrame = Math.Max(DuelHashMatchedFrame, reply.hashMatchedFrame);
            if (localAcknowledgement && _duelHashPending.Count > 0 &&
                reply.hashAckFrame == _duelHashPending.Peek().Frame)
            {
                _duelHashPending.Dequeue();
#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
                DuelHashSamplesAcknowledgedForCheck++;
#endif
            }
        }

        // 由权威位从快照生成电脑意图，跟真人一起进入下一完整帧。
        private int ComputerDuelInput()
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
            if (DuelComputerAiPaused)
            {
                // 白方仍交一个完整帧所需的输入，玩家、已起手的动作和判伤可以继续推进。
                return 0;
            }
#endif
            // AI 只生成一个座位的意图。它与真人一样经过权威帧，伤害和距离判定仍由 C++ 负责。
            var view = _duelAuthority.Snapshot;
            int distance = view.FirstX - view.SecondX;
            if (Math.Abs(distance) > DuelConfig.ActionRange(true, 1))
            {
                return distance < 0 ? 1 : 2;
            }
            // 电脑与玩家共享原生开放的取消窗口，优先接能负担的强招。
            if (view.SecondEnergy >= DuelConfig.UltimateCost && view.CanTakeAction(false, 3))
            {
                return 3 << 2;
            }
            if (view.SecondEnergy >= DuelConfig.SkillTwoCost && view.CanTakeAction(false, 4))
            {
                return 4 << 2;
            }
            return view.SecondEnergy >= DuelConfig.SkillCost && view.CanTakeAction(false, 2) ? 2 << 2 : 1 << 2;
        }

        // 核对房间，按连续游标补帧并消费结果。每个权威帧只能执行一次，回执必须等相应快照提交。
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
            ObserveDuelHashReceipt(reply, true);
            if (DuelHashMismatchFrame != 0)
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
                    _duelLastPacked = reply.frames[i];
                    DuelDisplay.Record(_duelPipe.Snapshot);
                    DuelPreviousSnapshot = before;
                    RecordDuelHashSample(before);
                }
                // 平时只交换稀疏样本。终局输出另核对一次，确认结果与权威一致。
                if (DuelSnapshot.Result != 0 && DuelSnapshot.Frame == reply.afterFrame + reply.frames.Length &&
                    (DuelSnapshot.Result != reply.result || DuelSnapshot.Hash != reply.finalHash))
                {
                    DuelError = "决斗同帧指纹不一致。";
                }
            }
            catch (Exception error)
            {
                DuelError = error.Message;
            }
            RebaseDuelPrediction(reply, current);
        }

        // 权威副本消费整段连续历史后才处理回执。长追帧不沿用旧预测轨迹，直到拿到最新锚点再开始。
        private void RebaseDuelPrediction(DuelReply reply, int beforeFrame)
        {
            if (Mode == PvpConnectionMode.Local || _duelPredictionFailed)
            {
                return;
            }
            if (!Connected || !DuelBothReady || DuelError.Length != 0 || DuelSnapshot.Result != 0)
            {
                _duelPrediction?.Suspend();
                return;
            }
            int advanced = DuelSnapshot.Frame - beforeFrame;
            if (advanced > DuelPrediction.MaxLead || (advanced > 0 && reply.inputAckFrame == 0))
            {
                _duelPrediction?.Suspend();
            }
            if (reply.inputAckFrame == 0 || reply.inputAckFrame != DuelSnapshot.Frame)
            {
                return;
            }
            try
            {
                if (reply.inputAckSequence > _duelInputSequence)
                {
                    throw new InvalidOperationException("决斗回执超出了本端已发送的输入序号。");
                }
                _duelPrediction ??= new DuelPrediction(DuelId, MySide == GoSide.Black);
                if (_duelPrediction.Rebase(_duelPipe, reply, _duelLastPacked))
                {
                    _duelPredictionAnchorAt = _clock.Elapsed.TotalSeconds;
                }
            }
            catch (Exception error)
            {
                DisableDuelPrediction(error);
            }
        }

        // 只关闭预测实例。权威快照、回放游标和联网输入继续工作，用户仍可完成本局。
        private void DisableDuelPrediction(Exception error)
        {
            _duelPrediction?.Dispose();
            _duelPrediction = null;
            _duelPredictionFailed = true;
            RevLog.Warn("决斗预测已退回权威插值：" + error.Message, "Pvp");
        }

        // 识别 P2P 决斗消息，区分宿主授权、客端请求与回执。
        private bool ReceiveLanDuel(PvpLanMessage message)
        {
            switch (message.type)
            {
                case "duel-challenge":
                    if (MySide == GoSide.Black && Core != null && DuelId.Length == 0 && message.name == Core.StateHash.ToString() &&
                        message.duelRequest?.fingerprint == DuelAuthority.NetworkFingerprint)
                    {
                        int operation = message.duelRequest.operation;
                        if ((operation == DuelAuthority.TestChallenge && CanTestDuel) ||
                            (operation == DuelAuthority.Challenge && DuelEligibility.CanChallenge(Core, GoSide.White, _duelAnalysis)))
                        {
                            OpenOwnedDuel(true, operation);
                        }
                    }
                    return true;
                case "duel-start":
                    if (MySide == GoSide.White && Core != null && message.rules == Core.StateHash.ToString() &&
                        message.duelRequest?.fingerprint == DuelAuthority.NetworkFingerprint &&
                        (message.duelRequest.operation == DuelAuthority.Challenge ||
                         (DuelTestEnabled && message.duelRequest.operation == DuelAuthority.TestChallenge)))
                    {
                        ActivateDuel(message.name);
                    }
                    return true;
                case "duel-input":
                    if (_duelAuthority != null && message.name == DuelId)
                    {
                        var reply = _duelAuthority.Exchange(2, message.duelRequest, _clock.ElapsedMilliseconds);
                        // 这份输入回执属于白方。宿主只观察共同指纹，不能把白方序号用于自己的预测。
                        ObserveDuelHashReceipt(reply, false);
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
            DuelHashSamplesAcknowledgedForCheck = 0;
            DuelHashCorruptFrameForCheck = 0;
            DuelComputerAiPaused = false;
#endif
            DuelPreviousSnapshot = default;
            DuelDisplay.Reset();
            _duelPrediction?.Dispose();
            _duelPrediction = null;
            _duelPredictionFailed = false;
            _duelPredictionAnchorAt = 0;
            _duelLastPacked = 0;
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
            _duelInputSequence = 0;
            _duelNextSend = 0;
            _duelHashPending.Clear();
            _duelHashNextSend = 0;
            _duelTerminalHashUntil = 0;
            DuelHashMatchedFrame = 0;
            DuelHashMismatchFrame = 0;
            _duelChallengeUntil = 0;
        }

#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
        // 恢复验收只读取已就绪的本地人机权威，正式联机房间不开放这个入口。
        internal bool DuelRecoveryAvailableForCheck => Mode == PvpConnectionMode.Local && IsComputerGame &&
            _duelSceneReady && DuelBothReady && _duelAuthority != null;

        // 只接受已就绪的本地人机决斗，下一次封帧按新开关采样白方。
        internal bool TrySetDuelComputerAiPaused(bool paused)
        {
            if (!DuelRecoveryAvailableForCheck || DuelError.Length != 0 || DuelSnapshot.Result != 0)
            {
                return false;
            }
            DuelComputerAiPaused = paused;
            return true;
        }

        // 在 Unity 主线程读取同一局的权威快照和检查点。没有暂停、换局或替换当前内核。
        internal byte[] SaveDuelCheckpointForCheck(out DuelSnapshot snapshot)
        {
            snapshot = default;
            if (!DuelRecoveryAvailableForCheck)
            {
                return null;
            }
            snapshot = _duelAuthority.Snapshot;
            return _duelAuthority.SaveCheckpoint();
        }

        // 只复制本局已封口的输入历史。测试会在新的原生实例中回放。
        internal DuelReplay CaptureDuelReplayForCheck()
        {
            return DuelRecoveryAvailableForCheck ? _duelAuthority.CaptureReplay() : null;
        }

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
