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
        // 规则配置指纹，不同版本拒绝混用。
        public int fingerprint;
        // 请求端已消费的最大连续帧，权威方从下一帧起补发。
        public int afterFrame;
        // 本席意图对应帧号，只用于 Input。请求的 afterFrame 是本端已消费游标，回复同名字段是历史起点。
        public int inputFrame;
        // 单席五位方向、招式和跳跃意图。
        public int input;
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
        // 提交本席对下一逻辑帧的意图。
        public const int Input = 3;
        // 单个回复最多 64 帧，落后端按连续游标分批补。
        private const int MaxBatch = 64;
        // 已执行的整帧历史，数组下标 0 对应逻辑帧 1。
        private readonly List<int> _frames = new List<int>(DuelConfig.FrameLimit);
        // 下一待封帧的黑白输入槽，必须齐备才执行。
        private readonly int[] _input = new int[2];
        // 权威原生实例，和客户端副本执行同一整帧协议。
        private readonly DuelCorePipe _judge = new DuelCorePipe();
        // 场景加载屏障，双席就绪前不接战斗意图。
        private int _readyMask;
        // 当前帧已经占用的输入槽位，独立于场景就绪。
        private int _inputMask;
        // 允许下一次封帧的单调时间（毫秒），只用于限速。
        private long _nextSeal;

        // 创建权威封帧器，由所属房间或本地宿主结束时释放。
        public DuelAuthority(string id)
        {
            Id = id;
        }

        // 决斗标识，创建后固定。
        public string Id { get; }
        // 权威端已执行的最大连续帧号。
        public int Frame => _frames.Count;
        // 原生权威世界最后一帧的值副本。
        public DuelSnapshot Snapshot => _judge.Snapshot;
#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
        // 仅本机人机的开发指令可设置，每局实例创建时默认关闭。
        internal bool DebugNoDamage { get; set; }
#endif

        // 按已认证座位处理请求，返回游标后的历史。缺一席时保持当前世界。
        public DuelReply Exchange(int side, DuelRequest request, long now)
        {
            if (side < 1 || side > 2 || request == null || request.fingerprint != DuelConfig.Fingerprint ||
                request.afterFrame < 0 || request.afterFrame > Frame ||
                (request.operation != Read && request.operation != Ready && request.operation != Input))
            {
                return new DuelReply { error = "决斗协议或配置版本不一致。" };
            }
            // 从已认证的 1/2 座位换算到掩码位，输入包本身不能指定替谁操作。
            int bit = 1 << (side - 1);
            if (request.operation == Ready)
            {
                _readyMask |= bit;
            }
            else if (request.operation == Input && _readyMask == 3 && Snapshot.Result == 0)
            {
                if (!DuelCorePipe.ValidInput(request.input) || request.inputFrame < 1 || request.inputFrame > Frame + 1)
                {
                    return new DuelReply { error = "决斗输入帧不在接收窗口。" };
                }
                if (request.inputFrame == Frame + 1 && (_inputMask & bit) == 0)
                {
                    // 每个座位每帧第一条有效意图锁定，重发不能改写已确认材料。
                    _input[side - 1] = request.input;
                    _inputMask |= bit;
                }
            }
            if (_inputMask == 3 && now >= _nextSeal && Snapshot.Result == 0)
            {
                // 用低五位放黑方、高五位放白方。必须先成功执行，再把整帧加入可重放历史。
                int packed = _input[0] | (_input[1] << DuelCorePipe.InputBits);
#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
                if (DebugNoDamage)
                {
                    packed |= DuelCorePipe.NoDamageFrame;
                }
#endif
                _judge.Append(Frame + 1, packed);
                _frames.Add(packed);
                _inputMask = 0;
                _nextSeal = now + DuelConfig.FrameMs;
            }
            // 只返回请求游标之后的已执行帧，避免客户端自行填空或重播已经消费的输入。
            int count = Math.Min(MaxBatch, Frame - request.afterFrame);
            int[] frames = new int[count];
            _frames.CopyTo(request.afterFrame, frames, 0, count);
            return new DuelReply { id = Id, readyMask = _readyMask, afterFrame = request.afterFrame, frames = frames,
                result = Snapshot.Result, finalHash = Snapshot.Hash };
        }

        // 释放权威原生实例，随对局或房间退出调用。
        public void Dispose()
        {
            _judge.Dispose();
        }
    }
}
