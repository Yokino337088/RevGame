"""检查 RevGame 的脚本边界、原生插件、包依赖与分平台 AB。"""

import hashlib
import json
import re
from pathlib import PurePosixPath


# 业务脚本和 Unity 程序集配置统一遵循同一目录约定。
SCRIPTS = {".cs", ".asmdef", ".asmref"}
# 源码、图形着色器和 Shader Graph 共用 GameShader。
SHADERS = {".shader", ".compute", ".shadergraph", ".shadersubgraph", ".hlsl", ".cginc", ".glslinc", ".raytrace"}
# Unity 可加载的原生或托管库与插件容器。
LIBRARIES = {".dll", ".so", ".dylib", ".a", ".lib", ".bundle", ".framework", ".aar", ".jar", ".jslib", ".jspre", ".wasm", ".winmd"}
# 现有决斗内核及其浏览器桥，仅可写到项目实际使用的导入路径。
NATIVE_PATHS = {
    "Plugins/RevGameCore/x86_64/RevGameCore.dll",
    "Plugins/RevGameCore/WebGL/libRevGameCore.a",
    "Plugins/RevGameCore/WebGL/RevGameBrowser.jslib",
    "Plugins/Android/libs/arm64-v8a/libRevGameCore.so",
    "Plugins/Android/libs/armeabi-v7a/libRevGameCore.so",
}
# 对应的库名也受约束，移动到其他平台目录时，即使名字已入库也要报错。
NATIVE_NAMES = {PurePosixPath(path).name.casefold() for path in NATIVE_PATHS}
# 框架的发布目录名和清单 BuildTarget 必须成对。
AB_PLATFORMS = {"PC": "StandaloneWindows64", "Android": "Android", "WebGL": "WebGL"}
# 验收使用构建工具写出的原始 SHA-256，而非 Git 对象摘要。
DIGEST = re.compile(r"[a-f0-9]{64}")


def read_object(snapshot, path, *, old=False):
    """从指定 Git 快照读取 Unity 的 JSON 对象。"""
    value = json.loads(snapshot.read(path, old=old).decode("utf-8-sig"))
    if not isinstance(value, dict):
        raise ValueError(f"{path} 应为 JSON 对象。")
    return value


def source_identity(version):
    """区分注册表版本与同一 Git 仓库的不同修订，保留本地与网络来源身份。"""
    if re.fullmatch(r"\d+\.\d+\.\d+(?:[-+][\w.+-]+)?", version):
        return "registry"
    return version.split("#", 1)[0]


def package_findings(snapshot, root, changed, baseline):
    """按 main 的依赖名称及来源比对直接依赖、锁定的传递依赖和扩展注册表。"""
    for name in ("manifest.json", "packages-lock.json"):
        path = root + name
        if path not in changed or path not in snapshot.files:
            continue
        try:
            old = read_object(snapshot, path, old=True) if path in baseline else {}
            new = read_object(snapshot, path)
            seen = old.get("dependencies", {})
            for key, entry in new.get("dependencies", {}).items():
                if key not in seen:
                    yield path, "REV124", f"main 尚无依赖 {key}。"
                    continue
                before = seen[key]
                if name == "manifest.json":
                    same_source = source_identity(before) == source_identity(entry)
                else:
                    same_source = (before.get("source"), before.get("url", ""), source_identity(before["version"])) == (
                        entry.get("source"), entry.get("url", ""), source_identity(entry["version"]))
                if not same_source:
                    yield path, "REV124", f"依赖 {key} 的来源与 main 不同。"
            for registry in new.get("scopedRegistries", []):
                if registry not in old.get("scopedRegistries", []):
                    yield path, "REV124", "PR 不得新增或替换包注册表。"
        except (ValueError, KeyError, TypeError, AttributeError):
            yield path, "REV124", "无法识别 Unity 依赖声明，请检查 JSON 字段与类型。"


def bundle_expectations(manifest, platform):
    """从目标平台清单取得白名单和摘要，拒绝重复包名及跨目录路径。"""
    if (manifest["platform"] != AB_PLATFORMS[platform] or manifest["integrityVersion"] != 1
            or not isinstance(manifest["bundles"], list) or not manifest["bundles"]):
        raise ValueError("构建清单的平台或完整性版本不正确。")
    files = {platform: manifest["mainSha256"], platform + ".manifest": manifest["mainManifestSha256"]}
    names = {item.casefold() for item in files} | {"buildmanifest.json"}
    for info in manifest["bundles"]:
        name = info["name"]
        if re.fullmatch(r"[a-zA-Z0-9_-][a-zA-Z0-9_.-]*", name) is None or name.casefold().endswith((".meta", ".manifest")):
            raise ValueError("构建清单含有无效包名。")
        if name.casefold() in names:
            raise ValueError("构建清单含有重复包名。")
        names.add(name.casefold())
        files[name] = info["sha256"]
        files[name + ".manifest"] = info["manifestSha256"]
    if any(DIGEST.fullmatch(value) is None for value in files.values()):
        raise ValueError("构建清单缺少有效的 SHA-256。")
    return files


