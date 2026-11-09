#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
using System;
using System.Collections.Generic;
using System.Diagnostics;
using RevGame.Pvp.Duel;
using RevGame.Pvp.Networking;

namespace RevGame.Pvp
{
    /// <summary>
    /// 两份隔离 PvP 服务的真实局域网业务验收。两端只走 Begin、StartLan、Invite、ReplyInvitation、Submit、LeaveRoom。
    /// 不注入消息、不修改棋盘、不共享 GoCore：每手实际经过 UDP/UTP、版本握手、协议解析与独立规则重算。
    /// 外层每帧 Tick。超时只用于测试收尾，失败和取消都 Dispose 两端，保留玩家服务和存档。
    /// </summary>
    public sealed class PvpLanGameChecks : IDisposable
    {
        private readonly PvpConfig _config;
        private readonly bool _agents;
        private readonly bool _longGame;
        // 是否运行本次决斗分支；旧围棋回归仍使用原路径。
        private readonly bool _duel;
        // 通过白方真实 UTP 连接验证空棋盘测试入口，不伪造局面或权威回包。
        private readonly bool _duelTestEntry;
        // 对账验收延长决斗到真实终局。错误样本测试只改白方发出的对账值，原生命令和快照均不变。
        private readonly bool _duelHashAudit;
        private readonly bool _duelHashMismatch;
        // Dispose 会清理服务的局内计数，报告保留最后一次观测。
        private int _hostMatchedFrame, _guestMatchedFrame, _hostMismatchFrame, _guestMismatchFrame;
        private int _hostAcknowledged, _guestAcknowledged, _authorityBlackReports, _authorityWhiteReports;
        private int _hostDuelFrame, _guestDuelFrame, _hostDuelHash, _guestDuelHash, _duelTerminalFrame, _firstPeriodicMatchedFrame;
        private string _hostDuelError = "", _guestDuelError = "";
        // 双方收到分歧后，跨过三个逻辑帧的墙钟时间再核对双方没有继续推进。
        private double _duelMismatchObservedAt;
        private int _hostFrameAtMismatch, _guestFrameAtMismatch;
        private bool _frozenAfterMismatch;
        // 两端已核对的布置棋谱序号；下一手必须等双方同步才提交。
        private int _duelSeedSequence;
        private PvpService _host;
        private PvpService _guest;
        private double _deadline;
        private int _step;
        private bool _disposed;

        public bool Complete { get; private set; }
        public bool Passed { get; private set; }
        public bool Cleaned { get; private set; }
        public string Detail { get; private set; } = "尚未开始";
        public int Assertions { get; private set; }
        public int HostSequence { get; private set; }
        public int GuestSequence { get; private set; }
        public string HostHash { get; private set; } = string.Empty;
        public string GuestHash { get; private set; } = string.Empty;
        public string Winner { get; private set; } = string.Empty;
        public int BlackCaptures { get; private set; }

        // 创建隔离 UTP 夹具，duel 模式验证从棋局到原生封帧。
        public PvpLanGameChecks(PvpConfig config, bool agents = false, bool longGame = false, bool duel = false, bool duelHashAudit = false, bool duelHashMismatch = false, bool duelTestEntry = false)
        {
            _config = config;
            _agents = agents;
            _longGame = longGame;
            _duel = duel;
            _duelTestEntry = duelTestEntry;
            _duelHashAudit = duelHashAudit;
            _duelHashMismatch = duelHashMismatch;
        }

        // 把同一 UTP 夹具的两席观测与宿主权威收包计数一起交给验收工具。
        internal Dictionary<string, object> DuelHashState()
        {
            return new Dictionary<string, object>
            {
                ["enabled"] = _duelHashAudit, ["injectMismatch"] = _duelHashMismatch,
                ["sampleInterval"] = DuelConfig.HashSampleFrames, ["terminalFrame"] = _duelTerminalFrame,
                ["firstPeriodicMatchedFrame"] = _firstPeriodicMatchedFrame,
                ["hostFrame"] = _hostDuelFrame, ["guestFrame"] = _guestDuelFrame,
                ["hostHash"] = _hostDuelHash, ["guestHash"] = _guestDuelHash,
                ["hostMatchedFrame"] = _hostMatchedFrame, ["guestMatchedFrame"] = _guestMatchedFrame,
                ["hostMismatchFrame"] = _hostMismatchFrame, ["guestMismatchFrame"] = _guestMismatchFrame,
                ["hostAcknowledged"] = _hostAcknowledged, ["guestAcknowledged"] = _guestAcknowledged,
                ["authorityBlackReports"] = _authorityBlackReports, ["authorityWhiteReports"] = _authorityWhiteReports,
                ["frozenAfterMismatch"] = _frozenAfterMismatch,
                ["hostError"] = _hostDuelError, ["guestError"] = _guestDuelError
            };
        }

