"""复测 Unity 目录门禁与依赖基线的典型正反例。"""
import hashlib
import json
import unittest

from layout import inspect_layout


# 测试用短路径模拟从 Git 获得的 Assets 和 Packages 树。
PROJECT = "Game"
# Unity 资产根在该项目之下。
ASSETS = PROJECT + "/Assets/"
# Unity 包根在该项目之下。
PACKAGES = PROJECT + "/Packages/"


class FileSnapshot:
    """给布局规则提供当前树和 main 旧树，测试不操作真实资产。"""

    def __init__(self, old, current):
        self.old = old
        self.current = current
        self.files = set(current)
        self.base = "main"

    def git(self, *args):
        """模拟 ls-tree 的零分隔路径响应。"""
        return b"".join(path.encode() + b"\0" for path in self.old)

    @staticmethod
    def paths(raw):
        """切分模拟 Git 的路径响应。"""
        return raw.decode().split("\0")[:-1]

    def read(self, path, *, old=False):
        """按二进制返回对应树中实际文件。"""
        return (self.old if old else self.current)[path]


def report(old, current):
    """对比两份快照，只将内容或路径发生变化的条目送检。"""
    changes = [(path if path in old else None, path if path in current else None)
               for path in old.keys() | current.keys() if old.get(path) != current.get(path)]
    return {(path, code) for path, code, _ in inspect_layout(FileSnapshot(old, current), PROJECT, changes)}


def bundles(platform):
    """创建一份符合构建清单约定的最小示例，含AB、文本清单和全部配对元数据。"""
    prefix = ASSETS + "StreamingAssets/" + platform
    values = {platform: b"UnityFS\0main", platform + ".manifest": b"main",
              "ui": b"UnityFS\0ui", "ui.manifest": b"ui"}
    hashes = {name: hashlib.sha256(data).hexdigest() for name, data in values.items()}
    manifest = {"platform": "Android" if platform == "Android" else "StandaloneWindows64", "integrityVersion": 1,
                "mainSha256": hashes[platform], "mainManifestSha256": hashes[platform + ".manifest"],
                "bundles": [{"name": "ui", "sha256": hashes["ui"], "manifestSha256": hashes["ui.manifest"]}]}
    values["BuildManifest.json"] = json.dumps(manifest).encode()
    return ({prefix + ".meta": b"folder"} | {prefix + "/" + name: data for name, data in values.items()}
            | {prefix + "/" + name + ".meta": b"asset" for name in values})


class LayoutTests(unittest.TestCase):
    """保持已有合法布局通过，避免 PR 引入目录和供应链违规。"""

    def test_script_shader_and_library_paths(self):
        """允许业务正位脚本与目标库，拒绝错目录及新增第三方二进制。"""
        files = {
            ASSETS + "Editor/Tool.cs": b"script",
            ASSETS + "GameScripts/Runtime.cs": b"script",
            ASSETS + "GameScripts/Tools/Editor/Wrong.cs": b"script",
            ASSETS + "GameRes/Shaders/Wrong.compute": b"shader",
            ASSETS + "GameShader/A.hlsl": b"shader",
            ASSETS + "Plugins/RevGameCore/x86_64/RevGameCore.dll": b"dll",
            ASSETS + "Plugins/RevGameCore/arm64/RevGameCore.dll": b"dll",
            ASSETS + "GameScripts/Other.dll": b"dll",
        }
        findings = report({}, files)
        self.assertEqual({path for path, _ in findings}, {
            ASSETS + "GameScripts/Tools/Editor/Wrong.cs", ASSETS + "GameRes/Shaders/Wrong.compute",
            ASSETS + "Plugins/RevGameCore/arm64/RevGameCore.dll", ASSETS + "GameScripts/Other.dll"})
        self.assertTrue({(ASSETS + "GameScripts/Tools/Editor/Wrong.cs", "REV120"),
                         (ASSETS + "GameRes/Shaders/Wrong.compute", "REV121"),
                         (ASSETS + "Plugins/RevGameCore/arm64/RevGameCore.dll", "REV122")}.issubset(findings))

    def test_editor_assembly_cannot_appear_in_runtime(self):
        """即使 asmdef 目录名称普通，显式的 Editor 目标也归入 Editor。"""
        path = ASSETS + "GameScripts/Tools/Tool.asmdef"
        self.assertIn((path, "REV120"), report({}, {path: b'{"name":"RevGame.Tool","includePlatforms":["Editor"]}'}))

    def test_dependencies_compare_ids_and_sources(self):
        """保留同仓 Git 更新，检查同名换源和新增传递依赖。"""
        manifest, lock = (PACKAGES + part for part in ("manifest.json", "packages-lock.json"))
        old = {manifest: b'{"dependencies":{"com.base":"https://example.com/base.git#v1"}}',
               lock: b'{"dependencies":{"com.base":{"source":"git","version":"https://example.com/base.git#v1"}}}'}
        self.assertEqual(report(old, {**old, manifest: old[manifest].replace(b"#v1", b"#v2")}), set())
        changed = {manifest: old[manifest].replace(b"/base.git", b"/other.git"),
                   lock: b'{"dependencies":{"com.new":{"source":"registry","version":"1.0.0"}}}'}
        self.assertEqual(report(old, changed), {(manifest, "REV124"), (lock, "REV124")})

    def test_existing_plugin_and_new_nested_file(self):
        """已入库的二进制可维护，已有目录中夹带的新插件仍被拒绝。"""
        approved = ASSETS + "Plugins/Demigiant/DOTween.dll"
        extra = ASSETS + "Plugins/Demigiant/Other/Extra.cs"
        embedded = PACKAGES + "com.new/package.json"
        issues = report({approved: b"old"}, {approved: b"updated", extra: b"script", embedded: b"{}"})
        self.assertNotIn(approved, {path for path, _ in issues})
        self.assertTrue({(extra, "REV124"), (embedded, "REV124")}.issubset(issues))

    def test_streaming_checks_paths_platform_manifest_and_digest(self):
        """夹带裸文件、跨端清单或修改包内容都会报错。"""
        pc = bundles("PC")
        self.assertEqual(report({}, pc), set())
        path = ASSETS + "StreamingAssets/PC/ui"
        root_file = ASSETS + "StreamingAssets/PC"
        manifest = ASSETS + "StreamingAssets/PC/BuildManifest.json"
        altered = {**pc, path: b"UnityFS\0wrong", root_file: b"misc"}
        self.assertEqual(report(pc, altered), {(path, "REV123"), (root_file, "REV123")})
        self.assertEqual(report(pc, {**pc, manifest: pc[manifest].replace(b"StandaloneWindows64", b"Android")}),
                         {(manifest, "REV123")})


if __name__ == "__main__":
    unittest.main()
