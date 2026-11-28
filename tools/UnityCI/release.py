"""在专用检出中构建并验收 RevGame 的 Windows 与 Android IL2CPP 正式包。"""

import argparse
import base64
from contextlib import contextmanager
from datetime import datetime, timedelta, timezone
import gzip
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import re
import shutil
import stat
import subprocess
import sys
import tempfile
import uuid
from zipfile import ZipFile


# 使用同一项目已有的入口与构建目标名。第三列是框架用于 StreamingAssets 的平台名。
TARGETS = {"windows": ("Win64", "StandaloneWindows64", "PC"), "android": ("Android", "Android", "Android")}
# 当前框架和网页发布流程使用的平台目录，大小写统一后匹配。
PLATFORM_FOLDERS = {"pc", "android", "webgl", "ios", "linux", "mac", "osx"}
# runner 必须拿到适用于固定 c1 Editor 的无人值守构建授权。
UNITY_SECRETS = ("REVGAME_UNITY_EMAIL", "REVGAME_UNITY_PASSWORD", "REVGAME_UNITY_SERIAL")
# 正式 APK 使用同一份已有发布 keystore，Unity 和 Gradle 不读取这些值。
ANDROID_SECRETS = ("REVGAME_ANDROID_KEYSTORE_B64", "REVGAME_ANDROID_STORE_PASS",
                   "REVGAME_ANDROID_KEY_ALIAS", "REVGAME_ANDROID_KEY_PASS")
# Unity 生成的临时 Gradle 工程路径与项目原有 ExportAndroidReleaseBatch 入口一致。
GRADLE_EXPORT = Path("Builds/AndroidRelease/Gradle")
# 从本脚本位置解析仓库，所有 CLI 调用均使用对应检出。
REPO = Path(__file__).resolve().parents[2]
PROJECT = REPO / "RevGame_Client/UnityProject/RevGame.Unity"


def platform_entry(name):
    """按目录名或对应的 Unity .meta 识别已知平台文件。"""
    return name.casefold().removesuffix(".meta")


def is_link(path):
    """同时识别 Windows junction 和普通符号链接，拒绝移动指向工程外的目录。"""
    return bool(getattr(path.lstat(), "st_file_attributes", 0) & 0x400) or path.is_symlink()


@contextmanager
def isolate_streaming(project, platform):
    """构建前将非目标平台目录及原 .meta 移到同盘的工程外，结束时逐项归还。"""
    assets = project / "Assets"
    streaming = assets / "StreamingAssets"
    if streaming.resolve() != streaming or not streaming.is_dir() or is_link(streaming):
        raise ValueError("StreamingAssets 必须是当前检出的真实目录")
    selected = streaming / platform
    if not selected.is_dir() or is_link(selected):
        raise ValueError(f"缺少目标平台资源目录 {platform}")

    # 隔离目录在工程外且不会跨盘。出包失败也必须恢复，恢复冲突时保留备份供检查。
    stash = Path(tempfile.mkdtemp(prefix="revgame-ab-", dir=project.parent))
    moved = []
    try:
        for item in sorted(streaming.iterdir()):
            folder = platform_entry(item.name)
            if folder in PLATFORM_FOLDERS and folder != platform.casefold():
                if is_link(item) or item.resolve().parent != streaming:
                    raise ValueError(f"非目标资源路径异常 {item.name}")
                staged = stash / item.name
                item.rename(staged)
                moved.append((staged, item))
        assert_selected_streaming(streaming, platform)
        yield streaming
    finally:
        for saved, original in reversed(moved):
            if original.exists() or original.is_symlink():
                raise RuntimeError(f"恢复 {original.name} 时发现同名路径，原资源保留在 {stash}")
            saved.rename(original)
        stash.rmdir()


