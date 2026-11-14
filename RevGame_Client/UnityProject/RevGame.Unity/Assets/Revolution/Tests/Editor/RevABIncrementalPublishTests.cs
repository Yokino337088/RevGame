using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using NUnit.Framework;
using Revolution.Editor;
using UnityEditor;
using UnityEngine;

namespace Revolution.Tests
{
    /// <summary>使用独立发布目录验证文件变更、旧包清理以及提交失败回滚。</summary>
    public sealed class RevABIncrementalPublishTests
    {
        private string _root;
        private string _source;
        private string _copyTarget;
        private string _destination;

        [SetUp]
        public void SetUp()
        {
            string name = "RevABPublishTest-" + Guid.NewGuid().ToString("N");
            _root = Path.Combine("Temp", name);
            _source = Path.Combine(_root, "PC");
            _copyTarget = name;
            _destination = Path.Combine("Assets", name, "PC");
            Directory.CreateDirectory(_source);
            Write(_source, "PC", "root");
            Write(_source, "PC.manifest", "root metadata");
        }

        [TearDown]
        public void TearDown()
        {
            AssetDatabase.DeleteAsset("Assets/" + _copyTarget);
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, true);
            }
        }

        [Test]
        public void PublishesOnlyChangedBytesAndRemovesOnlyPreviouslyPublishedBundles()
        {
            Bundle("tables", "old");
            Bundle("other", "constant");
            Bundle("retired", "obsolete");
            Manifest("tables", "other", "retired");
            Assert.That(Publish().UpdatedBundles, Is.EqualTo(3));
            string unchanged = Path.Combine(_destination, "other");
            File.SetLastWriteTimeUtc(unchanged, new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc));
            DateTime unchangedTime = File.GetLastWriteTimeUtc(unchanged);
            string guid = File.ReadAllText(Path.Combine(_destination, "tables.meta"));
            Write(_destination, "notes.txt", "keep");

            // 相同字节数的内容更新也应被识别，旧包仍留在构建目录中以验证清单优先。
            Bundle("tables", "new");
            Manifest("tables", "other");
            ABPublishResult result = Publish();
            Assert.That(result.UpdatedBundles, Is.EqualTo(1));
            Assert.That(result.RemovedBundles, Is.EqualTo(1));
            Assert.That(Read("tables"), Is.EqualTo("new"));
            Assert.That(Read("tables.meta"), Is.EqualTo(guid));
            Assert.That(Read("notes.txt"), Is.EqualTo("keep"));
            Assert.That(File.GetLastWriteTimeUtc(unchanged), Is.EqualTo(unchangedTime));
            Assert.That(File.Exists(Path.Combine(_destination, "retired")), Is.False);
            Assert.That(File.Exists(Path.Combine(_destination, "retired.meta")), Is.False);

            result = Publish();
            Assert.That(result.UpdatedBundles + result.RemovedBundles + result.UpdatedMetadata, Is.Zero);
        }

        [Test]
        public void MissingSourceAndInterruptedBuildLeavePreviousFilesIntact()
        {
            Bundle("a", "old");
            Manifest("a");
            Publish();
            Bundle("a", "new");
            Manifest("a", "z");
            Assert.Throws<FileNotFoundException>(() => Publish());
            Assert.That(Read("a"), Is.EqualTo("old"));

            Bundle("z", "added");
            Write(_source, ".revab-build-pending", "StandaloneWindows64");
            Assert.Throws<InvalidOperationException>(() => Publish());
            Assert.That(Read("a"), Is.EqualTo("old"));
        }

        [Test]
        public void DestinationConflictRollsBackAnEarlierReplacement()
        {
            Bundle("a", "old");
            Manifest("a");
            Publish();
            string previousManifest = Read("BuildManifest.json");
            Bundle("a", "new");
            Bundle("z", "added");
            Manifest("a", "z");
            // z 排在 a 后且目标是目录，提交 z 会失败，之前替换的 a 必须还原。
            Directory.CreateDirectory(Path.Combine(_destination, "z"));
            Assert.That(() => Publish(), Throws.Exception);
            Assert.That(Read("a"), Is.EqualTo("old"));
            Assert.That(Read("BuildManifest.json"), Is.EqualTo(previousManifest));
        }

        [Test]
        public void SameLengthTamperAndMixedMainManifestAreRejectedBeforeReplacement()
        {
            Bundle("a", "old");
            Manifest("a");
            Publish();
            Bundle("a", "new");
            Manifest("a");
            Write(_source, "a", "bad");
            Assert.Throws<InvalidDataException>(() => Publish());
            Assert.That(Read("a"), Is.EqualTo("old"));

            Bundle("a", "new");
            Write(_source, "PC", "mixd");
            Assert.Throws<InvalidDataException>(() => Publish());
            Assert.That(Read("a"), Is.EqualTo("old"));
        }

        [Test]
        public void RealBuildReusesUnchangedBundleAndPublishesCompleteManifest()
        {
            string assets = "Assets/" + _copyTarget + "-inputs";
            AssetDatabase.CreateFolder("Assets", Path.GetFileName(assets));
            try
            {
                Write(assets, "a.txt", "old");
                Write(assets, "b.txt", "constant");
                AssetDatabase.Refresh();
                var definitions = new[]
                {
                    new AssetBundleBuild { assetBundleName = "a", assetNames = new[] { assets + "/a.txt" } },
                    new AssetBundleBuild { assetBundleName = "b", assetNames = new[] { assets + "/b.txt" } }
                };
                ABBuildResult first = BuildFixture(definitions);
                Hash128 previousA = first.manifest.raw.GetAssetBundleHash("a");
                Hash128 previousB = first.manifest.raw.GetAssetBundleHash("b");
                Assert.That(Publish().UpdatedBundles, Is.EqualTo(2));

                Write(assets, "a.txt", "new");
                AssetDatabase.ImportAsset(assets + "/a.txt", ImportAssetOptions.ForceSynchronousImport);
                ABBuildResult second = BuildFixture(definitions);
                Assert.That(second.manifest.raw.GetAssetBundleHash("a"), Is.Not.EqualTo(previousA));
                Assert.That(second.manifest.raw.GetAssetBundleHash("b"), Is.EqualTo(previousB));
                Assert.That(Publish().UpdatedBundles, Is.EqualTo(1));

                AssetBundle main = AssetBundle.LoadFromFile(Path.Combine(_destination, "PC"));
                Assert.That(main, Is.Not.Null);
                try
                {
                    var manifest = main.LoadAsset<AssetBundleManifest>("AssetBundleManifest");
                    Assert.That(manifest.GetAllAssetBundles(), Is.EquivalentTo(new[] { "a", "b" }));
                    Assert.That(manifest.GetAssetBundleHash("b"), Is.EqualTo(previousB));
                }
                finally
                {
                    main.Unload(true);
                }
            }
            finally
            {
                AssetDatabase.DeleteAsset(assets);
            }
        }

        private ABBuildResult BuildFixture(AssetBundleBuild[] definitions)
        {
            BuildTarget target = EditorUserBuildSettings.activeBuildTarget;
            AssetBundleManifest manifest = BuildPipeline.BuildAssetBundles(
                _source, definitions, BuildAssetBundleOptions.ChunkBasedCompression, target);
            Assert.That(manifest, Is.Not.Null);
            var result = new ABBuildResult
            {
                success = true,
                target = target,
                outputDir = _source,
                manifest = new ABManifestWrap { raw = manifest, allBundles = manifest.GetAllAssetBundles() }
            };
            ABManifestWriter.WriteBuildManifest(result);
            return result;
        }

        private ABPublishResult Publish()
        {
            return ABBuilderCore.PublishToAssets(_source, _copyTarget);
        }

        private string Read(string name)
        {
            return File.ReadAllText(Path.Combine(_destination, name));
        }

        private void Bundle(string name, string content)
        {
            Write(_source, name, content);
            Write(_source, name + ".manifest", "bundle metadata");
        }

        private void Manifest(params string[] names)
        {
            var entries = names.Select(name => $"{{\"name\":\"{name}\",\"sha256\":\"{Digest(name)}\"," +
                $"\"manifestSha256\":\"{Digest(name + ".manifest")}\"}}");
            Write(_source, "BuildManifest.json", $"{{\"integrityVersion\":1,\"mainSha256\":\"{Digest("PC")}\"," +
                $"\"mainManifestSha256\":\"{Digest("PC.manifest")}\",\"bundles\":[{string.Join(",", entries)}]}}");
        }

        private string Digest(string name)
        {
            string path = Path.Combine(_source, name);
            using (var sha = SHA256.Create())
            {
                byte[] bytes = File.Exists(path) ? File.ReadAllBytes(path) : Array.Empty<byte>();
                return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
            }
        }

        private static void Write(string directory, string name, string contents)
        {
            File.WriteAllText(Path.Combine(directory, name), contents);
        }
    }
}
