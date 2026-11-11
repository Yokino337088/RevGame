using System;
using System.Collections.Generic;

namespace RevGame.Pvp.Duel
{
    // 网络请求是意图；它从来不能直接交给 C++。只有 Authority 的两席封帧才有执行资格。
    [Serializable]
    public sealed class DuelRequest
    {
        // 操作类型，区分只读、发起、就绪和意图提交。
        public int operation;
        // 网络协议与规则的联合指纹，不同版本拒绝混用。
        public int fingerprint;
        // 请求端已消费的最大连续帧，权威方从下一帧起补发。
        public int afterFrame;
        // 联机时是本席本次连接的单调输入序号，本地人机时是下一逻辑帧号。afterFrame 独立用于补帧。
        public int inputFrame;
        // 单席六位方向、招式和跳跃意图。
        public int input;
        // 附带的已执行样本帧号。0 表示本次不对账。
        public int hashFrame;
        // 样本帧的完整原生世界指纹。指纹本身可以为 0。
        public int hash;
    }

    [Serializable]
    // 权威方的回复，携带两席就绪状态和已提交历史。
    public sealed class DuelReply
    {
        // 当前决斗标识，用来识别迟到的旧局回复。
        public string id = "";
        // 拒绝原因，空串表示本次已处理。
        public string error = "";
        // 低两位分别表示黑、白已完成场景加载。
        public int readyMask;
        // 这份回复的起算游标；第一个元素的帧号等于该值加一。
        public int afterFrame;
        // 从 afterFrame + 1 起的连续完整帧，每项含两席意图。
        public int[] frames = Array.Empty<int>();
        // 最新胜负码，0 进行中、1 黑胜、2 白胜、3 平局。
        public int result;
        // 最新权威世界指纹，必须在相同帧号对账。
        public int finalHash;
        // 本次收到的有效对账样本帧。客户端收到确认后才从待发队列移除。
        public int hashAckFrame;
        // 双方共同确认一致的最新帧。0 表示本包没有公告。
        public int hashMatchedFrame;
        // 首个分歧样本的帧号。大于 0 时本局停止推进。
        public int hashMismatchFrame;
        // 以下回执属于哪个已封口帧。补帧未追到最新时为 0，防止客户端提前清理输入。
        public int inputAckFrame;
        // 回执帧已处理到的本席序号。方向和跳跃可退出预测重演，等待恢复期的招式见下两项。
        public int inputAckSequence;
        // 回执帧之后仍在等恢复期结束的招式序号。0 表示没有等待的招式。
        public int pendingActionSequence;
        // 上述招式最后可进入的权威帧，包含本帧。由权威确定，回滚重演不能延长它。
        public int pendingActionUntil;
    }

    // 共享的挑战资格，界面用来展示，权威方用来重新校验。
    public static class DuelEligibility
    {
        // 根据手数和估算分差检查指定座位是否明显落后，复用调用方提供的分析缓冲。
        public static bool CanChallenge(GoCore core, GoSide side, GoBoardAnalysis analysis)
        {
            if (core == null || side == GoSide.Empty || core.Phase == GoPhase.Finished || core.MoveNumber < DuelConfig.MinimumMove)
            {
                return false;
            }
            core.Analyze(analysis);
            int blackLead = analysis.Score.DifferenceHalfPoints;
            return side == GoSide.Black ? blackLead <= -DuelConfig.MinimumLead : blackLead >= DuelConfig.MinimumLead;
        }
    }

    /// <summary>每局一个权威封帧器。服务端归 Gate 线程，P2P 归黑方宿主线程，本机人机由本机所有。</summary>
    public sealed class DuelAuthority : IDisposable
    {
        // 只读取就绪与连续历史。
        public const int Read = 0;
        // 请求从围棋切换，传输入口会先校验资格。
        public const int Challenge = 1;
        // 声明本席已加载决斗场景。
        public const int Ready = 2;
        // 提交本席有序意图，由封帧器决定进入哪一逻辑帧。
        public const int Input = 3;
        // 开发环境从同一围棋房间强制发起决斗；传输入口校验后再转为 Read。
        public const int TestChallenge = 4;
        // 当前联机版本包含随整帧下发的输入回执。原生配置和回放格式独立版本化。
        public const int NetworkFingerprint = DuelConfig.Fingerprint ^ 0x60000000;
        // 单次宿主更新最多补三个固定步长，防止暂停恢复后长时间占用主线程或 Gate。
        private const int MaxCatchUpFrames = 3;
        // 单个回复最多 64 帧，落后端按连续游标分批补。
        internal const int MaxBatch = 64;
        // 已执行的整帧历史，数组下标 0 对应逻辑帧 1。
        private readonly List<DuelReplayFrame> _frames = new List<DuelReplayFrame>(1200);
        // 下一待封帧的黑白输入槽，必须齐备才执行。
        private readonly int[] _input = new int[2];
        // 权威原生实例，和客户端副本执行同一整帧协议。
        private readonly DuelCorePipe _judge;
        // 本机人机仍按两席同一帧输入运行。联网席位与权威时钟解耦。
        private readonly bool _networked;
        // 黑白各有一个连接内的输入邮箱，随本局创建，断线时清除未执行意图。
        private readonly DuelInputMailbox[] _networkInputs = new DuelInputMailbox[2];
        // 场景加载屏障，双席就绪前不接战斗意图。
        private int _readyMask;
        // 当前帧已经占用的输入槽位，独立于场景就绪。
        private int _inputMask;
        // 允许下一次封帧的单调时间（毫秒），只用于限速。
        private long _nextSeal;

