import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from revautomation.cli import ROOT, parser, payload_of, run_pipeline, validate_action
from revautomation.client import RevError, read_json
from revautomation.pipeline import matches, pointer, prepare, resolve


class FakeClient:
    timeout = 3
    def __init__(self):
        self.calls = []
        self.responses = []
    def call(self, action, payload=None, confirm=False, progress=None):
        self.calls.append((action, payload, confirm))
        return self.responses.pop(0) if self.responses else dict(success=True, data=dict(id="instance:7", total=1))


class AutomationTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.path = Path(self.directory.name) / "plan.json"
        self.client = FakeClient()

    def plan(self, steps, cleanup=None):
        self.path.write_text(json.dumps(dict(steps=steps, **({"finally": cleanup} if cleanup is not None else {}))), encoding="utf-8")
        return self.path

    def test_unknown_parameter_fails_before_any_effect(self):
        self.plan([dict(action="gameobject.create", payload=dict(name="fixture")), dict(action="component.set", payload=dict(typo=True))])
        with self.assertRaises(RevError):
            run_pipeline(self.client, self.path, False)
        self.assertEqual(self.client.calls, [])

    def test_typed_reference_uses_real_previous_result(self):
        self.plan([dict(id="created", action="gameobject.create", payload=dict(name="fixture")),
                   dict(action="component.list", payload=dict(id={"$ref": "created#/data/id"}))])
        self.assertTrue(run_pipeline(self.client, self.path, False)["success"])
        self.assertEqual(self.client.calls[1][1]["id"], "instance:7")

    def test_wrong_resolved_type_does_not_invoke_action(self):
        self.client.responses = [dict(success=True, data=dict(id=42))]
        self.plan([dict(id="created", action="gameobject.create", payload=dict(name="fixture")),
                   dict(action="component.list", payload=dict(id={"$ref": "created#/data/id"}))])
        self.assertFalse(run_pipeline(self.client, self.path, False)["success"])
        self.assertEqual(len(self.client.calls), 1)

    def test_forward_reference_is_rejected(self):
        self.plan([dict(action="component.list", payload=dict(id={"$ref": "future#/data/id"}))])
        with self.assertRaises(RevError):
            run_pipeline(self.client, self.path, False)

    def test_duplicate_step_id_is_rejected(self):
        self.plan([dict(id="same", action="editor.status"), dict(id="same", action="editor.status")])
        with self.assertRaises(RevError):
            run_pipeline(self.client, self.path, False)

    def test_assertion_failure_stops_work_and_runs_all_cleanup(self):
        self.plan([dict(action="editor.status", expect=[dict(path="/data/total", equals=2)]), dict(action="scene.list")],
                  [dict(action="editor.status"), dict(action="editor.status")])
        result = run_pipeline(self.client, self.path, False)
        self.assertFalse(result["success"])
        self.assertEqual(len(result["steps"]), 1)
        self.assertEqual(len(result["cleanup"]), 2)
        self.assertEqual(result["steps"][0]["response"]["error"], "AssertionFailed")

    def test_cleanup_failure_keeps_remaining_cleanup(self):
        self.client.responses = [dict(success=True), dict(success=False), dict(success=True)]
        self.plan([dict(action="editor.status")], [dict(action="editor.status"), dict(action="editor.status")])
        result = run_pipeline(self.client, self.path, False)
        self.assertFalse(result["success"])
        self.assertEqual(len(self.client.calls), 3)

    def test_keyboard_interrupt_runs_cleanup(self):
        self.plan([dict(action="editor.status")], [dict(action="scene.list")])
        original = self.client.call
        def invoke(action, *args):
            if action == "editor.status":
                raise KeyboardInterrupt()
            return original(action, *args)
        self.client.call = invoke
        result = run_pipeline(self.client, self.path, False)
        self.assertTrue(result["cancelled"])
        self.assertEqual(self.client.calls[0][0], "scene.list")

    def test_mutation_cannot_be_repeated_as_query(self):
        self.plan([dict(action="gameobject.create", payload=dict(name="x"), until=dict(path="/data/id", exists=True))])
        with self.assertRaises(RevError):
            run_pipeline(self.client, self.path, False)

    def test_query_repeats_until_actual_value_matches(self):
        self.client.responses = [dict(success=True, data=dict(total=0)), dict(success=True, data=dict(total=1))]
        self.plan([dict(action="gameobject.find", until=dict(path="/data/total", equals=1, intervalSeconds=.001))])
        result = run_pipeline(self.client, self.path, False)
        self.assertTrue(result["success"])
        self.assertEqual(result["steps"][0]["polls"], 2)
        self.assertEqual(self.client.timeout, 3)

    def test_poll_timeout_is_failure(self):
        self.client.responses = [dict(success=True, data=dict(total=0))] * 100
        self.plan([dict(action="gameobject.find", until=dict(path="/data/total", equals=1, timeoutSeconds=.01, intervalSeconds=.001))])
        result = run_pipeline(self.client, self.path, False)
        self.assertFalse(result["success"])
        self.assertEqual(result["steps"][0]["response"]["error"], "ConditionTimeout")

    def test_json_pointer_preserves_null_and_escaped_keys(self):
        self.assertIsNone(pointer({"a/b": {"~x": [None]}}, "/a~1b/~0x/0"))
        self.assertTrue(matches(dict(a=None), dict(path="/a", exists=True)))
        self.assertTrue(matches({}, dict(path="/a", exists=False)))
        self.assertFalse(matches({}, dict(path="/a", equals=None)))
        self.assertFalse(matches(dict(a=True), dict(path="/a", equals=1)))

    def test_nested_reference_preserves_array_type(self):
        self.assertEqual(resolve({"scenes": {"$ref": "before#/data/scenes"}}, {"before": dict(data=dict(scenes=[1]))}), dict(scenes=[1]))

    def test_condition_must_have_exactly_one_operator(self):
        for condition in (dict(path="/data"), dict(path="/data", exists=True, equals=1), dict(path="wrong", equals=1)):
            self.plan([dict(action="editor.status", expect=[condition])])
            with self.assertRaises(RevError):
                run_pipeline(self.client, self.path, False)

    def test_change_value_is_string_and_typo_is_rejected(self):
        for changes in ([dict(property="x", value=4)], [dict(property="x", typo="4")]):
            with self.assertRaises(RevError):
                validate_action("component.set", dict(changes=changes))

    def test_transform_vectors_accept_typed_local_and_world_coordinates(self):
        validate_action("transform.set", dict(id="instance:7", position=[1, 2.5, -3], eulerAngles=[0, 90, 0], localScale=[1, 0, -2]))
        validate_action("transform.set", dict(objectPath="Root/Child", space="world", position=[4, 5, 6]))
        validate_action("transform.get", dict(id="instance:7"))
        args = parser().parse_args(["transform", "set", "--id", "instance:7", "--set", "position=[1,2,3]"])
        self.assertEqual(payload_of(args), dict(id="instance:7", position=[1, 2, 3]))

    def test_transform_invalid_vectors_fail_before_pipeline_effects(self):
        for vector in ([1, 2], [1, 2, 3, 4], [1, True, 3], [1, "2", 3], [1, None, 3],
                       [1, float("nan"), 3], [1, float("inf"), 3], [1, 1e39, 3], [1, 10 ** 400, 3]):
            with self.subTest(vector=vector):
                with self.assertRaises(RevError):
                    validate_action("transform.set", dict(id="instance:7", position=vector))
        for payload in (dict(id="instance:7"), dict(id="instance:7", space="World", position=[0, 0, 0]),
                        dict(id="instance:7", scale=[1, 1, 1])):
            with self.assertRaises(RevError):
                validate_action("transform.set", payload)
        self.plan([dict(action="gameobject.create", payload=dict(name="before")),
                   dict(action="transform.set", payload=dict(id="instance:7", position=[0, 1]))])
        with self.assertRaises(RevError):
            run_pipeline(self.client, self.path, False)
        self.assertEqual(self.client.calls, [])

    def test_transform_reference_is_validated_again_after_resolution(self):
        self.client.responses = [dict(success=True, data=dict(position=[1, 2, 3]))]
        self.plan([dict(id="read", action="transform.get", payload=dict(id="instance:7")),
                   dict(action="transform.set", payload=dict(id="instance:7", position={"$ref": "read#/data/position"}))])
        self.assertTrue(run_pipeline(self.client, self.path, False)["success"])
        self.assertEqual(self.client.calls[1][1]["position"], [1, 2, 3])
        self.client.calls.clear()
        self.client.responses = [dict(success=True, data=dict(position=[True, 2, 3]))]
        self.assertFalse(run_pipeline(self.client, self.path, False)["success"])
        self.assertEqual(len(self.client.calls), 1)

    def test_transform_query_can_poll_but_mutation_cannot(self):
        for action in ("transform.set", "gameobject.primitive"):
            payload = dict(id="instance:7", position=[1, 2, 3]) if action == "transform.set" else dict(type="Cube", name="fixture")
            self.plan([dict(action=action, payload=payload, until=dict(path="/data/id", exists=True))])
            with self.assertRaises(RevError):
                run_pipeline(self.client, self.path, False)
        self.plan([dict(action="transform.get", payload=dict(id="instance:7"), until=dict(path="/data/id", exists=True))])
        self.assertTrue(run_pipeline(self.client, self.path, False)["success"])

    def test_primitive_names_are_explicit_and_require_name(self):
        for primitive in ("Cube", "Sphere", "Capsule", "Cylinder", "Plane", "Quad"):
            validate_action("gameobject.primitive", dict(type=primitive, name="fixture"))
        for payload in (dict(type="0", name="fixture"), dict(type="cube", name="fixture"), dict(type="Cube, Sphere", name="fixture"),
                        dict(type="Cube"), dict(type="Cube", name=" "), dict(name="fixture")):
            with self.assertRaises(RevError):
                validate_action("gameobject.primitive", payload)

    def test_material_contract_accepts_typed_values_and_asset_paths(self):
        validate_action("material.create", dict(path="Assets/Fixture.mat", shader="Standard"))
        validate_action("material.inspect", dict(path="Packages/com.example/Fixture.mat"))
        validate_action("material.clone", dict(path="Assets/Fixture.mat", destination="Assets/Clone.mat"))
        validate_action("material.set", dict(path="Assets/Fixture.mat", properties={"_Color": [1, .5, 0, 1], "_Glossiness": .75,
                        "_MainTex": "Assets/Fixture.png", "_OtherTex": None}, renderQueue=-1))
        validate_action("material.set", dict(path="Assets/Fixture.mat", shader="Standard", enableKeywords=["_NORMALMAP"]))
        validate_action("shader.list", dict(query="Standard", limit=500))

    def test_material_rejects_bad_paths_before_any_effect(self):
        for path in ("Assets/../Other.mat", "Assets\\Other.mat", "ProjectSettings/Other.mat", "Assets/Fixture.asset", "C:/Other.mat"):
            with self.assertRaises(RevError):
                validate_action("material.create", dict(path=path, shader="Standard"))
        for payload in (dict(path="Packages/com.example/Fixture.mat", properties={"_Glossiness": 1}),
                        dict(path="Assets/Fixture.mat", properties={"_MainTex": "../secret.png"})):
            with self.assertRaises(RevError):
                validate_action("material.set", payload)
        self.plan([dict(action="gameobject.create", payload=dict(name="before")),
                   dict(action="material.clone", payload=dict(path="Assets/Fixture.mat", destination="Other/Clone.mat"))])
        with self.assertRaises(RevError):
            run_pipeline(self.client, self.path, False)
        self.assertEqual(self.client.calls, [])

    def test_material_rejects_bad_values_keywords_and_render_queue(self):
        for value in (True, {"x": 1}, [1, 2, 3], [1, True, 3, 4], [1, None, 3, 4], float("inf"), float("nan"), 10 ** 400):
            with self.assertRaises(RevError):
                validate_action("material.set", dict(path="Assets/Fixture.mat", properties={"_Property": value}))
        for payload in (dict(properties=[]), dict(properties={}), dict(renderQueue=True), dict(renderQueue=-2), dict(renderQueue=5001),
                        dict(enableKeywords=[" "]), dict(enableKeywords=["X"], disableKeywords=["X"]), dict(shader="")):
            with self.assertRaises(RevError):
                validate_action("material.set", dict(path="Assets/Fixture.mat", **payload))
        for payload in (dict(shader="Standard"), dict(path="Assets/Fixture.mat")):
            with self.assertRaises(RevError):
                validate_action("material.create", payload)

    def test_material_references_are_checked_after_resolution(self):
        self.client.responses = [dict(success=True, data=dict(texture="Assets/Fixture.png"))]
        self.plan([dict(id="source", action="material.inspect", payload=dict(path="Assets/Fixture.mat")),
                   dict(action="material.set", payload=dict(path="Assets/Fixture.mat", properties={"_MainTex": {"$ref": "source#/data/texture"}}))])
        self.assertTrue(run_pipeline(self.client, self.path, False)["success"])
        self.assertEqual(self.client.calls[1][1]["properties"], {"_MainTex": "Assets/Fixture.png"})
        self.client.calls.clear()
        self.client.responses = [dict(success=True, data=dict(texture="../secret.png"))]
        self.assertFalse(run_pipeline(self.client, self.path, False)["success"])
        self.assertEqual(len(self.client.calls), 1)

    def test_shader_and_material_inspect_are_read_only_not_mutations(self):
        manifest = read_json(ROOT / "commands.json")
        specs = {entry["id"]: entry for entry in manifest["actions"]}
        for action in ("shader.list", "material.inspect"):
            self.assertTrue(specs[action]["readOnly"])
        for action in ("material.create", "material.set", "material.clone"):
            self.assertFalse(specs[action]["readOnly"])
        for payload in (dict(limit=501), dict(limit=0), dict(offset=-1)):
            with self.assertRaises(RevError):
                validate_action("shader.list", payload)

    def test_manifest_has_unique_ids_and_parser_supports_every_action(self):
        manifest = read_json(ROOT / "commands.json")
        ids = [a["id"] for a in manifest["actions"]]
        self.assertEqual(len(ids), len(set(ids)))
        cli = parser()
        for action in ids:
            family, operation = action.split(".", 1)
            argv = [family, operation]
            if action == "editor.menu": argv += ["Revolution.Tools/平台/包体大小预测"]
            if action == "gm.exec": argv += ["示例/查询"]
            if family == "scenario": argv += ["scenario.json"]
            self.assertEqual(cli.parse_args(argv).operation, operation)

    def test_parser_roundtrips_new_convenience_flags(self):
        args = parser().parse_args(["component", "get", "--id", "instance:12", "--property", "m_Enabled"])
        self.assertEqual(payload_of(args), dict(id="instance:12", property="m_Enabled"))

    def test_existing_pipeline_format_remains_valid(self):
        prepare(ROOT / "examples/editor-inspect.pipeline.json", validate_action, read_json(ROOT / "commands.json"))

    def test_relative_payload_file(self):
        (self.path.parent / "params.json").write_text('{"query":"t:Prefab"}', encoding="utf-8")
        self.plan([dict(action="asset.find", file="params.json")])
        self.assertTrue(run_pipeline(self.client, self.path, False)["success"])
        self.assertEqual(self.client.calls[0][1], dict(query="t:Prefab"))


if __name__ == "__main__":
    unittest.main()
