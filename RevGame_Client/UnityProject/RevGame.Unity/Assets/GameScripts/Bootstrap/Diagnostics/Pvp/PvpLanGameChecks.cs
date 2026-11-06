#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
using System;
using System.Diagnostics;
using RevGame.Pvp.Networking;

namespace RevGame.Pvp
{
    /// <summary>
    /// 两份隔离 PvP 服务的真实局域网业务验收。两端只走 Begin、StartLan、Invite、ReplyInvitation、Submit、LeaveRoom。
    /// 不注入消息、不修改棋盘、不共享 GoCore：每手实际经过 UDP/UTP、版本握手、协议解析与独立规则重算。
    /// 外层每帧 Tick；20秒是测试护栏，不是游戏规则。失败和取消都 Dispose 两端，不影响玩家服务或存档。
    /// </summary>
    public sealed class PvpLanGameChecks : IDisposable
    {
        private readonly PvpConfig _config;
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

        public PvpLanGameChecks(PvpConfig config)
        {
            _config = config;
        }

        public bool Start(out string reason)
        {
            if (_disposed || _host != null || _config == null)
            {
                reason = "局域网业务夹具已使用或缺少真实导表配置。";
                return false;
            }
            _deadline = Now + 20;
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
                    Fail("20秒业务护栏超时，步骤 " + _step + "：" + Detail + "；黑方=" + _host.Status + "；白方=" + _guest.Status);
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
                    if (!Submit(_host, GoAction.Play, 1, 0))
                    {
                        return;
                    }
                    Next("等待黑(1,0)在双方提交");
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
                    Next("等待黑方真实停一手并进入协商计分");
                    break;
                case 7:
                    if (!Synced(5))
                    {
                        return;
                    }
                    if (!Check(_host.Core.Phase == GoPhase.Scoring && _guest.Core.Phase == GoPhase.Scoring, "双方连续停一手后没有进入计分阶段")
                        || !Submit(_guest, GoAction.ConfirmScore))
                    {
                        return;
                    }
                    Next("等待白方单独确认计分");
                    break;
                case 8:
                    if (!Synced(6))
                    {
                        return;
                    }
                    bool onlyWhite = _host.Core.HasConfirmed(GoSide.White) && _guest.Core.HasConfirmed(GoSide.White)
                        && !_host.Core.HasConfirmed(GoSide.Black) && !_guest.Core.HasConfirmed(GoSide.Black);
                    if (!Check(onlyWhite, "一方确认不应代替另一方确认") || !Submit(_host, GoAction.ConfirmScore))
                    {
                        return;
                    }
                    Next("等待双方完成同一终局");
                    break;
                case 9:
                    if (!Synced(7))
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
                    if (!Check(_guest.Core != null && _guest.Core.Sequence == 7 && _guest.Core.Phase == GoPhase.Finished, "对方退出不应删掉本端已完成棋谱"))
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
                    Detail = "双端真实业务联机、版本规则握手、三手提子、每手指纹一致、双停一手、双方计分确认及退出清理通过";
                    break;
            }
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
