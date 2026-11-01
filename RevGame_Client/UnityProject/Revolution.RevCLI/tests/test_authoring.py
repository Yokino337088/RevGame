"""UI 面板与 Prefab 制作动作的 CLI 契约：预检在发出前拦截、清单与参数类型一致、只读/变更分类正确。"""
from pathlib import Path
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from revautomation import cli
from revautomation.cli import payload_of, validate_action
from revautomation.client import RevError, read_json

ROOT = Path(__file__).resolve().parents[1]
MANIFEST = read_json(ROOT / "commands.json")
SPECS = {entry["id"]: entry for entry in MANIFEST["actions"]}


class AuthoringManifestTests(unittest.TestCase):
    def test_actions_are_registered_with_correct_read_write_class(self):
        self.assertTrue(SPECS["rect.get"]["readOnly"])
        self.assertEqual(SPECS["rect.get"]["environment"], "either")
        for action in ("rect.set", "ui.create", "prefab.apply", "prefab.unpack"):
            self.assertIn(action, SPECS)
            self.assertFalse(SPECS[action].get("readOnly", False), action)
            self.assertEqual(SPECS[action]["environment"], "edit")

    def test_every_new_key_has_a_declared_type(self):
        types = MANIFEST["parameterTypes"]
        for action in ("rect.get", "rect.set", "ui.create", "prefab.apply", "prefab.unpack"):
            for key in SPECS[action]["keys"]:
                self.assertIn(key, types, f"{action}.{key} 缺少 parameterTypes，MCP 会把它当成 string")
        for key in ("anchorMin", "anchorMax", "pivot", "anchoredPosition", "sizeDelta", "offsetMin", "offsetMax"):
            self.assertEqual(types[key], "array")

    def test_action_ids_are_unique(self):
        ids = [entry["id"] for entry in MANIFEST["actions"]]
        self.assertEqual(len(ids), len(set(ids)))


class RectContractTests(unittest.TestCase):
    def test_rect_set_accepts_both_layout_styles_separately(self):
        validate_action("rect.set", dict(id="instance:1", anchorMin=[0, 0], anchorMax=[1, 1], offsetMin=[10, 10], offsetMax=[-10, -10]))
        validate_action("rect.set", dict(objectPath="Canvas/Panel", pivot=[0.5, 0.5], anchoredPosition=[0, 0], sizeDelta=[400, 300]))
        validate_action("rect.set", dict(id="instance:1", anchorMin=[0.5, 0.5]))

    def test_rect_set_requires_something_to_change(self):
        with self.assertRaises(RevError):
            validate_action("rect.set", dict(id="instance:1"))

    def test_offsets_and_position_size_are_mutually_exclusive(self):
        for conflict in (dict(offsetMin=[0, 0], sizeDelta=[1, 1]), dict(offsetMax=[0, 0], anchoredPosition=[1, 1])):
            with self.assertRaises(RevError, msg=conflict):
                validate_action("rect.set", dict(id="instance:1", **conflict))

    def test_rect_set_rejects_bad_vectors_before_any_effect(self):
        for bad in ([1], [1, 2, 3], [1, True], [1, "2"], [1, None], [float("nan"), 0], [float("inf"), 0], [1e7 + 1, 0], [-1e8, 0], [10 ** 400, 0], "1,2", {"x": 1}):
            with self.assertRaises(RevError, msg=bad):
                validate_action("rect.set", dict(id="instance:1", sizeDelta=bad))

    def test_rect_set_accepts_the_inclusive_limit(self):
        validate_action("rect.set", dict(id="instance:1", sizeDelta=[1e7, -1e7]))

    def test_unknown_rect_key_is_rejected(self):
        with self.assertRaises(RevError):
            validate_action("rect.set", dict(id="instance:1", sizeDelta=[1, 1], width=5))

    def test_pipeline_references_pass_through_validation(self):
        ref = {"$ref": "created#/data/id"}
        validate_action("rect.set", dict(id=ref, sizeDelta=[ref, 5]), allow_refs=True)
        validate_action("ui.create", dict(kind="Button", parent=ref, name=ref), allow_refs=True)


class UiCreateContractTests(unittest.TestCase):
    def test_every_kind_is_accepted_with_a_valid_parent(self):
        validate_action("ui.create", dict(kind="Canvas"))
        validate_action("ui.create", dict(kind="Canvas", name="MainCanvas", scene="Assets/Scenes/Main.unity"))
        for kind in ("Panel", "Image", "Text", "Button"):
            validate_action("ui.create", dict(kind=kind, parent="instance:5"))

    def test_unknown_kind_and_missing_parent_are_rejected(self):
        for payload in (dict(kind="Teapot", parent="instance:5"), dict(kind="canvas"), dict(kind=3), dict(), dict(kind="Button"), dict(kind="Panel", parent="")):
            with self.assertRaises(RevError, msg=payload):
                validate_action("ui.create", payload)

    def test_blank_name_is_rejected_but_omitted_name_is_fine(self):
        validate_action("ui.create", dict(kind="Canvas"))
        with self.assertRaises(RevError):
            validate_action("ui.create", dict(kind="Canvas", name="   "))


class PrefabContractTests(unittest.TestCase):
    def test_unpack_modes(self):
        validate_action("prefab.unpack", dict(id="instance:1"))
        validate_action("prefab.unpack", dict(id="instance:1", unpackMode="root"))
        validate_action("prefab.unpack", dict(id="instance:1", unpackMode="completely"))
        for bad in ("all", "Root", "", 1, None):
            with self.assertRaises(RevError, msg=bad):
                validate_action("prefab.unpack", dict(id="instance:1", unpackMode=bad))

    def test_apply_only_takes_object_identity(self):
        validate_action("prefab.apply", dict(id="instance:1"))
        validate_action("prefab.apply", dict(objectPath="Canvas/Panel", scene="Assets/Scenes/Main.unity"))
        with self.assertRaises(RevError):
            validate_action("prefab.apply", dict(id="instance:1", path="Assets/Other.prefab"))


class CommandLineSurfaceTests(unittest.TestCase):
    def parse(self, *argv):
        return cli.parser().parse_args(["--project", str(ROOT.parent / "RevolutionFrameWork_Unity"), *argv])

    def test_ui_create_flags_become_payload(self):
        args = self.parse("ui", "create", "--kind", "Button", "--name", "确定", "--parent", "instance:9")
        self.assertEqual(payload_of(args), dict(kind="Button", name="确定", parent="instance:9"))

    def test_rect_set_uses_json_set_for_vectors(self):
        args = self.parse("rect", "set", "--id", "instance:3", "--set", "sizeDelta=[200,80]", "--set", "pivot=[0.5,0.5]")
        self.assertEqual(payload_of(args), dict(id="instance:3", sizeDelta=[200, 80], pivot=[0.5, 0.5]))

    def test_prefab_apply_and_unpack_are_reachable(self):
        self.assertEqual(payload_of(self.parse("prefab", "apply", "--id", "instance:1")), dict(id="instance:1"))
        args = self.parse("prefab", "unpack", "--id", "instance:1", "--set", 'unpackMode="completely"')
        self.assertEqual(payload_of(args), dict(id="instance:1", unpackMode="completely"))


if __name__ == "__main__":
    unittest.main()
