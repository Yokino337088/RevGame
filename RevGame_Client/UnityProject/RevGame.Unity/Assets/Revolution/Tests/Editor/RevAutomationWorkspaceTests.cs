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

        private RevAutomationRequest TransformRequest(string action, GameObject go, string fields = "")
        {
            var request = Request(action);
            request.payloadJson = "{\"id\":\"" + RevAutomationEditorModel.Identity(go) + "\"" + fields + "}";
            return request;
        }

        [Test] public void TypedTransformRoundTripsLocalValuesAndWorldSnapshot()
        {
            var parent = new GameObject("Parent"); parent.transform.position = new Vector3(10, 0, 0);
            var go = new GameObject("Child"); go.transform.SetParent(parent.transform, false);
            var result = (RevAutomationWorkspace.TransformInfo)RevAutomationActions.Execute(TransformRequest("transform.set", go,
                ",\"position\":[1,2,3],\"eulerAngles\":[0,90,0],\"localScale\":[2,3,4]"));
            Assert.That(go.transform.localPosition, Is.EqualTo(new Vector3(1, 2, 3)));
            Assert.That(Quaternion.Angle(go.transform.localRotation, Quaternion.Euler(0, 90, 0)), Is.LessThan(.1f));
            Assert.That(go.transform.localScale, Is.EqualTo(new Vector3(2, 3, 4)));
            Assert.That(result.position, Is.EqualTo(new[] { 11f, 2f, 3f }));
            Assert.That(result.parent, Is.EqualTo(RevAutomationEditorModel.Identity(parent)));
            var snapshot = (RevAutomationWorkspace.TransformInfo)RevAutomationActions.Execute(TransformRequest("transform.get", go));
            Assert.That(snapshot.localPosition, Is.EqualTo(result.localPosition));
            Assert.That(_scene.isDirty, Is.True);
        }

        [Test] public void WorldTransformAccountsForRotatedScaledParent()
        {
            var parent = new GameObject("Parent"); parent.transform.position = new Vector3(10, 20, 30);
            parent.transform.rotation = Quaternion.Euler(0, 45, 0); parent.transform.localScale = new Vector3(2, 3, 4);
            var go = new GameObject("Child"); go.transform.SetParent(parent.transform, false);
            RevAutomationActions.Execute(TransformRequest("transform.set", go,
                ",\"space\":\"world\",\"position\":[4,5,6],\"eulerAngles\":[15,60,30],\"localScale\":[1,2,3]"));
            Assert.That(Vector3.Distance(go.transform.position, new Vector3(4, 5, 6)), Is.LessThan(.001f));
            Assert.That(Quaternion.Angle(go.transform.rotation, Quaternion.Euler(15, 60, 30)), Is.LessThan(.1f));
            Assert.That(go.transform.localScale, Is.EqualTo(new Vector3(1, 2, 3)));
        }

        [TestCase("[1,2]")]
        [TestCase("[1,2,3,4]")]
        [TestCase("[1,true,3]")]
        [TestCase("[1,\"2\",3]")]
        [TestCase("[1,null,3]")]
        [TestCase("[1,1e39,3]")]
        public void InvalidTransformVectorDoesNotApplyOtherValues(string vector)
        {
            var go = new GameObject("ProtectedTransform");
            go.transform.localPosition = new Vector3(7, 8, 9);
            Assert.Throws<ArgumentException>(() => RevAutomationActions.Execute(TransformRequest("transform.set", go,
                ",\"position\":[1,2,3],\"localScale\":" + vector)));
            Assert.That(go.transform.localPosition, Is.EqualTo(new Vector3(7, 8, 9)));
            Assert.That(go.transform.localScale, Is.EqualTo(Vector3.one));
        }

        [Test] public void EmptyOrUnknownTransformArgumentsAreRejected()
        {
            var go = new GameObject("ProtectedTransform");
            Assert.Throws<ArgumentException>(() => RevAutomationActions.Execute(TransformRequest("transform.set", go)));
            Assert.Throws<ArgumentException>(() => RevAutomationActions.Execute(TransformRequest("transform.set", go, ",\"space\":\"World\",\"position\":[0,0,0]")));
            Assert.Throws<ArgumentException>(() => RevAutomationActions.Execute(TransformRequest("transform.set", go, ",\"scale\":[1,1,1]")));
            Assert.That(go.transform.localPosition, Is.EqualTo(Vector3.zero));
        }

        [Test] public void SingularParentRejectsWorldPositionBeforeAnyMutation()
        {
            var parent = new GameObject("Singular"); parent.transform.localScale = new Vector3(0, 1, 1);
            var go = new GameObject("Child"); go.transform.SetParent(parent.transform, false);
            Assert.Throws<ArgumentException>(() => RevAutomationActions.Execute(TransformRequest("transform.set", go,
                ",\"space\":\"world\",\"position\":[1,2,3],\"localScale\":[2,2,2]")));
            Assert.That(go.transform.localPosition, Is.EqualTo(Vector3.zero));
            Assert.That(go.transform.localScale, Is.EqualTo(Vector3.one));
        }

        [Test] public void TransformMutationSupportsUndoAndPrefabOverride()
        {
            var source = new GameObject("PrefabSource");
            var sourceChild = new GameObject("Child"); sourceChild.transform.SetParent(source.transform, false);
            string path = _folder + "/Transform.prefab";
            PrefabUtility.SaveAsPrefabAsset(source, path);
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(path), _scene);
            // 最外层 Prefab 根的位置/旋转是 Unity 的 default override，false 会明确排除它们。
            // 先验证根节点的过滤语义，再用子节点验证真正的非默认覆写，不能把过滤后的 false 当实现失败。
            Undo.IncrementCurrentGroup();
            RevAutomationActions.Execute(TransformRequest("transform.set", instance, ",\"position\":[7,8,9]"));
            Undo.FlushUndoRecordObjects();
            Assert.That(instance.transform.localPosition, Is.EqualTo(new Vector3(7, 8, 9)));
            Assert.That(PrefabUtility.HasPrefabInstanceAnyOverrides(instance, true), Is.True);
            Assert.That(PrefabUtility.HasPrefabInstanceAnyOverrides(instance, false), Is.False);
            Undo.PerformUndo();
            Assert.That(instance.transform.localPosition, Is.EqualTo(Vector3.zero));

            var child = instance.transform.Find("Child").gameObject;
            Undo.IncrementCurrentGroup();
            RevAutomationActions.Execute(TransformRequest("transform.set", child, ",\"position\":[3,4,5]"));
            Undo.FlushUndoRecordObjects();
            Assert.That(child.transform.localPosition, Is.EqualTo(new Vector3(3, 4, 5)));
            Assert.That(PrefabUtility.HasPrefabInstanceAnyOverrides(instance, false), Is.True);
            var modifications = PrefabUtility.GetPropertyModifications(instance);
            Assert.That(modifications, Is.Not.Null);
            Assert.That(Array.Exists(modifications, modification => modification.propertyPath.StartsWith("m_LocalPosition", StringComparison.Ordinal)
                && !PrefabUtility.IsDefaultOverride(modification)), Is.True);
            Undo.PerformUndo();
            Assert.That(child.transform.localPosition, Is.EqualTo(Vector3.zero));
            Assert.That(PrefabUtility.HasPrefabInstanceAnyOverrides(instance, false), Is.False);
        }

        [TestCase("Cube")]
        [TestCase("Sphere")]
        [TestCase("Capsule")]
        [TestCase("Cylinder")]
        [TestCase("Plane")]
        [TestCase("Quad")]
        public void PrimitiveCreatesRealMeshColliderAndParent(string type)
        {
            var parent = new GameObject("PrimitiveParent");
            var request = Request("gameobject.primitive");
            request.payloadJson = "{\"type\":\"" + type + "\",\"name\":\"PrimitiveFixture\",\"parent\":\"" + RevAutomationEditorModel.Identity(parent) + "\"}";
            Undo.IncrementCurrentGroup();
            var result = (RevAutomationEditorModel.ObjectInfo)RevAutomationActions.Execute(request);
            var go = (GameObject)RevAutomationEditorModel.Resolve(result.id);
            Assert.That(go.transform.parent, Is.EqualTo(parent.transform));
            Assert.That(go.GetComponent<MeshFilter>().sharedMesh, Is.Not.Null);
            Assert.That(go.GetComponent<MeshRenderer>(), Is.Not.Null);
            Assert.That(go.GetComponent<Collider>(), Is.Not.Null);
            Undo.PerformUndo();
            Assert.That(go == null, Is.True);
        }

        [TestCase("0")]
        [TestCase("cube")]
        [TestCase("Cube, Sphere")]
        public void InvalidPrimitiveLeavesNoSceneObject(string type)
        {
            int roots = _scene.rootCount;
            var request = Request("gameobject.primitive");
            request.payloadJson = "{\"type\":\"" + type + "\",\"name\":\"Rejected\"}";
            Assert.Throws<ArgumentException>(() => RevAutomationActions.Execute(request));
            Assert.That(_scene.rootCount, Is.EqualTo(roots));
        }

        [Test] public void PrimitiveRejectsMissingNameAndUnknownParentBeforeCreation()
        {
            int roots = _scene.rootCount;
            var request = Request("gameobject.primitive"); request.payloadJson = "{\"type\":\"Cube\"}";
            Assert.Throws<ArgumentException>(() => RevAutomationActions.Execute(request));
            request.payloadJson = "{\"type\":\"Cube\",\"name\":\"Rejected\",\"parent\":\"instance:0\"}";
            Assert.Throws<ArgumentException>(() => RevAutomationActions.Execute(request));
            request.payloadJson = "{\"type\":\"Cube\",\"name\":\"Rejected\",\"scene\":\"Assets/NotLoaded.unity\"}";
            Assert.Throws<ArgumentException>(() => RevAutomationActions.Execute(request));
            Assert.That(_scene.rootCount, Is.EqualTo(roots));
        }

        private Shader MaterialFixtureShader()
        {
            string name = "Hidden/RevolutionAutomationTests/" + Path.GetFileName(_folder);
            string text = "Shader \"" + name + "\" { Properties { _Float(\"Float\", Float)=0 _Range(\"Range\", Range(0,1))=0 "
                + "_Int(\"Int\", Integer)=0 _Color(\"Color\", Color)=(1,1,1,1) _Vector(\"Vector\", Vector)=(0,0,0,0) "
                + "_Tex(\"Texture\", 2D)=\"white\"{} _Cube(\"Cube\", Cube)=\"\"{} } SubShader { Pass { CGPROGRAM\n"
                + "#pragma vertex vert\n#pragma fragment frag\n#pragma shader_feature_local _ REV_TEST\n#include \"UnityCG.cginc\"\n"
                + "float4 vert(float4 value : POSITION) : SV_POSITION { return UnityObjectToClipPos(value); }\n"
                + "fixed4 frag() : SV_Target { return fixed4(1,1,1,1); }\nENDCG } } }";
            string path = _folder + "/Fixture.shader";
            File.WriteAllText(path, text);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            Shader shader = AssetDatabase.LoadAssetAtPath<Shader>(path);
            Assert.That(shader, Is.Not.Null);
            Assert.That(ShaderUtil.ShaderHasError(shader), Is.False);
            return shader;
        }

        private RevAutomationRequest MaterialRequest(string action, string path, string fields = "")
        {
            var request = Request(action); request.payloadJson = "{\"path\":\"" + path + "\"" + fields + "}";
            return request;
        }
        private Material MaterialFixture()
        {
            Shader shader = MaterialFixtureShader();
            string path = _folder + "/Fixture.mat";
            RevAutomationActions.Execute(MaterialRequest("material.create", path, ",\"shader\":\"" + shader.name + "\""));
            return AssetDatabase.LoadAssetAtPath<Material>(path);
        }

        [Test] public void ShaderQueryReturnsRealAssetAndRejectsInvalidPage()
        {
            Shader shader = MaterialFixtureShader();
            var request = Request("shader.list"); request.payloadJson = "{\"query\":\"" + shader.name + "\",\"limit\":1}";
            var page = (RevAutomationWorkspace.ShaderPage)RevAutomationActions.Execute(request);
            Assert.That(page.total, Is.EqualTo(1));
            Assert.That(page.items[0].name, Is.EqualTo(shader.name));
            Assert.That(page.items[0].path, Is.EqualTo(_folder + "/Fixture.shader"));
            Assert.That(page.items[0].findable, Is.True);
            request.payloadJson = "{\"limit\":501}";
            Assert.Throws<ArgumentException>(() => RevAutomationActions.Execute(request));
            request.payloadJson = "{\"offset\":-1}";
            Assert.Throws<ArgumentException>(() => RevAutomationActions.Execute(request));
        }

        [Test] public void ShaderListingSeparatesLoadedNamesFromFindableNames()
        {
            MaterialFixtureShader();
            var request = Request("shader.list"); request.payloadJson = "{\"limit\":500}";
            var page = (RevAutomationWorkspace.ShaderPage)RevAutomationActions.Execute(request);
            Assert.That(page.items.Length, Is.GreaterThan(0));
            foreach (var item in page.items)
            { Assert.That(item.findable, Is.EqualTo(Shader.Find(item.name) != null), item.name); }
            Assert.That(page.limitation, Does.Contain("findable"));
        }

        [Test] public void TypedMaterialValuesTexturesKeywordsAndQueueRoundTrip()
        {
            Material material = MaterialFixture(); string path = AssetDatabase.GetAssetPath(material);
            string texturePath = _folder + "/Texture.asset"; var texture = new Texture2D(2, 2);
            AssetDatabase.CreateAsset(texture, texturePath);
            var result = (RevAutomationWorkspace.MaterialInfo)RevAutomationActions.Execute(MaterialRequest("material.set", path,
                ",\"properties\":{\"_Float\":1.25,\"_Range\":0.75,\"_Int\":7,\"_Color\":[1,0.5,0,1],\"_Vector\":[1,2,3,4],\"_Tex\":\"" + texturePath
                + "\"},\"renderQueue\":3000,\"enableKeywords\":[\"REV_TEST\"]"));
            Assert.That(material.GetFloat("_Float"), Is.EqualTo(1.25f));
            Assert.That(material.GetFloat("_Range"), Is.EqualTo(.75f));
            Assert.That(material.GetInteger("_Int"), Is.EqualTo(7));
            Assert.That(material.GetColor("_Color"), Is.EqualTo(new Color(1, .5f, 0, 1)));
            Assert.That(material.GetVector("_Vector"), Is.EqualTo(new Vector4(1, 2, 3, 4)));
            Assert.That(material.GetTexture("_Tex"), Is.EqualTo(texture));
            Assert.That(result.renderQueue, Is.EqualTo(3000));
            Assert.That(result.keywords, Does.Contain("REV_TEST"));
            Assert.That(Array.Find(result.properties, property => property.name == "_Tex").texturePath, Is.EqualTo(texturePath));
            var snapshot = (RevAutomationWorkspace.MaterialInfo)RevAutomationActions.Execute(MaterialRequest("material.inspect", path));
            Assert.That(Array.Find(snapshot.properties, property => property.name == "_Int").type, Is.EqualTo("Int"));
            RevAutomationActions.Execute(MaterialRequest("material.set", path, ",\"properties\":{\"_Tex\":null},\"renderQueue\":-1,\"disableKeywords\":[\"REV_TEST\"]"));
            Assert.That(material.GetTexture("_Tex"), Is.Null);
            Assert.That(material.IsKeywordEnabled("REV_TEST"), Is.False);
        }

        [TestCase(",\"properties\":{\"_Float\":2,\"_Unknown\":1}")]
        [TestCase(",\"properties\":{\"_Float\":true}")]
        [TestCase(",\"properties\":{\"_Int\":1.25}")]
        [TestCase(",\"properties\":{\"_Color\":[1,2,3]}")]
        [TestCase(",\"properties\":{\"_Tex\":\"Assets/../secret.asset\"}")]
        [TestCase(",\"properties\":{\"_Float\":2},\"renderQueue\":5001")]
        [TestCase(",\"properties\":{\"_Float\":2},\"shader\":\"Hidden/DefinitelyMissingRevShader\"")]
        [TestCase(",\"properties\":{\"_Float\":2},\"enableKeywords\":[\"UNKNOWN_KEYWORD\"]")]
        [TestCase(",\"enableKeywords\":[\"REV_TEST\"],\"disableKeywords\":[\"REV_TEST\"]")]
        [TestCase("")]
        [TestCase(",\"properties\":[]")]
        public void InvalidMaterialArgumentsNeverChangeSourceAsset(string fields)
        {
            Material material = MaterialFixture(); string path = AssetDatabase.GetAssetPath(material);
            string before = File.ReadAllText(path); Shader shader = material.shader;
            Assert.Throws<ArgumentException>(() => RevAutomationActions.Execute(MaterialRequest("material.set", path, fields)));
            Assert.That(material.GetFloat("_Float"), Is.EqualTo(0));
            Assert.That(material.shader, Is.EqualTo(shader));
            Assert.That(File.ReadAllText(path), Is.EqualTo(before));
        }

        [Test] public void WrongTextureDimensionFailsBeforeSourceChanges()
        {
            Material material = MaterialFixture(); string path = AssetDatabase.GetAssetPath(material);
            string texturePath = _folder + "/Texture.asset"; AssetDatabase.CreateAsset(new Texture2D(2, 2), texturePath);
            Assert.Throws<ArgumentException>(() => RevAutomationActions.Execute(MaterialRequest("material.set", path,
                ",\"properties\":{\"_Float\":2,\"_Cube\":\"" + texturePath + "\"}")));
            Assert.That(material.GetFloat("_Float"), Is.EqualTo(0));
        }

        [Test] public void MaterialClonePreservesSourceAndRejectsOverwrite()
        {
            Material material = MaterialFixture(); string path = AssetDatabase.GetAssetPath(material);
            RevAutomationActions.Execute(MaterialRequest("material.set", path, ",\"properties\":{\"_Float\":3.5}"));
            string guid = AssetDatabase.AssetPathToGUID(path), destination = _folder + "/Clone.mat";
            RevAutomationActions.Execute(MaterialRequest("material.clone", path, ",\"destination\":\"" + destination + "\""));
            var clone = AssetDatabase.LoadAssetAtPath<Material>(destination);
            Assert.That(clone.GetFloat("_Float"), Is.EqualTo(3.5f));
            Assert.That(AssetDatabase.AssetPathToGUID(path), Is.EqualTo(guid));
            Assert.That(AssetDatabase.AssetPathToGUID(destination), Is.Not.EqualTo(guid));
            Assert.Throws<ArgumentException>(() => RevAutomationActions.Execute(MaterialRequest("material.clone", path, ",\"destination\":\"" + destination + "\"")));
            Assert.Throws<ArgumentException>(() => RevAutomationActions.Execute(MaterialRequest("material.create", path, ",\"shader\":\"" + material.shader.name + "\"")));
        }

        [Test] public void MaterialCreationRejectsInvalidPathAndMissingShader()
        {
            string path = _folder + "/Rejected.mat";
            Assert.Throws<ArgumentException>(() => RevAutomationActions.Execute(MaterialRequest("material.create", path, ",\"shader\":\"Hidden/DefinitelyMissingRevShader\"")));
            Assert.That(File.Exists(path), Is.False);
            Assert.Throws<ArgumentException>(() => RevAutomationActions.Execute(MaterialRequest("material.create", "Assets/../Rejected.mat", ",\"shader\":\"Standard\"")));
            Assert.Throws<ArgumentException>(() => RevAutomationActions.Execute(MaterialRequest("material.create", _folder + "/Rejected.asset", ",\"shader\":\"Standard\"")));
            Assert.Throws<ArgumentException>(() => RevAutomationActions.Execute(MaterialRequest("material.inspect", path)));
        }

        [Test] public void MaterialMutationSupportsUndoAndPreservesAssetGuid()
        {
            Material material = MaterialFixture(); string path = AssetDatabase.GetAssetPath(material), guid = AssetDatabase.AssetPathToGUID(path);
            Undo.IncrementCurrentGroup();
            RevAutomationActions.Execute(MaterialRequest("material.set", path, ",\"properties\":{\"_Float\":9}"));
            Undo.FlushUndoRecordObjects(); Undo.PerformUndo();
            Assert.That(material.GetFloat("_Float"), Is.EqualTo(0));
            Assert.That(AssetDatabase.AssetPathToGUID(path), Is.EqualTo(guid));
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
