using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Revolution.Editor;
using UnityEditor;
using UnityEngine;

namespace Revolution.Tests
{
    /// <summary>
    /// 用真实的 Prefab → Material 引用检查未分包清单，而不是仅检查字符串拼接。
    /// 每个用例使用独立临时资产目录，结束后删除自己的资产，不改工程已有资源或分包配置。
    /// </summary>
    public sealed class RevABDependencyTests
    {
        private string _folder;
        private ABCollectResult _collect;

        [SetUp]
        public void Setup()
        {
            _folder = "Assets/RevABDependencyTests_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", Path.GetFileName(_folder));
            _collect = new ABCollectResult();
        }

        [TearDown]
        public void Cleanup()
        {
            // 临时目录由本用例生成，绝不清空或重标记工程原有的 AB 包。
            AssetDatabase.DeleteAsset(_folder);
        }

        [Test]
        public void UnbundledListKeepsAllPathsSortedAndCountsRepeatedReferencesOnce()
        {
            // 超过旧界面“最多 8 个”的限制；两份 Prefab 共用同一组材质。
            // 每个材质仍只能出现一次，总大小必须等于清单中的源文件之和。
            Material[] materials = Enumerable.Range(0, 12).Select(i => CreateMaterial("Material" + i)).ToArray();
            AddPrefab("First", "revab_test_primary", materials);
            AddPrefab("Second", "revab_test_primary", materials);

            ABDependencyReport report = ABDependencyAnalyzer.AnalyzeFromDatabase(_collect);
            string[] expected = materials.Select(AssetDatabase.GetAssetPath).OrderBy(p => p, StringComparer.Ordinal).ToArray();

            Assert.That(report.unbundledAssets["revab_test_primary"], Is.EqualTo(expected));
            Assert.That(report.extraBytes["revab_test_primary"], Is.EqualTo(expected.Sum(p => new FileInfo(p).Length)));
            Assert.That(report.sharedAssets, Is.Empty, "只在同一个包内重复引用，不应算跨包共享。");
        }

        [Test]
        public void UnbundledListIncludesResourcesReferencedByOnlyOneBundle()
        {
            Material shared = CreateMaterial("Shared");
            Material onlyPrimary = CreateMaterial("OnlyPrimary");
            AddPrefab("Primary", "revab_test_primary", shared, onlyPrimary);
            AddPrefab("Secondary", "revab_test_secondary", shared);

            ABDependencyReport report = ABDependencyAnalyzer.AnalyzeFromDatabase(_collect);
            string sharedPath = AssetDatabase.GetAssetPath(shared);
            string onlyPrimaryPath = AssetDatabase.GetAssetPath(onlyPrimary);

            Assert.That(report.unbundledAssets["revab_test_primary"], Is.EquivalentTo(new[] { sharedPath, onlyPrimaryPath }));
            Assert.That(report.unbundledAssets["revab_test_secondary"], Is.EqualTo(new[] { sharedPath }));
            Assert.That(report.sharedAssets.Keys, Is.EquivalentTo(new[] { sharedPath }),
                "sharedAssets 只包含跨包共享项，不能用它替代单包的完整未分包清单。");
        }

        [Test]
        public void AssetsInheritBundleFromFolderAndLeaveUnbundledListAfterMarkChanges()
        {
            AssetDatabase.CreateFolder(_folder, "MarkedMaterials");
            Material material = CreateMaterial("MarkedMaterials/Inherited");
            AddPrefab("Primary", "revab_test_primary", material);

            // 先未分包，再给上级目录分包：新的依赖扫描必须移除旧清单项，并保留跨包依赖。
            ABDependencyReport before = ABDependencyAnalyzer.AnalyzeFromDatabase(_collect);
            Assert.That(before.unbundledAssets["revab_test_primary"], Does.Contain(AssetDatabase.GetAssetPath(material)));
            AssetImporter importer = AssetImporter.GetAtPath(_folder + "/MarkedMaterials");
            importer.SetAssetBundleNameAndVariant("revab_test_materials", string.Empty);
            importer.SaveAndReimport();

            ABDependencyReport after = ABDependencyAnalyzer.AnalyzeFromDatabase(_collect);
            Assert.That(after.unbundledAssets["revab_test_primary"], Is.Empty);
            Assert.That(after.extraBytes["revab_test_primary"], Is.Zero);
            Assert.That(after.directDeps["revab_test_primary"], Does.Contain("revab_test_materials"));
        }

        [Test]
        public void DetailLocateActionSelectsTheActualAssetInProject()
        {
            Material material = CreateMaterial("LocateTarget");
            UnityEngine.Object[] originalSelection = Selection.objects;
            try
            {
                // 清单按钮调用框架内部的 ABGUI.Ping。通过反射验证同一入口，
                // 不为测试把原本 internal 的界面辅助类公开到框架 API。
                Type guiType = typeof(ABDependencyAnalyzer).Assembly.GetType("Revolution.Editor.ABGUI", true);
                MethodInfo ping = guiType.GetMethod("Ping", BindingFlags.Public | BindingFlags.Static);
                Assert.That(ping, Is.Not.Null);
                ping.Invoke(null, new object[] { AssetDatabase.GetAssetPath(material) });

                Assert.That(Selection.activeObject, Is.EqualTo(material));
                Assert.That(AssetDatabase.GetAssetPath(Selection.activeObject), Is.EqualTo(_folder + "/LocateTarget.mat"));
            }
            finally
            {
                // 定位会影响全局编辑器选择；测试结束必须恢复使用者之前的选择。
                Selection.objects = originalSelection;
            }
        }

        /// <summary>使用内置 Shader，避免测试额外依赖项目自定义 Shader 或渲染管线。</summary>
        private Material CreateMaterial(string name)
        {
            Shader shader = Shader.Find("Sprites/Default");
            Assert.That(shader, Is.Not.Null);
            var material = new Material(shader);
            AssetDatabase.CreateAsset(material, _folder + "/" + name + ".mat");
            return material;
        }

        /// <summary>创建真实引用后立即销毁临时场景对象；扫描入口只读取保存好的 Prefab 资产。</summary>
        private void AddPrefab(string name, string bundle, params Material[] materials)
        {
            var go = new GameObject(name);
            string path = _folder + "/" + name + ".prefab";
            try
            {
                go.AddComponent<MeshRenderer>().sharedMaterials = materials;
                PrefabUtility.SaveAsPrefabAsset(go, path);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }

            AssetImporter importer = AssetImporter.GetAtPath(path);
            importer.SetAssetBundleNameAndVariant(bundle, string.Empty);
            importer.SaveAndReimport();
            if (!_collect.bundleToAssets.TryGetValue(bundle, out List<string> logics))
            {
                logics = new List<string>();
                _collect.bundleToAssets[bundle] = logics;
            }
            logics.Add(name);
            _collect.logicToAssetPath[name] = path;
        }
    }
}