        public bool Start(out string reason)
        {
            if (_disposed || _host != null || _config == null)
            {
                reason = "局域网业务夹具已使用或缺少真实导表配置。";
                return false;
            }
            _deadline = Now + (_longGame ? 120 : _duel ? 45 : 20);
            _host = new PvpService();
            _guest = new PvpService();
            try
            {
                _host.Begin(_config);
                _guest.Begin(_config);
                _host.StartLan("业务验收黑方");
                _guest.StartLan("业务验收白方");
                if (_host.Lan == null || !_host.Lan.IsDiscovering || _guest.Lan == null || !_guest.Lan.IsDiscovering)
                {
                    reason = "局域网业务发现未启动：" + _host.Status + "；" + _guest.Status;
                    Fail(reason);
                    return false;
                }
                Detail = "等待两份真实业务服务发现彼此";
                reason = string.Empty;
                return true;
            }
            catch (Exception error)
            {
                reason = "业务夹具启动失败：" + error.Message;
                Fail(reason);
                return false;
            }
        }

        public void Tick()
        {
            if (Complete || _disposed || _host == null)
            {
                return;
            }
            try
            {
                _host.Poll();
                _guest.Poll();
                CaptureState();
                if (Now >= _deadline)
                {
                    Fail("局域网业务护栏超时，步骤 " + _step + "：" + Detail + "；黑方=" + _host.Status + "；白方=" + _guest.Status);
                    return;
                }
                Advance();
            }
            catch (Exception error)
            {
                Fail("局域网业务夹具异常：" + error);
            }
        }