def assert_selected_streaming(streaming, platform):
    """Unity 出包前再次核对目录和真实 AB 头，同时允许公共 StreamingAssets 文件。"""
    for item in streaming.iterdir():
        folder = platform_entry(item.name)
        if folder in PLATFORM_FOLDERS and folder != platform.casefold():
            raise ValueError(f"发现非目标平台资源 {item.name}")
        if item.name.casefold() == platform.casefold():
            continue
        if is_link(item):
            raise ValueError(f"StreamingAssets 路径是链接 {item.name}")
        for asset in item.rglob("*") if item.is_dir() else (item,):
            if is_link(asset):
                raise ValueError(f"StreamingAssets 路径是链接 {asset.name}")
            if asset.is_file() and not asset.name.endswith(".meta"):
                with asset.open("rb") as stream:
                    if stream.read(8).rstrip(b"\0") in (b"UnityFS", b"UnityRaw", b"UnityWeb"):
                        raise ValueError(f"目标平台目录之外存在 AB {asset.relative_to(streaming)}")


def sha256(stream):
    """分块读取清单和资源，避免验包把完整 AB 装入内存。"""
    return hashlib.file_digest(stream, "sha256").hexdigest()


def file_hash(path):
    """取文件原始字节摘要，避免文本换行变换影响交付验收。"""
    with path.open("rb") as stream:
        return sha256(stream)


def bundle_inventory(streaming, target, platform):
    """按本次构建清单逐一核对源 AB，记录之后要在 Player 内验收的字节。"""
    folder = streaming / platform
    manifest_path = folder / "BuildManifest.json"
    data = json.loads(manifest_path.read_text(encoding="utf-8-sig"))
    if data.get("platform") != target or data.get("integrityVersion") != 1 or not data.get("bundles"):
        raise ValueError(f"{platform} 构建清单身份或完整性版本无效")

    expected = {"BuildManifest.json": file_hash(manifest_path), platform: data["mainSha256"],
                platform + ".manifest": data["mainManifestSha256"]}
    for row in data["bundles"]:
        name = row["name"]
        parts = PurePosixPath(name).parts
        if not parts or name != PurePosixPath(name).as_posix() or any(part in ("/", "..") for part in parts) or (
            "\\" in name or ":" in name or name in expected or name + ".manifest" in expected
        ):
            raise ValueError("AB 清单包含异常的资源名")
        expected[name] = row["sha256"]
        expected[name + ".manifest"] = row["manifestSha256"]

    actual = {path.relative_to(folder).as_posix(): path for path in folder.rglob("*")
              if path.is_file() and not path.name.endswith(".meta")}
    if actual.keys() != expected.keys():
        raise ValueError(f"{platform} 实际文件集合与构建清单不同")
    for name, digest in expected.items():
        file = actual[name]
        if not file.resolve().is_relative_to(folder) or is_link(file) or file_hash(file) != digest:
            raise ValueError(f"{platform} 资源文件校验失败 {name}")
    return expected


def check_packaged_paths(names, prefix, platform, expected):
    """核对已打包内容的目标目录，拒绝额外版本和其它平台的 AB。"""
    shipped = {}
    target_prefix = f"{prefix}{platform}/"
    for name in names:
        if not name.startswith(prefix):
            continue
        relative = name[len(prefix):]
        folder = relative.split("/", 1)[0]
        if platform_entry(folder) in PLATFORM_FOLDERS and folder != platform:
            raise ValueError(f"Player 混入非目标平台资源 {relative}")
        if name.startswith(target_prefix):
            key = name[len(target_prefix):]
            if key in shipped:
                raise ValueError(f"Player 存在重复资源 {relative}")
            shipped[key] = name
    if shipped.keys() != expected.keys():
        raise ValueError(f"Player 中的 {platform} 资源与本轮构建清单不同")
    return shipped


def check_windows(output, expected):
    """从实际 Windows 文件目录验证 IL2CPP、原生决斗库与目标平台的全部 AB。"""
    data = output / "RevGame_Data"
    required = (output / "RevGame.exe", output / "GameAssembly.dll", output / "UnityPlayer.dll",
                data / "Plugins/x86_64/RevGameCore.dll")
    if not all(path.is_file() and path.stat().st_size > 0 for path in required):
        raise ValueError("Windows Player 缺少可执行文件、IL2CPP 或决斗原生库")
    streaming = data / "StreamingAssets"
    names = [path.relative_to(streaming).as_posix() for path in streaming.rglob("*") if path.is_file()]
    for key, name in check_packaged_paths(names, "", "PC", expected).items():
        if file_hash(streaming / name) != expected[key]:
            raise ValueError(f"Windows Player 内资源摘要不一致 {key}")