def streaming_findings(snapshot, root, changed):
    """只检查本次触及的平台，但核对该目录中的完整清单、真实内容及配对 .meta。"""
    platforms = set()
    for path in changed:
        if not path.startswith(root):
            continue
        relative = path.removeprefix(root)
        platform = relative.split("/", 1)[0].removesuffix(".meta")
        if "/" not in relative and relative not in {name + ".meta" for name in AB_PLATFORMS}:
            if path in snapshot.files:
                yield path, "REV123", "StreamingAssets 根只允许目标平台目录的配对 .meta。"
        elif platform in AB_PLATFORMS:
            platforms.add(platform)
        elif path in snapshot.files:
            yield path, "REV123", "StreamingAssets 只允许 PC、Android、WebGL 的 AB 目录及配套文件。"
    for platform in sorted(platforms):
        prefix = root + platform + "/"
        actual = {path.removeprefix(prefix) for path in snapshot.files if path.startswith(prefix)}
        folder_meta = root + platform + ".meta"
        if not actual and folder_meta not in snapshot.files:
            continue
        manifest_path = prefix + "BuildManifest.json"
        if manifest_path not in snapshot.files:
            yield manifest_path, "REV123", "平台目录缺少 BuildManifest.json。"
            continue
        try:
            hashes = bundle_expectations(read_object(snapshot, manifest_path), platform)
        except (ValueError, TypeError, KeyError, AttributeError) as exc:
            yield manifest_path, "REV123", f"平台 AB 清单无效。{exc}"
            continue
        expected = set(hashes) | {"BuildManifest.json"}
        expected.update(name + ".meta" for name in tuple(expected))
        for name in sorted(actual - expected):
            yield prefix + name, "REV123", "平台目录里包含不属于当前构建清单的文件。"
        for name in sorted(expected - actual):
            yield prefix + name, "REV123", "平台 AB 缺少清单文件或配对的 .meta。"
        if folder_meta not in snapshot.files:
            yield folder_meta, "REV123", "平台 AB 目录缺少配对的 .meta。"
        for name, digest in sorted(hashes.items()):
            if name not in actual:
                continue
            data = snapshot.read(prefix + name)
            if (hashlib.sha256(data).hexdigest() != digest or
                    (not name.endswith(".manifest") and not data.startswith(b"UnityFS\0"))):
                yield prefix + name, "REV123", "AB 内容与当前平台构建清单不符，请重新发布目标平台资源。"


def inspect_layout(snapshot, project, changes):
    """按目标分支已存在的插件和项目目录检查提交中的变更，输出文件级诊断。"""
    assets = f"{project}/Assets/"
    packages = f"{project}/Packages/"
    baseline = set(snapshot.paths(snapshot.git("ls-tree", "-r", "--name-only", "-z", snapshot.base,
                                               "--", assets, packages)))
    changed = {path for pair in changes for path in pair if path}
    package_names = {path.removeprefix(packages).split("/", 1)[0] for path in baseline
                     if path.startswith(packages) and "/" in path.removeprefix(packages)}
    for path in sorted(changed & snapshot.files):
        if path.startswith(packages):
            relative = path.removeprefix(packages)
            parts = relative.split("/")
            if len(parts) > 1 and parts[0] not in package_names:
                yield path, "REV124", f"main 尚无嵌入式包 {parts[0]}。"
            elif len(parts) == 2 and parts[1] == "package.json" and path in baseline:
                if read_object(snapshot, path).get("name") != read_object(snapshot, path, old=True).get("name"):
                    yield path, "REV124", "嵌入式包名称与 main 不同。"
            if PurePosixPath(relative.removesuffix(".meta")).suffix.lower() in LIBRARIES and path not in baseline:
                yield path, "REV124", "main 中没有这个第三方插件文件。"
            continue
        if not path.startswith(assets):
            continue
        relative = path.removeprefix(assets).removesuffix(".meta")
        parts = relative.split("/")
        extension = PurePosixPath(relative).suffix.lower()
        # 当前框架快照由 REV110 核对上游锚点，其他已入库插件只允许维护现有文件。
        snapshot_file = parts[0] == "Revolution"
        vendor_file = parts[0] in {"Revolution.Gal", "Plugins"} and path in baseline
        if extension in SCRIPTS and not snapshot_file and not vendor_file:
            placed = parts[0] == "Editor" or (parts[0] == "GameScripts" and "editor" not in map(str.lower, parts[1:-1]))
            if not placed:
                yield path, "REV120", "业务编辑器脚本放 Assets/Editor，运行时脚本放 Assets/GameScripts。"
            if extension == ".asmdef" and placed and assets + relative in snapshot.files:
                try:
                    definition = read_object(snapshot, assets + relative)
                    includes = definition.get("includePlatforms", [])
                    if not definition.get("name", "").startswith("RevGame."):
                        yield path, "REV124", "业务程序集使用 RevGame. 名称，禁止藏入新的第三方程序集。"
                    if (parts[0] == "Editor" and includes != ["Editor"]) or (parts[0] == "GameScripts" and includes == ["Editor"]):
                        yield path, "REV120", "仅 Editor 的程序集应放在 Assets/Editor 并显式限制为 Editor。"
                except (ValueError, TypeError, AttributeError):
                    yield path, "REV120", "程序集定义 JSON 无效。"
        if extension in SHADERS and parts[0] != "GameShader" and not snapshot_file and not vendor_file:
            yield path, "REV121", "业务 Shader、Shader Graph、Compute 与 include 放 Assets/GameShader。"
        if extension in LIBRARIES:
            own = PurePosixPath(relative).name.casefold() in NATIVE_NAMES
            if (own and relative not in NATIVE_PATHS) or (not own and path not in baseline):
                yield path, "REV122", "原生库必须采用项目现有平台路径，新增第三方库须先纳入 main 的基线。"
        if (parts[0] in {"Plugins", "Revolution.Gal"} and path not in baseline and relative not in NATIVE_PATHS
                or (extension == ".unitypackage" or parts[-1] == "package.json") and path not in baseline):
            yield path, "REV124", "禁止把 main 中没有的第三方插件文件或导入包带进 PR。"
    yield from package_findings(snapshot, packages, changed, baseline)
    yield from streaming_findings(snapshot, assets + "StreamingAssets/", changed)