        /// <summary>每个变更动作只在进入下一步前执行一次；等待只读状态，不能因超时重放同一手棋。</summary>
        private void Advance()
        {
            switch (_step)
            {
                case 0:
                    if (!HasPeer(_host.Lan, _guest.Lan.LocalId) || !HasPeer(_guest.Lan, _host.Lan.LocalId))
                    {
                        return;
                    }
                    Assertions += 2;
                    if (!Check(_host.Invite(_guest.Lan.LocalId), _host.Status))
                    {
                        return;
                    }
                    Next("等待白方收到并接受实际邀请");
                    break;
                case 1:
                    if (_guest.Lan.IncomingInvitation == null)
                    {
                        return;
                    }
                    if (!Check(_guest.ReplyInvitation(true), _guest.Status))
                    {
                        return;
                    }
                    Next("等待双方协议版本和规则握手完成");
                    break;
                case 2:
                    if (!_host.Connected || !_guest.Connected || _host.Core == null || _guest.Core == null)
                    {
                        return;
                    }
                    if (!Check(_host.MySide == GoSide.Black && _guest.MySide == GoSide.White, "邀请者没有执黑或受邀者没有执白") || !Synced(0))
                    {
                        return;
                    }
                    if (_duel)
                    {
                        _step = 30;
                        return;
                    }
                    if (!Submit(_host, GoAction.Play, 1, 0))
                    {
                        return;
                    }
                    Next("等待黑(1,0)在双方提交");
                    break;
                // 用真实传输让黑方下子、白方停手，构造有分差的局面。
                case 30:
                    if (_duelTestEntry)
                    {
                        if (Check(_host.Core.MoveNumber == 0 && _guest.Core.MoveNumber == 0 &&
                                  !_host.CanChallengeDuel && !_guest.CanChallengeDuel &&
                                  _host.CanTestDuel && _guest.CanTestDuel && _guest.TestDuel(),
                                  "空棋盘正常挑战关闭，白方测试入口可经真实 UTP 请求"))
                        {
                            Next("等待黑方批准空棋盘测试并同步同一决斗标识");
                        }
                        return;
                    }
                    if (!Synced(_duelSeedSequence))
                    {
                        return;
                    }
                    if (_duelSeedSequence >= Duel.DuelConfig.MinimumMove && _guest.CanChallengeDuel)
                    {
                        if (Check(!_host.CanChallengeDuel && _guest.ChallengeDuel(), "只有劣势白方可走 P2P 决斗入口"))
                        {
                            Next("等待实际 UTP 传送决斗邀请");
                        }
                        return;
                    }
                    int point = _duelSeedSequence / 2;
                    bool blackTurn = _duelSeedSequence % 2 == 0;
                    if (Submit(blackTurn ? _host : _guest, blackTurn ? GoAction.Play : GoAction.Pass, point % 9, point / 9))
                    {
                        _duelSeedSequence++;
                    }
                    break;
                // 先对齐双端决斗标识，只标记宿主就绪以检验屏障。
                case 31:
                    if (_host.DuelId.Length == 0 || _guest.DuelId.Length == 0)
                    {
                        return;
                    }
                    if (Check(_host.DuelId == _guest.DuelId, "P2P 双端决斗标识一致"))
                    {
                        _host.MarkDuelSceneReady();
                        Next("只标记一端就绪");
                    }
                    break;
                // 确认单边就绪没有推进后，再让白方就绪。
                case 32:
                    if (!Check(_host.DuelSnapshot.Frame == 0 && _guest.DuelSnapshot.Frame == 0, "单边就绪不能推进帧"))
                    {
                        return;
                    }
                    if (_duelHashMismatch)
                    {
                        // 配置只属于本测试的客端。它仍走真实白方 UTP 请求，宿主不会收到本地伪造的结果。
                        _guest.DuelHashCorruptFrameForCheck = DuelConfig.HashSampleFrames;
                    }
                    _guest.MarkDuelSceneReady();
                    Next("等待双方原生 FIFO 和 UTP 帧流推进");
                    break;
                // 两端通过正式意图入口移动和攻击，在同一帧核对世界指纹与命中。
                case 33:
                    int frame = _host.DuelSnapshot.Frame;
                    _host.SetDuelInput(frame < 12 ? 1 : 0, frame >= 12 ? 1 : 0);
                    _guest.SetDuelInput(frame < 12 ? -1 : 0, 0);
                    if (_duelHashAudit)
                    {
                        CheckDuelHashExchange();
                        return;
                    }
                    if (frame >= 25 && frame == _guest.DuelSnapshot.Frame &&
                        Check(_host.DuelSnapshot.Hash == _guest.DuelSnapshot.Hash &&
                              _host.DuelSnapshot.SecondHealth < Duel.DuelConfig.Health &&
                              _host.DuelError.Length == 0 && _guest.DuelError.Length == 0, "P2P 同帧原生指纹和实际命中一致"))
                    {
                        Passed = true;
                        Complete = true;
                        Detail = (_duelTestEntry ? "P2P UTP 空棋盘白方测试入口与封帧通过，棋步=0 帧=" : "P2P UTP 封帧验证通过，帧=") +
                            frame + " 指纹=" + _host.DuelSnapshot.Hash;
                        Dispose();
                    }
                    break;
                case 3:
                    if (!Synced(1) || !Submit(_guest, GoAction.Play, 0, 0))
                    {
                        return;
                    }
                    Next("等待白(0,0)在双方提交");
                    break;
                case 4:
                    if (!Synced(2) || !Submit(_host, GoAction.Play, 0, 1))
                    {
                        return;
                    }
                    Next("等待黑(0,1)提掉角上白子");
                    break;
                case 5:
                    if (!Synced(3))
                    {
                        return;
                    }
                    if (_agents)
                    {
                        // 仅替换HTTP策略，两端仍通过真实UTP发送、解析并独立重算每手指纹。
                        if (_longGame)
                        {
                            PvpAgentChecks.InstallLongGame(_host);
                            PvpAgentChecks.InstallLongGame(_guest);
                        }
                        else
                        {
                            PvpAgentChecks.Install(_host);
                            PvpAgentChecks.Install(_guest);
                        }
                        if (!Check(_host.SetAgentControl(true) && _guest.SetAgentControl(true), "双方Agent托管未开启"))
                        {
                            return;
                        }
                        BlackCaptures = _host.Core.BlackCaptures;
                        _step = 20;
                        return;
                    }
                    bool capture = _host.Core.BlackCaptures == 1 && _guest.Core.BlackCaptures == 1
                        && _host.Core.GetStone(0, 0) == GoSide.Empty && _guest.Core.GetStone(0, 0) == GoSide.Empty;
                    if (!Check(capture, "角上提子结果或双方提子计数不一致") || !Submit(_guest, GoAction.Pass))
                    {
                        return;
                    }
                    BlackCaptures = 1;
                    Next("等待白方真实停一手");
                    break;
                case 6:
                    if (!Synced(4) || !Submit(_host, GoAction.Pass))
                    {
                        return;
                    }
                    _step = 9;
                    break;
                case 20:
                    if (_host.Core.Phase != GoPhase.Finished || _guest.Core.Phase != GoPhase.Finished)
                    {
                        return;
                    }
                    if (_longGame && !Check(_host.Core.PlacementLimitReached && _host.Core.StateHash == _guest.Core.StateHash, "长局未落满68手或指纹分歧"))
                    {
                        return;
                    }
                    _step = 9;
                    break;
                case 9:
                    if (!Synced(_longGame ? _host.Core.Sequence : 5))
                    {
                        return;
                    }
                    bool result = _host.Core.Phase == GoPhase.Finished && _guest.Core.Phase == GoPhase.Finished
                        && _host.Core.Winner == _guest.Core.Winner && _host.Core.EndReason == GoEndReason.Scored
                        && _guest.Core.EndReason == GoEndReason.Scored && _host.Core.Score.DifferenceHalfPoints == _guest.Core.Score.DifferenceHalfPoints;
                    if (!Check(result, "双方终局阶段、胜者或计分不一致"))
                    {
                        return;
                    }
                    Winner = _host.Core.Winner.ToString();
                    _host.LeaveRoom();
                    Next("等待真实离开消息送达并关闭连接");
                    break;
                case 10:
                    if (_host.Lan != null || _host.Core != null || _guest.Connected)
                    {
                        return;
                    }
                    if (!Check(_guest.Core != null && (_longGame ? _guest.Core.PlacementLimitReached : _guest.Core.Sequence == 5) && _guest.Core.Phase == GoPhase.Finished, "对方退出不应删掉本端已完成棋谱"))
                    {
                        return;
                    }
                    Dispose();
                    if (!Check(Cleaned && _host.Lan == null && _guest.Lan == null, "业务夹具退出后仍持有局域网端点"))
                    {
                        return;
                    }
                    Complete = true;
                    Passed = true;
                    Detail = "双端真实业务联机、规则握手、提子、每手指纹一致、" + (_longGame ? "落满68手" : "双停一手") + "自动计分及退出清理通过";
                    break;
            }
        }

