// RevAutomationHttp —— Editor 的本机内存传输，业务执行完全复用 RevAutomationBridge，不复制动作实现。
using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace Revolution.Editor
{
    [InitializeOnLoad]
    internal static class RevAutomationHttp
    {
        [Serializable] private sealed class Checkpoint { public string sessionId; public RevAutomationMemoryJobs.Job[] jobs; }
        private static readonly RevAutomationMemoryJobs Jobs = new RevAutomationMemoryJobs();
        private static readonly object SubmissionGate = new object();
        private static HttpListener _listener;
        private static Thread _thread;
        private static string _token, _url, _session;
        private static bool _stopping;
        private static int _connections;
        private static string RecoveryPath => Path.Combine(RevAutomationBridge.Root, "http-recovery.json");
        public static string Url => _url;
        public static bool Available => _listener != null && _listener.IsListening;

        static RevAutomationHttp()
        {
            AssemblyReloadEvents.beforeAssemblyReload += BeforeReload;
            EditorApplication.quitting += Quit;
        }
        public static void Start(string session)
        {
            if (Application.isBatchMode || Available) { return; }
            _session = session; _stopping = false;
            _token = SessionState.GetString("RevAutomation.Http.Token", "");
            if (_token.Length == 0) { _token = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"); SessionState.SetString("RevAutomation.Http.Token", _token); }
            int port = SessionState.GetInt("RevAutomation.Http.Port", 0);
            for (int attempt = 0; attempt < 12; attempt++)
            {
                if (port == 0 || attempt > 0) { port = new System.Random(Guid.NewGuid().GetHashCode()).Next(20000, 60000); }
                var listener = new HttpListener();
                try { listener.Prefixes.Add("http://127.0.0.1:" + port + "/"); listener.Start(); _listener = listener; break; }
                catch (HttpListenerException) { listener.Close(); }
            }
            if (_listener == null) { return; }
            SessionState.SetInt("RevAutomation.Http.Port", port); _url = "http://127.0.0.1:" + port;
            Restore();
            _thread = new Thread(Accept) { IsBackground = true, Name = "RevAutomation.EditorHTTP" }; _thread.Start();
        }
        // token 单独存在忽略的本机文件，不能进入 endpoint 心跳、UI、报告或 Git。
        public static void WriteCredentials()
        {
            if (!Available) { return; }
            RevAutomationBridge.WriteJson(Path.Combine(RevAutomationBridge.Root, "editor-http.json"), new Credentials
            { protocol = 1, url = _url, token = _token, sessionId = _session, project = Path.GetFullPath("."), pid = System.Diagnostics.Process.GetCurrentProcess().Id });
        }
        [Serializable] private sealed class Credentials { public int protocol, pid; public string url, token, sessionId, project; }

        private static void Accept()
        {
            while (!_stopping)
            {
                HttpListenerContext context;
                try { context = _listener.GetContext(); }
                catch (Exception e) when (e is HttpListenerException || e is ObjectDisposedException) { return; }
                if (Interlocked.Increment(ref _connections) > 16) { Interlocked.Decrement(ref _connections); Send(context, 503, "{\"success\":false,\"error\":\"Busy\"}"); continue; }
                ThreadPool.QueueUserWorkItem(_ => { try { Handle(context); } finally { Interlocked.Decrement(ref _connections); } });
            }
        }
        private static void Handle(HttpListenerContext context)
        {
            try
            {
                var request = context.Request;
                if (!IPAddress.IsLoopback(request.RemoteEndPoint.Address) || !string.IsNullOrEmpty(request.Headers["Origin"]) ||
                    request.Headers["Authorization"] != "Bearer " + _token) { Send(context, 401, "{\"success\":false,\"error\":\"Unauthorized\"}"); return; }
                if (request.Headers["X-Rev-Session"] != _session) { Send(context, 409, "{\"success\":false,\"error\":\"SessionChanged\"}"); return; }
                string path = request.Url.AbsolutePath;
                if (path == "/health" && request.HttpMethod == "GET") { Send(context, 200, "{\"success\":true,\"transport\":\"http-memory\"}"); return; }
                if (path == "/call" && request.HttpMethod == "POST")
                {
                    if (request.ContentLength64 < 0 || request.ContentLength64 > 4 * 1024 * 1024) { throw new ArgumentException("请求超过 4MiB 或没有长度"); }
                    if (_stopping) { throw new InvalidOperationException("Editor 正在重载，请等待同一请求结果，不要重发变更"); }
                    if (request.InputStream.CanTimeout) { request.InputStream.ReadTimeout = 2000; }
                    string body; using (var reader = new StreamReader(request.InputStream, Encoding.UTF8)) { body = reader.ReadToEnd(); }
                    var data = RevAutomationJson.Object(RevAutomationJson.Parse(body));
                    if (data.Keys.Except(new[] { "protocol", "id", "action", "payloadJson", "confirm", "sessionId", "timeoutSeconds", "deadlineUtc" }).Any())
                    { throw new ArgumentException("未知请求字段"); }
                    string id = data["id"] as string, action = data["action"] as string;
                    if (id == null || id.Length != 32 || !id.All(Uri.IsHexDigit) || string.IsNullOrWhiteSpace(action) || (data["sessionId"] as string) != _session)
                    { throw new ArgumentException("工单身份无效"); }
                    if (!DateTimeOffset.TryParse(data["deadlineUtc"] as string, out DateTimeOffset deadline) || deadline <= DateTimeOffset.UtcNow || deadline > DateTimeOffset.UtcNow.AddDays(7))
                    { throw new ArgumentException("Expired：请求期限无效"); }
                    if (!(data["confirm"] is bool confirm) || !(data["payloadJson"] is string payload) || !(data["protocol"] is double protocol) || protocol != 1)
                    { throw new ArgumentException("请求类型或协议无效"); }
                    RevAutomationJson.Object(RevAutomationJson.Parse(payload));
                    lock (SubmissionGate)
                    {
                        // 在写检查点前冻结收件。已获准的请求一定在快照里。
                        if (_stopping)
                        {
                            Send(context, 503, "{\"success\":false,\"error\":\"Reloading\"}");
                            return;
                        }
                        Jobs.Add(new RevAutomationMemoryJobs.Job { id = id, action = action, payload = payload, confirm = confirm,
                            fingerprint = RevAutomationMemoryJobs.Fingerprint(body), deadlineTicks = deadline.UtcDateTime.Ticks, expirable = true }, DateTime.UtcNow);
                    }
                    Send(context, 202, "{\"success\":true,\"id\":\"" + id + "\"}"); return;
                }
                if (path.StartsWith("/jobs/", StringComparison.Ordinal))
                {
                    string id = path.Substring(6); bool ack = id.EndsWith("/ack", StringComparison.Ordinal);
                    if (ack) { id = id.Substring(0, id.Length - 4); }
                    if (ack && request.HttpMethod == "POST") { Send(context, 200, RevAutomationJson.Write(new Dictionary<string, object> { ["success"] = Jobs.Ack(id) })); return; }
                    if (request.HttpMethod == "DELETE") { Send(context, 200, RevAutomationJson.Write(new Dictionary<string, object> { ["success"] = Jobs.Cancel(id) })); return; }
                    if (request.HttpMethod == "GET")
                    { string result = Jobs.Response(id, _session); Send(context, result == null ? 404 : 200, result ?? "{\"success\":false,\"error\":\"JobNotFound\"}"); return; }
                }
                Send(context, 404, "{\"success\":false,\"error\":\"RouteNotFound\"}");
            }
            catch (Exception e) { Send(context, 400, RevAutomationJson.Write(new Dictionary<string, object> { ["success"] = false, ["error"] = e.GetType().Name, ["message"] = e.Message })); }
        }
        private static void Send(HttpListenerContext context, int status, string result)
        {
            try { byte[] data = Encoding.UTF8.GetBytes(result); context.Response.StatusCode = status; context.Response.ContentType = "application/json; charset=utf-8";
                context.Response.ContentLength64 = data.Length; context.Response.OutputStream.Write(data, 0, data.Length); }
            catch (Exception e) when (e is IOException || e is HttpListenerException || e is ObjectDisposedException) { }
            finally { try { context.Response.Close(); } catch (ObjectDisposedException) { } }
        }
        public static bool Pump()
        {
            var job = Jobs.Take(); if (job == null) { return false; }
            RevAutomationBridge.Begin(new RevAutomationRequest { id = job.id, action = job.action, payloadJson = job.payload,
                confirm = job.confirm, sessionId = _session, timeoutSeconds = Math.Max(1, (job.deadlineTicks - DateTime.UtcNow.Ticks) / (double)TimeSpan.TicksPerSecond),
                deadlineUtc = new DateTime(job.deadlineTicks, DateTimeKind.Utc).ToString("O"), memoryTransport = true });
            return true;
        }
        public static bool Cancelled(string id) => Jobs.Find(id)?.cancelled == true;
        public static void Publish(RevAutomationRequest request, RevAutomationResponse response)
        { Jobs.Complete(request.id, JsonUtility.ToJson(response)); }
        private static void BeforeReload()
        {
            if (!Available) { return; }
            lock (SubmissionGate)
            {
                _stopping = true;
            }
            _listener.Close(); _thread?.Join(500); _listener = null;
            var jobs = Jobs.Snapshot();
            int responseBudget = 256 * 1024;
            foreach (var job in jobs)
            {
                // 已确认的大响应已经释放；未确认的极大结果不写盘，重载后明确 ResultExpired。
                if (job.response != null)
                {
                    try { job.response = RevAutomationEvidence.WithoutImages(job.response); }
                    catch (ArgumentException)
                    {
                        // 单条损坏结果可以过期，但不能让其它工单的身份从检查点消失。
                        job.response = null;
                        job.acknowledged = true;
                        continue;
                    }
                    responseBudget -= job.response.Length;
                    if (responseBudget < 0) { job.response = null; job.acknowledged = true; }
                }
            }
            RevAutomationBridge.WriteJson(RecoveryPath, new Checkpoint { jobs = jobs, sessionId = _session });
        }
        private static void Restore()
        {
            if (!File.Exists(RecoveryPath)) { return; }
            var checkpoint = JsonUtility.FromJson<Checkpoint>(File.ReadAllText(RecoveryPath));
            File.Delete(RecoveryPath);
            if (checkpoint == null || checkpoint.sessionId != _session) { return; }
            Jobs.Restore(checkpoint.jobs);
            foreach (var job in checkpoint.jobs ?? Array.Empty<RevAutomationMemoryJobs.Job>())
            {
                if (job.finishedTicks == 0 && job.id != RevAutomationBridge.ActiveRequestId)
                { Jobs.Complete(job.id, JsonUtility.ToJson(new RevAutomationResponse { id = job.id, success = false, error = "InterruptedByReload", message = "排队请求被重载中断，没有自动重放" })); }
            }
        }
        private static void Quit()
        {
            _stopping = true; _listener?.Close(); _thread?.Join(500);
            string credential = Path.Combine(RevAutomationBridge.Root, "editor-http.json");
            if (File.Exists(credential)) { File.Delete(credential); }
        }
    }
}