        // 每席只顺序接纳固定间隔样本，再接纳终局样本。两者最小值就是共同对账上界。
        private readonly int[] _hashAccepted = new int[2];
        // 已经向各席发送的共同对账上界，避免每次普通回包都重复携带它。
        private readonly int[] _hashAnnounced = new int[2];
        // 首个有效样本分歧后冻结权威执行，保留原历史供诊断。
        private int _hashMismatchFrame;
        // 每席最后一帧实际封口后的输入回执。到包但尚未封口的意图只在邮箱内，不会出现在回执里。
        private readonly DuelInputMailbox[] _sealedInputs = new DuelInputMailbox[2];
#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
        // 按黑白座位统计通过核对的唯一对账样本，用于验证重试不会重复计数。
        private readonly int[] _hashReportsForCheck = new int[2];
        // 读取指定座位已接纳样本的数量，side 使用棋局约定的 1/2。
        internal int HashReportsForCheck(int side) => _hashReportsForCheck[side - 1];
#endif

        // 创建权威封帧器，由所属房间或本地宿主结束时释放。
        public DuelAuthority(string id, bool networked = true)
        {
            Id = id;
            _networked = networked;
            _judge = new DuelCorePipe(id);
        }

        // 决斗标识，创建后固定。
        public string Id { get; }
        // 权威端已执行的最大连续帧号。
        public int Frame => _frames.Count;
        // 原生权威世界最后一帧的值副本。
        public DuelSnapshot Snapshot => _judge.Snapshot;
        // 原生终局或指纹分歧后，宿主可以停止定时推进，历史仍供补帧读取。
        public bool Stopped => Snapshot.Result != 0 || _hashMismatchFrame != 0;

        // 本地宿主读权威时钟再采样两席输入，避免主线程和封帧器各自等待 50 毫秒。
        internal bool LocalFrameDue(long now)
        {
            return !_networked && now >= _nextSeal;
        }

        // 调用方必须仍持有本局线程。只导出已执行的世界边界，不包含两席尚未封口的输入。
        public byte[] SaveCheckpoint()
        {
            return _judge.SaveCheckpoint();
        }

        // 复制截至当前的权威帧。后续继续对战不会改动已经交给调用方的录像。
        public DuelReplay CaptureReplay()
        {
            return new DuelReplay(Id, _frames.ToArray());
        }
#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
        // 仅本机人机的开发指令可设置，每局实例创建时默认关闭。
        internal bool DebugNoDamage { get; set; }
#endif

