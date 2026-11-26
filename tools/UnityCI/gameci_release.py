"""准备 GameCI 云端检出，并复用 RevGame 的正式包字节验收。"""

import argparse
import json
import os
import re
import shutil
from pathlib import Path

import release


# 隔离目录和 StreamingAssets 在同一检出盘，且位于 Unity 的 Assets 之外。
STASH = release.PROJECT.parent / ".revgame-gameci-streaming"
# GameCI 入口与项目资源构建脚本使用同一个程序集，沿用它对 Revolution.Editor 的显式引用。
ENTRY = release.PROJECT / "Assets/Editor/RevGame/ResourceBuild/RevGameGameCiRelease.cs"
# 本轮生成的密钥库由 GameCI 放在项目根目录。
KEYSTORE = release.PROJECT / "revgame-gameci-release.keystore"


def require_checkout():
    """限定路径改动发生在 Actions 传入的本轮工作区。"""
    workspace = os.environ.get("GITHUB_WORKSPACE")
    if os.environ.get("GITHUB_ACTIONS") != "true" or not workspace or Path(workspace).resolve() != release.REPO:
        raise ValueError("请在 RevGame 的 GitHub Actions 检出中执行。")


def streaming_root():
    """校验资源根和暂存位置均为本次工作区中的真实路径。"""
    streaming = release.PROJECT / "Assets/StreamingAssets"
    if streaming.resolve() != streaming or not streaming.is_dir() or release.is_link(streaming):
        raise ValueError("StreamingAssets 必须是本轮检出的真实目录。")
    if STASH.resolve() != STASH or STASH.parent != release.PROJECT.parent or (
        (STASH.exists() or STASH.is_symlink()) and (not STASH.is_dir() or release.is_link(STASH))
    ):
        raise ValueError("本次 StreamingAssets 暂存路径异常。")
    return streaming


def prepare(platform):
    """在 Unity 导入前隔离非目标平台，并安装本次固定版本和 Editor 入口。"""
    streaming = streaming_root()
    selected = streaming / platform
    if not selected.is_dir() or release.is_link(selected):
        raise ValueError(f"缺少目标 AB 目录 {platform}。")
    if ENTRY.exists() or ENTRY.is_symlink() or KEYSTORE.exists() or KEYSTORE.is_symlink():
        raise ValueError("GameCI 临时入口或密钥库路径已经存在。")
    STASH.mkdir(exist_ok=False)
    for item in sorted(streaming.iterdir()):
        folder = release.platform_entry(item.name)
        if folder in release.PLATFORM_FOLDERS and folder != platform.casefold():
            saved = STASH / item.name
            if release.is_link(item) or item.resolve().parent != streaming or saved.resolve().parent != STASH:
                raise ValueError(f"非目标 StreamingAssets 路径异常 {item.name}。")
            item.rename(saved)
    release.assert_selected_streaming(streaming, platform)
    version = release.PROJECT / "ProjectSettings/ProjectVersion.txt"
    version.write_text("m_EditorVersion: 2022.3.15f1\nm_EditorVersionWithRevision: 2022.3.15f1 (b58023a2b463)\n",
                       encoding="utf-8")
    shutil.copyfile(Path(__file__).with_name(ENTRY.name), ENTRY)


def restore():
    """归还其它平台资源和对应 .meta 文件，清除 GameCI 本轮写入的签名文件。"""
    streaming = streaming_root()
    if not STASH.is_dir():
        return
    KEYSTORE.unlink(missing_ok=True)
    ENTRY.unlink(missing_ok=True)
    ENTRY.with_suffix(".cs.meta").unlink(missing_ok=True)
    for item in sorted(STASH.iterdir()):
        destination = streaming / item.name
        if (release.platform_entry(item.name) not in release.PLATFORM_FOLDERS or release.is_link(item)
                or item.resolve().parent != STASH or destination.resolve().parent != streaming
                or destination.exists() or destination.is_symlink()):
            raise ValueError(f"无法安全恢复 {item.name}，原资源保留在 {STASH}。")
        item.rename(destination)
    STASH.rmdir()


def verify(target, build_target, platform):
    """使用真实 BuildReport、AB 摘要与 Player 文件验证本轮出包。"""
    streaming = streaming_root()
    release.assert_selected_streaming(streaming, platform)
    expected = release.bundle_inventory(streaming, build_target, platform)
    output = release.REPO / "build" / build_target
    report = json.loads((output / "rev-build.json").read_text(encoding="utf-8-sig"))
    if (report["result"] != "Succeeded" or report["errors"] != 0 or report["backend"] != "IL2CPP"
            or report["target"] != build_target or report["unityVersion"] != "2022.3.15f1"
            or report["development"] or report["automation"]):
        raise ValueError("Unity BuildReport 与本轮正式 IL2CPP 出包要求不匹配。")
    if target == "windows":
        release.check_windows(output, expected)
    else:
        apk = output / "RevGame.apk"
        release.check_android(apk, expected)
        sdk = os.environ.get("ANDROID_HOME") or os.environ.get("ANDROID_SDK_ROOT")
        tools = sorted((Path(sdk) / "build-tools").glob("*/apksigner"), reverse=True) if sdk else []
        if not tools:
            raise ValueError("GitHub runner 没有可用于验签的 Android apksigner。")
        signature = release.run_tool([str(tools[0]), "verify", "--print-certs", str(apk)],
                                     "Android APK 签名校验", output, 180)
        cert = re.search(r"certificate SHA-256 digest:\s*([0-9a-fA-F]{64})", signature)
        if not cert:
            raise ValueError("Android APK 缺少可核对的签名证书。")
        print(f"签名证书 SHA-256 {cert.group(1)}")
    print(f"{target} IL2CPP 验包通过，核对 {len(expected)} 个 {platform} AB 清单文件。")


def main():
    """从 Actions 运行 prepare、verify 或 restore，错误消息不含凭据。"""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("operation", choices=("prepare", "verify", "restore"))
    parser.add_argument("--target", choices=release.TARGETS, required=True)
    args = parser.parse_args()
    require_checkout()
    _, build_target, platform = release.TARGETS[args.target]
    if args.operation == "prepare":
        prepare(platform)
    elif args.operation == "verify":
        verify(args.target, build_target, platform)
    else:
        restore()


if __name__ == "__main__":
    main()
