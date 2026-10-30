// RevResPathOutputTests —— 使用隔离资产验证输出路径、GUID 迁移与手写代码保护，不改项目已有常量。
using System;
using System.IO;
using NUnit.Framework;
using Revolution.Editor;
using UnityEditor;

namespace Revolution.Tests
{
    public sealed class RevResPathOutputTests
    {
        private string _folder;
        private const string FileName = "RevResPath.cs";
        private const string Generator = "ABResPathGenerator";
        // 独立测试类名，避免临时文件与工程的 Revolution.RevResPath 冲突。
        private const string Code = "// 本文件由 ABResPathGenerator 自动生成\npublic static class RevResPathOutputFixture {}\n";

        [SetUp]
        public void Setup()
        {
            _folder = "Assets/RevResPathOutputTests_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", Path.GetFileName(_folder));
            AssetDatabase.CreateFolder(_folder, "First");
            AssetDatabase.CreateFolder(_folder, "Second");
        }
        [TearDown] public void Cleanup() { AssetDatabase.DeleteAsset(_folder); }

        [Test]
        public void EmptyConfigurationKeepsOriginalDefault()
        {
            Assert.That(ABGeneratedCodePath.Resolve("", ABBuildSetting.ResPathCodePath, FileName), Is.EqualTo(ABBuildSetting.ResPathCodePath));
            Assert.That(ABGeneratedCodePath.Resolve("  ", ABBuildSetting.ResPathCodePath, FileName), Is.EqualTo(ABBuildSetting.ResPathCodePath));
        }

        [Test]
        public void DirectoryFileAndAbsoluteProjectPathsResolveToSameOutput()
        {
            string directory = _folder + "/First";
            string expected = directory + "/" + FileName;
            Assert.That(ABGeneratedCodePath.Resolve(directory, ABBuildSetting.ResPathCodePath, FileName), Is.EqualTo(expected));
            Assert.That(ABGeneratedCodePath.Resolve(expected, ABBuildSetting.ResPathCodePath, FileName), Is.EqualTo(expected));
            Assert.That(ABGeneratedCodePath.Resolve(Path.GetFullPath(directory), ABBuildSetting.ResPathCodePath, FileName), Is.EqualTo(expected));
        }

        [TestCase("Assets/../Outside")]
        [TestCase("Assets/Editor/Generated")]
        [TestCase("Assets/Generated/WrongName.cs")]
        [TestCase("../Outside")]
        public void InvalidOutputIsRejectedBeforeWriting(string path)
        {
            Assert.Throws<ArgumentException>(() => ABGeneratedCodePath.Resolve(path, ABBuildSetting.ResPathCodePath, FileName));
        }

        [Test]
        public void ChangingOutputMovesGeneratedAssetAndPreservesGuid()
        {
            string first = _folder + "/First/" + FileName, second = _folder + "/Second/" + FileName;
            ABGeneratedCodePath.Write(first, first, Code, Generator);
            string guid = AssetDatabase.AssetPathToGUID(first);
            Assert.That(guid, Is.Not.Empty);
            ABGeneratedCodePath.Write(second, first, Code, Generator);
            Assert.That(File.Exists(first), Is.False);
            Assert.That(File.Exists(second), Is.True);
            Assert.That(AssetDatabase.AssetPathToGUID(second), Is.EqualTo(guid));
            Assert.That(ABGeneratedCodePath.Write(second, second, Code, Generator), Is.False, "内容不变不得再次写盘触发编译。");
        }

        [Test]
        public void UserWrittenFileAndConflictingOutputsArePreserved()
        {
            string first = _folder + "/First/" + FileName, second = _folder + "/Second/" + FileName;
            File.WriteAllText(second, "// 用户手写代码");
            Assert.Throws<InvalidOperationException>(() => ABGeneratedCodePath.Write(second, first, Code, Generator));
            Assert.That(File.ReadAllText(second), Is.EqualTo("// 用户手写代码"));
            File.WriteAllText(second, Code);
            ABGeneratedCodePath.Write(first, first, Code, Generator);
            Assert.Throws<InvalidOperationException>(() => ABGeneratedCodePath.Write(second, first, Code, Generator));
            Assert.That(File.Exists(first) && File.Exists(second), Is.True);
        }
    }
}