        // 按已认证座位收意图和对账样本，返回游标后的连续历史。
        public DuelReply Exchange(int side, DuelRequest request, long now)
        {
            if (side < 1 || side > 2 || request == null ||
                request.fingerprint != (_networked ? NetworkFingerprint : DuelConfig.Fingerprint) ||
                request.afterFrame < 0 || request.afterFrame > Frame ||
                (request.operation != Read && request.operation != Ready && request.operation != Input))
            {
                return new DuelReply { error = "决斗协议或配置版本不一致。" };
            }
            int seat = side - 1;
            if (_hashMismatchFrame == 0 && !AcceptHashSample(seat, request))
            {
                return new DuelReply { id = Id, error = "决斗对账样本的帧号或顺序无效。" };
            }
            if (_hashMismatchFrame != 0)
            {
                return new DuelReply { id = Id, hashMismatchFrame = _hashMismatchFrame };
            }
            // 从已认证的 1/2 座位换算到掩码位，输入包本身不能指定替谁操作。
            int bit = 1 << (side - 1);
            if (request.operation == Ready)
            {
                _readyMask |= bit;
                if (_networked && _readyMask == 3 && _nextSeal == 0)
                {
                    _nextSeal = now + DuelConfig.FrameMs;
                }
            }
            else if (request.operation == Input && _readyMask == 3 && Snapshot.Result == 0)
            {
                if (!DuelCorePipe.ValidInput(request.input) || request.inputFrame < 1 ||
                    (!_networked && request.inputFrame > Frame + 1))
                {
                    return new DuelReply { error = "决斗输入帧不在接收窗口。" };
                }
                if (_networked)
                {
                    _networkInputs[seat].Stage(request.inputFrame, request.input, Frame);
                }
                else if (request.inputFrame == Frame + 1 && (_inputMask & bit) == 0)
                {
                    // 每个座位每帧第一条有效意图锁定，重发不能改写已确认材料。
                    _input[side - 1] = request.input;
                    _inputMask |= bit;
                }
            }
            if (!_networked && _inputMask == 3 && now >= _nextSeal && Snapshot.Result == 0)
            {
                Seal(_input[0], _input[1]);
                _inputMask = 0;
                // 普通渲染帧迟到仍沿原节拍；长停顿后从当前时刻重启，只封一帧。
                long next = _nextSeal + DuelConfig.FrameMs;
                _nextSeal = next > now ? next : now + DuelConfig.FrameMs;
            }
            // 只返回请求游标之后的已执行帧，避免客户端自行填空或重播已经消费的输入。
            int count = Math.Min(MaxBatch, Frame - request.afterFrame);
            int[] frames = count == 0 ? Array.Empty<int>() : new int[count];
            for (int i = 0; i < count; i++)
            {
                frames[i] = _frames[request.afterFrame + i].Packed;
            }
            int matched = Math.Min(_hashAccepted[0], _hashAccepted[1]);
            // 终局 Read 会低频重询。即使共同确认的首个回包丢了，也能拿回相同上界。
            int announce = request.hashFrame > 0 || request.operation == Read || matched > _hashAnnounced[seat] ? matched : 0;
            if (announce > 0)
            {
                _hashAnnounced[seat] = announce;
            }
            // 只有本包能把接收方带到当前权威帧时，才附这个帧的回执，不能用较新的回执清掉旧基线的输入。
            bool includesReceipt = _networked && Frame > 0 && request.afterFrame + count == Frame;
            DuelInputMailbox receipt = includesReceipt ? _sealedInputs[seat] : default;
            return new DuelReply { id = Id, readyMask = _readyMask, afterFrame = request.afterFrame, frames = frames,
                result = Snapshot.Result, finalHash = Snapshot.Result != 0 ? Snapshot.Hash : 0,
                hashAckFrame = request.hashFrame, hashMatchedFrame = announce,
                inputAckFrame = includesReceipt ? Frame : 0, inputAckSequence = receipt.Sequence,
                pendingActionSequence = receipt.ActionSequence, pendingActionUntil = receipt.ActionUntil };
        }

        // 宿主在自己的线程按单调毫秒推进。即使某席没有输入，也由权威端产生可回放的完整帧。
        public void AdvanceNetwork(long now)
        {
            if (!_networked || _readyMask != 3 || _nextSeal == 0 || Stopped)
            {
                return;
            }
            for (int i = 0; i < MaxCatchUpFrames && now >= _nextSeal && !Stopped; i++)
            {
                DuelSnapshot before = Snapshot;
                Seal(_networkInputs[0].Take(before, black: true), _networkInputs[1].Take(before, black: false));
                // Take 先消费按钮，Seal 成功后再发布回执。连续帧与回执由同一宿主线程按这个顺序写入。
                _sealedInputs[0] = _networkInputs[0];
                _sealedInputs[1] = _networkInputs[1];
                _nextSeal += DuelConfig.FrameMs;
            }
            if (now >= _nextSeal)
            {
                // 超过本次预算时重定时墙钟，逻辑帧号仍然连续，每步仍是固定的 50 毫秒。
                _nextSeal = now + DuelConfig.FrameMs;
            }
        }

        // 已认证的席位掉线后停在当前整帧，丢弃双方尚未封口的输入。换连接的席位必须重新就绪。
        public void DisconnectNetworkSeat(int side)
        {
            if (!_networked || side < 1 || side > 2)
            {
                return;
            }
            _readyMask &= ~(1 << (side - 1));
            _nextSeal = 0;
            int other = 2 - side;
            int otherSequence = _networkInputs[other].Sequence;
            Array.Clear(_networkInputs, 0, _networkInputs.Length);
            _networkInputs[other].Sequence = otherSequence;
            _sealedInputs[side - 1] = default;
            // 未断线的席位保留序号水位。两边的旧招式都已取消，不能在重连就绪时从旧回执恢复。
            _sealedInputs[other].RestoreAction(0, 0, 0);
        }

