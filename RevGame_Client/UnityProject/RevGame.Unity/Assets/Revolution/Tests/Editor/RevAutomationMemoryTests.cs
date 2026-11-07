// 内存任务与缓存策略回归：同 id 不重放、Ack 释放大响应、未知/活动工单保护、图片摘要不复制 base64。
using System;
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Revolution.Tests
{
    public sealed class RevAutomationMemoryTests
    {
        private RevAutomationMemoryJobs.Job Job(string id = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")
            => new RevAutomationMemoryJobs.Job { id = id, action = "test", payload = "{}", fingerprint = "hash", expirable = true,
                deadlineTicks = DateTime.UtcNow.AddMinutes(5).Ticks };
        [Test]
        public void AckReleasesResultWithoutReplayingSameIdentity()
        {
            var jobs = new RevAutomationMemoryJobs(); var request = Job();
            Assert.That(jobs.Add(request, DateTime.UtcNow), Is.True);
            Assert.That(jobs.Take().id, Is.EqualTo(request.id));
            jobs.Complete(request.id, "{\"success\":true}");
            Assert.That(jobs.Ack(request.id), Is.True);
            Assert.That(jobs.Add(Job(), DateTime.UtcNow), Is.False);
            Assert.That(jobs.Take(), Is.Null);
            Assert.That(jobs.Response(request.id, "session"), Does.Contain("ResultExpired"));
        }
        [Test]
        public void DuplicateWithDifferentParametersAndExpiredSubmissionAreRejected()
        {
            var jobs = new RevAutomationMemoryJobs(); var request = Job(); jobs.Add(request, DateTime.UtcNow);
            var other = Job(); other.fingerprint = "different";
            Assert.Throws<ArgumentException>(() => jobs.Add(other, DateTime.UtcNow));
            var expired = Job("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"); expired.deadlineTicks = DateTime.UtcNow.AddSeconds(-1).Ticks;
            Assert.Throws<ArgumentException>(() => jobs.Add(expired, DateTime.UtcNow));
        }
        [Test]
        public void RestorePreservesIdentityWithoutAutoRequeueingMutation()
        {
            var before = new RevAutomationMemoryJobs(); before.Add(Job(), DateTime.UtcNow); before.Take();
            var after = new RevAutomationMemoryJobs(); after.Restore(before.Snapshot());
            Assert.That(after.Take(), Is.Null);
            Assert.That(after.Add(Job(), DateTime.UtcNow), Is.False);
            Assert.That(after.Cancel(Job().id), Is.True);
        }
        [Test]
        public void JSONEvidenceRemovesImageFromNestedDataString()
        {
            string json = "{\"dataJson\":\"{\\\"path\\\":\\\"file.png\\\",\\\"base64\\\":\\\"large-image\\\"}\"}";
            string compact = RevAutomationEvidence.WithoutImages(json);
            Assert.That(compact, Does.Not.Contain("large-image"));
            Assert.That(compact, Does.Contain("file.png"));
        }
        [Test]
        public void ReloadCheckpointAcceptsEmptyOptionalResultData()
        {
            var before = new RevAutomationMemoryJobs();
            var finished = Job();
            var play = Job("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            before.Add(finished, DateTime.UtcNow);
            before.Add(play, DateTime.UtcNow);
            before.Take();
            before.Take();
            before.Complete(finished.id, "{\"success\":true,\"dataJson\":\"\"}");
            var checkpoint = before.Snapshot();
            foreach (var job in checkpoint)
            {
                if (job.response != null)
                {
                    job.response = RevAutomationEvidence.WithoutImages(job.response);
                }
            }
            var after = new RevAutomationMemoryJobs();
            after.Restore(checkpoint);
            Assert.That(after.Response(play.id, "session"), Does.Contain("\"pending\":true"));
            Assert.That(after.Response(finished.id, "session"), Does.Contain("\"dataJson\":\"\""));
            Assert.That(after.Take(), Is.Null);
        }
        [Test]
        public void RestoredEmptyResponseUsesJobState()
        {
            var pending = Job();
            pending.response = "";
            var acknowledged = Job("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            acknowledged.response = "";
            acknowledged.finishedTicks = DateTime.UtcNow.Ticks;
            acknowledged.acknowledged = true;
            var jobs = new RevAutomationMemoryJobs();
            jobs.Restore(new[] { pending, acknowledged });
            Assert.That(jobs.Response(pending.id, "session"), Does.Contain("\"pending\":true"));
            Assert.That(jobs.Response(acknowledged.id, "session"), Does.Contain("ResultExpired"));
        }
        [Test]
        public void CleanupProtectsIncompleteActiveAndKeepJobs()
        {
            string root = Path.GetFullPath("Library/StorageFixture_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "jobs"));
            try
            {
                string[] ids = { "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "cccccccccccccccccccccccccccccccc", "dddddddddddddddddddddddddddddddd" };
                for (int i = 0; i < ids.Length; i++)
                {
                    string folder = Path.Combine(root, "jobs", ids[i]); Directory.CreateDirectory(folder);
                    if (i != 1) { string response = Path.Combine(folder, "response.json"); File.WriteAllText(response, "{\"success\":true}"); File.SetLastWriteTimeUtc(response, DateTime.UtcNow.AddDays(-40)); }
                    if (i == 2) { File.WriteAllText(Path.Combine(folder, ".keep"), "keep"); }
                }
                var preview = RevAutomationStorage.Jobs(root, new RevAutomationStoragePolicy(), true, ids[3]);
                Assert.That(preview.candidates, Is.EqualTo(1));
                Assert.That(Directory.Exists(Path.Combine(root, "jobs", ids[0])), Is.True);
                var applied = RevAutomationStorage.Jobs(root, new RevAutomationStoragePolicy(), false, ids[3]);
                Assert.That(applied.removed, Is.EqualTo(1));
                for (int i = 1; i < ids.Length; i++) { Assert.That(Directory.Exists(Path.Combine(root, "jobs", ids[i])), Is.True); }
            }
            finally { Directory.Delete(root, true); }
        }
    }
}
