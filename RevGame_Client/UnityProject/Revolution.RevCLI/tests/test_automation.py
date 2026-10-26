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
