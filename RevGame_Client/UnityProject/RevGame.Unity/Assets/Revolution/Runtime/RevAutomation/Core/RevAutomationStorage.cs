// RevAutomationStorage —— 自动化缓存的有界保留策略。只处理明确的缓存子目录，不清理 Assets/业务存档。
#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
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
    /// 回收旧版本的工具缓存和无人领取的临时工单。正在执行、消费期限内及人工保留的文件受保护。
    /// 只回收工具已知的文件名和签名。工单含自定义文件、子目录或链接时保留整张工单。
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
        /// <summary>兼容文件桥和旧版内存动作创建的临时文件白名单。</summary>
        private static readonly HashSet<string> JobFiles = new HashSet<string>(StringComparer.Ordinal)
        {
            "request.json", "response.json", "response.json.tmp", "progress.json", "progress.json.tmp", "cancel", "unity.log",
            "target-started", "scene-capture.png", "build-result.json", "build-result.json.tmp", "tests-started",
            "tests-result.json", "tests-result.json.tmp", "scenario-result.json", "scenario-result.json.tmp",
            "iterations.jsonl", "unity-errors.jsonl"
        };

        /// <summary>旧版 Unity 与 Agent 自动创建的活动分段名，历史人工文件可以原地保留。</summary>
        private static readonly Regex ActivitySegment = new Regex(@"^(unity|agent)-[0-9a-f]{32}-[0-9]{4,}\.jsonl$", RegexOptions.CultureInvariant);

        /// <summary>为兼容桥接留出回执领取时间，超时遗留工单从最后一次文件写入开始计算。</summary>
        private static readonly TimeSpan ClaimGrace = TimeSpan.FromMinutes(15);

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
        /// <summary>扫描已知工单临时目录。首次检查和实际回收都验证 ID、文件所有权及消费期限。</summary>
        public static Result Jobs(string root, RevAutomationStoragePolicy policy, bool dryRun, params string[] activeIds)
        {
            string directory = Path.GetFullPath(Path.Combine(root, "jobs"));
            var items = new List<Item>();
            var errors = new List<string>();
            if (Directory.Exists(directory) && !IsLink(new DirectoryInfo(directory)))
            {
                foreach (var folder in new DirectoryInfo(directory).EnumerateDirectories())
                {
                    try
                    {
                        if (folder.Name.Length != 32 || !folder.Name.All(Uri.IsHexDigit) || IsLink(folder))
                        {
                            continue;
                        }
                        items.Add(InspectJob(folder, policy, activeIds));
                    }
                    catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ArgumentException)
                    {
                        errors.Add(folder.Name + ": " + e.Message);
                    }
                }
            }
            return Apply(directory, items, policy.jobMaxMiB, dryRun, errors, ClaimGrace, folder =>
            {
                Item current = InspectJob(folder, policy, activeIds);
                if (current.protectedItem || current.modified > DateTime.UtcNow - ClaimGrace)
                {
                    return false;
                }
                // 只删标准文件且用非递归删除。若出现自定义文件，目录会保留。
                foreach (var file in folder.EnumerateFiles())
                {
                    if (OwnedFile(file))
                    {
                        file.Delete();
                    }
                }
                folder.Delete();
                return true;
            });
        }

        /// <summary>以目录 ID、已知文件名和期限确认工单由工具创建。过期未完成工单可被回收。</summary>
        private static Item InspectJob(DirectoryInfo folder, RevAutomationStoragePolicy policy, string[] activeIds)
        {
            var item = new Item { path = folder.FullName, modified = DateTime.MinValue, protectedItem = activeIds.Contains(folder.Name) || IsLink(folder) };
            if (item.protectedItem)
            {
                return item;
            }
            bool hasOwnedFile = false;
            foreach (FileSystemInfo child in folder.EnumerateFileSystemInfos())
            {
                if (!(child is FileInfo file) || !OwnedFile(file))
                {
                    item.protectedItem = true;
                    continue;
                }
                hasOwnedFile = true;
                item.bytes += file.Length;
                if (file.LastWriteTimeUtc > item.modified)
                {
                    item.modified = file.LastWriteTimeUtc;
                }
            }
            if (item.protectedItem || !hasOwnedFile)
            {
                return item;
            }

            string request = Path.Combine(folder.FullName, "request.json");
            if (File.Exists(request))
            {
                var data = RevAutomationJson.Object(RevAutomationJson.Parse(File.ReadAllText(request)));
                if (!data.TryGetValue("id", out object id) || !string.Equals(id as string, folder.Name, StringComparison.OrdinalIgnoreCase))
                {
                    item.protectedItem = true;
                    return item;
                }
                if (data.TryGetValue("deadlineUtc", out object deadline) && DateTimeOffset.TryParse(deadline as string, out DateTimeOffset end))
                {
                    item.protectedItem = end.UtcDateTime > DateTime.UtcNow.AddMinutes(-1);
                }
                else if (!File.Exists(Path.Combine(folder.FullName, "response.json")))
                {
                    item.protectedItem = true;
                }
            }
            string response = Path.Combine(folder.FullName, "response.json");
            if (File.Exists(response))
            {
                var result = RevAutomationJson.Object(RevAutomationJson.Parse(File.ReadAllText(response)));
                item.days = result.TryGetValue("success", out object ok) && ok is bool success && success ? policy.jobDays : policy.failureDays;
            }
            return item;
        }

        /// <summary>回收超出领取时限的后台工单凭条。活动请求和仍在期限内的身份受到保护。</summary>
        public static Result DetachedJobs(string root, bool dryRun, params string[] activeIds)
        {
            string directory = Path.GetFullPath(Path.Combine(root, "detached-jobs"));
            var items = new List<Item>();
            var errors = new List<string>();
            if (Directory.Exists(directory) && !IsLink(new DirectoryInfo(directory)))
            {
                foreach (var file in new DirectoryInfo(directory).EnumerateFiles("*.json"))
                {
                    try
                    {
                        string id = Path.GetFileNameWithoutExtension(file.Name);
                        if (!Guid.TryParseExact(id, "N", out _) || IsLink(file) || file.Length > 4096)
                        {
                            continue;
                        }
                        var receipt = RevAutomationJson.Object(RevAutomationJson.Parse(File.ReadAllText(file.FullName)));
                        if (!receipt.TryGetValue("id", out object receiptId) || !string.Equals(receiptId as string, id, StringComparison.OrdinalIgnoreCase) ||
                            !receipt.TryGetValue("sessionId", out object session) || string.IsNullOrEmpty(session as string) ||
                            !receipt.TryGetValue("deadlineUtc", out object deadline) || !DateTimeOffset.TryParse(deadline as string, out DateTimeOffset end))
                        {
                            continue;
                        }
                        items.Add(new Item { path = file.FullName, bytes = file.Length,
                            modified = end.UtcDateTime > file.LastWriteTimeUtc ? end.UtcDateTime : file.LastWriteTimeUtc,
                            protectedItem = activeIds.Contains(id) || File.Exists(file.FullName + ".keep") });
                    }
                    catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is ArgumentException)
                    {
                        errors.Add(file.Name + ": " + error.Message);
                    }
                }
            }
            return Apply(directory, items, 0, dryRun, errors, ClaimGrace);
        }

        /// <summary>预览或删除已停用的旧活动分段，仅匹配框架原先使用的文件名并保留 .keep。</summary>
        public static Result LegacyActivity(string root, bool dryRun)
        {
            string directory = Path.GetFullPath(Path.Combine(root, "activity"));
            var items = new List<Item>();
            var errors = new List<string>();
            if (Directory.Exists(directory) && !IsLink(new DirectoryInfo(directory)) && !File.Exists(Path.Combine(directory, ".keep")))
            {
                foreach (var file in new DirectoryInfo(directory).EnumerateFiles("*.jsonl"))
                {
                    if (!ActivitySegment.IsMatch(file.Name) || IsLink(file))
                    {
                        continue;
                    }
                    items.Add(new Item { path = file.FullName, bytes = file.Length, modified = file.LastWriteTimeUtc,
                        protectedItem = File.Exists(file.FullName + ".keep") });
                }
            }
            return Apply(directory, items, 0, dryRun, errors, TimeSpan.Zero);
        }

        /// <summary>标准文件必须使用已知名称，不能是软链接或重解析点。</summary>
        private static bool OwnedFile(FileInfo file)
        {
            string name = file.Name;
            bool sceneCapture = name.Length == 50 && name.StartsWith("scene-capture-", StringComparison.Ordinal) &&
                name.EndsWith(".png", StringComparison.Ordinal) && Guid.TryParseExact(name.Substring(14, 32), "N", out _);
            return (JobFiles.Contains(name) || sceneCapture) && !IsLink(file);
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
            return Apply(directory, items, policy.screenshotMaxMiB, dryRun, errors, ClaimGrace);
        }
        /// <summary>回收领取失败或进程退出留下的运行截图，保护最近两分钟可能正在传输的画面。</summary>
        public static Result TemporaryScreenshots(string directory, bool dryRun)
        {
            directory = Path.GetFullPath(directory);
            var items = new List<Item>();
            var errors = new List<string>();
            try
            {
                if (Directory.Exists(directory) && !IsLink(new DirectoryInfo(directory)))
                {
                    foreach (var file in new DirectoryInfo(directory).EnumerateFiles("rev-shot-*.png"))
                    {
                        if (IsLink(file) || !Guid.TryParseExact(Path.GetFileNameWithoutExtension(file.Name).Substring(9), "N", out _))
                        {
                            continue;
                        }
                        items.Add(new Item { path = file.FullName, bytes = file.Length, modified = file.LastWriteTimeUtc,
                            protectedItem = File.Exists(file.FullName + ".keep") });
                    }
                }
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
            {
                errors.Add(error.Message);
            }
            return Apply(directory, items, 0, dryRun, errors, TimeSpan.FromMinutes(2));
        }

        /// <summary>先预览期限和容量，再按最旧时间回收，边界和链接在写盘前再次核对。</summary>
        private static Result Apply(string root, List<Item> items, int maxMiB, bool dryRun, List<string> errors, TimeSpan grace,
            Func<DirectoryInfo, bool> removeDirectory = null)
        {
            DateTime now = DateTime.UtcNow; long limit = maxMiB * 1024L * 1024;
            long total = items.Sum(item => item.bytes);
            var result = new Result { dryRun = dryRun, inspected = items.Count, beforeBytes = total };
            foreach (Item item in items.OrderBy(item => item.modified))
            {
                if (item.protectedItem || item.modified > now - grace) { result.protectedCount++; continue; }
                if (item.modified >= now.AddDays(-item.days) && total <= limit) { continue; }
                result.candidates++; result.removableBytes += item.bytes;
                if (dryRun) { total -= item.bytes; continue; }
                try
                {
                    // 最终路径再验证；不跟随 junction/symlink。工单必须已经完成，删除前再次检查。
                    string absolute = Path.GetFullPath(item.path);
                    if (!absolute.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    { throw new IOException("清理目标越出缓存目录"); }
                    if (IsLink(new DirectoryInfo(root)))
                    {
                        throw new IOException("缓存根目录指向其他位置");
                    }
                    if (removeDirectory != null)
                    {
                        if (!removeDirectory(new DirectoryInfo(absolute)))
                        {
                            continue;
                        }
                    }
                    else
                    {
                        if (IsLink(new FileInfo(absolute)) || File.Exists(absolute + ".keep") || File.Exists(Path.Combine(root, ".keep")))
                        {
                            continue;
                        }
                        File.Delete(absolute);
                    }
                    total -= item.bytes; result.removed++;
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { errors.Add(e.Message); }
            }
            result.afterBytes = total; result.overBudget = total > limit; result.errors = errors.Take(20).ToArray(); return result;
        }
        /// <summary>识别文件或目录别名以避免清理跨出缓存根。</summary>
        private static bool IsLink(FileSystemInfo path)
        {
            return (path.Attributes & FileAttributes.ReparsePoint) != 0;
        }
    }
}
#endif