        // 健康用例必须等到两席收到终局确认。失败用例必须是同一个已封口样本的分歧。
        private void CheckDuelHashExchange()
        {
            DuelSnapshot host = _host.DuelSnapshot;
            DuelSnapshot guest = _guest.DuelSnapshot;
            _hostDuelFrame = host.Frame;
            _guestDuelFrame = guest.Frame;
            _hostDuelHash = host.Hash;
            _guestDuelHash = guest.Hash;
            _hostMatchedFrame = _host.DuelHashMatchedFrame;
            _guestMatchedFrame = _guest.DuelHashMatchedFrame;
            _hostMismatchFrame = _host.DuelHashMismatchFrame;
            _guestMismatchFrame = _guest.DuelHashMismatchFrame;
            _hostAcknowledged = _host.DuelHashSamplesAcknowledgedForCheck;
            _guestAcknowledged = _guest.DuelHashSamplesAcknowledgedForCheck;
            _authorityBlackReports = _host.DuelHashReportsForCheck((int)GoSide.Black);
            _authorityWhiteReports = _host.DuelHashReportsForCheck((int)GoSide.White);
            _hostDuelError = _host.DuelError;
            _guestDuelError = _guest.DuelError;
            if (_duelHashMismatch)
            {
                if (_hostMismatchFrame == 0 || _guestMismatchFrame == 0)
                {
                    return;
                }
                if (_duelMismatchObservedAt == 0)
                {
                    if (!Check(_hostMismatchFrame == DuelConfig.HashSampleFrames &&
                               _guestMismatchFrame == _hostMismatchFrame &&
                               _hostDuelError.Length > 0 && _guestDuelError.Length > 0 &&
                               host.Frame >= _hostMismatchFrame && guest.Frame >= _guestMismatchFrame,
                               "白方错误指纹应通过 UTP 被权威发现，并由两端报告同一个样本帧。"))
                    {
                        return;
                    }
                    _hostFrameAtMismatch = host.Frame;
                    _guestFrameAtMismatch = guest.Frame;
                    _duelMismatchObservedAt = Now;
                    return;
                }
                if (Now - _duelMismatchObservedAt < 3 * DuelConfig.FrameMs / 1000.0)
                {
                    return;
                }
                _frozenAfterMismatch = host.Frame == _hostFrameAtMismatch && guest.Frame == _guestFrameAtMismatch;
                if (!Check(_frozenAfterMismatch, "两端发现指纹分歧后不应继续推进。"))
                {
                    return;
                }
                CompleteDuelHashCheck("白方错误样本经 UTP 被权威拒绝，两端报告帧 " + _hostMismatchFrame + " 分歧并停止推进。");
                return;
            }
            if (_hostMismatchFrame != 0 || _guestMismatchFrame != 0 ||
                _hostDuelError.Length != 0 || _guestDuelError.Length != 0)
            {
                Fail("正常决斗的同帧对账不应报错。");
                return;
            }
            int interval = DuelConfig.HashSampleFrames;
            if (host.Result == 0 || guest.Result == 0)
            {
                if (_firstPeriodicMatchedFrame == 0 && _hostMatchedFrame >= interval && _guestMatchedFrame >= interval)
                {
                    _firstPeriodicMatchedFrame = Math.Min(_hostMatchedFrame, _guestMatchedFrame);
                }
                return;
            }
            _duelTerminalFrame = host.Frame;
            if (host.Frame != guest.Frame || host.Result != guest.Result || host.Hash != guest.Hash || host.SecondHealth != 0)
            {
                Fail("两个副本必须在实际命中结束后得到同一终局。");
                return;
            }
            if (_hostMatchedFrame < _duelTerminalFrame || _guestMatchedFrame < _duelTerminalFrame)
            {
                return;
            }
            // 终局恰好落在周期采样帧时，两种理由只产生一份报告。
            int expectedSamples = (_duelTerminalFrame + interval - 1) / interval;
            if (!Check(_firstPeriodicMatchedFrame == interval && _duelTerminalFrame > interval &&
                       _hostMatchedFrame == _duelTerminalFrame &&
                       _guestMatchedFrame == _duelTerminalFrame && _hostAcknowledged == expectedSamples &&
                       _guestAcknowledged == expectedSamples && _authorityBlackReports == expectedSamples &&
                       _authorityWhiteReports == expectedSamples, "黑白两席每个周期及终局样本应恰好被权威确认一次"))
            {
                return;
            }
            CompleteDuelHashCheck("P2P UTP 周期和终局对账通过，终局帧=" + _duelTerminalFrame + "，每席样本=" + expectedSamples);
        }

