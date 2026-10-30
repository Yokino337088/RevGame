// 开发态本机 HTTP 传输。网络线程仅处理纯数据；Unity 调用和跨帧工作全部由 Update 串行推进。
#if (UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION) && (!UNITY_WEBGL || UNITY_EDITOR)
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using UnityEngine;

namespace Revolution
{
    internal sealed class RevRuntimeAutomationHost : MonoBehaviour
    {
        // 工单分三个阶段：HTTP 线程入队 → Unity Update 执行业务 → HTTP 线程读取结果。
        // Job 中不能保存待从网络线程访问的 Transform/GameObject；Operation 只由主线程使用。
        private sealed class Job
        {
            public string Id, Action, Payload, Fingerprint;
            // 主线程写入最终 JSON 后，后台 GET 必须看见发布后的完整字符串。
            public volatile string Response;
            public bool Confirm;
            public volatile bool Cancelled;
            public DateTime Deadline;
            public RevRuntimeOperation Operation;
            public int StartErrorCount;
            public bool Started;
            public RevAutomationActivity.Ticket Activity;
        }
        private static RevRuntimeAutomationHost _instance;
        private readonly ConcurrentQueue<Job> _queue = new ConcurrentQueue<Job>();
        // 字典保留本 session 的请求 id，防止客户端重发相同请求造成“道具加两次”。
        // _gate 只保护短暂的数据登记，不能持锁等待 Unity 帧或网络返回。
        private readonly Dictionary<string, Job> _jobs = new Dictionary<string, Job>(StringComparer.Ordinal);
        private readonly object _gate = new object();
        private HttpListener _listener;
        private Thread _thread;
        private Job _active;
        private string _token, _session, _endpoint, _health;
        private bool _wasBackground;
        private volatile bool _stopping;
        private int _connections;
        public static bool HasPendingWork => _instance != null && (_instance._active != null || !_instance._queue.IsEmpty);
        public static string StartServer(int port, string token)
        {
            if (_instance != null) { throw new InvalidOperationException("运行时服务已启动，请先停止再切换配置"); }
            if (port < 1024 || port > 65535) { throw new ArgumentOutOfRangeException(nameof(port)); }
            if (token != null && (token.Length < 32 || token.Any(char.IsWhiteSpace))) { throw new ArgumentException("token 至少 32 字符且不能含空白"); }
            var go = new GameObject("[RevRuntimeAutomation]") { hideFlags = HideFlags.HideInHierarchy };
            DontDestroyOnLoad(go);
            var host = go.AddComponent<RevRuntimeAutomationHost>();
            try
            {
                host._token = token ?? Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
                host._session = Guid.NewGuid().ToString("N");
                host._listener = new HttpListener();
                string url = "http://127.0.0.1:" + port;
                host._listener.Prefixes.Add(url + "/"); host._listener.Start();
                string folder = Application.isEditor ? Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Library", "RevolutionAutomation")) : Path.Combine(Application.persistentDataPath, "RevolutionAutomation");
                // launcher为每次进程指定独立凭据目录，不进入构建资源或Git。
                string customEndpoint = Environment.GetEnvironmentVariable("REV_AUTOMATION_ENDPOINT");
                if (!Application.isEditor && !string.IsNullOrEmpty(customEndpoint))
                {
                    if (!Path.IsPathRooted(customEndpoint))
                    {
                        throw new ArgumentException("REV_AUTOMATION_ENDPOINT必须是绝对路径");
                    }
                    host._endpoint = Path.GetFullPath(customEndpoint);
                    folder = Path.GetDirectoryName(host._endpoint);
                }
                else
                {
                    host._endpoint = Path.Combine(folder, "runtime.json");
                }
                Directory.CreateDirectory(folder);
                RevAutomationActivity.Configure(Path.Combine(folder, "activity"));
                var endpoint = new Dictionary<string, object> { ["protocol"] = 1, ["url"] = url, ["token"] = host._token,
                    ["sessionId"] = host._session, ["pid"] = System.Diagnostics.Process.GetCurrentProcess().Id,
                    ["product"] = Application.productName, ["platform"] = Application.platform.ToString(), ["project"] = Application.isEditor ? Path.GetFullPath(Path.Combine(Application.dataPath, "..")) : "" };
                endpoint["launchId"] = Environment.GetEnvironmentVariable("REV_AUTOMATION_LAUNCH_ID") ?? "";
                endpoint["buildId"] = Application.buildGUID;
                string temporary = host._endpoint + ".tmp";
                File.WriteAllText(temporary, RevAutomationJson.Write(endpoint), new UTF8Encoding(false));
                if (File.Exists(host._endpoint))
                {
                    File.Delete(host._endpoint);
                }
                File.Move(temporary, host._endpoint);
                endpoint.Remove("token"); host._health = RevAutomationJson.Write(endpoint);
                host._wasBackground = Application.runInBackground; Application.runInBackground = true;
                host._thread = new Thread(host.Accept) { IsBackground = true, Name = "RevRuntimeAutomation.HTTP" };
                _instance = host; host._thread.Start(); return host._endpoint;
            }
            catch { host.Shutdown(); Destroy(go); throw; }
        }
        public static void StopServer()
        {
            if (_instance == null) { return; }
            var host = _instance; host.Shutdown(); Destroy(host.gameObject); _instance = null;
        }
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void StartOptIn()
        {
            string[] args = Environment.GetCommandLineArgs();
            if (!args.Contains("-revAutomation")) { return; }
            int index = Array.IndexOf(args, "-revAutomationPort");
            int port = index >= 0 && index + 1 < args.Length ? int.Parse(args[index + 1]) : 18766;
            RevRuntimeAutomation.Start(port, Environment.GetEnvironmentVariable("REV_AUTOMATION_TOKEN"));
        }
        private void Accept()
        {
            // HttpListener.GetContext 是阻塞等待，因此放后台线程；这里绝不调用 Unity API。
            // 一个连接慢也不能卡住 Unity，每次请求交给受连接数限制的线程池处理。
            while (!_stopping)
            {
                HttpListenerContext context;
                try { context = _listener.GetContext(); }
                catch (HttpListenerException) { break; } catch (ObjectDisposedException) { break; }
                if (Interlocked.Increment(ref _connections) > 16)
                { Interlocked.Decrement(ref _connections); Send(context, 503, "{\"success\":false,\"error\":\"ConnectionLimit\"}"); continue; }
                ThreadPool.QueueUserWorkItem(_ => { try { Handle(context); } finally { Interlocked.Decrement(ref _connections); } });
            }
        }
        private void Handle(HttpListenerContext context)
        {
            // 认证、session、参数解析都是纯数据工作，可以留在后台线程。
            // POST 返回 202 仅表示“已接单”，真正成功必须等 GET /jobs/<id> 的最终 success。
            try
            {
                var req = context.Request;
                if (!IPAddress.IsLoopback(req.RemoteEndPoint.Address) || !string.IsNullOrEmpty(req.Headers["Origin"]) || req.Headers["Authorization"] != "Bearer " + _token)
                { Send(context, 401, "{\"success\":false,\"error\":\"Unauthorized\"}"); return; }
                if (req.Headers["X-Rev-Session"] != _session) { Send(context, 409, "{\"success\":false,\"error\":\"SessionChanged\"}"); return; }
                string path = req.Url.AbsolutePath;
                if (path == "/health" && req.HttpMethod == "GET") { Send(context, 200, _health); return; }
                if (path == "/call" && req.HttpMethod == "POST")
                {
                    if (req.ContentLength64 < 0 || req.ContentLength64 > 4 * 1024 * 1024) { throw new ArgumentException("body 必须声明长度且不能超过 4 MiB"); }
                    if (req.InputStream.CanTimeout) { req.InputStream.ReadTimeout = 2000; }
                    string body; using (var reader = new StreamReader(req.InputStream, Encoding.UTF8)) { body = reader.ReadToEnd(); }
                    var data = RevAutomationJson.Object(RevAutomationJson.Parse(body));
                    if (data.Keys.Except(new[] { "id", "action", "payload", "confirm", "timeoutSeconds" }).Any()) { throw new ArgumentException("未知请求字段"); }
                    string id = data["id"] as string, action = data["action"] as string;
                    if (id == null || id.Length != 32 || id.Any(c => !Uri.IsHexDigit(c)) || string.IsNullOrEmpty(action)) { throw new ArgumentException("请求 id/action 无效"); }
                    double timeout = data.TryGetValue("timeoutSeconds", out object t) && t is double seconds ? seconds : 120;
                    if (timeout <= 0 || timeout > 3600) { throw new ArgumentException("timeoutSeconds 为 0..3600"); }
                    object payload = data.TryGetValue("payload", out object p) ? p : new Dictionary<string, object>(); RevAutomationJson.Object(payload);
                    if (data.TryGetValue("confirm", out object confirmation) && !(confirmation is bool)) { throw new ArgumentException("confirm 需要 bool"); }
                    var job = new Job { Id = id, Action = action, Payload = RevAutomationJson.Write(payload), Confirm = confirmation is bool c && c,
                        Deadline = DateTime.UtcNow.AddSeconds(timeout), Fingerprint = body };
                    lock (_gate)
                    {
                        if (_jobs.TryGetValue(id, out Job previous))
                        { if (previous.Fingerprint != body) { throw new ArgumentException("请求 id 已被其他参数使用"); } }
                        else
                        {
                            if (_stopping) { throw new InvalidOperationException("服务正在停止"); }
                            if (_queue.Count >= 16) { throw new InvalidOperationException("Busy：主线程队列已满"); }
                            // 有界保留：达到容量时拒绝新工单，防止旧 id 被遗忘后再次执行变更。重启产生新 session。
                            if (_jobs.Count >= 1024) { throw new InvalidOperationException("SessionJobLimit：请结束本轮并重启运行时服务"); }
                            _jobs.Add(id, job); _queue.Enqueue(job);
                        }
                    }
                    Send(context, 202, "{\"success\":true,\"id\":\"" + id + "\",\"sessionId\":\"" + _session + "\"}"); return;
                }
                if (path.StartsWith("/jobs/", StringComparison.Ordinal) && (req.HttpMethod == "GET" || req.HttpMethod == "DELETE"))
                {
                    string id = path.Substring(6); Job job;
                    lock (_gate) { _jobs.TryGetValue(id, out job); }
                    if (job == null) { Send(context, 404, "{\"success\":false,\"error\":\"JobNotFound\"}"); return; }
                    if (req.HttpMethod == "DELETE") { job.Cancelled = true; }
                    Send(context, 200, job.Response ?? "{\"success\":true,\"pending\":true,\"id\":\"" + id + "\"}"); return;
                }
                Send(context, 404, "{\"success\":false,\"error\":\"RouteNotFound\"}");
            }
            catch (Exception e) { Send(context, 400, RevAutomationJson.Write(new Dictionary<string, object> { ["success"] = false, ["error"] = e.GetType().Name, ["message"] = e.Message })); }
        }
        private static void Send(HttpListenerContext context, int status, string json)
        {
            try
            {
                byte[] bytes = Encoding.UTF8.GetBytes(json); context.Response.StatusCode = status;
                context.Response.ContentType = "application/json; charset=utf-8"; context.Response.Headers["Cache-Control"] = "no-store";
                context.Response.ContentLength64 = bytes.Length; context.Response.OutputStream.Write(bytes, 0, bytes.Length);
            }
            catch (Exception e) when (e is IOException || e is HttpListenerException || e is ObjectDisposedException) { }
            finally { try { context.Response.Close(); } catch (ObjectDisposedException) { } }
        }
        private void Update()
        {
            // 唯一执行 Unity 业务的位置。始终只推进一个 active 工单；
            // Editor 文件桥持有忙标记时让出执行权，避免两个入口同时改场景/游戏状态。
            if (_stopping) { return; }
#if UNITY_EDITOR
            if (RevRuntimeAutomation.EditorBusy) { return; }
#endif
            if (_active == null && !_queue.TryDequeue(out _active)) { return; }
            var job = _active;
            job.Activity ??= RevAutomationActivity.Begin("Unity HTTP", job.Action == "gm.exec" ? "gm" : "game",
                job.Action, job.Payload, "http-" + job.Id);
            try
            {
                if (job.Cancelled || DateTime.UtcNow >= job.Deadline)
                { job.Operation?.Cancel(); Complete(job, false, "CancelledOrTimeout", "已停止等待；已执行的业务副作用不会回滚", job.Operation?.Result); return; }
                if (job.Operation == null)
                {
                    job.Started = true; job.StartErrorCount = RevRuntimeAutomation.ErrorCount;
                    object result;
                    if (job.Action == "gm.exec")
                    {
                        var p = RevAutomationJson.Object(RevAutomationJson.Parse(job.Payload));
                        if (p.Count != 1 || !p.TryGetValue("command", out object command) || !(command is string)) { throw new ArgumentException("gm.exec 只接收 command 字符串"); }
                        result = RevRuntimeAutomation.ExecuteGM((string)command, job.Confirm);
                    }
                    else { result = RevRuntimeAutomation.Invoke(job.Action, job.Payload, job.Confirm); }
                    if (result is RevRuntimeOperation operation) { job.Operation = operation; }
                    else { Complete(job, true, null, "完成", result); return; }
                }
                if (job.Operation.Tick()) { Complete(job, job.Operation.Success, job.Operation.Success ? null : "OperationFailed", job.Operation.Message, job.Operation.Result); }
            }
            catch (Exception e) { Complete(job, false, e.GetType().Name, e.Message, job.Operation?.Result); }
        }
        private void Complete(Job job, bool success, string error, string message, object data)
        {
            // 即使 handler 正常 return，期间新增的 Unity Error 也会判失败。
            // Dispose 也属于动作结果的一部分，清理异常不能被吞掉伪装成成功。
            if (success && job.Started && RevRuntimeAutomation.ErrorCount > job.StartErrorCount)
            { success = false; error = "UnityError"; message = "操作期间出现 Unity Error/Exception，请读取 runtime.logs"; }
            try { job.Operation?.Dispose(); }
            catch (Exception e) { success = false; error = "DisposeFailed"; message += "; " + e.Message; }
            job.Operation = null;
            try { job.Response = RevAutomationJson.Write(new Dictionary<string, object> { ["id"] = job.Id, ["sessionId"] = _session,
                ["success"] = success, ["error"] = error, ["message"] = message, ["data"] = data }); }
            catch (Exception e)
            {
                success = false; error = "SerializationFailed"; message = e.Message;
                job.Response = RevAutomationJson.Write(new Dictionary<string, object> { ["id"] = job.Id, ["sessionId"] = _session, ["success"] = false, ["error"] = error, ["message"] = message });
            }
            job.Activity ??= RevAutomationActivity.Begin("Unity HTTP", job.Action == "gm.exec" ? "gm" : "game",
                job.Action, job.Payload, "http-" + job.Id);
            RevAutomationActivity.Finish(job.Activity, success, message, data, error);
            if (_active == job) { _active = null; }
        }
        private void Shutdown()
        {
            // 幂等收尾：Stop、OnDestroy、退出游戏可能接连触发，只执行一次实际释放。
            // 只删除仍属于本 session 的凭据，防止删掉后来启动的另一个实例文件。
            if (_stopping) { return; } _stopping = true;
            if (_active != null) { _active.Operation?.Cancel(); Complete(_active, false, "ServerStopped", "运行时服务已停止", null); }
            while (_queue.TryDequeue(out Job job)) { Complete(job, false, "ServerStopped", "运行时服务已停止", null); }
            _listener?.Close(); _thread?.Join(500);
            if (_endpoint != null)
            {
                try { if (File.Exists(_endpoint) && File.ReadAllText(_endpoint).Contains(_session)) { File.Delete(_endpoint); } }
                catch (IOException) { }
                Application.runInBackground = _wasBackground;
            }
            if (_instance == this) { _instance = null; }
        }
        private void OnDestroy() { Shutdown(); }
        private void OnApplicationQuit() { Shutdown(); }
    }
}
#elif UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
namespace Revolution
{
    internal static class RevRuntimeAutomationHost
    {
        public static bool HasPendingWork => false;
        public static string StartServer(int port, string token) => throw new System.PlatformNotSupportedException("WebGL 无本机 HTTP listener；请提供项目传输适配");
        public static void StopServer() { }
    }
}
#endif
