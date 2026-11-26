"""覆盖正式出包最容易漏掉的资源隔离、恢复与产物验包。"""

from io import BytesIO
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
from zipfile import ZipFile

import gameci_release
import release


class ReleaseTests(unittest.TestCase):
    """在临时工程模拟同一个检出里存在三平台 AB 和各自的 GUID 元文件。"""

    def setUp(self):
        """每个测试各自持有临时工程，避免读写打开的 RevGame。"""
        self.context = tempfile.TemporaryDirectory()
        self.addCleanup(self.context.cleanup)
        self.project = Path(self.context.name).resolve() / "project"
        self.streaming = self.project / "Assets/StreamingAssets"
        for platform in ("PC", "Android", "WebGL"):
            folder = self.streaming / platform
            folder.mkdir(parents=True)
            (folder / platform).write_bytes(b"UnityFS\0" + platform.encode())
            folder.with_suffix(".meta").write_text("guid: " + platform, encoding="utf-8")
        (self.streaming / "common.txt").write_text("shared", encoding="utf-8")

    def test_releases_only_selected_platform_and_restores_on_failure(self):
        """目标 AB 和公共文件仍在，异常退出后原目录和 .meta 原样回位。"""
        for selected in ("PC", "Android"):
            with self.subTest(selected=selected):
                before = sorted(path.name for path in self.streaming.iterdir())
                with self.assertRaisesRegex(RuntimeError, "构建失败"):
                    with release.isolate_streaming(self.project, selected) as streaming:
                        self.assertEqual({path.name for path in streaming.iterdir()}, {selected, selected + ".meta", "common.txt"})
                        raise RuntimeError("构建失败")
                self.assertEqual(sorted(path.name for path in self.streaming.iterdir()), before)
                self.assertFalse(list(self.project.parent.glob("revgame-ab-*")))

    def test_rejects_foreign_ab_in_unexpected_directory(self):
        """即使目录名不在平台表中，也会用 Unity AB 文件头找出意外混入。"""
        folder = self.streaming / "Unknown"
        folder.mkdir()
        (folder / "bundle").write_bytes(b"UnityFS\0...")
        with self.assertRaisesRegex(ValueError, "目录之外存在 AB"):
            with release.isolate_streaming(self.project, "PC"):
                pass
        self.assertTrue((self.streaming / "Android/Android").is_file())

    def write_pc_bundle_manifest(self):
        """构造一个能通过预检的 PC 主包和资源包，供坏文件与构建期替换用例复用。"""
        folder = self.streaming / "PC"
        for name in ("PC.manifest", "ui", "ui.manifest"):
            (folder / name).write_bytes(name.encode())
        manifest = {
            "platform": "StandaloneWindows64",
            "integrityVersion": 1,
            "mainSha256": release.file_hash(folder / "PC"),
            "mainManifestSha256": release.file_hash(folder / "PC.manifest"),
            "bundles": [{"name": "ui", "sha256": release.file_hash(folder / "ui"),
                         "manifestSha256": release.file_hash(folder / "ui.manifest")}],
        }
        (folder / "BuildManifest.json").write_text(json.dumps(manifest), encoding="utf-8")
        return folder, manifest

    def test_checks_manifest_identity_and_bundle_hashes(self):
        """清单描述 Windows 也须逐个验实际文件，一处内容变动就拒绝出包。"""
        folder, _ = self.write_pc_bundle_manifest()
        expected = release.bundle_inventory(self.streaming, "StandaloneWindows64", "PC")
        self.assertEqual(len(expected), 5)
        (folder / "ui").write_text("changed", encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "资源文件校验失败"):
            release.bundle_inventory(self.streaming, "StandaloneWindows64", "PC")

    def test_gameci_rejects_replaced_bundle_even_with_a_matching_manifest(self):
        """即使换入的 AB 与其新清单相符，也须用出包前记录的那批资源；失败后归还其它平台。"""
        folder, manifest = self.write_pc_bundle_manifest()
        entry = self.project / "Assets/Editor/RevGameGameCiRelease.cs"
        entry.parent.mkdir(parents=True)
        (self.project / "ProjectSettings").mkdir()
        stash = self.project.parent / ".revgame-gameci-streaming"
        key = self.project / "revgame-gameci-release.keystore"
        with patch.object(release, "PROJECT", self.project), patch.multiple(gameci_release, STASH=stash, ENTRY=entry, KEYSTORE=key):
            gameci_release.prepare("StandaloneWindows64", "PC")
            try:
                self.assertFalse((self.streaming / "Android").exists())
                (folder / "ui").write_bytes(b"fresh valid replacement")
                manifest["bundles"][0]["sha256"] = release.file_hash(folder / "ui")
                (folder / "BuildManifest.json").write_text(json.dumps(manifest), encoding="utf-8")
                self.assertEqual(len(release.bundle_inventory(self.streaming, "StandaloneWindows64", "PC")), 5)
                with self.assertRaisesRegex(ValueError, "目标 AB 在 Player 构建期间发生变化"):
                    gameci_release.verify("windows", "StandaloneWindows64", "PC")
            finally:
                gameci_release.restore()
        self.assertTrue((self.streaming / "Android/Android").is_file())
        self.assertFalse(stash.exists())

    def test_android_package_checks_abi_and_cross_platform_files(self):
        """读取真实 ZIP 名单验证 Android AB 和 ARM64 IL2CPP，拒绝额外 PC 目录。"""
        apk = self.project / "release.apk"
        ab_bytes = b"sample"
        expected = {"Android": release.sha256(BytesIO(ab_bytes))}
        with ZipFile(apk, "w") as package:
            for library in ("libil2cpp.so", "libunity.so", "libRevGameCore.so"):
                package.writestr("lib/arm64-v8a/" + library, b"so")
            package.writestr("assets/Android/Android", ab_bytes)
        release.check_android(apk, expected)
        with ZipFile(apk, "a") as package:
            package.writestr("assets/PC/PC", b"foreign")
        with self.assertRaisesRegex(ValueError, "混入非目标平台资源"):
            release.check_android(apk, expected)

    def test_windows_checks_il2cpp_and_actual_pc_files(self):
        """完整 Windows 包的二进制与资源都需要存在，串平台目录会被拦截。"""
        player = self.project / "player"
        for name in ("RevGame.exe", "GameAssembly.dll", "UnityPlayer.dll", "RevGame_Data/Plugins/x86_64/RevGameCore.dll",
                     "RevGame_Data/StreamingAssets/PC/PC"):
            path = player / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(b"verified")
        expected = {"PC": release.sha256(BytesIO(b"verified"))}
        release.check_windows(player, expected)
        extra = player / "RevGame_Data/StreamingAssets/WebGL/WebGL"
        extra.parent.mkdir()
        extra.write_bytes(b"foreign")
        with self.assertRaisesRegex(ValueError, "混入非目标平台资源"):
            release.check_windows(player, expected)

    def test_only_android_requires_signing_configuration(self):
        """Windows 构建门禁只检查 Unity 授权，Android 另外要求正式签名。"""
        licensing = {key: "fixture" for key in release.UNITY_SECRETS}
        self.assertEqual(release.missing_secrets("windows", licensing), [])
        self.assertEqual(release.missing_secrets("android", licensing), list(release.ANDROID_SECRETS))


if __name__ == "__main__":
    unittest.main()
