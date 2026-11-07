// RevAutomationMemoryJobs —— 有界的内存工单账本，传输线程只接收数据，业务仍由原来的主线程执行器处理。
#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Revolution
{
    /// <summary>
    /// 一次提交只执行一次。读取结果后的 Ack 释放大响应，但保留小型身份记录至原始 deadline。
    /// 新协议请求携带绝对期限；期限已过的重发直接拒绝，不能因清掉记录再次产生业务效果。
    /// 旧协议没有绝对期限，身份保留到服务重启；达到容量明确拒绝，不悄悄忘记旧请求。
    /// </summary>
    public sealed class RevAutomationMemoryJobs
    {
        [Serializable]
        public sealed class Job
        {
            public string id, action, payload, fingerprint, response;
            public bool confirm, cancelled, started, acknowledged, expirable;
            public long deadlineTicks, finishedTicks;
        }
        private readonly object _gate = new object();
        private readonly Dictionary<string, Job> _jobs = new Dictionary<string, Job>(StringComparer.Ordinal);
        private readonly Queue<Job> _queue = new Queue<Job>();
        public const int IdentityCapacity = 16384;
        public const int QueueCapacity = 16;
        public const int ResultCapacity = 128;
        public const long ResultByteCapacity = 32L * 1024 * 1024;

        public static string Fingerprint(string body)
        {
            using (var hash = SHA256.Create()) { return Convert.ToBase64String(hash.ComputeHash(Encoding.UTF8.GetBytes(body))); }
        }
        public bool Add(Job job, DateTime now)
        {
            lock (_gate)
            {
                Prune(now);
                if (job.deadlineTicks <= now.Ticks) { throw new ArgumentException("Expired：原始请求已过期，未重新执行"); }
                if (_jobs.TryGetValue(job.id, out Job old))
                {
                    if (old.fingerprint != job.fingerprint) { throw new ArgumentException("请求 id 已被不同参数使用"); }
                    return false;
                }
                if (_jobs.Count >= IdentityCapacity || _queue.Count >= QueueCapacity)
                { throw new InvalidOperationException("Busy：内存工单容量已满，请等待或结束旧会话"); }
                _jobs.Add(job.id, job); _queue.Enqueue(job); return true;
            }
        }
        public Job Take()
        {
            lock (_gate)
            {
                if (_queue.Count == 0) { return null; }
                Job job = _queue.Dequeue(); job.started = true; return job;
            }
        }
        public Job Find(string id) { lock (_gate) { return _jobs.TryGetValue(id, out Job job) ? job : null; } }
        public bool Cancel(string id)
        {
            lock (_gate) { if (!_jobs.TryGetValue(id, out Job job)) { return false; } job.cancelled = true; return true; }
        }
        public void Complete(string id, string response)
        {
            lock (_gate)
            {
                if (!_jobs.TryGetValue(id, out Job job)) { return; }
                job.response = response; job.finishedTicks = DateTime.UtcNow.Ticks; job.payload = null;
                Prune(DateTime.UtcNow);
            }
        }
        public bool Ack(string id)
        {
            lock (_gate)
            {
                if (!_jobs.TryGetValue(id, out Job job) || job.finishedTicks == 0) { return false; }
                job.response = null; job.acknowledged = true; return true;
            }
        }
        public string Response(string id, string session)
        {
            lock (_gate)
            {
                Prune(DateTime.UtcNow);
                if (!_jobs.TryGetValue(id, out Job job)) { return null; }
                if (job.response != null) { return job.response; }
                if (job.finishedTicks == 0) { return RevAutomationJson.Write(new Dictionary<string, object> { ["success"] = true, ["pending"] = true, ["id"] = id }); }
                return RevAutomationJson.Write(new Dictionary<string, object> { ["success"] = false, ["id"] = id, ["sessionId"] = session,
                    ["error"] = "ResultExpired", ["message"] = "结果已确认或超过保留上限；请求身份仍保留，业务没有重新执行。" });
            }
        }
        public int Count { get { lock (_gate) { return _jobs.Count; } } }
        public int Pending { get { lock (_gate) { return _jobs.Values.Count(j => j.finishedTicks == 0); } } }

        // 主动释放结果后只剩身份 hash，不能持有 GameObject、委托、截图或整个参数正文。
        private void Prune(DateTime now)
        {
            foreach (string id in _jobs.Where(p => p.Value.finishedTicks > 0 && p.Value.expirable &&
                p.Value.deadlineTicks + TimeSpan.TicksPerMinute <= now.Ticks).Select(p => p.Key).ToArray()) { _jobs.Remove(id); }
            long bytes = 0; int count = 0;
            foreach (Job job in _jobs.Values.Where(j => j.response != null).OrderByDescending(j => j.finishedTicks))
            {
                bytes += Encoding.UTF8.GetByteCount(job.response); count++;
                if (count > ResultCapacity || bytes > ResultByteCapacity || job.finishedTicks + TimeSpan.TicksPerMinute * 2 < now.Ticks)
                { job.response = null; job.acknowledged = true; }
            }
        }
        /// <summary>重载时只保留尚未确认的任务，响应过大时标记结果不可恢复；不把图像 base64 写进检查点。</summary>
        public Job[] Snapshot()
        {
            lock (_gate)
            {
                Prune(DateTime.UtcNow);
                return _jobs.Values.Select(j => new Job { id = j.id, action = j.action, fingerprint = j.fingerprint,
                    payload = j.finishedTicks == 0 ? j.payload : null, confirm = j.confirm, cancelled = j.cancelled,
                    started = j.started, expirable = j.expirable, deadlineTicks = j.deadlineTicks, finishedTicks = j.finishedTicks,
                    acknowledged = j.acknowledged, response = j.response }).ToArray();
            }
        }
        public void Restore(Job[] jobs)
        {
            lock (_gate)
            {
                foreach (Job job in jobs ?? Array.Empty<Job>())
                {
                    if (_jobs.Count >= IdentityCapacity || _jobs.ContainsKey(job.id))
                    {
                        continue;
                    }
                    // JsonUtility 反序列化时可能把没有回执的字符串变为空串。
                    // 归一为 null，GET 才会按实际状态返回 pending 或 ResultExpired。
                    if (string.IsNullOrEmpty(job.response))
                    {
                        job.response = null;
                    }
                    _jobs.Add(job.id, job);
                }
                // 未启动的排队请求在重载后由宿主判 Interrupted，不能自动重放变更。
                Prune(DateTime.UtcNow);
            }
        }
    }
}
#endif