def check_webgl(output, expected, product_name):
    """检查网页入口实际引用的文件、Wasm 二进制、包类型和本轮 WebGL AB。"""
    index = (output / "index.html").read_text(encoding="utf-8-sig")
    actual_product = re.search(r'\bproductName:\s*("(?:[^"\\]|\\.)*")', index)
    if not actual_product or json.loads(actual_product.group(1)) != product_name:
        raise ValueError("WebGL 入口的实际产品名与测试包或正式包配置不符")
    if 'const buildUrl = "Build/";' not in index or 'const streamingAssetsUrl = "StreamingAssets";' not in index:
        raise ValueError("WebGL 入口必须指向包内的 Build 与 StreamingAssets")

    # Unity 生成的加载器固定为 JS，运行库、场景数据和 Wasm 可用原始字节、Brotli 或 Gzip。
    references = {"loader.src": r"\.loader\.js", "dataUrl": r"\.data(?:\.(?:br|gz))?",
                  "frameworkUrl": r"\.framework\.js(?:\.(?:br|gz))?", "codeUrl": r"\.wasm(?:\.(?:br|gz))?"}
    paths = {}
    for field, suffix in references.items():
        reference = re.search(rf'\b{re.escape(field)}\s*[:=]\s*buildUrl\s*\+\s*"([\w-]+{suffix})"', index)
        if not reference:
            raise ValueError(f"WebGL 入口缺少有效的构建文件引用 {field}")
        path = output / "Build" / reference.group(1)
        if not path.is_file() or is_link(path) or path.stat().st_size == 0:
            raise ValueError(f"WebGL Player 缺少有效的构建文件 {path.name}")
        paths[field] = path

    wasm = paths["codeUrl"]
    if wasm.suffix == ".br":
        # 使用 Runner 已安装的 Node 原生 Brotli 解码。先验可解码，再认实际 Wasm 格式。
        script = ("const fs = require('node:fs'); const zlib = require('node:zlib'); "
                  "const wasm = zlib.brotliDecompressSync(fs.readFileSync(process.argv[1])); "
                  "if (wasm.subarray(0, 8).toString('hex') !== '0061736d01000000') process.exitCode = 1;")
        run_tool(["node", "-e", script, str(wasm.resolve())], "WebGL Brotli Wasm 校验", output, 120)
    else:
        read_wasm = gzip.open if wasm.suffix == ".gz" else Path.open
        with read_wasm(wasm, "rb") as stream:
            if stream.read(8) != b"\0asm\x01\0\0\0":
                raise ValueError("WebGL Player 的代码文件缺少有效的 Wasm 文件头")

    streaming = output / "StreamingAssets"
    names = [path.relative_to(streaming).as_posix() for path in streaming.rglob("*") if path.is_file()]
    for key, name in check_packaged_paths(names, "", "WebGL", expected).items():
        path = streaming / name
        if is_link(path) or file_hash(path) != expected[key]:
            raise ValueError(f"WebGL Player 内资源摘要不一致 {key}")


def check_android(apk, expected):
    """读取 APK 的实际 ZIP 成员，核对 ARM64 IL2CPP 与 Android AB。"""
    with ZipFile(apk) as package:
        names = [info.filename for info in package.infolist() if not info.is_dir()]
        native = {name.split("/")[1] for name in names if name.startswith("lib/") and name.endswith(".so")}
        libs = {f"lib/arm64-v8a/{name}" for name in ("libil2cpp.so", "libunity.so", "libRevGameCore.so")}
        if native != {"arm64-v8a"} or not libs.issubset(names):
            raise ValueError("APK 必须包含 ARM64 IL2CPP、Unity 和决斗原生库")
        for key, name in check_packaged_paths(names, "assets/", "Android", expected).items():
            with package.open(name) as stream:
                if sha256(stream) != expected[key]:
                    raise ValueError(f"APK 内资源摘要不一致 {key}")


