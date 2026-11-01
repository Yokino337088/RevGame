using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Revolution.Editor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Revolution.Tests
{
    /// <summary>UI 面板与 Prefab 制作动作：用真实 Unity 对象验证 Undo、预检、不留半成品与资产落盘。</summary>
    public sealed class RevAutomationAuthoringTests
    {
        private Scene _scene, _original;
        private GameObject[] _existingRoots;
        private string _folder;

        [SetUp] public void Setup()
        {
            _original = SceneManager.GetActiveScene();
            _scene = string.IsNullOrEmpty(_original.path) ? _original : EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            _existingRoots = _scene.GetRootGameObjects();
            SceneManager.SetActiveScene(_scene);
            _folder = "Assets/__RevAuthoringTests_" + Guid.NewGuid().ToString("N");
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

        private static RevAutomationRequest Request(string action, string payload) =>
            new RevAutomationRequest { id = Guid.NewGuid().ToString("N"), action = action, payloadJson = payload };

        private static T Run<T>(string action, string payload) => (T)RevAutomationActions.Execute(Request(action, payload));

        private static string Id(UnityEngine.Object value) => RevAutomationEditorModel.Identity(value);

        private GameObject NewCanvas()
        {
            var info = Run<RevAutomationEditorModel.ObjectInfo>("ui.create", "{\"kind\":\"Canvas\",\"name\":\"AuthoringCanvas\"}");
            return (GameObject)RevAutomationEditorModel.Resolve(info.id);
        }

        private GameObject NewPanel(GameObject canvas)
        {
            var info = Run<RevAutomationEditorModel.ObjectInfo>("ui.create", "{\"kind\":\"Panel\",\"name\":\"AuthoringPanel\",\"parent\":\"" + Id(canvas) + "\"}");
            return (GameObject)RevAutomationEditorModel.Resolve(info.id);
        }

        // ---------------------------------------------------------------- ui.create

        [Test] public void CanvasIsCreatedWithScalerRaycasterAndUiLayer()
        {
            var canvas = NewCanvas();
            Assert.That(canvas.name, Is.EqualTo("AuthoringCanvas"));
            Assert.That(canvas.GetComponent<Canvas>().renderMode, Is.EqualTo(RenderMode.ScreenSpaceOverlay));
            Assert.That(canvas.GetComponent<CanvasScaler>(), Is.Not.Null);
            Assert.That(canvas.GetComponent<GraphicRaycaster>(), Is.Not.Null);
            Assert.That(canvas.scene, Is.EqualTo(_scene));
            Assert.That(_scene.isDirty, Is.True);
        }

        [TestCase("Panel", typeof(Image))]
        [TestCase("Image", typeof(Image))]
        [TestCase("Text", typeof(Text))]
        [TestCase("Button", typeof(Button))]
        public void ControlsAreRealUguiNodesUnderTheCanvas(string kind, Type expected)
        {
            var canvas = NewCanvas();
            var info = Run<RevAutomationEditorModel.ObjectInfo>("ui.create", "{\"kind\":\"" + kind + "\",\"parent\":\"" + Id(canvas) + "\"}");
            var go = (GameObject)RevAutomationEditorModel.Resolve(info.id);
            Assert.That(go.name, Is.EqualTo(kind), "省略 name 时使用 kind");
            Assert.That(go.transform.parent, Is.EqualTo(canvas.transform));
            Assert.That(go.GetComponent(expected), Is.Not.Null);
            Assert.That(go.transform is RectTransform, Is.True);
            Assert.That(go.layer, Is.EqualTo(canvas.layer));
            Assert.That(go.GetComponentInParent<Canvas>(), Is.EqualTo(canvas.GetComponent<Canvas>()));
        }

        [Test] public void ButtonHasItsLabelChildAndInheritsUiLayerRecursively()
        {
            var canvas = NewCanvas();
            var info = Run<RevAutomationEditorModel.ObjectInfo>("ui.create", "{\"kind\":\"Button\",\"parent\":\"" + Id(canvas) + "\"}");
            var button = (GameObject)RevAutomationEditorModel.Resolve(info.id);
            Assert.That(button.GetComponentInChildren<Text>(true), Is.Not.Null);
            foreach (var child in button.GetComponentsInChildren<Transform>(true)) { Assert.That(child.gameObject.layer, Is.EqualTo(canvas.layer)); }
        }

        [Test] public void ControlWithoutCanvasAncestorIsRejectedAndLeavesNothingBehind()
        {
            int before = _scene.GetRootGameObjects().Length;
            var plain = new GameObject("NotACanvas");
            Assert.Throws<ArgumentException>(() => Run<object>("ui.create", "{\"kind\":\"Button\"}"));
            Assert.Throws<ArgumentException>(() => Run<object>("ui.create", "{\"kind\":\"Button\",\"parent\":\"" + Id(plain) + "\"}"));
            Assert.That(_scene.GetRootGameObjects().Length, Is.EqualTo(before + 1), "只有测试自己建的 NotACanvas");
        }

        [TestCase("Slider")]
        [TestCase("canvas")]
        [TestCase("")]
        [TestCase("Canvas, Panel")]
        public void UnknownKindIsRejected(string kind)
        {
            Assert.Throws<ArgumentException>(() => Run<object>("ui.create", "{\"kind\":\"" + kind + "\"}"));
            Assert.Throws<ArgumentException>(() => Run<object>("ui.create", "{}"));
        }

        [Test] public void CreationSupportsUndo()
        {
            Undo.IncrementCurrentGroup();
            var canvas = NewCanvas();
            Undo.PerformUndo();
            Assert.That(canvas == null, Is.True);
        }

        // ---------------------------------------------------------------- rect.get / rect.set

        [Test] public void RectGetReportsLayoutAndOwningCanvas()
        {
            var canvas = NewCanvas();
            var panel = NewPanel(canvas);
            var rect = Run<RevAutomationAuthoring.RectInfo>("rect.get", "{\"id\":\"" + Id(panel) + "\"}");
            Assert.That(rect.anchorMin, Is.EqualTo(new[] { 0f, 0f }));
            Assert.That(rect.anchorMax, Is.EqualTo(new[] { 1f, 1f }), "Panel 默认铺满父级");
            Assert.That(rect.pivot, Is.EqualTo(new[] { 0.5f, 0.5f }));
            Assert.That(rect.canvas, Is.EqualTo("AuthoringCanvas"));
            Assert.That(rect.parent, Is.EqualTo(Id(canvas)));
            Assert.That(rect.rect.Length, Is.EqualTo(4));
        }

        [Test] public void RectSetPositionAndSizeRoundTrips()
        {
            var panel = NewPanel(NewCanvas());
            var rect = Run<RevAutomationAuthoring.RectInfo>("rect.set", "{\"id\":\"" + Id(panel) + "\",\"anchorMin\":[0.5,0.5],\"anchorMax\":[0.5,0.5],\"pivot\":[0,1],\"sizeDelta\":[400,300],\"anchoredPosition\":[-20,35]}");
            var rt = (RectTransform)panel.transform;
            Assert.That(rt.anchorMin, Is.EqualTo(new Vector2(0.5f, 0.5f)));
            Assert.That(rt.anchorMax, Is.EqualTo(new Vector2(0.5f, 0.5f)));
            Assert.That(rt.pivot, Is.EqualTo(new Vector2(0, 1)));
            Assert.That(rt.sizeDelta, Is.EqualTo(new Vector2(400, 300)));
            Assert.That(rt.anchoredPosition, Is.EqualTo(new Vector2(-20, 35)));
            Assert.That(rect.sizeDelta, Is.EqualTo(new[] { 400f, 300f }), "返回值就是写入后的读回");
            Assert.That(rect.rect[2], Is.EqualTo(400f));
            Assert.That(rect.rect[3], Is.EqualTo(300f));
        }

        [Test] public void RectSetOffsetsStretchesToParentWithMargins()
        {
            var panel = NewPanel(NewCanvas());
            Run<RevAutomationAuthoring.RectInfo>("rect.set", "{\"id\":\"" + Id(panel) + "\",\"anchorMin\":[0,0],\"anchorMax\":[1,1],\"offsetMin\":[10,20],\"offsetMax\":[-30,-40]}");
            var rt = (RectTransform)panel.transform;
            Assert.That(rt.offsetMin, Is.EqualTo(new Vector2(10, 20)));
            Assert.That(rt.offsetMax, Is.EqualTo(new Vector2(-30, -40)));
        }

        [Test] public void ChangingOnlyAnchorsKeepsOffsetsSoTheRectMovesUnlessOffsetsAreGiven()
        {
            // 实测结论（不是猜测）：Unity 脚本改锚点时保留的是 offset，而不是屏幕位置，所以矩形会移动。
            // 因此描述里要求"要保持位置请同时给偏移"，这里把两种用法都锁住。
            var panel = NewPanel(NewCanvas());
            var rt = (RectTransform)panel.transform;
            Run<RevAutomationAuthoring.RectInfo>("rect.set", "{\"id\":\"" + Id(panel) + "\",\"anchorMin\":[0.5,0.5],\"anchorMax\":[0.5,0.5],\"sizeDelta\":[200,100],\"anchoredPosition\":[10,10]}");
            var corners = new Vector3[4]; rt.GetWorldCorners(corners);
            var before = corners.ToArray();
            Vector2 offsetMin = rt.offsetMin, offsetMax = rt.offsetMax;

            Run<RevAutomationAuthoring.RectInfo>("rect.set", "{\"id\":\"" + Id(panel) + "\",\"anchorMin\":[0,0],\"anchorMax\":[0,0]}");
            Assert.That(rt.offsetMin, Is.EqualTo(offsetMin), "未给出的字段保持原值");
            Assert.That(rt.offsetMax, Is.EqualTo(offsetMax));
            rt.GetWorldCorners(corners);
            Assert.That(Vector3.Distance(corners[0], before[0]), Is.GreaterThan(1f), "单独改锚点矩形会移动，这是描述里明确写出的行为");

            // 同时给出偏移就能表达“换锚点并指定新位置”，结果是确定的。
            Run<RevAutomationAuthoring.RectInfo>("rect.set", "{\"id\":\"" + Id(panel) + "\",\"anchorMin\":[0.5,0.5],\"anchorMax\":[0.5,0.5],\"sizeDelta\":[200,100],\"anchoredPosition\":[10,10]}");
            rt.GetWorldCorners(corners);
            for (int i = 0; i < 4; i++) { Assert.That(Vector3.Distance(corners[i], before[i]), Is.LessThan(0.01f), "回到同一组完整参数应回到同一位置：角点 " + i); }
        }

        [Test] public void OffsetsAndPositionSizeAreMutuallyExclusiveAndNothingIsWritten()
        {
            var panel = NewPanel(NewCanvas());
            var rt = (RectTransform)panel.transform;
            Vector2 sizeBefore = rt.sizeDelta, anchorBefore = rt.anchorMin;
            Assert.Throws<ArgumentException>(() => Run<object>("rect.set", "{\"id\":\"" + Id(panel) + "\",\"anchorMin\":[0.3,0.3],\"offsetMin\":[1,1],\"sizeDelta\":[5,5]}"));
            Assert.That(rt.sizeDelta, Is.EqualTo(sizeBefore));
            Assert.That(rt.anchorMin, Is.EqualTo(anchorBefore), "校验失败时连合法字段也不能先写入");
        }

        [TestCase("[1]")]
        [TestCase("[1,2,3]")]
        [TestCase("[1,\"2\"]")]
        [TestCase("[1,true]")]
        [TestCase("[1,null]")]
        [TestCase("[1,10000001]")]
        [TestCase("[-10000001,1]")]
        [TestCase("\"1,2\"")]
        [TestCase("{\"x\":1}")]
        public void BadVectorsAreRejectedBeforeAnyWrite(string vector)
        {
            var panel = NewPanel(NewCanvas());
            var rt = (RectTransform)panel.transform;
            Vector2 pivotBefore = rt.pivot;
            Assert.Throws<ArgumentException>(() => Run<object>("rect.set", "{\"id\":\"" + Id(panel) + "\",\"pivot\":[0,0],\"sizeDelta\":" + vector + "}"));
            Assert.That(rt.pivot, Is.EqualTo(pivotBefore), "同一次调用里合法的 pivot 也不能被写入");
        }

        [Test] public void RectSetRejectsEmptyChangeAndNonUiTargets()
        {
            var panel = NewPanel(NewCanvas());
            Assert.Throws<ArgumentException>(() => Run<object>("rect.set", "{\"id\":\"" + Id(panel) + "\"}"));
            var plain = new GameObject("PlainTransform");
            Assert.Throws<ArgumentException>(() => Run<object>("rect.get", "{\"id\":\"" + Id(plain) + "\"}"));
            Assert.Throws<ArgumentException>(() => Run<object>("rect.set", "{\"id\":\"" + Id(plain) + "\",\"sizeDelta\":[1,1]}"));
        }

        [Test] public void RectSetSupportsUndo()
        {
            var panel = NewPanel(NewCanvas());
            var rt = (RectTransform)panel.transform;
            Vector2 before = rt.sizeDelta;
            Undo.IncrementCurrentGroup();
            Run<object>("rect.set", "{\"id\":\"" + Id(panel) + "\",\"anchorMin\":[0.5,0.5],\"anchorMax\":[0.5,0.5],\"sizeDelta\":[321,123]}");
            Undo.FlushUndoRecordObjects();
            Assert.That(rt.sizeDelta, Is.EqualTo(new Vector2(321, 123)));
            Undo.PerformUndo();
            Assert.That(rt.sizeDelta, Is.EqualTo(before));
        }

        [Test] public void RectSetOnPrefabInstanceRecordsAnOverrideSoApplyCanPersistIt()
        {
            var instance = InstanceOfFixturePrefab(out string path);
            var child = instance.transform.Find("ExistingChild").gameObject;
            Assert.That(PrefabUtility.HasPrefabInstanceAnyOverrides(instance, false), Is.False);
            Run<object>("rect.set", "{\"id\":\"" + Id(child) + "\",\"anchorMin\":[0.5,0.5],\"anchorMax\":[0.5,0.5],\"sizeDelta\":[222,111]}");
            Assert.That(PrefabUtility.HasPrefabInstanceAnyOverrides(instance, false), Is.True, "子节点的排版修改是普通覆盖，必须被登记");
            Assert.That(PrefabUtility.GetPropertyModifications(instance).Any(modification =>
                modification.propertyPath == "m_SizeDelta.x" && !PrefabUtility.IsDefaultOverride(modification)), Is.True);
            Run<RevAutomationAuthoring.PrefabApplyInfo>("prefab.apply", "{\"id\":\"" + Id(instance) + "\"}");
            var content = PrefabUtility.LoadPrefabContents(path);
            try { Assert.That(((RectTransform)content.transform.Find("ExistingChild")).sizeDelta, Is.EqualTo(new Vector2(222, 111))); }
            finally { PrefabUtility.UnloadPrefabContents(content); }
        }

        [Test] public void RootRectOverridesAreUnityDefaultOverridesButStillRecorded()
        {
            // Unity 把实例根节点的位置/排版属性当作“默认覆盖”：includeDefaultOverrides=false 的查询看不到它们，
            // 但它们确实被记录（true 时可见），apply 时也会被带走。这是 Unity 自己的语义，不是缺陷。
            var panel = NewPanel(NewCanvas());
            PrefabUtility.SaveAsPrefabAssetAndConnect(panel, _folder + "/Panel.prefab", InteractionMode.AutomatedAction);
            Assert.That(PrefabUtility.IsPartOfPrefabInstance(panel), Is.True);
            Run<object>("rect.set", "{\"id\":\"" + Id(panel) + "\",\"anchorMin\":[0.5,0.5],\"anchorMax\":[0.5,0.5],\"sizeDelta\":[222,111]}");
            Assert.That(PrefabUtility.HasPrefabInstanceAnyOverrides(panel, false), Is.False);
            Assert.That(PrefabUtility.HasPrefabInstanceAnyOverrides(panel, true), Is.True);
        }

        // ---------------------------------------------------------------- prefab.apply / prefab.unpack

        private GameObject InstanceOfFixturePrefab(out string path)
        {
            var root = new GameObject("PanelRoot", typeof(RectTransform));
            new GameObject("ExistingChild", typeof(RectTransform)).transform.SetParent(root.transform, false);
            path = _folder + "/Fixture.prefab";
            var saved = PrefabUtility.SaveAsPrefabAsset(root, path);
            UnityEngine.Object.DestroyImmediate(root);
            return (GameObject)PrefabUtility.InstantiatePrefab(saved, _scene);
        }

        [Test] public void ApplyWritesNewChildAndRectBackToThePrefabAsset()
        {
            var instance = InstanceOfFixturePrefab(out string path);
            string guid = AssetDatabase.AssetPathToGUID(path);
            var added = new GameObject("AddedByAuthoring", typeof(RectTransform));
            added.transform.SetParent(instance.transform, false);
            Run<object>("rect.set", "{\"id\":\"" + Id(added) + "\",\"anchorMin\":[0.5,0.5],\"anchorMax\":[0.5,0.5],\"sizeDelta\":[64,32]}");
            var result = Run<RevAutomationAuthoring.PrefabApplyInfo>("prefab.apply", "{\"id\":\"" + Id(instance) + "\"}");
            Assert.That(result.prefabPath, Is.EqualTo(path));
            Assert.That(result.hadOverrides, Is.True);
            Assert.That(AssetDatabase.AssetPathToGUID(path), Is.EqualTo(guid), "资产 GUID 不能因 apply 改变");
            var content = PrefabUtility.LoadPrefabContents(path);
            try
            {
                var child = content.transform.Find("AddedByAuthoring");
                Assert.That(child, Is.Not.Null, "新增子节点必须落进 Prefab 资产");
                Assert.That(((RectTransform)child).sizeDelta, Is.EqualTo(new Vector2(64, 32)));
                Assert.That(content.transform.Find("ExistingChild"), Is.Not.Null);
            }
            finally { PrefabUtility.UnloadPrefabContents(content); }
            Assert.That(PrefabUtility.HasPrefabInstanceAnyOverrides(instance, false), Is.False, "应用后实例不再有覆盖");
        }

        [Test] public void ApplyAcceptsAChildOfTheInstanceAndResolvesTheOutermostRoot()
        {
            var instance = InstanceOfFixturePrefab(out string path);
            var child = instance.transform.Find("ExistingChild").gameObject;
            child.AddComponent<CanvasRenderer>();
            var result = Run<RevAutomationAuthoring.PrefabApplyInfo>("prefab.apply", "{\"id\":\"" + Id(child) + "\"}");
            Assert.That(result.prefabPath, Is.EqualTo(path));
            Assert.That(result.instance.id, Is.EqualTo(Id(instance)));
            var content = PrefabUtility.LoadPrefabContents(path);
            try { Assert.That(content.transform.Find("ExistingChild").GetComponent<CanvasRenderer>(), Is.Not.Null); }
            finally { PrefabUtility.UnloadPrefabContents(content); }
        }

        [Test] public void ApplyWithoutOverridesIsReportedNotSilentlyClaimedAsAChange()
        {
            var instance = InstanceOfFixturePrefab(out _);
            var result = Run<RevAutomationAuthoring.PrefabApplyInfo>("prefab.apply", "{\"id\":\"" + Id(instance) + "\"}");
            Assert.That(result.hadOverrides, Is.False);
        }

        [Test] public void ApplyAndUnpackRejectObjectsThatAreNotPrefabInstances()
        {
            var plain = new GameObject("Plain");
            Assert.Throws<ArgumentException>(() => Run<object>("prefab.apply", "{\"id\":\"" + Id(plain) + "\"}"));
            Assert.Throws<ArgumentException>(() => Run<object>("prefab.unpack", "{\"id\":\"" + Id(plain) + "\"}"));
        }

        [Test] public void UnpackRootDetachesTheInstanceFromTheAsset()
        {
            var instance = InstanceOfFixturePrefab(out string path);
            string guid = AssetDatabase.AssetPathToGUID(path);
            Run<RevAutomationEditorModel.ObjectInfo>("prefab.unpack", "{\"id\":\"" + Id(instance) + "\"}");
            Assert.That(PrefabUtility.IsPartOfPrefabInstance(instance), Is.False);
            Assert.That(instance.transform.Find("ExistingChild"), Is.Not.Null, "解包保留全部节点");
            Assert.That(AssetDatabase.AssetPathToGUID(path), Is.EqualTo(guid), "解包不能动资产");
            Assert.That(AssetDatabase.LoadAssetAtPath<GameObject>(path), Is.Not.Null);
        }

        private GameObject InstanceOfNestedPrefab()
        {
            var inner = new GameObject("InnerRoot", typeof(RectTransform));
            var innerAsset = PrefabUtility.SaveAsPrefabAsset(inner, _folder + "/Inner.prefab");
            UnityEngine.Object.DestroyImmediate(inner);
            var outer = new GameObject("OuterRoot", typeof(RectTransform));
            var nested = (GameObject)PrefabUtility.InstantiatePrefab(innerAsset);
            nested.name = "NestedInner";
            nested.transform.SetParent(outer.transform, false);
            var outerAsset = PrefabUtility.SaveAsPrefabAsset(outer, _folder + "/Outer.prefab");
            UnityEngine.Object.DestroyImmediate(outer);
            return (GameObject)PrefabUtility.InstantiatePrefab(outerAsset, _scene);
        }

        [Test] public void UnpackRootKeepsNestedPrefabsLinkedButCompletelyDoesNot()
        {
            var rootMode = InstanceOfNestedPrefab();
            Run<object>("prefab.unpack", "{\"id\":\"" + Id(rootMode) + "\",\"unpackMode\":\"root\"}");
            Assert.That(PrefabUtility.IsPartOfPrefabInstance(rootMode), Is.False);
            var nestedKept = rootMode.transform.Find("NestedInner").gameObject;
            Assert.That(PrefabUtility.IsPartOfPrefabInstance(nestedKept), Is.True, "root 模式只解最外层，嵌套 Prefab 仍然关联");

            var completely = InstanceOfNestedPrefab();
            Run<object>("prefab.unpack", "{\"id\":\"" + Id(completely) + "\",\"unpackMode\":\"completely\"}");
            Assert.That(PrefabUtility.IsPartOfPrefabInstance(completely), Is.False);
            Assert.That(PrefabUtility.IsPartOfPrefabInstance(completely.transform.Find("NestedInner").gameObject), Is.False, "completely 连嵌套一并解开");
        }

        [TestCase("all")]
        [TestCase("Root")]
        [TestCase("")]
        public void UnpackRejectsUnknownModeBeforeTouchingTheInstance(string mode)
        {
            var instance = InstanceOfFixturePrefab(out _);
            Assert.Throws<ArgumentException>(() => Run<object>("prefab.unpack", "{\"id\":\"" + Id(instance) + "\",\"unpackMode\":\"" + mode + "\"}"));
            Assert.That(PrefabUtility.IsPartOfPrefabInstance(instance), Is.True);
        }

        // ---------------------------------------------------------------- catalog

        [Test] public void NewActionsAreRegisteredWithMatchingClassification()
        {
            Assert.That(RevAutomationCatalog.EnvironmentOf("rect.get"), Is.EqualTo("either"));
            foreach (string action in new[] { "rect.set", "ui.create", "prefab.apply", "prefab.unpack" })
            { Assert.That(RevAutomationCatalog.EnvironmentOf(action), Is.EqualTo("edit"), action); }
            Assert.That(RevAutomationCatalog.EnvironmentOf("rect.nonexistent"), Is.Null);
        }
    }
}
