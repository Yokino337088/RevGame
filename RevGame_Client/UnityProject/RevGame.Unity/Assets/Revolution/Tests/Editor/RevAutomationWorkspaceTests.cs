using System;
using System.IO;
using NUnit.Framework;
using Revolution.Editor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Revolution.Tests
{
    public sealed class RevAutomationWorkspaceTests
    {
        private Scene _scene, _original;
        private GameObject[] _existingRoots;
        private string _folder;
        private RevAutomationRequest Request(string action, object payload = null, bool confirm = false) => new RevAutomationRequest
        { id = Guid.NewGuid().ToString("N"), action = action, payloadJson = payload == null ? "{}" : JsonUtility.ToJson(payload), confirm = confirm };
        [Serializable] private sealed class Params
        { public string id, objectPath, path, destination, type; public bool includeInactive = true; public RevAutomationPropertyChange[] changes; }
        private static RevAutomationPropertyChange Change(string property, string value) => new RevAutomationPropertyChange { property = property, value = value };

        [SetUp] public void Setup()
        {
            _original = SceneManager.GetActiveScene();
            // Test Runner 自己会切到未命名隔离场景；Unity 禁止在它旁边再创建
            // 一个未命名 additive 场景。此时复用 runner 的场景，仅清理本用例创建的根节点。
            _scene = string.IsNullOrEmpty(_original.path) ? _original : EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            _existingRoots = _scene.GetRootGameObjects();
            SceneManager.SetActiveScene(_scene);
            _folder = "Assets/__RevAutomationTests_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", Path.GetFileName(_folder));
        }
        [TearDown] public void Cleanup()
        {
            if (_original.IsValid() && _original.isLoaded) { SceneManager.SetActiveScene(_original); }
            if (_scene.IsValid() && _scene.isLoaded)
            {
                if (_scene != _original) { EditorSceneManager.CloseScene(_scene, true); }
                else { foreach (var root in _scene.GetRootGameObjects()) { if (Array.IndexOf(_existingRoots, root) < 0) { UnityEngine.Object.DestroyImmediate(root); } } }
            }
            if (!string.IsNullOrEmpty(_folder)) { AssetDatabase.DeleteAsset(_folder); }
        }

        [Test] public void SerializedChangesUseRealUnityValues()
        {
            var go = new GameObject("Inspector");
            RevAutomationEditorModel.Set(go.transform, new[] { Change("m_LocalPosition.x", "12.5"), Change("m_LocalPosition.y", "-3") });
            Assert.That(go.transform.localPosition, Is.EqualTo(new Vector3(12.5f, -3, 0)));
            Assert.That(_scene.isDirty, Is.True);
        }

        [Test] public void InvalidSecondPropertyDoesNotApplyFirstProperty()
        {
            var go = new GameObject("Before");
            Assert.Throws<ArgumentException>(() => RevAutomationEditorModel.Set(go,
                new[] { Change("m_Name", "After"), Change("does_not_exist", "x") }));
            Assert.That(go.name, Is.EqualTo("Before"));
        }

        [Test] public void AmbiguousHierarchyPathIsRejected()
        {
            new GameObject("Same"); new GameObject("Same");
            Assert.Throws<ArgumentException>(() => RevAutomationEditorModel.GameObject(new RevAutomationEditorOptions { objectPath = "Same" }));
        }

        [Test] public void FindIncludesInactiveObjects()
        {
            var go = new GameObject("InactiveFixture"); go.SetActive(false);
            var page = (RevAutomationEditorModel.ObjectPage)RevAutomationWorkspace.Find(Request("gameobject.find", new Params()));
            Assert.That(Array.Exists(page.items, item => item.id == RevAutomationEditorModel.Identity(go) && !item.active), Is.True);
        }

        [Test] public void PrefabModificationSavesAndUnloadsContents()
        {
            var go = new GameObject("OriginalPrefab");
            string path = _folder + "/Fixture.prefab";
            PrefabUtility.SaveAsPrefabAsset(go, path);
            // Prefab 根名由资产文件名决定，改 m_Name 会被 Unity 规范化。
            // 改 Transform 的真实序列化字段，验证落盘后重新加载仍保持数值。
            RevAutomationWorkspace.ModifyPrefab(Request("prefab.modify", new Params { path = path, type = "UnityEngine.Transform", changes = new[] { Change("m_LocalPosition.x", "17.5") } }));
            var content = PrefabUtility.LoadPrefabContents(path);
            try { Assert.That(content.transform.localPosition.x, Is.EqualTo(17.5f)); }
            finally { PrefabUtility.UnloadPrefabContents(content); }
        }

        [Test] public void MovePreservesGuidAndRejectsOverwrite()
        {
            string source = _folder + "/Source.txt", destination = _folder + "/Moved.txt";
            File.WriteAllText(source, "fixture"); AssetDatabase.ImportAsset(source);
            string guid = AssetDatabase.AssetPathToGUID(source);
            RevAutomationWorkspace.MoveAsset(Request("asset.move", new Params { path = source, destination = destination }));
            Assert.That(AssetDatabase.AssetPathToGUID(destination), Is.EqualTo(guid));
            Assert.Throws<ArgumentException>(() => RevAutomationWorkspace.CopyAsset(Request("asset.copy", new Params { path = destination, destination = destination })));
            Assert.That(File.ReadAllText(destination), Is.EqualTo("fixture"));
        }

        [TestCase("Assets/../Outside.txt")]
        [TestCase("ProjectSettings/Settings.asset")]
        [TestCase("Assets")]
        [TestCase("Assets\\bad.txt")]
        public void InvalidWritePathsAreRejected(string path) => Assert.Throws<ArgumentException>(() => RevAutomationEditorModel.AssetPath(path, true));

        [Test] public void RemovingTransformAndAssetComponentsIsRejected()
        {
            var go = new GameObject("TransformFixture");
            Assert.Throws<ArgumentException>(() => RevAutomationWorkspace.RemoveComponent(Request("component.remove", new Params { id = RevAutomationEditorModel.Identity(go.transform) }, true)));
            Assert.That(go.transform, Is.Not.Null);
        }

        [Test] public void RegisteredDeleteRequiresConfirmationBeforeMutation()
        {
            var go = new GameObject("Protected");
            var request = Request("gameobject.delete");
            request.payloadJson = "{\"id\":\"" + RevAutomationEditorModel.Identity(go) + "\"}";
            Assert.Throws<InvalidOperationException>(() => RevAutomationActions.Execute(request));
            Assert.That(go != null, Is.True);
        }

        [Test] public void ClosedParentCycleDoesNotChangeHierarchy()
        {
            var root = new GameObject("Root"); var child = new GameObject("Child"); child.transform.SetParent(root.transform);
            var request = Request("gameobject.parent");
            request.payloadJson = "{\"id\":\"" + RevAutomationEditorModel.Identity(root) + "\",\"parent\":\"" + RevAutomationEditorModel.Identity(child) + "\"}";
            Assert.Throws<ArgumentException>(() => RevAutomationWorkspace.Reparent(request));
            Assert.That(root.transform.parent, Is.Null);
            Assert.That(child.transform.parent, Is.EqualTo(root.transform));
        }

        [Test] public void CatalogContainsInstalledExtensionsWithoutDuplicateErrors()
        {
            // 注册信息是执行守卫的来源，查询类扩展不被默认的 EditMode 守卫错误拦截。
            var request = Request("editor.capabilities");
            string json = JsonUtility.ToJson(RevAutomationActions.Execute(request));
            StringAssert.Contains("component.set", json);
            StringAssert.Contains("runtime.screenshot", json);
            StringAssert.Contains("\"errors\":[]", json);
        }
    }
}
