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

        /// <summary>同步离线断言不能冒充 Unity 下一帧。误用时沿同一不支持路径中止。</summary>
        internal static RevTask NextFrame() => Yield();

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
        internal static void Warn(string message, string tag)
        {
            Console.WriteLine(tag + ": " + message);
        }
        internal static void Exception(Exception error, string message, string tag)
        {
            throw new InvalidOperationException(tag + ": " + message, error);
        }
    }
}

namespace UnityEngine
{
    /// <summary>离线断言不推进 Unity 帧，房间快照仍可用业务版本识别同帧提交。</summary>
    internal static class Time
    {
        /// <summary>固定为同一逻辑帧，任何状态变化由真实 PvpService 记录。</summary>
        internal static int frameCount => 0;
    }

    /// <summary>列出正式代码比较的平台。夹具始终在 Windows 路径运行，其他值只用于编译真实分支。</summary>
    internal enum RuntimePlatform
    {
        /// <summary>离线服务回归实际选择的平台。</summary>
        WindowsPlayer,
        /// <summary>供正式 Android 存档分支判断的平台值。</summary>
        Android,
        /// <summary>供正式 WebGL 设置存储分支判断的平台值。</summary>
        WebGLPlayer
    }

    internal static class Application
    {
        public static RuntimePlatform platform => RuntimePlatform.WindowsPlayer;
        public static string persistentDataPath => throw new PlatformNotSupportedException("仅验证 Windows 本地设置");
    }

    // 测试内存槽仅替代Unity的注册表读写；加密仍调用Windows真实DPAPI。
    internal static class PlayerPrefs
    {
        private static readonly Dictionary<string, string> Values = new Dictionary<string, string>();
        internal static bool HasKey(string key) => Values.ContainsKey(key);
        internal static string GetString(string key) => Values.TryGetValue(key, out string value) ? value : "";
        /// <summary>从离线内存读取字符串，未写入时保留调用方默认值。</summary>
        internal static string GetString(string key, string fallback) => Values.TryGetValue(key, out string value) ? value : fallback;
        /// <summary>从隔离内存读取联机授权整数，不访问玩家注册表。</summary>
        internal static int GetInt(string key, int fallback) => int.TryParse(GetString(key), out int value) ? value : fallback;
        /// <summary>写入本轮测试的临时整数设置。</summary>
        internal static void SetInt(string key, int value) => SetString(key, value.ToString());
        internal static void SetString(string key, string value)
        {
            Values[key] = value;
        }
        internal static void DeleteKey(string key)
        {
            Values.Remove(key);
        }
        internal static void Save()
        {
        }
    }
    internal static class JsonUtility
    {
        internal static void FromJsonOverwrite(string value, object destination)
        {
            throw new NotSupportedException("云端局势分析响应由 Unity 运行期回归验证");
        }

        internal static T FromJson<T>(string value)
        {
            return System.Text.Json.JsonSerializer.Deserialize<T>(value, new System.Text.Json.JsonSerializerOptions { IncludeFields = true });
        }
        internal static string ToJson(object value)
        {
            return System.Text.Json.JsonSerializer.Serialize(value, value.GetType(), new System.Text.Json.JsonSerializerOptions { IncludeFields = true });
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
        // 离线夹具中的决斗房间标识，模拟网络状态字段。
        public string DuelId = "";
        // 离线夹具模拟服务端通知的决斗版本兼容位。
        public bool DuelAvailable;
        public string Phase, RoomId, Reason, CoreVersion, RulesJson, BlackName, WhiteName;
        public int MySide, BaseSequence, RatingDelta;
        public bool RatingFinalized;
        public List<GoRelayCommand> Commands = new List<GoRelayCommand>();
    }
}

namespace RevGame.Pvp
{
    public sealed class PvpConfig
    {
        public int komiHalfPoints = 15, mainTimeSeconds = 1, byoyomiSeconds = 1, byoyomiCount = 1;
        public int localPlacementLimit = 0, localBaitEvery = 0;
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
        public string SubmittedToken;
        public bool AllowsNewIdentity;
        public int SyncCalls;
        /// <summary>与正式传输一致，连接回执另带是否确认了新房间的标记。</summary>
        public event Action<Fantasy.GoRelayState, bool> StateReceived;
        /// <summary>离线传输只有标记为已连接时才接纳同步意图。</summary>
        public bool CanRequestSync => Connected;
        /// <summary>离线桩保留房间确认回调签名，连接状态仍由测试输入和真实服务门禁决定。</summary>
        public void ConfirmRoom(string id)
        {
        }
        public event Action<string> Error;
        public event Action<string> RequestRejected;
        public void Connect(string endpoint, string nickname, string rules, string kind, string token, bool allowNewIdentity = false)
        {
            Latest = this;
            Connected = true;
            SubmittedToken = token;
            AllowsNewIdentity = allowNewIdentity;
        }
        public int SubmittedRating;
        public void Match(bool cancel, int localRating = 0)
        {
            SubmittedRating = localRating;
        }
        // 离线传输替身没有对端，拒绝决斗 RPC，真实联机由 KCP / UTP 探针验收。
        public bool ExchangeDuel(RevGame.Pvp.Duel.DuelRequest request, Action<RevGame.Pvp.Duel.DuelReply> reply)
        {
            return false;
        }
        public void Sync()
        {
            SyncCalls++;
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
        /// <summary>投递测试构造的协议回执，正式服务仍负责校验与提交。</summary>
        public void Push(Fantasy.GoRelayState state, bool confirmsMembership = true)
        {
            StateReceived?.Invoke(state, confirmsMembership);
        }
        public void Reject(string reason)
        {
            RequestRejected?.Invoke(reason);
        }
    }
    public sealed class LanPeerOptions
    {
        // 离线桩保留正式构造签名。它没有真实套接字，回环选项由 Unity 夹具验证。
        public LanPeerOptions(int port, float announce, float expiry, float invitation, float connect, bool loopbackOnly = false)
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
        /// <summary>离线桩没有 UDP 数据，始终没有收到邀请。</summary>
        public LanPeerInfo IncomingInvitation => null;
        public string Status;
        public LanPeerTransport(LanPeerOptions options)
        {
        }
        /// <summary>离线桩只保留方法签名，真实主动搜索和被动接收在 Unity 联机回归测试。</summary>
        public bool StartDiscovery(string name, out string reason, bool listenOnly = false)
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
