// RevAutomationStorageActions —— 保留策略与清理预览的 Editor 入口；默认先预览，不扫描任意用户目录。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace Revolution.Editor
{
    public static class RevAutomationStorageActions
    {
        [Serializable] private sealed class Options { public string configJson; public bool dryRun = true; }
        private static double _next;
        private static int _cleaning;
        public static string LastCleanupError { get; private set; }
        private static string Screenshots => Path.Combine(Application.persistentDataPath, "RevolutionAutomation", "screenshots");
        [RevAutomationAction("storage.status", Environment = "either", Keys = new string[0], Description = "预览遗留活动分段、自动化缓存与可回收工单")]
        public static object Status(RevAutomationRequest request) => Inspect(true);
        [RevAutomationAction("storage.configure", Environment = "either", Keys = new[] { "configJson" }, Description = "保存自动化工单/截图/报告的保留期限与容量")]
        public static object Configure(RevAutomationRequest request)
        {
            var options = JsonUtility.FromJson<Options>(request.payloadJson ?? "{}");
            var policy = RevAutomationStorage.Read(RevAutomationBridge.Root);
            var fields = RevAutomationJson.Object(RevAutomationJson.Parse(options.configJson ?? "{}"));
            foreach (string key in fields.Keys)
            {
                if (typeof(RevAutomationStoragePolicy).GetField(key) == null) { throw new ArgumentException("未知保留配置：" + key); }
                if (key == "automatic" ? !(fields[key] is bool) : !(fields[key] is double number) || number != Math.Truncate(number))
                { throw new ArgumentException("保留配置类型错误：" + key); }
            }
            JsonUtility.FromJsonOverwrite(options.configJson, policy); policy.Validate();
            RevAutomationStorage.Save(RevAutomationBridge.Root, policy); return policy;
        }
        [RevAutomationAction("storage.prune", Environment = "either", Keys = new[] { "dryRun" }, Description = "回收遗留活动分段和已过期自动化缓存；默认 dryRun=true")]
        public static object Prune(RevAutomationRequest request) => Inspect(JsonUtility.FromJson<Options>(request.payloadJson ?? "{}").dryRun);
        /// <summary>统一返回工单、后台凭条、截图与遗留活动的预览或清理结果。</summary>
        private static object Inspect(bool dryRun)
        {
            string root = RevAutomationBridge.Root;
            var policy = RevAutomationStorage.Read(root);
            string active = RevAutomationBridge.ActiveRequestId;
            string test = SessionState.GetString(RevAutomationBridge.TestJobKey, "");
            return new Dictionary<string, object>
            {
                ["policy"] = policy,
                ["jobs"] = RevAutomationStorage.Jobs(root, policy, dryRun, active, test),
                ["detachedJobs"] = RevAutomationStorage.DetachedJobs(root, dryRun, active, test),
                ["screenshots"] = RevAutomationStorage.Screenshots(Screenshots, policy, dryRun),
                ["temporaryScreenshots"] = RevAutomationStorage.TemporaryScreenshots(RevRuntimeScreenshot.ScreenshotFolder(), dryRun),
                ["activity"] = RevAutomationStorage.LegacyActivity(root, dryRun)
            };
        }
        /// <summary>在后台补偿异常退出留下的旧缓存，实时请求的清理仍由调用方负责。</summary>
        internal static void Tick()
        {
            if (EditorApplication.timeSinceStartup < _next) { return; }
            _next = EditorApplication.timeSinceStartup + 600;
            try
            {
                var policy = RevAutomationStorage.Read(RevAutomationBridge.Root);
                if (!policy.automatic || Interlocked.CompareExchange(ref _cleaning, 1, 0) != 0) { return; }
                // 先在主线程取 Unity 路径/活动id；磁盘枚举和纯JSON解析在后台，避免一万工单冻结编辑器。
                string root = RevAutomationBridge.Root, screenshots = Screenshots, temporary = RevRuntimeScreenshot.ScreenshotFolder();
                string active = RevAutomationBridge.ActiveRequestId, test = SessionState.GetString(RevAutomationBridge.TestJobKey, "");
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try
                    {
                        var result = RevAutomationStorage.Jobs(root, policy, false, active, test);
                        var receipts = RevAutomationStorage.DetachedJobs(root, false, active, test);
                        var oldScreenshots = RevAutomationStorage.Screenshots(screenshots, policy, false);
                        var activity = RevAutomationStorage.LegacyActivity(root, false);
                        var pendingScreenshots = RevAutomationStorage.TemporaryScreenshots(temporary, false);
                        string[] errors = result.errors.Concat(receipts.errors).Concat(oldScreenshots.errors).Concat(activity.errors).Concat(pendingScreenshots.errors).ToArray();
                        LastCleanupError = errors.Length == 0 ? null : string.Join(" | ", errors);
                    }
                    catch (Exception e) { LastCleanupError = e.Message; }
                    finally { Interlocked.Exchange(ref _cleaning, 0); }
                });
            }
            catch (Exception e) { LastCleanupError = e.Message; }
        }
    }
}
