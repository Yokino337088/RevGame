using System;
using System.Collections.Generic;

namespace RevGame.Pvp.Duel
{
    // 网络请求是意图；它从来不能直接交给 C++。只有 Authority 的两席封帧才有执行资格。
    [Serializable]
    public sealed class DuelRequest
    {
        public int operation;
        public int fingerprint;
        public int afterFrame;
        public int inputFrame;
        public int input;
    }

    [Serializable]
    public sealed class DuelReply
    {
        public string id = "";
        public string error = "";
        public int readyMask;
        public int afterFrame;
        public int[] frames = Array.Empty<int>();
        public int result;
        public int finalHash;
    }

    public static class DuelEligibility
    {
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
        public const int Read = 0;
        public const int Challenge = 1;
        public const int Ready = 2;
        public const int Input = 3;
        private const int MaxBatch = 64;
        private readonly List<int> _frames = new List<int>(DuelConfig.FrameLimit);
        private readonly int[] _input = new int[2];
        private readonly DuelCorePipe _judge = new DuelCorePipe();
        private int _readyMask;
        private int _inputMask;
        private long _nextSeal;

        public DuelAuthority(string id)
        {
            Id = id;
        }

        public string Id { get; }
        public int Frame => _frames.Count;
        public DuelSnapshot Snapshot => _judge.Snapshot;

        public DuelReply Exchange(int side, DuelRequest request, long now)
        {
            if (side < 1 || side > 2 || request == null || request.fingerprint != DuelConfig.Fingerprint ||
                request.afterFrame < 0 || request.afterFrame > Frame ||
                (request.operation != Read && request.operation != Ready && request.operation != Input))
            {
                return new DuelReply { error = "决斗协议或配置版本不一致。" };
            }
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
                int packed = _input[0] | (_input[1] << DuelCorePipe.InputBits);
                _judge.Append(Frame + 1, packed);
                _frames.Add(packed);
                _inputMask = 0;
                _nextSeal = now + DuelConfig.FrameMs;
            }
            int count = Math.Min(MaxBatch, Frame - request.afterFrame);
            int[] frames = new int[count];
            _frames.CopyTo(request.afterFrame, frames, 0, count);
            return new DuelReply { id = Id, readyMask = _readyMask, afterFrame = request.afterFrame, frames = frames,
                result = Snapshot.Result, finalHash = Snapshot.Hash };
        }

        public void Dispose()
        {
            _judge.Dispose();
        }
    }
}
