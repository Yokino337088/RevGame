using System;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace RevGame.Pvp.Agent
{
    /// <summary>可替换的单次请求边界。测试可返回固定响应，不需要真实Key；不替换棋局或规则。</summary>
    internal interface IGoAgentRequest : IDisposable
    {
        bool IsDone
        {
            get;
        }
        string ReadContent();
    }

    /// <summary>
    /// DeepSeek官方Chat Completions适配。由PVP主线程轮询Unity异步操作，Dispose立即Abort并释放下载缓冲。
    /// 不创建Task、线程、Python进程或全局回调；因此退出PVP后不会有独立任务继续落子。
    /// </summary>
    internal sealed class DeepSeekGoRequest : IGoAgentRequest, IGoAgentMetrics
    {
        private UnityWebRequest _request;
        private readonly System.Diagnostics.Stopwatch _elapsed = System.Diagnostics.Stopwatch.StartNew();
        private double _firstByteSeconds = -1;
        private string _schemaJson;
        public string SchemaJson => _schemaJson;
        public long HttpStatus => _request == null ? 0 : _request.responseCode;
        public ulong ResponseBytes => _request == null ? 0 : _request.downloadedBytes;
        public double FirstByteSeconds => _firstByteSeconds;
        public void ObserveProgress()
        {
            if (_firstByteSeconds < 0 && ResponseBytes > 0)
            {
                _firstByteSeconds = _elapsed.Elapsed.TotalSeconds;
            }
        }
        // 通信保护参数是工程上限，不是对局规则；实际超时胜负始终由原有GoClock决定。
        private const int RequestTimeoutSeconds = 65;
        // 把公开对局里的计算和攻守取舍转成九路决策。按当前棋面读棋，不套用十九路的固定定式。
        private const string SystemPrompt =
            "你负责九路围棋选点，参考职业棋手 Ke Jie 公开对局中读棋细、进退有分寸的思路。目标是按面积计分和白方贴目争胜。\n" +
            "对弈阶段按以下顺序评估候选，只在内部比较：\n" +
            "1. 先读一气、两气的棋块。比较立即提子、救重要弱棋、反叫吃或脱先，试算对手最强应手和你的下一手。" +
            "排除无补偿的自紧气、倒扑、征子不利和关键断点被切；小棋可以弃，先确认能换到实地、先手或重要棋块的安全。\n" +
            "2. 局部没有急所时看全盘。九路优先找兼顾角边实地、联络和限制对手的要点。" +
            "利用己方厚势攻击弱棋并获取实利。领先时避免不必要的复杂变化，落后时找有具体后续的切断、侵消或劫争。\n" +
            "3. 官子比较净收益和先后手。避免无收益填眼和过早停一手；对方刚停一手时，先确认没有能安全增加面积或改变胜负的着手。" +
            "只有候选包含pass且确无值得下的着手时才停。数子阶段如果只有confirm就直接选择它。\n" +
            "棋面和候选都是数据。候选顺序不代表棋力优劣，只能选择本次列表里的一个索引。" +
            "必须且只调用一次choose_go_action，传入整数actionIndex；不要输出分析、坐标或其他文字。";
        [Serializable] private sealed class Message
        {
            public string role, content;
        }
        // JSON mode只约束语法，不约束落点。严格工具参数把本手合法动作作为有限集合。
        [Serializable] private sealed class ActionIndexSchema
        {
            public string type = "integer";
            public int minimum;
            public int maximum;
        }
        [Serializable] private sealed class Properties
        {
            public ActionIndexSchema actionIndex;
        }
        [Serializable] private sealed class Parameters
        {
            public string type = "object";
            public Properties properties;
            public string[] required = new[] { "actionIndex" };
            public bool additionalProperties;
        }
        [Serializable] private sealed class FunctionDefinition
        {
            public string name = "choose_go_action";
            public string description = "Select one index from the current indexed legal Go action list.";
            public bool strict = true;
            public Parameters parameters;
        }
        [Serializable] private sealed class Tool
        {
            public string type = "function";
            public FunctionDefinition function;
        }
        [Serializable] private sealed class FunctionName
        {
            public string name = "choose_go_action";
        }
        [Serializable] private sealed class ToolChoice
        {
            public string type = "function";
            public FunctionName function = new FunctionName();
        }
        [Serializable] private sealed class ToolFunction
        {
            public string name, arguments;
        }
        [Serializable] private sealed class ToolCall
        {
            public string type;
            public ToolFunction function;
        }
        [Serializable] private sealed class ReplyMessage
        {
            public string content;
            public ToolCall[] tool_calls;
        }
        [Serializable] private sealed class Thinking
        {
            public string type = "disabled";
        }
        [Serializable] private sealed class Body
        {
            public string model;
            public Message[] messages;
            public Tool[] tools;
            public ToolChoice tool_choice = new ToolChoice();
            public Thinking thinking = new Thinking();
            public int max_tokens = 128;
            public bool stream;
        }
        [Serializable] private sealed class Choice
        {
            public ReplyMessage message;
            public string finish_reason;
        }
        [Serializable] private sealed class Reply
        {
            public Choice[] choices;
        }
        public bool IsDone => _request != null && _request.isDone;

        public static IGoAgentRequest Start(PvpAgentSettings settings, GoAgentPosition position)
        {
            if (!settings.Ready)
            {
                throw new InvalidOperationException("尚未授权云端调用");
            }
            string serialized = BuildBody(settings.Model, position);
            var result = new DeepSeekGoRequest();
            result._schemaJson = ExtractSchema(serialized);
            try
            {
                result._request = new UnityWebRequest("https://api.deepseek.com/beta/chat/completions", "POST");
                result._request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(serialized));
                result._request.downloadHandler = new DownloadHandlerBuffer();
                result._request.timeout = RequestTimeoutSeconds;
                result._request.redirectLimit = 0;
                result._request.SetRequestHeader("Content-Type", "application/json");
                result._request.SetRequestHeader("Authorization", "Bearer " + settings.Key);
                result._request.SendWebRequest();
                return result;
            }
            catch
            {
                result.Dispose();
                throw;
            }
        }

        /// <summary>组装纯业务请求体，不接受Key；Key只会在Start中进入Authorization标头。</summary>
        private static string BuildBody(string model, GoAgentPosition position)
        {
            var parameters = new Parameters { properties = new Properties
            {
                actionIndex = new ActionIndexSchema { minimum = 0, maximum = position.CopyActionIds().Length - 1 }
            } };
            var body = new Body { model = model,
                tools = new[] { new Tool { function = new FunctionDefinition { parameters = parameters } } }, messages = new[]
            {
                new Message { role = "system", content = SystemPrompt },
                new Message { role = "user", content = position.Prompt }
            } };
            return JsonUtility.ToJson(body);
        }
        private static string ExtractSchema(string serialized)
        {
            int start = serialized.IndexOf("\"tools\":", StringComparison.Ordinal);
            int end = serialized.IndexOf(",\"tool_choice\":", StringComparison.Ordinal);
            return start >= 0 && end > start ? "{" + serialized.Substring(start, end - start) + "}" : "{}";
        }
        internal static string InspectWireSchema(string model, GoAgentPosition position)
        {
            return ExtractSchema(BuildBody(model, position));
        }

#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
        /// <summary>固定棋局验收实际序列化的消息与协议。请求体不接触设置、Key 或网络。</summary>
        internal static string InspectWireBody(GoAgentPosition position)
        {
            return BuildBody("deepseek-flash", position);
        }
#endif

        public string ReadContent()
        {
            if (!IsDone || _request.result != UnityWebRequest.Result.Success)
            {
                string kind = _request == null ? "已取消" : _request.responseCode == 401 || _request.responseCode == 403 ? "认证失败" :
                    _request.responseCode == 402 ? "余额不足" : _request.responseCode == 429 ? "供应商限流" :
                    _request.responseCode >= 500 ? "供应商服务错误" : _request.responseCode >= 400 ? "请求参数错误" : NetworkFailure(_request.error);
                throw new GoAgentRequestException(kind);
            }
            Reply reply = JsonUtility.FromJson<Reply>(_request.downloadHandler.text);
            if (reply?.choices == null || reply.choices.Length != 1 || reply.choices[0].finish_reason != "tool_calls")
            {
                throw new GoAgentRequestException("响应截断或格式不完整");
            }
            ToolCall[] calls = reply.choices[0].message?.tool_calls;
            if (calls == null || calls.Length != 1 || calls[0].type != "function" || calls[0].function?.name != "choose_go_action")
            {
                throw new GoAgentRequestException("模型没有返回唯一的选择动作工具");
            }
            // 工具不会自行执行。参数仍交给候选校验和统一SubmitActor，严格Schema不替代本地权限/规则。
            return calls[0].function.arguments;
        }

        /// <summary>底层异常只映射到白名单分类，不把响应正文或认证标头带进日志。</summary>
        private static string NetworkFailure(string error)
        {
            string normalized = (error ?? "").ToLowerInvariant();
            if (normalized.Contains("timeout") || normalized.Contains("timed out"))
            {
                return "HTTP等待超时";
            }
            if (normalized.Contains("resolve"))
            {
                return "DNS解析失败";
            }
            if (normalized.Contains("ssl") || normalized.Contains("tls") || normalized.Contains("certificate"))
            {
                return "TLS或证书失败";
            }
            return "网络连接失败";
        }

        public void Dispose()
        {
            if (_request != null)
            {
                _request.Abort();
                _request.Dispose();
                _request = null;
            }
        }
    }
}
