// 只替代本测试未触及的 Unity/网络/表加载边界，绝不模拟消息成功或声称真实联机已通过。
// 事件只保留编译契约；未覆盖的网络回调不会被本测试人为触发。
#pragma warning disable CS0067
using System;
using System.Collections.Generic;

namespace Revolution
{
    /// <summary>
    /// 测试链接真实 RevTask/MethodBuilder，仅缺少 Unity PlayerLoop 调度边界。
    /// 本套断言不调用 LeaveAsync；若未来误把它当成已验证路径，调度时立刻抛错，不伪造下一帧已完成。
    /// </summary>
    internal static class RevTaskScheduler
    {
        internal static RevTask Yield()
        {
            throw new NotSupportedException("本测试没有 Unity 帧调度器，LeaveAsync 必须在真实 Play 中验证。");
        }

        internal static RevTask Delay(int milliseconds)
        {
            throw new NotSupportedException("本测试不执行 RevTask 延时调度。");
        }

        internal static RevTask WhenAll(params RevTask[] tasks)
        {
            throw new NotSupportedException("本测试不执行 RevTask 并行调度。");
        }
    }

    internal static class RevLog
    {
        internal static void Exception(Exception error, string message, string tag)
        {
            throw new InvalidOperationException(tag + ": " + message, error);
        }
    }
}

namespace UnityEngine
{
    internal static class JsonUtility
    {
        internal static T FromJson<T>(string value)
        {
            throw new NotSupportedException("本测试不验证 Unity JSON 或局域网消息解析。");
        }
        internal static string ToJson(object value)
        {
            throw new NotSupportedException("本测试不验证 Unity JSON 或局域网消息编码。");
        }
    }
}

namespace Fantasy
{
    public sealed class GoRelayCommand
    {
        public int Sequence, Side, Kind, X, Y;
        public long ElapsedMs;
        public string PreviousHash, ResultHash, Payload;
    }
    public sealed class GoRelayState
    {
        public string Phase, RoomId, Reason, CoreVersion, RulesJson, BlackName, WhiteName;
        public int MySide;
        public List<GoRelayCommand> Commands = new List<GoRelayCommand>();
    }
}

namespace RevGame.Pvp
{
    public sealed class PvpConfig
    {
        public int komiHalfPoints = 15, mainTimeSeconds = 1, byoyomiSeconds = 1, byoyomiCount = 1;
        public int hintCount = 3, discoveryPort = 48581, matchTimeoutSeconds = 60, reconnectWindowSeconds = 90;
        public float announceSeconds = 1, peerExpirySeconds = 5, invitationTimeoutSeconds = 30, connectTimeoutSeconds = 10;
        public bool rookie;
        public string RulesJson = "test-config";
        public static PvpConfig Read(bool rookie)
        {
            return new PvpConfig { rookie = rookie };
        }
    }
}

namespace RevGame.Pvp.Networking
{
    public sealed class FantasyGoTransport : IDisposable
    {
        public static FantasyGoTransport Latest;
        public bool Connected, Busy;
        public string ResumeToken = "test-token";
        public event Action<Fantasy.GoRelayState> StateReceived;
        public event Action<string> Error;
        public event Action<string> RequestRejected;
        public void Connect(string endpoint, string nickname, string rules, string kind, string token)
        {
            Latest = this;
            Connected = true;
        }
        public void Match(bool cancel)
        {
        }
        public void Send(Fantasy.GoRelayCommand command)
        {
        }
        public void Poll()
        {
        }
        public void Leave()
        {
        }
        public void Dispose()
        {
        }
        public void Push(Fantasy.GoRelayState state)
        {
            StateReceived?.Invoke(state);
        }
        public void Reject(string reason)
        {
            RequestRejected?.Invoke(reason);
        }
    }
    public sealed class LanPeerOptions
    {
        public LanPeerOptions(int port, float announce, float expiry, float invitation, float connect)
        {
        }
    }
    public sealed class LanPeerInfo
    {
        public string Id, Name, Address;
        public bool Busy;
    }
    public sealed class LanPeerTransport : IDisposable
    {
        public event Action<bool> Connected;
        public event Action<string> PayloadReceived;
        public event Action<string> Disconnected;
        public event Action Changed;
        public bool IsConnected;
        public string Status;
        public LanPeerTransport(LanPeerOptions options)
        {
        }
        public bool StartDiscovery(string name, out string reason)
        {
            reason = "stub";
            return false;
        }
        public bool Invite(string id, out string reason)
        {
            reason = "stub";
            return false;
        }
        public bool ReplyInvitation(bool accept, out string reason)
        {
            reason = "stub";
            return false;
        }
        public bool Send(string value, out string reason)
        {
            reason = "stub";
            return false;
        }
        public void Poll()
        {
        }
        public void Dispose()
        {
        }
    }
}