        private void CompleteDuelHashCheck(string detail)
        {
            Passed = true;
            Complete = true;
            Detail = detail;
            Dispose();
        }

        private bool Synced(int sequence)
        {
            if (_host.Core == null || _guest.Core == null || _host.Core.Sequence < sequence || _guest.Core.Sequence < sequence)
            {
                return false;
            }
            return Check(_host.Core.Sequence == sequence && _guest.Core.Sequence == sequence && _host.Core.StateHash == _guest.Core.StateHash,
                "第 " + sequence + " 手双方序号或完整状态指纹不一致");
        }

        private bool Submit(PvpService service, GoAction action, int x = -1, int y = -1)
        {
            return Check(service.Submit(action, x, y), "提交 " + action + " 失败：" + service.Status);
        }

        private void CaptureState()
        {
            // 离开会清空房主棋局；保留退出前最后一次非空值，报告仍能核对最终双方指纹。
            if (_host.Core != null)
            {
                HostSequence = _host.Core.Sequence;
                HostHash = _host.Core.StateHash.ToString();
            }
            if (_guest.Core != null)
            {
                GuestSequence = _guest.Core.Sequence;
                GuestHash = _guest.Core.StateHash.ToString();
            }
        }

        private bool Check(bool condition, string reason)
        {
            if (!condition)
            {
                Fail(reason);
                return false;
            }
            Assertions++;
            return true;
        }

        private void Next(string detail)
        {
            _step++;
            Detail = detail;
        }

        private void Fail(string reason)
        {
            Complete = true;
            Passed = false;
            Detail = reason;
            Dispose();
        }

        private static bool HasPeer(LanPeerTransport transport, string id)
        {
            foreach (LanPeerInfo peer in transport.Peers)
            {
                if (peer.Id == id)
                {
                    return true;
                }
            }
            return false;
        }

        private static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            try
            {
                _host?.Dispose();
            }
            finally
            {
                _guest?.Dispose();
                Cleaned = true;
            }
        }
    }
}
#endif
