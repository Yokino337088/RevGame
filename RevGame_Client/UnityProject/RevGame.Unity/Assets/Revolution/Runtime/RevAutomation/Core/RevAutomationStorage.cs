// RevAutomationStorage —— 自动化缓存的有界保留策略。只处理明确的缓存子目录，不清理 Assets/业务存档。
#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace Revolution
{
    [Serializable]
    public sealed class RevAutomationStoragePolicy
    {
        public bool automatic = true;
        public int jobDays = 7, failureDays = 14, jobMaxMiB = 256;
        public int screenshotDays = 7, screenshotMaxMiB = 512;
        public int reportDays = 30, reportMaxMiB = 256;
        public void Validate()
        {
            foreach (int days in new[] { jobDays, failureDays, screenshotDays, reportDays })
            { if (days < 1 || days > 3650) { throw new ArgumentException("保留天数为 1..3650"); } }
            foreach (int bytes in new[] { jobMaxMiB, screenshotMaxMiB, reportMaxMiB })
            { if (bytes < 1 || bytes > 102400) { throw new ArgumentException("缓存容量为 1..102400 MiB"); } }
        }
    }
    /// <summary>
    /// 只删除已完成且超过保留策略的旧工单。没有 response 的目录视为未知/未完成，一律保护。
    /// .keep 文件表示人工保留；近15分钟目录和当前执行/TestRunner工单不删，避免清理打断证据消费。
    /// 超容量时按最老完成时间回收；若受保护内容已超过预算，报告 overBudget，不冒险删除。
    /// </summary>
    public static class RevAutomationStorage
    {
        [Serializable] public sealed class Result
        {
            public bool dryRun;
            public int inspected, protectedCount, candidates, removed;
            public long beforeBytes, removableBytes, afterBytes;
            public bool overBudget;
            public string[] errors;
        }
        private sealed class Item { public string path; public long bytes; public DateTime modified; public bool protectedItem; public int days; }
        public static RevAutomationStoragePolicy Read(string root)
        {
            string path = Path.Combine(root, "storage-policy.json");
            var policy = File.Exists(path) ? JsonUtility.FromJson<RevAutomationStoragePolicy>(File.ReadAllText(path)) : new RevAutomationStoragePolicy();
            policy.Validate(); return policy;
        }
        public static void Save(string root, RevAutomationStoragePolicy policy)
        {
            policy.Validate(); Directory.CreateDirectory(root);
            string path = Path.Combine(root, "storage-policy.json");
            File.WriteAllText(path + ".tmp", JsonUtility.ToJson(policy, true));
            if (File.Exists(path)) { File.Replace(path + ".tmp", path, null); } else { File.Move(path + ".tmp", path); }
        }
        public static Result Jobs(string root, RevAutomationStoragePolicy policy, bool dryRun, params string[] activeIds)
        {
            string directory = Path.GetFullPath(Path.Combine(root, "jobs"));
            var items = new List<Item>();
            var errors = new List<string>();
            if (Directory.Exists(directory) && !IsLink(new DirectoryInfo(directory)))
            {
                foreach (var folder in new DirectoryInfo(directory).GetDirectories())
                {
                    try
                    {
                        if (folder.Name.Length != 32 || !folder.Name.All(Uri.IsHexDigit) || IsLink(folder)) { continue; }
                        string response = Path.Combine(folder.FullName, "response.json");
                        bool complete = File.Exists(response);
                        DateTime modified = complete ? File.GetLastWriteTimeUtc(response) : folder.LastWriteTimeUtc;
                        var result = complete ? RevAutomationJson.Object(RevAutomationJson.Parse(File.ReadAllText(response))) : null;
                        bool failure = complete && (!result.TryGetValue("success", out object ok) || !(ok is bool success) || !success);
                        bool protect = !complete || activeIds.Contains(folder.Name) || File.Exists(Path.Combine(folder.FullName, ".keep")) || HasLinks(folder);
                        string request = Path.Combine(folder.FullName, "request.json");
                        if (File.Exists(request))
                        {
                            var data = RevAutomationJson.Object(RevAutomationJson.Parse(File.ReadAllText(request)));
                            if (data.TryGetValue("deadlineUtc", out object deadline) && DateTimeOffset.TryParse(deadline as string, out DateTimeOffset end))
                            { protect |= end.UtcDateTime.AddMinutes(1) > DateTime.UtcNow; }
                        }
                        items.Add(new Item { path = folder.FullName, bytes = FolderBytes(folder), modified = modified,
                            protectedItem = protect, days = failure ? policy.failureDays : policy.jobDays });
                    }
                    catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ArgumentException)
                    { errors.Add(folder.Name + ": " + e.Message); }
                }
            }
            return Apply(directory, items, policy.jobMaxMiB, dryRun, errors);
        }
        public static Result Screenshots(string directory, RevAutomationStoragePolicy policy, bool dryRun)
        {
            directory = Path.GetFullPath(directory);
            var items = new List<Item>(); var errors = new List<string>();
            if (Directory.Exists(directory) && !IsLink(new DirectoryInfo(directory)))
            {
                foreach (var file in new DirectoryInfo(directory).GetFiles("*.png"))
                {
                    if (IsLink(file)) { continue; }
                    items.Add(new Item { path = file.FullName, bytes = file.Length, modified = file.LastWriteTimeUtc,
                        days = policy.screenshotDays, protectedItem = File.Exists(file.FullName + ".keep") });
                }
            }
            return Apply(directory, items, policy.screenshotMaxMiB, dryRun, errors);
        }
        private static Result Apply(string root, List<Item> items, int maxMiB, bool dryRun, List<string> errors)
        {
            DateTime now = DateTime.UtcNow; long limit = maxMiB * 1024L * 1024;
            long total = items.Sum(item => item.bytes);
            var result = new Result { dryRun = dryRun, inspected = items.Count, beforeBytes = total };
            foreach (Item item in items.OrderBy(item => item.modified))
            {
                if (item.protectedItem || item.modified > now.AddMinutes(-15)) { result.protectedCount++; continue; }
                if (item.modified >= now.AddDays(-item.days) && total <= limit) { continue; }
                result.candidates++; result.removableBytes += item.bytes;
                if (dryRun) { total -= item.bytes; continue; }
                try
                {
                    // 最终路径再验证；不跟随 junction/symlink。工单必须已经完成，删除前再次检查。
                    string absolute = Path.GetFullPath(item.path);
                    if (!absolute.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    { throw new IOException("清理目标越出缓存目录"); }
                    if (Directory.Exists(absolute))
                    {
                        var folder = new DirectoryInfo(absolute);
                        if (HasLinks(folder) || !File.Exists(Path.Combine(absolute, "response.json")) || File.Exists(Path.Combine(absolute, ".keep"))) { continue; }
                        Directory.Delete(absolute, true);
                    }
                    else { if (IsLink(new FileInfo(absolute)) || File.Exists(absolute + ".keep")) { continue; } File.Delete(absolute); }
                    total -= item.bytes; result.removed++;
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { errors.Add(e.Message); }
            }
            result.afterBytes = total; result.overBudget = total > limit; result.errors = errors.Take(20).ToArray(); return result;
        }
        private static bool IsLink(FileSystemInfo path) => (path.Attributes & FileAttributes.ReparsePoint) != 0;
        private static bool HasLinks(DirectoryInfo directory)
        {
            foreach (FileSystemInfo child in directory.GetFileSystemInfos())
            { if (IsLink(child) || child is DirectoryInfo nested && HasLinks(nested)) { return true; } }
            return false;
        }
        private static long FolderBytes(DirectoryInfo directory)
        {
            long bytes = 0;
            foreach (FileSystemInfo child in directory.GetFileSystemInfos())
            { if (!IsLink(child)) { bytes += child is FileInfo file ? file.Length : FolderBytes((DirectoryInfo)child); } }
            return bytes;
        }
    }
}
#endif
