using System;
using RevGame.Pvp.Duel;
using UnityEngine;

namespace RevGame.Pvp
{
    public sealed partial class PvpService
    {
        private readonly GoBoardAnalysis _duelAnalysis = new GoBoardAnalysis();
        private GoCore _duelExaminedBoard;
        private bool _duelEligible;
        private bool _duelPeerCompatible;
        private DuelAuthority _duelAuthority;
        private DuelCorePipe _duelPipe;
        private bool _duelSceneReady;
        private int _duelReadyMask;
        private int _duelQueuedAction;
        private int _duelDirection;
        private bool _duelQueuedJump;
        private int _duelIntent;
        private int _duelIntentFrame;
        private double _duelNextSend;
        private double _duelChallengeUntil;
        public string DuelId { get; private set; } = "";
        public string DuelError { get; private set; } = "";
        public DuelSnapshot DuelSnapshot => _duelPipe?.Snapshot ?? default;
        public bool DuelBothReady => _duelReadyMask == 3;
        public bool DuelReconnectAvailable => _server != null && !_server.Busy;

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

        private void ActivateDuel(string id)
        {
            if (string.IsNullOrEmpty(id) || DuelId.Length != 0)
            {
                return;
            }
            try
            {
                _duelPipe = new DuelCorePipe();
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

        public void MarkDuelSceneReady()
        {
            _duelSceneReady = DuelId.Length != 0;
        }

        public void SetDuelInput(int direction, int action, bool jump = false)
        {
            _duelDirection = direction < 0 ? 1 : direction > 0 ? 2 : 0;
            // 点按可能发生在两个网络帧之间。锁存跳跃，直到它被打包进下一帧意图。
            _duelQueuedJump |= jump;
            if (action >= 1 && action <= 3)
            {
                _duelQueuedAction = action;
            }
        }

        public void SetDuelError(string error)
        {
            DuelError = error;
        }

        private DuelRequest DuelRequestFor(int operation)
        {
            return new DuelRequest { operation = operation, fingerprint = DuelConfig.Fingerprint, afterFrame = DuelSnapshot.Frame };
        }

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
                int next = DuelSnapshot.Frame + 1;
                if (_duelIntentFrame != next)
                {
                    _duelIntentFrame = next;
                    _duelIntent = _duelDirection | (_duelQueuedAction << 2) | (_duelQueuedJump ? DuelCorePipe.Jump : 0);
                    _duelQueuedJump = false;
                    _duelQueuedAction = 0;
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
                for (int i = current - reply.afterFrame; i < reply.frames.Length; i++)
                {
                    _duelPipe.Append(reply.afterFrame + i + 1, reply.frames[i]);
                }
                if (DuelSnapshot.Result != 0 && (DuelSnapshot.Result != reply.result || DuelSnapshot.Hash != reply.finalHash))
                {
                    DuelError = "决斗终局指纹不一致。";
                }
            }
            catch (Exception error)
            {
                DuelError = error.Message;
            }
        }

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

        private void ResetDuel()
        {
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
            _duelQueuedAction = 0;
            _duelDirection = 0;
            _duelQueuedJump = false;
            _duelIntent = 0;
            _duelIntentFrame = 0;
            _duelNextSend = 0;
            _duelChallengeUntil = 0;
        }

#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
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
