// RevAutomationStorageActions —— 保留策略与清理预览的 Editor 入口；默认先预览，不扫描任意用户目录。
using System;
using System.Collections.Generic;
using System.IO;
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
        [RevAutomationAction("storage.status", Environment = "either", Keys = new string[0], Description = "预览自动化缓存容量、保留策略和可回收的已完成工单")]
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
        [RevAutomationAction("storage.prune", Environment = "either", Keys = new[] { "dryRun" }, Description = "按策略回收已完成自动化缓存；默认 dryRun=true 只预览")]
        public static object Prune(RevAutomationRequest request) => Inspect(JsonUtility.FromJson<Options>(request.payloadJson ?? "{}").dryRun);
        private static object Inspect(bool dryRun)
        {
            var policy = RevAutomationStorage.Read(RevAutomationBridge.Root);
            return new Dictionary<string, object> { ["policy"] = policy,
                ["jobs"] = RevAutomationStorage.Jobs(RevAutomationBridge.Root, policy, dryRun, RevAutomationBridge.ActiveRequestId,
                    SessionState.GetString(RevAutomationBridge.TestJobKey, "")),
                ["screenshots"] = RevAutomationStorage.Screenshots(Screenshots, policy, dryRun) };
        }
        internal static void Tick()
        {
            if (EditorApplication.timeSinceStartup < _next) { return; }
            _next = EditorApplication.timeSinceStartup + 600;
            try
            {
                var policy = RevAutomationStorage.Read(RevAutomationBridge.Root);
                if (!policy.automatic || Interlocked.CompareExchange(ref _cleaning, 1, 0) != 0) { return; }
                // 先在主线程取 Unity 路径/活动id；磁盘枚举和纯JSON解析在后台，避免一万工单冻结编辑器。
                string root = RevAutomationBridge.Root, screenshots = Screenshots;
                string active = RevAutomationBridge.ActiveRequestId, test = SessionState.GetString(RevAutomationBridge.TestJobKey, "");
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try
                    {
                        var result = RevAutomationStorage.Jobs(root, policy, false, active, test);
                        RevAutomationStorage.Screenshots(screenshots, policy, false);
                        LastCleanupError = result.errors.Length == 0 ? null : string.Join("; ", result.errors);
                    }
                    catch (Exception e) { LastCleanupError = e.Message; }
                    finally { Interlocked.Exchange(ref _cleaning, 0); }
                });
            }
            catch (Exception e) { LastCleanupError = e.Message; }
        }
    }
}