        // 两席输入打包后一次提交原生世界，成功后才开放连续历史给其他端。
        private void Seal(int black, int white)
        {
            int packed = black | (white << DuelCorePipe.InputBits);
#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
            if (DebugNoDamage)
            {
                packed |= DuelCorePipe.NoDamageFrame;
            }
#endif
            _judge.Append(Frame + 1, packed);
            _frames.Add(new DuelReplayFrame(packed, Snapshot));
        }

        // 在收输入之前，用记录的同帧原生指纹核对样本。重复报文可重新确认，但不会增加已核对数量。
        private bool AcceptHashSample(int seat, DuelRequest request)
        {
            int frame = request.hashFrame;
            if (frame == 0)
            {
                return request.hash == 0;
            }
            bool finished = Snapshot.Result != 0;
            if (frame < 0 || frame > request.afterFrame || request.hash < 0 ||
                (frame % DuelConfig.HashSampleFrames != 0 && (!finished || frame != Frame)))
            {
                return false;
            }
            int accepted = _hashAccepted[seat];
            int next = accepted + DuelConfig.HashSampleFrames;
            if (finished)
            {
                next = Math.Min(next, Frame);
            }
            if (frame > accepted && frame != next)
            {
                return false;
            }
            if (_frames[frame - 1].Hash != request.hash)
            {
                _hashMismatchFrame = frame;
                return true;
            }
            if (frame > accepted)
            {
                _hashAccepted[seat] = frame;
#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
                _hashReportsForCheck[seat]++;
#endif
            }
            return true;
        }

        // 释放权威原生实例，随对局或房间退出调用。
        public void Dispose()
        {
            _judge.Dispose();
        }
    }

    // 权威与预测使用同一套输入邮箱规则。邮箱只决定送什么给 C++，出招、跳跃和命中是否成立由内核判断。
    internal struct DuelInputMailbox
    {
        // 如果没有更新，方向和等待中的招式各允许留四个待封帧，单位为逻辑帧。
        private const int HoldFrames = 4;
        // 已接纳的最大输入序号。严格递增，同序号重试不会重复触发按钮。
        public int Sequence;
        // 当前等待恢复期结束的招式所对应的输入序号。0 代表没有。
        public int ActionSequence { get; private set; }
        // 允许等待招式进入的最后一帧，包含此帧。
        public int ActionUntil { get; private set; }
        // 持续方向，0 回中、1 左、2 右。
        private int _direction;
        // 当前方向仍有效的最后一个逻辑帧。
        private int _directionUntil;
        // 仍在等待的招式槽，0 表示无招式。
        private int _action;
        // 已收到、下一帧消费的跳跃边沿。
        private bool _jump;

        // 接纳序号更新的一席输入。方向包不会抹掉之前收到、尚未封口的按钮边沿。
        public void Stage(int sequence, int packed, int frame)
        {
            if (sequence <= Sequence)
            {
                return;
            }
            Sequence = sequence;
            RestoreDirection(packed & 3, frame);
            int action = DuelCorePipe.ReadActionInput(packed);
            if (action != 0)
            {
                RestoreAction(sequence, action, frame + HoldFrames);
            }
            _jump |= (packed & DuelCorePipe.Jump) != 0;
        }

        // 用最近一次确认的方向启动预测。与新输入一样只能保留短窗，等待本机新采样替换。
        public void RestoreDirection(int direction, int frame)
        {
            _direction = direction;
            _directionUntil = frame + HoldFrames;
        }

        // 从权威回执恢复唯一还在缓冲的招式，沿用原到期帧，不能在重演时给它续期。
        public void RestoreAction(int sequence, int action, int untilFrame)
        {
            ActionSequence = sequence;
            _action = action;
            ActionUntil = untilFrame;
        }

        // 根据步进前快照选择下一帧输入。跳跃消费一次，攻击只在原生恢复期结束或过期时移出邮箱。
        public int Take(DuelSnapshot before, bool black)
        {
            int next = before.Frame + 1;
            int packed = next <= _directionUntil ? _direction : 0;
            if (_jump)
            {
                packed |= DuelCorePipe.Jump;
                _jump = false;
            }
            if (_action != 0)
            {
                if (next > ActionUntil)
                {
                    RestoreAction(0, 0, 0);
                }
                else if (before.CanTakeAction(black, _action))
                {
                    packed |= _action << 2;
                    RestoreAction(0, 0, 0);
                }
            }
            return packed;
        }
    }
}
