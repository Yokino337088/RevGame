using System;
using System.Net.Sockets;
using Revolution.Editor;

namespace RevGame.Editor
{
    /// <summary>仅开放固定PvP配方，不暴露任意脚本或反射入口。</summary>
    public static class GamePvpAutomation
    {
        // 诊断独占自己的回环 Socket，不接触玩家连接、服务器或 Fantasy 全局 Runtime。
        private static string _receiveCheck = "尚未执行";
        private static bool _receiveCheckRunning;

        [RevAutomationAction("game.pvp.socket-check", Environment = "edit", Keys = new string[0], Description = "固定字节对照Unity的Memory与ArraySegment TCP接收，结果用socket-check-state读取")]
        public static object CheckSocket(RevAutomationRequest request)
        {
            if (_receiveCheckRunning)
            {
                throw new InvalidOperationException("接收诊断正在运行");
            }
            _receiveCheckRunning = true;
            _receiveCheck = "运行中";
            RunReceiveCheck();
            return new Result { text = _receiveCheck };
        }

        [RevAutomationAction("game.pvp.socket-check-state", Environment = "edit", Keys = new string[0], Description = "读取固定字节TCP接收对照结果")]
        public static object SocketCheckState(RevAutomationRequest request)
        {
            return new Result { text = _receiveCheck };
        }

        // async void仅作Editor动作薄适配；所有异常在此收口，Socket和超时令牌必定释放。
        private static async void RunReceiveCheck()
        {
            try
            {
                string memory = await ReceiveFixedBytes(true);
                string array = await ReceiveFixedBytes(false);
                _receiveCheck = "Memory: " + memory + "; ArraySegment: " + array;
            }
            catch (Exception error)
            {
                _receiveCheck = "诊断失败：" + error;
            }
            finally
            {
                _receiveCheckRunning = false;
            }
        }

        private static async Revolution.RevTask<string> ReceiveFixedBytes(bool useMemory)
        {
            using var listener = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
            listener.Bind(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0));
            listener.Listen(1);
            using var sender = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
            sender.Connect(listener.LocalEndPoint);
            using var receiver = listener.Accept();
            // 固定内容属于协议诊断输入，不是策划数值。发送端关闭写方向，防止失败路径无限等包。
            byte[] expected = { 0x13, 0x27, 0x00, 0x18, 0xFE, 0x80, 0x31, 0x42 };
            int sent = 0;
            while (sent < expected.Length)
            {
                sent += sender.Send(expected, sent, expected.Length - sent, System.Net.Sockets.SocketFlags.None);
            }
            sender.Shutdown(System.Net.Sockets.SocketShutdown.Send);
            byte[] actual = new byte[expected.Length];
            int count = 0;
            using var cancellation = new System.Threading.CancellationTokenSource(2000);
            while (count < actual.Length)
            {
                int received = useMemory
                    ? await receiver.ReceiveAsync(new Memory<byte>(actual, count, actual.Length - count), System.Net.Sockets.SocketFlags.None, cancellation.Token)
                    : await receiver.ReceiveAsync(new ArraySegment<byte>(actual, count, actual.Length - count), System.Net.Sockets.SocketFlags.None);
                if (received == 0)
                {
                    break;
                }
                count += received;
            }
            string hex = BitConverter.ToString(actual);
            bool passed = count == expected.Length && hex == BitConverter.ToString(expected);
            return (passed ? "PASS" : "FAIL") + " count=" + count + " bytes=" + hex;
        }

        [RevAutomationAction("game.pvp.build", Environment = "edit", Keys = new string[0], Description = "制作九路围棋场景、棋盘Shader材质和联机面板")]
        public static object Build(RevAutomationRequest request)
        {
            try
            {
                return new Result { text = (string)Type.GetType("RevGame.Editor.GamePvpBuild, Assembly-CSharp-Editor", true).GetMethod("Build").Invoke(null, null) };
            }
            catch (System.Reflection.TargetInvocationException error)
            {
                throw new InvalidOperationException(error.InnerException?.ToString() ?? error.ToString());
            }
        }
        [RevAutomationAction("game.pvp.agent-assets", Environment = "edit", Keys = new string[0], Description = "制作Agent设置与对局开关，保留现有Prefab GUID")]
        public static object AgentAssets(RevAutomationRequest request)
        {
            return Type.GetType("RevGame.Editor.GamePvpBuild, Assembly-CSharp-Editor", true).GetMethod("BuildAgentAssets").Invoke(null, null);
        }
        [RevAutomationAction("game.pvp.match-hud", Environment = "edit", Keys = new string[0], Description = "更新围棋对局HUD与双眼图集绑定，不修改场景")]
        public static object MatchHud(RevAutomationRequest request)
        {
            return Type.GetType("RevGame.Editor.GamePvpBuild, Assembly-CSharp-Editor", true).GetMethod("BuildMatchHud").Invoke(null, null);
        }

        [Serializable]
        private sealed class Result
        {
            public string text;
        }
    }
}
