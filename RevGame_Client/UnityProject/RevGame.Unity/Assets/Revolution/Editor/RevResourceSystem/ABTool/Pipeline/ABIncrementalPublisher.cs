using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace Revolution.Editor
{
    /// <summary>一次发布实际新增或覆盖的业务包、移除的旧包，以及更新的清单文件数量。</summary>
    public sealed class ABPublishResult
    {
        public int UpdatedBundles { get; internal set; }
        public int RemovedBundles { get; internal set; }
        public int UpdatedMetadata { get; internal set; }

        public override string ToString()
        {
            return $"发布：更新 {UpdatedBundles} 个包，移除 {RemovedBundles} 个包，更新 {UpdatedMetadata} 个清单文件";
        }
    }

    /// <summary>按两端构建清单规划发布，只写入字节变化的文件，保留现有 Unity .meta。</summary>
    internal static class ABIncrementalPublisher
    {
        internal const string ManifestName = "BuildManifest.json";
        internal const string PendingBuild = ".revab-build-pending";

        [Serializable]
        private sealed class Listing
        {
            public BundleEntry[] bundles;
            public string platform;
            public int integrityVersion;
            public string mainSha256;
            public string mainManifestSha256;
        }

        [Serializable]
        private sealed class BundleEntry
        {
            public string name;
            public string sha256;
            public string manifestSha256;
        }

        /// <summary>暂存一项替换或移除。备份只在提交时生成，供失败后逆序还原。</summary>
        private sealed class Change
        {
            public string Target;
            public string Staged;
            public string Backup;
            public bool HadTarget;
        }

        /// <summary>源目录与目标目录必须由调用方验证；平台名同时是 Unity 二进制主包名。</summary>
        internal static ABPublishResult Publish(string source, string destination, string platform, string expectedTarget)
        {
            if (File.Exists(Path.Combine(source, PendingBuild)))
            {
                throw new InvalidOperationException("AB 构建尚未完成，请先重新构建：" + source);
            }

            HashSet<string> current = VerifyBuild(source, platform, expectedTarget);
            HashSet<string> previous = ReadBundles(destination, platform, false, null);
            var result = new ABPublishResult();
            // 放在 Assets 外，避免 Unity 把暂存文件当成资源导入。完整暂存成功前不碰目标文件。
            string staging = Path.GetFullPath(Path.Combine("Temp", "RevABPublish-" + Guid.NewGuid().ToString("N")));
            var changes = new List<Change>();
            var applied = new List<Change>();
            bool keepBackup = false;
            try
            {
                foreach (string name in current.OrderBy(name => name, StringComparer.Ordinal))
                {
                    if (StageFile(source, destination, name, staging, changes))
                    {
                        result.UpdatedBundles++;
                    }
                    StageManifest(source, destination, name + ".manifest", staging, changes, result);
                }
                // 只移除上一次清单声明、本次清单已不再使用的文件。发布目录允许并存其它项目文件。
                foreach (string name in previous.Except(current).OrderBy(name => name, StringComparer.Ordinal))
                {
                    bool removed = StageRemoval(destination, name, staging, changes);
                    bool removedManifest = StageRemoval(destination, name + ".manifest", staging, changes);
                    if (removed || removedManifest)
                    {
                        result.RemovedBundles++;
                    }
                    StageRemoval(destination, name + ".meta", staging, changes);
                    StageRemoval(destination, name + ".manifest.meta", staging, changes);
                }
                StageManifest(source, destination, platform + ".manifest", staging, changes, result);
                StageManifest(source, destination, platform, staging, changes, result);
                // 最后提交包列表，中断后再次发布仍能按旧清单计算废弃包。
                StageManifest(source, destination, ManifestName, staging, changes, result);

                foreach (Change change in changes)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(change.Target));
                    if (change.Staged == null)
                    {
                        File.Move(change.Target, change.Backup);
                    }
                    else if (change.HadTarget)
                    {
                        File.Replace(change.Staged, change.Target, change.Backup);
                    }
                    else
                    {
                        File.Move(change.Staged, change.Target);
                    }
                    applied.Add(change);
                }
                return result;
            }
            catch (Exception failure)
            {
                var errors = new List<Exception> { failure };
                for (int i = applied.Count - 1; i >= 0; i--)
                {
                    Change change = applied[i];
                    try
                    {
                        if (change.Staged == null)
                        {
                            File.Move(change.Backup, change.Target);
                        }
                        else if (change.HadTarget)
                        {
                            File.Replace(change.Backup, change.Target, null);
                        }
                        else
                        {
                            File.Delete(change.Target);
                        }
                    }
                    catch (Exception recoveryFailure)
                    {
                        errors.Add(recoveryFailure);
                    }
                }
                if (errors.Count > 1)
                {
                    keepBackup = true;
                    throw new AggregateException("AB 发布回滚不完整，备份保存在 " + staging, errors);
                }
                throw;
            }
            finally
            {
                if (!keepBackup && Directory.Exists(staging))
                {
                    try
                    {
                        Directory.Delete(staging, true);
                    }
                    catch (IOException error)
                    {
                        RevABLog.Warn("无法清理 AB 发布暂存目录：" + staging + "\n" + error.Message);
                    }
                    catch (UnauthorizedAccessException error)
                    {
                        RevABLog.Warn("无法清理 AB 发布暂存目录：" + staging + "\n" + error.Message);
                    }
                }
            }
        }

        /// <summary>复用缓存或发布前核对完整产物及其字节签名。旧发布目录的清单仍可用于识别旧包。</summary>
        internal static HashSet<string> VerifyBuild(string directory, string platform, string expectedTarget)
        {
            return ReadBundles(directory, platform, true, expectedTarget);
        }

        /// <summary>只接受正常相对包名，包括带子目录和变体的名称。禁止目录穿越与生成文件重名。</summary>
        private static HashSet<string> ReadBundles(string directory, string platform, bool required, string expectedTarget)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string manifest = Path.Combine(directory, ManifestName);
            if (!required && !File.Exists(manifest))
            {
                return names;
            }
            Listing listing = JsonUtility.FromJson<Listing>(File.ReadAllText(manifest));
            if (listing?.bundles == null)
            {
                throw new InvalidDataException("AB 构建清单缺少包列表：" + manifest);
            }
            if (expectedTarget != null && listing.platform != expectedTarget)
            {
                throw new InvalidDataException($"AB 清单平台为 {listing.platform}，请求平台为 {expectedTarget}：{manifest}");
            }
            if (required && listing.integrityVersion != 1)
            {
                throw new InvalidDataException("构建产物缺少当前版本的完整性信息，请重新构建：" + manifest);
            }
            foreach (BundleEntry item in listing.bundles)
            {
                string name = item?.name;
                if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(new[] { '\\', ':' }) >= 0
                    || name.Split('/').Any(part => part.Length == 0 || part == "." || part == ".."
                        || part.EndsWith(".") || part != part.Trim() || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                    || name.EndsWith(".manifest", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(name, platform, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(name, ManifestName, StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith(".revab-", StringComparison.OrdinalIgnoreCase) || !names.Add(name))
                {
                    throw new InvalidDataException("AB 构建清单含无效或重复包名：" + name);
                }
                if (required)
                {
                    VerifyFile(directory, name, item.sha256);
                    VerifyFile(directory, name + ".manifest", item.manifestSha256);
                }
            }
            if (required)
            {
                VerifyFile(directory, platform, listing.mainSha256);
                VerifyFile(directory, platform + ".manifest", listing.mainManifestSha256);
            }
            return names;
        }

        private static void VerifyFile(string directory, string name, string digest)
        {
            string path = Path.Combine(directory, name);
            if (string.IsNullOrEmpty(digest) || ABBuildFingerprint.DigestFile(path) != digest)
            {
                throw new InvalidDataException("AB 产物与构建清单不一致：" + path);
            }
        }

        private static void StageManifest(string source, string destination, string name, string staging, List<Change> changes, ABPublishResult result)
        {
            if (StageFile(source, destination, name, staging, changes))
            {
                result.UpdatedMetadata++;
            }
        }

        private static bool StageFile(string source, string destination, string name, string staging, List<Change> changes)
        {
            string input = Path.Combine(source, name);
            string target = Path.Combine(destination, name);
            if (SameContent(input, target))
            {
                return false;
            }
            Directory.CreateDirectory(staging);
            string staged = Path.Combine(staging, changes.Count + ".new");
            File.Copy(input, staged);
            changes.Add(new Change
            {
                Target = target,
                Staged = staged,
                Backup = Path.Combine(staging, changes.Count + ".old"),
                HadTarget = File.Exists(target)
            });
            return true;
        }

        private static bool StageRemoval(string destination, string name, string staging, List<Change> changes)
        {
            string target = Path.Combine(destination, name);
            if (!File.Exists(target))
            {
                return false;
            }
            Directory.CreateDirectory(staging);
            changes.Add(new Change { Target = target, Backup = Path.Combine(staging, changes.Count + ".old"), HadTarget = true });
            return true;
        }

        /// <summary>只在长度相同时流式比较。相同大小的配置改动也必须发布。</summary>
        private static bool SameContent(string input, string target)
        {
            var sourceInfo = new FileInfo(input);
            if (!sourceInfo.Exists)
            {
                throw new FileNotFoundException("AB 构建产物缺失", input);
            }
            var targetInfo = new FileInfo(target);
            if (!targetInfo.Exists || sourceInfo.Length != targetInfo.Length)
            {
                return false;
            }
            using (FileStream left = sourceInfo.OpenRead())
            using (FileStream right = targetInfo.OpenRead())
            {
                var a = new byte[65536];
                var b = new byte[65536];
                int count;
                while ((count = left.Read(a, 0, a.Length)) != 0)
                {
                    int read = 0;
                    while (read < count)
                    {
                        int next = right.Read(b, read, count - read);
                        if (next == 0)
                        {
                            return false;
                        }
                        read += next;
                    }
                    for (int i = 0; i < count; i++)
                    {
                        if (a[i] != b[i])
                        {
                            return false;
                        }
                    }
                }
                return right.ReadByte() == -1;
            }
        }
    }
}