def run_tool(command, label, cwd, timeout, *, env=None, quiet=False):
    """等待本轮工具退出。带凭据的命令仅返回状态和自定义说明。"""
    with tempfile.TemporaryFile() as output:
        try:
            result = subprocess.run(command, cwd=cwd, timeout=timeout, env=env, stdout=output, stderr=subprocess.STDOUT)
        except subprocess.TimeoutExpired:
            raise RuntimeError(f"{label}超时") from None
        output.seek(0)
        if quiet:
            if result.returncode:
                raise RuntimeError(f"{label}失败，退出码 {result.returncode}")
            return ""
        if result.returncode:
            output.seek(max(0, output.seek(0, os.SEEK_END) - 20000), os.SEEK_SET)
            print(output.read().decode("utf-8", errors="replace"), file=sys.stderr)
            raise RuntimeError(f"{label}失败，退出码 {result.returncode}")
        return output.read().decode("utf-8", errors="replace")


def unity_run(editor, build_flag, method, log, *, request=None, env=None, timeout=7200):
    """调用 Unity 批处理，使用独立日志并等实际退出，不接受缺少日志的回执。"""
    command = [str(editor), "-batchmode", "-nographics", "-buildTarget", build_flag, "-projectPath", str(PROJECT),
               "-executeMethod", method, "-logFile", str(log)]
    command += ["-revAutomationRequest", str(request)] if request else ["-quit"]
    result = subprocess.run(command, cwd=PROJECT, env=env, timeout=timeout, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    evidence = log.read_text(encoding="utf-8-sig", errors="replace") if log.is_file() else ""
    if result.returncode != 0 or not evidence:
        print("\n".join(evidence.splitlines()[-65:]), file=sys.stderr)
        raise RuntimeError(f"Unity 批处理 {method} 未完成，退出码 {result.returncode}")
    return evidence


def batch_action(editor, flag, action, payload, scratch, *, env=None):
    """通过 Revolution 已注册的文件桥执行一次动作，领取回执后回收本次工单。"""
    sys.path.insert(0, str(PROJECT.parent / "Revolution.RevCLI"))
    from revautomation.client import atomic_json, clean_job_files, read_json_settled

    ident = uuid.uuid4().hex
    job = PROJECT / "Library/RevolutionAutomation/jobs" / ident
    request = scratch / "request.json"
    response = job / "response.json"
    log = scratch / "unity.log"
    request.unlink(missing_ok=True)
    log.unlink(missing_ok=True)
    atomic_json(request, dict(id=ident, protocol=1, action=action, confirm=False, sessionId="",
                              deadlineUtc=(datetime.now(timezone.utc) + timedelta(hours=2)).isoformat(),
                              timeoutSeconds=7200, payloadJson=json.dumps(payload, ensure_ascii=False)))
    try:
        unity_run(editor, flag, "Revolution.Editor.RevAutomationBatch.Execute", log, request=request, env=env)
        result = read_json_settled(response)
        if result.get("id") != ident or result.get("protocol") != 1 or result.get("success") is not True:
            raise RuntimeError(f"{action} 缺少匹配的成功回执")
    finally:
        error = clean_job_files(job, ("response.json", "response.json.tmp", "progress.json", "progress.json.tmp"))
        if error:
            raise RuntimeError(f"本次工单回收失败 {error}")


def missing_secrets(target, env):
    """按所选平台报告尚未配置的变量名，避免打印凭据内容。"""
    names = UNITY_SECRETS + (ANDROID_SECRETS if target == "android" else ())
    return [name for name in names if not env.get(name, "").strip()]


def license_command(editor, activate):
    """激活或归还已授权的 CI 席位，口令只传给 Unity 许可进程。"""
    label = "Unity 无人值守许可激活" if activate else "Unity 构建许可归还"
    with tempfile.TemporaryDirectory(prefix="revgame-license-") as folder:
        command = [str(editor), "-batchmode", "-nographics", "-quit", "-logFile", str(Path(folder) / "license.log")]
        command += ["-serial", os.environ[UNITY_SECRETS[2]]] if activate else ["-returnlicense"]
        command += ["-username", os.environ[UNITY_SECRETS[0]], "-password", os.environ[UNITY_SECRETS[1]]]
        run_tool(command, label, Path(folder), 240, quiet=True)
    print(label + "命令已完成。")


def android_build(editor, output, expected, scratch, build_env):
    """复用原有 ARM64 Gradle 导出入口，再以正式 keystore 签名并校验产物。"""
    module = editor.parent / "Data/PlaybackEngines/AndroidPlayer"
    sdk = module / "SDK"
    java = module / "OpenJDK/bin/java.exe"
    gradle = module / "Tools/gradle/lib/gradle-launcher-7.2.jar"
    tools = sdk / "build-tools/34.0.0"
    export = PROJECT / GRADLE_EXPORT
    if export.exists():
        raise ValueError("Gradle 导出目录已有文件，请使用专用干净检出")
    android_env = dict(build_env, JAVA_HOME=str(java.parent.parent), ANDROID_HOME=str(sdk), ANDROID_SDK_ROOT=str(sdk))
    try:
        log = unity_run(editor, "Android", "RevGame.Editor.GamePvpBuild.ExportAndroidReleaseBatch", scratch / "export.log", env=android_env)
        if "Android release export Succeeded, errors=0" not in log:
            raise RuntimeError("Unity 未返回成功的 Android BuildReport")
        run_tool([str(java), "-classpath", str(gradle), "org.gradle.launcher.GradleMain", "--no-daemon",
                  ":launcher:assembleRelease"], "Gradle Release 出包", export, 3600, env=android_env)
        unsigned = list((export / "launcher/build/outputs/apk/release").glob("*.apk"))
        if len(unsigned) != 1:
            raise RuntimeError("Gradle 未产出唯一的 Release APK")
        aligned = scratch / "aligned.apk"
        run_tool([str(tools / "zipalign.exe"), "-f", "-p", "4", str(unsigned[0]), str(aligned)],
                 "APK 对齐", scratch, 120, env=android_env)
    finally:
        # 只清理由本入口新建且位于检出内的 Gradle 工程，保留同级无关目录。
        if export.exists():
            if export.resolve() != export or is_link(export):
                raise RuntimeError("Gradle 导出目录异常，保留现场")
            shutil.rmtree(export)

    key = scratch / "release.keystore"
    key.write_bytes(base64.b64decode(os.environ[ANDROID_SECRETS[0]].strip(), validate=True))
    destination = output / "RevGame.apk"
    signer = [str(java), "-jar", str(tools / "lib/apksigner.jar")]
    try:
        signing_env = dict(android_env, **{name: os.environ[name] for name in ANDROID_SECRETS[1:]})
        run_tool(signer + ["sign", "--ks", str(key), "--ks-key-alias", os.environ[ANDROID_SECRETS[2]],
                           "--ks-pass", f"env:{ANDROID_SECRETS[1]}", "--key-pass", f"env:{ANDROID_SECRETS[3]}",
                           "--out", str(destination), str(aligned)], "正式 APK 签名", scratch, 180, env=signing_env, quiet=True)
    finally:
        key.unlink(missing_ok=True)
    signature = run_tool(signer + ["verify", "--print-certs", str(destination)], "APK 签名校验", scratch, 180, env=android_env)
    cert = re.search(r"certificate SHA-256 digest:\s*([0-9a-fA-F]{64})", signature)
    if not cert:
        raise RuntimeError("APK 没有可核对的签名证书摘要")
    print(f"签名证书 SHA-256 {cert.group(1)}")
    badging = run_tool([str(tools / "aapt.exe"), "dump", "badging", str(destination)], "APK 包名与版本校验", scratch, 120, env=android_env)
    package = next((line for line in badging.splitlines() if line.startswith("package: ")), "")
    if not package or "application-debuggable" in badging:
        raise RuntimeError("APK 缺少包名版本或启用了调试")
    print(package)
    check_android(destination, expected)


def release(target, editor, output):
    """在本轮检出隔离并核对已有 AB，使用构建前记录的摘要验收 Player。"""
    if not editor.is_file() or not PROJECT.is_dir():
        raise ValueError("Unity Editor 或 RevGame 工程不存在")
    if output.exists() and any(output.iterdir()):
        raise ValueError("产物目录需要为空")
    if output.is_relative_to(PROJECT / "Assets") or output == REPO or output == PROJECT:
        raise ValueError("产物目录必须位于源资产之外")
    if target == "android":
        missing = [name for name in ANDROID_SECRETS if not os.environ.get(name, "").strip()]
        if missing:
            raise ValueError("缺少正式 Android 签名配置 " + ", ".join(missing))
    flag, build_target, platform = TARGETS[target]
    output.mkdir(parents=True, exist_ok=True)
    # 云端使用隔离检出。开发机执行预检或诊断时也不能打断正在使用同一工程的编辑器。
    sys.path.insert(0, str(PROJECT.parent / "Revolution.RevCLI"))
    from revautomation.client import stale_lock

    if stale_lock(PROJECT) is False:
        raise ValueError("Unity 已打开此工程，请为正式出包使用独立检出")
    build_env = {key: value for key, value in os.environ.items() if key not in UNITY_SECRETS + ANDROID_SECRETS}
    with tempfile.TemporaryDirectory(prefix="revgame-release-") as folder, isolate_streaming(PROJECT, platform) as streaming:
        scratch = Path(folder)
        assert_selected_streaming(streaming, platform)
        expected = bundle_inventory(streaming, build_target, platform)
        print(f"本轮直接使用检出中的 {platform} AB，已核对 {len(expected)} 个清单文件。")
        if target == "windows":
            batch_action(editor, flag, "build.player",
                         dict(target=build_target, path=str(output / "RevGame.exe"), backend="il2cpp",
                              development=False, automation=False, frameTiming=False), scratch, env=build_env)
            check_windows(output, expected)
        else:
            android_build(editor, output, expected, scratch, build_env)
    print(f"{target} IL2CPP Player 验包通过。")


def main():
    """提供云端预检、授权和出包入口，命令失败只输出可执行的诊断。"""
    sys.stdout.reconfigure(encoding="utf-8")
    sys.stderr.reconfigure(encoding="utf-8")
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("operation", choices=("assets", "credentials", "activate", "return-license", "build"))
    parser.add_argument("--target", choices=TARGETS)
    parser.add_argument("--editor", type=Path)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    if args.operation in ("activate", "return-license", "build") and args.editor is None:
        parser.error("需要 --editor")
    if args.operation in ("assets", "credentials", "build") and args.target is None:
        parser.error("需要 --target")
    if args.operation == "build" and args.output is None:
        parser.error("需要 --output")
    try:
        if args.operation == "assets":
            _, build_target, platform = TARGETS[args.target]
            with isolate_streaming(PROJECT, platform) as streaming:
                expected = bundle_inventory(streaming, build_target, platform)
                print(f"{platform} 的 StreamingAssets 通过隔离与 {len(expected)} 个清单文件校验。")
        elif args.operation == "credentials":
            missing = missing_secrets(args.target, os.environ)
            if missing:
                raise ValueError("配置 GitHub Actions Secrets 后再出包，缺少 " + ", ".join(missing))
        elif args.operation in ("activate", "return-license"):
            license_command(args.editor.resolve(), args.operation == "activate")
        else:
            release(args.target, args.editor.resolve(), args.output.resolve())
    except (OSError, ValueError, RuntimeError, KeyError, subprocess.TimeoutExpired) as failure:
        print(str(failure), file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
