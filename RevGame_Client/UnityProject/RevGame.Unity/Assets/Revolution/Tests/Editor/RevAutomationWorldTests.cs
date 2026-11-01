using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Revolution.Editor;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UI;

namespace Revolution.Tests
{
    /// <summary>全链路制作动作：声明式搭场景、引用赋值、脚本与文本、动画、构建设置。全部在真实 Unity 对象上验证。</summary>
    public sealed class RevAutomationWorldTests
    {
        private Scene _scene, _original;
        private GameObject[] _existingRoots;
        private string _folder;
        private readonly System.Collections.Generic.List<string> _addedTags = new System.Collections.Generic.List<string>();

        [SetUp] public void Setup()
        {
            _original = SceneManager.GetActiveScene();
            _scene = string.IsNullOrEmpty(_original.path) ? _original : EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            _existingRoots = _scene.GetRootGameObjects();
            SceneManager.SetActiveScene(_scene);
            _folder = "Assets/__RevWorldTests_" + Guid.NewGuid().ToString("N");
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

        private static RevAutomationRequest Request(string action, string payload, bool confirm = false) =>
            new RevAutomationRequest { id = Guid.NewGuid().ToString("N"), action = action, payloadJson = payload, confirm = confirm };

        private static T Run<T>(string action, string payload, bool confirm = false) => (T)RevAutomationActions.Execute(Request(action, payload, confirm));

        private static string Id(UnityEngine.Object value) => RevAutomationEditorModel.Identity(value);

        private static GameObject Created(RevAutomationWorld.ComposeResult result, string suffix) =>
            (GameObject)RevAutomationEditorModel.Resolve(result.nodes.Single(n => n.path.EndsWith(suffix, StringComparison.Ordinal)).id);

        private int RootCount => _scene.GetRootGameObjects().Length;

        /// <summary>运行时程序集里的真实组件：Editor 程序集的类 Unity 不允许挂载，所以 script.attach 的夹具不能写在本测试程序集里。</summary>
        private static readonly Type AttachFixture = typeof(RevResAutoUnloader);

        // ---------------------------------------------------------------- scene.compose

        [Test] public void ComposeBuildsTreeWithTransformComponentsAndChildren()
        {
            var result = Run<RevAutomationWorld.ComposeResult>("scene.compose",
                "{\"nodes\":[{\"name\":\"Level\",\"children\":[" +
                "{\"name\":\"Ground\",\"primitive\":\"Plane\",\"localScale\":[3,1,3],\"isStatic\":true}," +
                "{\"name\":\"Box\",\"primitive\":\"Cube\",\"position\":[1,2,3],\"eulerAngles\":[0,45,0],\"active\":false," +
                "\"components\":[{\"type\":\"Rigidbody\",\"properties\":{\"m_Mass\":2.5,\"m_UseGravity\":false}}]}]}]}");
            Assert.That(result.created, Is.EqualTo(3));
            var ground = Created(result, "/Ground");
            var box = Created(result, "/Box");
            Assert.That(ground.transform.parent.name, Is.EqualTo("Level"));
            Assert.That(ground.transform.localScale, Is.EqualTo(new Vector3(3, 1, 3)));
            Assert.That(ground.isStatic, Is.True);
            Assert.That(box.activeSelf, Is.False);
            Assert.That(box.transform.localPosition, Is.EqualTo(new Vector3(1, 2, 3)));
            var body = box.GetComponent<Rigidbody>();
            Assert.That(body.mass, Is.EqualTo(2.5f));
            Assert.That(body.useGravity, Is.False);
            Assert.That(_scene.isDirty, Is.True);
        }

        [Test] public void ComposeReferencesLinkNodesInTheSameBatchAndConvertToComponents()
        {
            var result = Run<RevAutomationWorld.ComposeResult>("scene.compose",
                "{\"nodes\":[{\"name\":\"Target\",\"primitive\":\"Cube\"}," +
                "{\"name\":\"Rig\",\"components\":[{\"type\":\"UnityEngine.Animations.PositionConstraint\"},{\"type\":\"FixedJoint\",\"properties\":{\"m_ConnectedBody\":\"@Body\"}}]}," +
                "{\"name\":\"Body\",\"components\":[{\"type\":\"Rigidbody\"}]}]}");
            var joint = Created(result, "Rig").GetComponent<FixedJoint>();
            // 字段类型是 Rigidbody，给的是 GameObject：要像 Inspector 拖拽一样换算成组件。
            Assert.That(joint.connectedBody, Is.EqualTo(Created(result, "Body").GetComponent<Rigidbody>()));
        }

        [Test] public void ComposeReusesExistingComponentInsteadOfAddingASecondOne()
        {
            var result = Run<RevAutomationWorld.ComposeResult>("scene.compose",
                "{\"nodes\":[{\"name\":\"Crate\",\"primitive\":\"Cube\",\"components\":[{\"type\":\"BoxCollider\",\"properties\":{\"m_IsTrigger\":true}}]}]}");
            var colliders = Created(result, "Crate").GetComponents<BoxCollider>();
            Assert.That(colliders.Length, Is.EqualTo(1));
            Assert.That(colliders[0].isTrigger, Is.True);
        }

        [Test] public void ComposeBuildsUiUnderCanvasWithRectLayout()
        {
            var result = Run<RevAutomationWorld.ComposeResult>("scene.compose",
                "{\"nodes\":[{\"name\":\"UI\",\"ui\":\"Canvas\",\"children\":[{\"name\":\"Ok\",\"ui\":\"Button\",\"rect\":{\"anchorMin\":[0.5,0.5],\"anchorMax\":[0.5,0.5],\"sizeDelta\":[200,60],\"anchoredPosition\":[0,-40]}}]}]}");
            var button = Created(result, "/Ok");
            Assert.That(button.GetComponent<Button>(), Is.Not.Null);
            var rect = (RectTransform)button.transform;
            Assert.That(rect.sizeDelta, Is.EqualTo(new Vector2(200, 60)));
            Assert.That(rect.anchoredPosition, Is.EqualTo(new Vector2(0, -40)));
            Assert.That(button.layer, Is.EqualTo(Created(result, "UI").layer));
        }

        [TestCase("{\"nodes\":[{\"name\":\"A\",\"primitive\":\"Teapot\"}]}", TestName = "未知原始体")]
        [TestCase("{\"nodes\":[{\"name\":\"A\",\"oops\":1}]}", TestName = "未知字段")]
        [TestCase("{\"nodes\":[{\"primitive\":\"Cube\"}]}", TestName = "缺名字")]
        [TestCase("{\"nodes\":[{\"name\":\"A/B\"}]}", TestName = "名字含斜杠")]
        [TestCase("{\"nodes\":[{\"name\":\"A\",\"primitive\":\"Cube\",\"ui\":\"Button\"}]}", TestName = "来源冲突")]
        [TestCase("{\"nodes\":[{\"name\":\"A\",\"position\":[1,2]}]}", TestName = "向量长度错误")]
        [TestCase("{\"nodes\":[{\"name\":\"A\",\"tag\":\"__NoSuchTag\"}]}", TestName = "Tag不存在")]
        [TestCase("{\"nodes\":[{\"name\":\"A\",\"layer\":\"__NoSuchLayer\"}]}", TestName = "Layer不存在")]
        [TestCase("{\"nodes\":[{\"name\":\"A\",\"rect\":{\"sizeDelta\":[1,1]}}]}", TestName = "非UI节点用rect")]
        [TestCase("{\"nodes\":[{\"name\":\"A\",\"components\":[{\"type\":\"__NoSuchType\"}]}]}", TestName = "组件类型不存在")]
        [TestCase("{\"nodes\":[]}", TestName = "空节点")]
        public void ComposePreflightFailsWithoutCreatingAnything(string payload)
        {
            int before = RootCount;
            Assert.That(() => Run<object>("scene.compose", payload), Throws.Exception);
            Assert.That(RootCount, Is.EqualTo(before));
        }

        [TestCase("{\"nodes\":[{\"name\":\"Ok\",\"primitive\":\"Cube\"},{\"name\":\"Lost\",\"ui\":\"Button\"}]}", TestName = "UI不在Canvas下")]
        [TestCase("{\"nodes\":[{\"name\":\"Root\",\"children\":[{\"name\":\"Mid\",\"children\":[{\"name\":\"Lost\",\"ui\":\"Button\"}]}]}]}", TestName = "深层子节点失败")]
        [TestCase("{\"nodes\":[{\"name\":\"Ok\",\"primitive\":\"Cube\",\"components\":[{\"type\":\"Rigidbody\",\"properties\":{\"no_such_field\":1}}]}]}", TestName = "属性不存在")]
        [TestCase("{\"nodes\":[{\"name\":\"Ok\",\"components\":[{\"type\":\"FixedJoint\",\"properties\":{\"m_ConnectedBody\":\"@Missing\"}}]}]}", TestName = "引用目标不存在")]
        [TestCase("{\"nodes\":[{\"name\":\"Same\"},{\"name\":\"Same\"},{\"name\":\"Ok\",\"components\":[{\"type\":\"FixedJoint\",\"properties\":{\"m_ConnectedBody\":\"@Same\"}}]}]}", TestName = "引用目标重名")]
        public void ComposeFailureAfterCreationRollsEverythingBack(string payload)
        {
            int before = RootCount;
            Assert.That(() => Run<object>("scene.compose", payload), Throws.Exception);
            Assert.That(RootCount, Is.EqualTo(before), "创建阶段失败也不能留下半个场景");
        }

        [Test] public void ComposeIsOneUndoStep()
        {
            Undo.IncrementCurrentGroup();
            int before = RootCount;
            Run<object>("scene.compose", "{\"nodes\":[{\"name\":\"A\",\"primitive\":\"Cube\",\"children\":[{\"name\":\"B\"}]},{\"name\":\"C\"}]}");
            Assert.That(RootCount, Is.EqualTo(before + 2));
            Undo.PerformUndo();
            Assert.That(RootCount, Is.EqualTo(before));
        }

        // ---------------------------------------------------------------- 引用赋值

        [Test] public void ReferenceFieldPicksMatchingComponentFromGameObject()
        {
            var owner = new GameObject("Owner", typeof(FixedJoint));
            var target = new GameObject("Target", typeof(Rigidbody));
            RevAutomationEditorModel.Set(owner.GetComponent<FixedJoint>(), new[] { new RevAutomationPropertyChange { property = "m_ConnectedBody", value = Id(target) } });
            Assert.That(owner.GetComponent<FixedJoint>().connectedBody, Is.EqualTo(target.GetComponent<Rigidbody>()));
        }

        [Test] public void IncompatibleReferenceFailsInsteadOfSilentlyWritingNull()
        {
            var owner = new GameObject("Owner", typeof(FixedJoint));
            var wrong = new GameObject("NoBody");
            Assert.Throws<ArgumentException>(() => RevAutomationEditorModel.Set(owner.GetComponent<FixedJoint>(), new[] { new RevAutomationPropertyChange { property = "m_ConnectedBody", value = Id(wrong) } }));
            Assert.That(owner.GetComponent<FixedJoint>().connectedBody, Is.Null);
        }

        [Test] public void ReferenceFieldAcceptsAssetPathAndNull()
        {
            string path = _folder + "/Fixture.mat";
            AssetDatabase.CreateAsset(new Material(Shader.Find("Hidden/BlitCopy")), path);
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            var renderer = go.GetComponent<MeshRenderer>();
            RevAutomationEditorModel.Set(renderer, new[] { new RevAutomationPropertyChange { property = "m_Materials.Array.data[0]", value = path } });
            Assert.That(AssetDatabase.GetAssetPath(renderer.sharedMaterial), Is.EqualTo(path));
            RevAutomationEditorModel.Set(renderer, new[] { new RevAutomationPropertyChange { property = "m_Materials.Array.data[0]", value = "null" } });
            Assert.That(renderer.sharedMaterial, Is.Null);
        }

        // ---------------------------------------------------------------- 对象属性

        [Test] public void ConfigureChangesOnlyTheGivenFieldsAndSupportsUndo()
        {
            var go = new GameObject("Before");
            Undo.IncrementCurrentGroup();
            Run<object>("gameobject.configure", "{\"id\":\"" + Id(go) + "\",\"name\":\"After\",\"active\":false,\"layer\":\"Ignore Raycast\",\"tag\":\"EditorOnly\",\"isStatic\":true}");
            Undo.FlushUndoRecordObjects();
            Assert.That(go.name, Is.EqualTo("After"));
            Assert.That(go.activeSelf, Is.False);
            Assert.That(go.layer, Is.EqualTo(LayerMask.NameToLayer("Ignore Raycast")));
            Assert.That(go.CompareTag("EditorOnly"), Is.True);
            Assert.That(go.isStatic, Is.True);
            Undo.PerformUndo();
            Assert.That(go.name, Is.EqualTo("Before"));
            Assert.That(go.activeSelf, Is.True);
        }

        [Test] public void ConfigureRejectsBadValuesBeforeAnyWrite()
        {
            var go = new GameObject("Keep");
            Assert.Throws<ArgumentException>(() => Run<object>("gameobject.configure", "{\"id\":\"" + Id(go) + "\",\"name\":\"Changed\",\"tag\":\"__NoSuchTag\"}"));
            Assert.Throws<ArgumentException>(() => Run<object>("gameobject.configure", "{\"id\":\"" + Id(go) + "\",\"name\":\"Changed\",\"layer\":\"__NoSuchLayer\"}"));
            Assert.Throws<ArgumentException>(() => Run<object>("gameobject.configure", "{\"id\":\"" + Id(go) + "\",\"active\":\"no\"}"));
            Assert.Throws<ArgumentException>(() => Run<object>("gameobject.configure", "{\"id\":\"" + Id(go) + "\"}"));
            Assert.That(go.name, Is.EqualTo("Keep"));
            Assert.That(go.activeSelf, Is.True);
        }

        // ---------------------------------------------------------------- 脚本与文本

        [Test] public void ScriptCreateWritesTemplateAndRefusesToOverwriteWithoutConfirm()
        {
            string path = _folder + "/Nested/Deep/RevFixtureBehaviour.cs";
            var info = Run<RevAutomationScripts.ScriptInfo>("script.create", "{\"path\":\"" + path + "\",\"nameSpace\":\"Fixture.Game\"}");
            Assert.That(info.overwritten, Is.False);
            string text = File.ReadAllText(path);
            Assert.That(text, Does.Contain("namespace Fixture.Game"));
            Assert.That(text, Does.Contain("public class RevFixtureBehaviour : MonoBehaviour"));
            Assert.Throws<InvalidOperationException>(() => Run<object>("script.create", "{\"path\":\"" + path + "\"}"));
            var again = Run<RevAutomationScripts.ScriptInfo>("script.create", "{\"path\":\"" + path + "\",\"text\":\"// replaced\\n\"}", true);
            Assert.That(again.overwritten, Is.True);
            Assert.That(File.ReadAllText(path), Does.Contain("// replaced"));
        }

        [TestCase("Assets/../Outside.cs")]
        [TestCase("ProjectSettings/Bad.cs")]
        [TestCase("Assets/NotCode.txt")]
        public void ScriptCreateRejectsBadPaths(string path) =>
            Assert.That(() => Run<object>("script.create", "{\"path\":\"" + path + "\"}"), Throws.Exception);

        [TestCase("{\"className\":\"Other\"}", TestName = "类名与文件名不一致")]
        [TestCase("{\"nameSpace\":\"a b\"}", TestName = "命名空间非法")]
        [TestCase("{\"baseClass\":\"Mono;Behaviour\"}", TestName = "基类非法")]
        public void ScriptCreateRejectsBadIdentifiers(string extra)
        {
            string path = _folder + "/GoodName.cs";
            string payload = "{\"path\":\"" + path + "\"," + extra.TrimStart('{');
            Assert.Throws<ArgumentException>(() => Run<object>("script.create", payload));
            Assert.That(File.Exists(path), Is.False);
        }

        [Test] public void ScriptAttachAddsCompiledScriptFoundByPath()
        {
            string path = AssetDatabase.GUIDToAssetPath(AssetDatabase.FindAssets(AttachFixture.Name + " t:MonoScript").Single());
            var go = new GameObject("Host");
            var info = Run<RevAutomationEditorModel.ObjectInfo>("script.attach", "{\"id\":\"" + Id(go) + "\",\"script\":\"" + path + "\"}");
            Component attached = go.GetComponent(AttachFixture);
            Assert.That(attached != null, Is.True);
            Assert.That(info.id, Is.EqualTo(Id(attached)));
        }

        [Test] public void ScriptAttachExplainsWhenTheClassIsNotCompiledYet()
        {
            string path = _folder + "/NotCompiledYet.txt";
            File.WriteAllText(path, "x"); AssetDatabase.ImportAsset(path);
            var go = new GameObject("Host");
            Assert.That(() => Run<object>("script.attach", "{\"id\":\"" + Id(go) + "\",\"script\":\"" + path + "\"}"), Throws.Exception);
            Assert.That(go.GetComponents<Component>().Length, Is.EqualTo(1));
        }

        [Test] public void TextRoundTripsAndSerializedAssetsAreRefused()
        {
            string path = _folder + "/Sub/note.txt";
            Run<object>("asset.write-text", "{\"path\":\"" + path + "\",\"text\":\"中文内容\\n第二行\"}");
            var read = Run<RevAutomationScripts.TextInfo>("asset.read-text", "{\"path\":\"" + path + "\"}");
            Assert.That(read.text, Is.EqualTo("中文内容\n第二行"));
            Assert.Throws<InvalidOperationException>(() => Run<object>("asset.write-text", "{\"path\":\"" + path + "\",\"text\":\"x\"}"));
            foreach (string extension in new[] { ".meta", ".unity", ".prefab", ".asset", ".mat" })
            { Assert.Throws<ArgumentException>(() => Run<object>("asset.write-text", "{\"path\":\"" + _folder + "/bad" + extension + "\",\"text\":\"x\"}"), extension); }
        }

        [TestCase("Main.unity.", TestName = "尾随点")]
        [TestCase("Main.unity ", TestName = "尾随空格")]
        [TestCase("X.png.meta.", TestName = "尾随点伪造meta")]
        [TestCase("note.txt:stream", TestName = "备用数据流")]
        [TestCase(" lead.txt", TestName = "前导空格")]
        public void TextWriteRejectsPathsWindowsWouldSilentlyRewrite(string name)
        {
            string path = _folder + "/" + name;
            Assert.Throws<ArgumentException>(() => Run<object>("asset.write-text", "{\"path\":\"" + path + "\",\"text\":\"x\"}", true));
            Assert.That(Directory.GetFiles(_folder), Is.Empty, "一个文件都不能写出来");
        }

        [Test] public void YamlContentAndUnlistedSerializedAssetsAreRefused()
        {
            string atlas = _folder + "/Atlas.spriteatlas";
            Assert.Throws<ArgumentException>(() => Run<object>("asset.write-text", "{\"path\":\"" + atlas + "\",\"text\":\"x\"}"));
            string unknown = _folder + "/Thing.customyaml";
            Assert.Throws<ArgumentException>(() => Run<object>("asset.write-text", "{\"path\":\"" + unknown + "\",\"text\":\"%YAML 1.1\\n--- !u!1 &1\"}"));
            string existing = _folder + "/Existing.customyaml";
            File.WriteAllText(existing, "%YAML 1.1\n--- !u!1 &1\n");
            Assert.Throws<ArgumentException>(() => Run<object>("asset.write-text", "{\"path\":\"" + existing + "\",\"text\":\"plain\"}", true));
            Assert.That(File.ReadAllText(existing), Does.StartWith("%YAML"), "原文件不能被覆盖");
        }

        [Test] public void ImportFileCopiesExternalFileAndRefusesRelativeSource()
        {
            string source = Path.Combine(Path.GetTempPath(), "rev-import-" + Guid.NewGuid().ToString("N") + ".txt");
            File.WriteAllText(source, "外部文件");
            try
            {
                string path = _folder + "/Imported/data.txt";
                string escaped = source.Replace("\\", "\\\\");
                Run<object>("asset.import-file", "{\"source\":\"" + escaped + "\",\"path\":\"" + path + "\"}");
                Assert.That(File.ReadAllText(path), Is.EqualTo("外部文件"));
                Assert.That(AssetDatabase.LoadAssetAtPath<TextAsset>(path), Is.Not.Null);
                Assert.Throws<InvalidOperationException>(() => Run<object>("asset.import-file", "{\"source\":\"" + escaped + "\",\"path\":\"" + path + "\"}"));
                Assert.Throws<ArgumentException>(() => Run<object>("asset.import-file", "{\"source\":\"relative.txt\",\"path\":\"" + path + "\"}"));
            }
            finally { File.Delete(source); }
        }

        // ---------------------------------------------------------------- 场景截图

        [Test] public void CaptureWritesARealPngIntoTheJobDirectory()
        {
            GameObject.CreatePrimitive(PrimitiveType.Cube);
            var request = Request("scene.capture", "{\"view\":\"iso\",\"width\":64,\"height\":48}");
            var result = (RevAutomationWorld.CaptureResult)RevAutomationActions.Execute(request);
            byte[] bytes = File.ReadAllBytes(result.path);
            Assert.That(bytes.Take(4).ToArray(), Is.EqualTo(new byte[] { 0x89, 0x50, 0x4E, 0x47 }));
            var image = new Texture2D(2, 2);
            Assert.That(image.LoadImage(bytes), Is.True);
            Assert.That((image.width, image.height), Is.EqualTo((64, 48)));
            UnityEngine.Object.DestroyImmediate(image);
            Assert.That(result.renderers, Is.GreaterThan(0));
            Assert.That(UnityEngine.Object.FindObjectsOfType<Camera>(true).Any(c => c.name == "RevAutomationCapture"), Is.False, "临时相机必须销毁");
            Directory.Delete(RevAutomationBridge.JobDirectory(request.id), true);
        }

        [TestCase("{\"view\":\"bad\"}")]
        [TestCase("{\"width\":1}")]
        [TestCase("{\"height\":99999}")]
        public void CaptureRejectsBadArguments(string payload) => Assert.Throws<ArgumentException>(() => Run<object>("scene.capture", payload));

        // ---------------------------------------------------------------- Prefab

        [Test] public void VariantInheritsFromBaseAndDoesNotDirtyTheScene()
        {
            var source = new GameObject("Base");
            string basePath = _folder + "/Base.prefab", variantPath = _folder + "/Variant.prefab";
            PrefabUtility.SaveAsPrefabAsset(source, basePath);
            UnityEngine.Object.DestroyImmediate(source);
            EditorSceneManager.MarkSceneDirty(_scene);
            int roots = RootCount;
            Run<object>("prefab.variant", "{\"path\":\"" + basePath + "\",\"destination\":\"" + variantPath + "\"}");
            var variant = AssetDatabase.LoadAssetAtPath<GameObject>(variantPath);
            Assert.That(PrefabUtility.GetPrefabAssetType(variant), Is.EqualTo(PrefabAssetType.Variant));
            Assert.That(PrefabUtility.GetCorrespondingObjectFromSource(variant), Is.EqualTo(AssetDatabase.LoadAssetAtPath<GameObject>(basePath)));
            Assert.That(RootCount, Is.EqualTo(roots));
            Assert.Throws<ArgumentException>(() => Run<object>("prefab.variant", "{\"path\":\"" + basePath + "\",\"destination\":\"" + variantPath + "\"}"));
        }

        [Test] public void RevertDiscardsOverridesAndRequiresConfirm()
        {
            var source = new GameObject("Root"); new GameObject("Child").transform.SetParent(source.transform, false);
            string path = _folder + "/Revert.prefab";
            var saved = PrefabUtility.SaveAsPrefabAsset(source, path);
            UnityEngine.Object.DestroyImmediate(source);
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(saved, _scene);
            var child = instance.transform.Find("Child");
            child.localPosition = new Vector3(5, 5, 5);
            PrefabUtility.RecordPrefabInstancePropertyModifications(child);
            Assert.Throws<InvalidOperationException>(() => Run<object>("prefab.revert", "{\"id\":\"" + Id(instance) + "\"}"));
            Assert.That(child.localPosition, Is.EqualTo(new Vector3(5, 5, 5)));
            Run<object>("prefab.revert", "{\"id\":\"" + Id(instance) + "\"}", true);
            Assert.That(child.localPosition, Is.EqualTo(Vector3.zero));
        }

        // ---------------------------------------------------------------- 动画

        [Test] public void ClipCurveRoundTripsAndRejectsNonIncreasingTime()
        {
            string path = _folder + "/Move.anim";
            Run<object>("clip.create", "{\"path\":\"" + path + "\",\"frameRate\":60,\"loop\":true}");
            string curve = ",\"componentType\":\"UnityEngine.Transform\",\"property\":\"m_LocalPosition.x\",\"targetPath\":\"Arm\"";
            var info = Run<RevAutomationAnimation.ClipInfo>("clip.curve", "{\"path\":\"" + path + "\"" + curve + ",\"keys\":[[0,0],[1,2.5]]}");
            Assert.That(info.frameRate, Is.EqualTo(60f));
            Assert.That(info.loop, Is.True);
            Assert.That(info.length, Is.EqualTo(1f));
            Assert.That(info.curves.Single().path, Is.EqualTo("Arm"));
            Assert.That(info.curves.Single().keys, Is.EqualTo(2));
            Assert.Throws<ArgumentException>(() => Run<object>("clip.curve", "{\"path\":\"" + path + "\"" + curve + ",\"keys\":[[1,0],[1,1]]}"));
            Assert.Throws<ArgumentException>(() => Run<object>("clip.curve", "{\"path\":\"" + path + "\"" + curve + ",\"keys\":[]}"));
            Assert.Throws<ArgumentException>(() => Run<object>("clip.create", "{\"path\":\"" + path + "\"}"), "不覆盖已有资产");
        }

        [Test] public void AnimatorStateMachineRoundTrips()
        {
            string clip = _folder + "/Idle.anim", path = _folder + "/Hero.controller";
            Run<object>("clip.create", "{\"path\":\"" + clip + "\"}");
            Run<object>("animator.create", "{\"path\":\"" + path + "\"}");
            Run<object>("animator.parameter", "{\"path\":\"" + path + "\",\"name\":\"Speed\",\"parameterType\":\"Float\"}");
            Run<object>("animator.state", "{\"path\":\"" + path + "\",\"name\":\"Idle\",\"clip\":\"" + clip + "\",\"isDefault\":true}");
            Run<object>("animator.state", "{\"path\":\"" + path + "\",\"name\":\"Run\"}");
            Run<object>("animator.transition", "{\"path\":\"" + path + "\",\"from\":\"Idle\",\"to\":\"Run\",\"conditions\":[{\"parameter\":\"Speed\",\"mode\":\"Greater\",\"threshold\":0.1}]}");
            Run<object>("animator.transition", "{\"path\":\"" + path + "\",\"from\":\"Any\",\"to\":\"Idle\",\"hasExitTime\":true}");
            var info = Run<RevAutomationAnimation.ControllerInfo>("animator.inspect", "{\"path\":\"" + path + "\"}");
            Assert.That(info.parameters, Is.EqualTo(new[] { "Speed:Float" }));
            Assert.That(info.states.Single(s => s.isDefault).name, Is.EqualTo("Idle"));
            Assert.That(info.states.Single(s => s.name == "Idle").motion, Is.EqualTo("Idle"));
            Assert.That(info.transitions.Any(t => t.from == "Idle" && t.to == "Run" && t.conditions.Single() == "Speed Greater 0.1"), Is.True);
            Assert.That(info.transitions.Any(t => t.from == "Any" && t.to == "Idle"), Is.True);
            Assert.That(AssetDatabase.LoadAssetAtPath<AnimatorController>(path), Is.Not.Null);
        }

        [TestCase("\"from\":\"Idle\",\"to\":\"Run\"", TestName = "无条件也无退出时间")]
        [TestCase("\"from\":\"Idle\",\"to\":\"Nope\",\"hasExitTime\":true", TestName = "状态不存在")]
        [TestCase("\"from\":\"Idle\",\"to\":\"Run\",\"conditions\":[{\"parameter\":\"Nope\",\"mode\":\"If\"}]", TestName = "参数不存在")]
        [TestCase("\"from\":\"Idle\",\"to\":\"Run\",\"conditions\":[{\"parameter\":\"Go\",\"mode\":\"Maybe\"}]", TestName = "条件模式非法")]
        [TestCase("\"from\":\"Any\",\"to\":\"Exit\",\"hasExitTime\":true", TestName = "Any到Exit")]
        public void AnimatorRejectsTransitionsThatCouldNeverWork(string body)
        {
            string path = _folder + "/Bad.controller";
            Run<object>("animator.create", "{\"path\":\"" + path + "\"}");
            Run<object>("animator.parameter", "{\"path\":\"" + path + "\",\"name\":\"Go\",\"parameterType\":\"Bool\"}");
            Run<object>("animator.state", "{\"path\":\"" + path + "\",\"name\":\"Idle\"}");
            Run<object>("animator.state", "{\"path\":\"" + path + "\",\"name\":\"Run\"}");
            Assert.Throws<ArgumentException>(() => Run<object>("animator.transition", "{\"path\":\"" + path + "\"," + body + "}"));
            Assert.That(Run<RevAutomationAnimation.ControllerInfo>("animator.inspect", "{\"path\":\"" + path + "\"}").transitions, Is.Empty);
        }

        // ---------------------------------------------------------------- 工程设置

        [Test] public void BuildSceneAddIsIdempotentAndRemoveRestores()
        {
            var before = EditorBuildSettings.scenes;
            string path = _folder + "/Listed.unity";
            // Test Runner 的未命名场景旁边不能再开 additive 场景，所以另存一份副本当夹具。
            Assert.That(EditorSceneManager.SaveScene(_scene, path, true), Is.True);
            try
            {
                Run<object>("build.scene-add", "{\"path\":\"" + path + "\"}");
                Run<object>("build.scene-add", "{\"path\":\"" + path + "\",\"enabled\":false}");
                var entry = EditorBuildSettings.scenes.Where(s => s.path == path).ToArray();
                Assert.That(entry.Length, Is.EqualTo(1));
                Assert.That(entry[0].enabled, Is.False);
                Run<object>("build.scene-remove", "{\"path\":\"" + path + "\"}");
                Assert.That(EditorBuildSettings.scenes.Any(s => s.path == path), Is.False);
                Assert.Throws<ArgumentException>(() => Run<object>("build.scene-add", "{\"path\":\"" + _folder + "/Missing.unity\"}"));
            }
            finally { EditorBuildSettings.scenes = before; }
        }

        [Test] public void BuildTargetsReportsExactlyOneActivePlatform()
        {
            var list = Run<RevAutomationProjectOps.TargetList>("build.targets", "{}");
            Assert.That(list.targets.Count(t => t.active), Is.LessThanOrEqualTo(1));
            Assert.That(list.targets.Any(t => t.target == "Android"), Is.True);
        }

        [Test] public void DestructiveProjectActionsRequireConfirm()
        {
            foreach (string action in new[] { "lighting.clear", "navmesh.clear" })
            { Assert.Throws<InvalidOperationException>(() => Run<object>(action, "{}"), action); }
            Assert.Throws<InvalidOperationException>(() => Run<object>("package.add", "{\"package\":\"com.example.none\"}"));
            Assert.Throws<InvalidOperationException>(() => Run<object>("package.remove", "{\"package\":\"com.example.none\"}"));
            Assert.Throws<InvalidOperationException>(() => Run<object>("ab.extract-shared", "{\"bundle\":\"shared\"}"));
        }

        [Test] public void BakingRefusesUnsavedScenes()
        {
            new GameObject("Dirty");
            EditorSceneManager.MarkSceneDirty(_scene);
            Assert.Throws<InvalidOperationException>(() => Run<object>("navmesh.bake", "{}"));
        }

        [Test] public void UiSnapshotReportsNodesWithoutPlayMode()
        {
            var result = Run<RevAutomationWorld.ComposeResult>("scene.compose", "{\"nodes\":[{\"name\":\"Root\",\"ui\":\"Canvas\",\"children\":[{\"name\":\"Label\",\"ui\":\"Text\"},{\"name\":\"Blank\",\"ui\":\"Image\"}]}]}");
            var snapshot = Run<RevAutomationAuthoring.UiSnapshot>("ui.snapshot", "{\"id\":\"" + Id(Created(result, "Root")) + "\",\"limit\":2}");
            Assert.That(snapshot.total, Is.EqualTo(3));
            Assert.That(snapshot.truncated, Is.True);
            Assert.That(snapshot.nodes.Length, Is.EqualTo(2));
            var all = Run<RevAutomationAuthoring.UiSnapshot>("ui.snapshot", "{\"id\":\"" + Id(Created(result, "Root")) + "\"}");
            Assert.That(all.nodes.Single(n => n.path.EndsWith("/Blank", StringComparison.Ordinal)).text, Is.Null, "没有 Text 组件的节点不能抛异常");
            Assert.That(all.nodes.Single(n => n.path.EndsWith("/Label", StringComparison.Ordinal)).components, Does.Contain("Text"));
        }

        [TestCase("Toggle")] [TestCase("Slider")] [TestCase("Scrollbar")] [TestCase("Dropdown")] [TestCase("InputField")] [TestCase("ScrollView")] [TestCase("RawImage")]
        public void EveryExtendedUiKindCanBeCreated(string kind)
        {
            var result = Run<RevAutomationWorld.ComposeResult>("scene.compose", "{\"nodes\":[{\"name\":\"Root\",\"ui\":\"Canvas\",\"children\":[{\"name\":\"Item\",\"ui\":\"" + kind + "\"}]}]}");
            Assert.That(Created(result, "/Item").transform is RectTransform, Is.True);
        }

        [Test] public void EventSystemDoesNotNeedACanvas()
        {
            var info = Run<RevAutomationEditorModel.ObjectInfo>("ui.create", "{\"kind\":\"EventSystem\"}");
            Assert.That(((GameObject)RevAutomationEditorModel.Resolve(info.id)).GetComponent<UnityEngine.EventSystems.EventSystem>(), Is.Not.Null);
        }

        // ---------------------------------------------------------------- 配置持久化

        [TestCase("Revolution.Editor.ExcelTool.RevExcelUserSettings")]
        [TestCase("Revolution.Editor.ExcelTool.RevExcelProjectSettings")]
        public void SettingsSingletonsHaveAScriptBindingSoTheySurviveEditorRestart(string typeName)
        {
            // 一个 .cs 只有一个类能绑定脚本。没绑定时落盘的 m_Script 为空，编辑器重启后设置全部丢失。
            Type type = typeof(RevAutomationActions).Assembly.GetType(typeName);
            var instance = (ScriptableObject)type.GetProperty("instance", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.FlattenHierarchy).GetValue(null);
            Assert.That(MonoScript.FromScriptableObject(instance) != null, Is.True, typeName);
        }

        // ---------------------------------------------------------------- 批量边界

        [Test] public void BatchKeepsDictionaryResultsInsteadOfWritingEmptyObject()
        {
            string json = JsonUtility.ToJson(RevAutomationActions.Execute(Request("batch.call", "{\"steps\":[{\"action\":\"excel.check\",\"payloadJson\":\"{}\"}]}")));
            Assert.That(json, Does.Contain("autoByFolder"), json);
        }

        [TestCase("script.create")]
        [TestCase("editor.recompile")]
        [TestCase("package.list")]
        [TestCase("lighting.bake")]
        public void DeferredActionsAreRejectedInsideBatch(string action)
        {
            string json = JsonUtility.ToJson(RevAutomationActions.Execute(Request("batch.call", "{\"steps\":[{\"action\":\"" + action + "\",\"payloadJson\":\"{}\"}]}")));
            Assert.That(json, Does.Contain("\"failed\":1"), json);
        }
    }
}
