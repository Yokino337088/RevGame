"""检查 Unity 项目的本次改动。工作区、暂存区和 CI 分别读取对应的 Git 快照。"""

import argparse
import json
import re
import subprocess
import sys
import tempfile
from dataclasses import dataclass
from difflib import SequenceMatcher
from pathlib import Path, PurePosixPath


POLICY = ".codex/rev-policy.json"
GUID = re.compile(rb"(?m)^guid:[ \t]*([0-9a-fA-F]{32})[ \t]*\r?$")
GUID_FIELD = re.compile(rb"(?m)^guid:")
SHA = re.compile(rb"(?m)^commit[ \t]*=[ \t]*([0-9a-fA-F]{40})[ \t]*\r?$")


@dataclass(frozen=True)
class Diagnostic:
    """仓库相对路径和从 1 起算的坐标。目录和缺文件问题可只定位到文件。"""

    path: str
    code: str
    message: str
    line: int | None = None
    column: int | None = None


class Snapshot:
    """只读 Git 视图，索引和提交检查始终忽略工作区的覆盖内容。"""

    def __init__(self, repo, staged, base):
        self.root = Path(self.git_at(repo, "rev-parse", "--show-toplevel").decode().strip())
        self.staged = staged
        self.base = self.git("merge-base", base, "HEAD").decode().strip() if base else "HEAD"
        self.diff_args = [self.base]
        if base:
            self.diff_args.append("HEAD")
        if staged:
            self.diff_args.insert(0, "--cached")
        self.tree = "HEAD" if base else None
        self.extra = set()
        if self.tree:
            files = self.git("ls-tree", "-r", "--name-only", "-z", self.tree)
        else:
            if self.git("ls-files", "--unmerged", "-z"):
                raise ValueError("请先解决 Git 冲突。")
            files = self.git("ls-files", "-z", "--cached")
            if not staged:
                self.extra = set(self.paths(self.git("ls-files", "-z", "--others", "--exclude-standard")))
                files = set(self.paths(files)) | self.extra
                files -= set(self.paths(self.git("ls-files", "-z", "--deleted")))
        self.files = files if isinstance(files, set) else set(self.paths(files))

    @staticmethod
    def git_at(repo, *args, missing_ok=False):
        """用字节流保留中文路径、文件原文和 Git 的失败原因。"""
        result = subprocess.run(["git", "-C", str(repo), *args], capture_output=True, timeout=30)
        if result.returncode and not (missing_ok and result.returncode == 1):
            raise ValueError(result.stderr.decode("utf-8", errors="replace").strip() or "Git 查询失败。")
        return result.stdout

    def git(self, *args, missing_ok=False):
        return self.git_at(self.root, *args, missing_ok=missing_ok)

    @staticmethod
    def paths(raw):
        return raw.decode("utf-8").split("\0")[:-1]

    def changes(self):
        """记录旧路径与新路径，使 Git 识别的移动可以比对两个 GUID。"""
        fields = self.paths(self.git("diff", "--name-status", "-z", "-M", *self.diff_args, "--"))
        index = 0
        while index < len(fields):
            status, path = fields[index:index + 2]
            index += 2
            if status.startswith("R"):
                yield path, fields[index]
                index += 1
            elif status.startswith("D"):
                yield path, None
            elif status.startswith("A"):
                yield None, path
            else:
                yield path, path
        for path in sorted(self.extra):
            yield None, path

    def read(self, path, *, old=False):
        """旧内容来自比较基线，新内容来自选定的工作区、索引或提交。"""
        if old:
            return self.git("show", f"{self.base}:{path}")
        if self.staged or self.tree:
            return self.git("show", f"{self.tree or ''}:{path}")
        return (self.root / path).read_bytes()

    def guid_at_base(self, path):
        """旧基线可能已经缺 meta；只把 Git 明确存在的路径交给读取器。"""
        if not self.git("ls-tree", "-z", self.base, "--", path):
            return None
        return identifier(self.read(path, old=True), GUID)

    def guid_paths(self, assets, guids):
        """只搜索新增或修改的 GUID，工作区模式包含新建且未忽略的 meta。"""
        options = ["--cached"] if self.staged else ([] if self.tree else ["--untracked", "--exclude-standard"])
        tree = [self.tree] if self.tree else []
        with tempfile.TemporaryDirectory(prefix="rev-guid-") as directory:
            patterns = Path(directory) / "guid.txt"
            patterns.write_text("".join(f"^guid:[[:blank:]]*{guid}[[:space:]]*$\n" for guid in sorted(guids)),
                                encoding="ascii", newline="\n")
            result = self.git("grep", "-I", "-ilz", "-E", *options, "-f", str(patterns), *tree,
                              "--", f":(glob){assets}/**/*.meta", missing_ok=True)
        return [path.removeprefix(f"{self.tree}:") if self.tree else path for path in self.paths(result)]


def identifier(data, pattern):
    """从 Unity meta 或包基线记录中读取规范化的标识。"""
    match = pattern.search(data)
    return match[1].decode("ascii").lower() if match else None


def field_line(data, pattern):
    """返回实际字段所在行。字段不存在时由调用方保留文件级定位。"""
    match = pattern.search(data)
    return data.count(b"\n", 0, match.start()) + 1 if match else None


def changed_guid_error(path, source, data):
    """资产和 meta 分别识别为移动时，共用同一条诊断以便去重。"""
    return Diagnostic(path, "REV102", f"保留原 .meta 的 GUID，来源为 {source}。", field_line(data, GUID))


def check_assets(snapshot, policy, assets, changes):
    """检查本次改动涉及的资源目录、配对、稳定 GUID 和重复 GUID。"""
    errors = set()
    changed_meta = {}
    meta_changes = {after: before for before, after in changes if after and after.endswith(".meta")}
    missing_directory_meta = {}
    affected_directories = set()
    prefixes = tuple(f"{assets}/{path}/" for path in policy.get("assetExcludes", []))
    folders = set()
    for path in snapshot.files:
        if path.startswith(assets + "/"):
            folders.update(str(folder) for folder in PurePosixPath(path).parents)

    def owned(path):
        return (path and path.startswith(assets + "/") and not path.startswith(prefixes)
                and not any(part.startswith(".") or part.endswith("~") for part in path[len(assets) + 1:].split("/")))

    for before, after in changes:
        for path in {before, after}:
            if not owned(path):
                continue
            asset = path.removesuffix(".meta")
            meta = asset + ".meta"
            if asset in snapshot.files or asset in folders:
                if meta not in snapshot.files:
                    if asset in folders:
                        affected_directories.add(asset)
                    else:
                        errors.add(Diagnostic(asset, "REV101", f"缺少对应的 .meta。请补交 {meta}。"))
            elif meta in snapshot.files:
                errors.add(Diagnostic(meta, "REV101", f"找不到对应的 Unity 资产或目录 {asset}。"))

        if not owned(after) or after not in snapshot.files:
            continue
        if after.endswith(".meta"):
            # 改 importer 或补 meta 也会触及这份资源的目录约定。
            asset = after.removesuffix(".meta")
            if asset in snapshot.files:
                errors.update(asset_location_errors(asset, assets, policy))
            data = snapshot.read(after)
            guid = identifier(data, GUID)
            if not guid:
                errors.add(Diagnostic(after, "REV102", ".meta 缺少有效 GUID。", field_line(data, GUID_FIELD)))
            else:
                changed_meta[after] = guid
                if before and before.endswith(".meta") and identifier(snapshot.read(before, old=True), GUID) != guid:
                    errors.add(changed_guid_error(after, before, data))
            continue

        # 同时检查新目录的配对，避免提交文件却漏暂存目录自己的 meta。
        for parent in PurePosixPath(after).parents:
            folder = str(parent)
            if folder == assets:
                break
            if folder + ".meta" not in snapshot.files:
                missing_directory_meta[folder] = min(after, missing_directory_meta.get(folder, after))
        errors.update(asset_location_errors(after, assets, policy))

        if before and before != after and owned(before):
            meta = after + ".meta"
            # meta 自身已比较过同一来源时可跳过。资产与 meta 配对不一致仍以资产来源为准。
            if meta in snapshot.files and meta_changes.get(meta) != before + ".meta":
                data = snapshot.read(meta)
                current = identifier(data, GUID)
                if current and snapshot.guid_at_base(before + ".meta") != current:
                    errors.add(changed_guid_error(meta, before + ".meta", data))

    for folder in affected_directories | missing_directory_meta.keys():
        # 仅删除目录 meta 时没有新增子资产，保留被删除的 meta 路径。
        path = missing_directory_meta.get(folder, folder + ".meta")
        errors.add(Diagnostic(path, "REV101", f"目录 {folder} 缺少对应的 .meta。请补交 {folder}.meta。"))

    if changed_meta:
        changed_guids = set(changed_meta.values())
        by_guid = {}
        for path in snapshot.guid_paths(assets, changed_guids):
            data = snapshot.read(path)
            guid = identifier(data, GUID)
            if guid in changed_guids:
                by_guid.setdefault(guid, {})[path] = field_line(data, GUID)
        # 把注解放在本次改动的 meta 上，消息中同时列出所有冲突对象。
        for path, guid in changed_meta.items():
            matches = by_guid.get(guid, {})
            if path in matches and len(matches) > 1:
                others = "、".join(sorted(other for other in matches if other != path))
                errors.add(Diagnostic(path, "REV103", f"GUID {guid} 也被以下 .meta 使用：{others}。", matches[path]))
    return errors


def asset_location_errors(path, assets, policy):
    """目录约定同样适用于资源内容和 importer 设置的改动。"""
    relative = path[len(assets) + 1:]
    extension = PurePosixPath(path).suffix.lower()
    rule = policy.get("assetLocations", {}).get(extension)
    if rule:
        root = rule["directory"]
        valid = relative.startswith(root + "/")
        if rule.get("moduleSubdirectory"):
            valid = valid and "/" in relative[len(root) + 1:]
        if not valid:
            yield Diagnostic(path, "REV100", f"资产应位于 {assets}/{root}/"
                             + ("模块子目录。" if rule.get("moduleSubdirectory") else "。"))
    for rule in policy.get("assetExclusions", []):
        if relative.startswith(rule["directory"] + "/") and extension in rule["extensions"]:
            yield Diagnostic(path, "REV100", rule["message"])


def check_vendor(snapshot, policy, assets, changes):
    """框架快照随上游包版本更新，项目自己的生成文件走项目构建链。"""
    vendor = policy.get("frameworkSnapshot")
    if not vendor:
        return set()
    prefix = f"{assets}/{vendor['directory']}/"
    anchor = prefix + vendor["anchor"]
    allowed = {anchor, *(prefix + path for path in vendor.get("generated", []))}
    changed = {path for pair in changes for path in pair if path}
    protected = sorted(path for path in changed if path.startswith(prefix) and path not in allowed)
    if not protected:
        return set()
    if anchor in changed and anchor in snapshot.files:
        current = identifier(snapshot.read(anchor), SHA)
        previous = identifier(snapshot.read(anchor, old=True), SHA) if (anchor, anchor) in changes else None
        if current and current != previous:
            return set()
    return {Diagnostic(path, "REV110", f"框架快照更新需要在 {anchor} 记录新的上游包提交。")
            for path in protected}


def check_code(snapshot, policy, assets, changes):
    """仅送入变更的业务 C#，由 Roslyn 识别语法并与原有诊断比较。"""
    code = policy.get("code")
    if not code:
        return []
    roots = tuple(f"{assets}/{path}/" for path in code["directories"])
    files = []
    for before, after in sorted(set(changes), key=lambda pair: (pair[1] or "", pair[0] or "")):
        if not after or not after.startswith(roots) or not after.endswith(".cs") or after not in snapshot.files:
            continue
        relative = after[len(assets) + 1:]
        if "Generated" in PurePosixPath(relative).parts or relative.endswith(".Generated.cs"):
            continue
        old = snapshot.read(before, old=True).decode("utf-8-sig") if before else ""
        new = snapshot.read(after).decode("utf-8-sig")
        if old == new and before == after:
            continue
        # 优先匹配保留的旧行，避免同类新增违规抢占旧位置。换行口径与 Roslyn 一致。
        old_lines = re.split(r"\r\n|[\n\r\x85\u2028\u2029]", old)
        new_lines = re.split(r"\r\n|[\n\r\x85\u2028\u2029]", new)
        unchanged = [{"BeforeStart": block.a + 1, "AfterStart": block.b + 1, "Count": block.size}
                     for block in SequenceMatcher(None, old_lines, new_lines, autojunk=False).get_matching_blocks() if block.size]
        files.append({"Path": after, "RelativePath": relative, "Before": old, "After": new, "Unchanged": unchanged})
    if not files:
        return []

    directory = Path(__file__).resolve().parent / "CSharp"
    output = directory / "bin/Release/net8.0/RevPolicy.dll"
    build = subprocess.run(["dotnet", "build", "RevPolicy.csproj", "-c", "Release", "--nologo", "-v:q"],
                           cwd=directory, capture_output=True, timeout=90)
    if build.returncode:
        sys.stderr.buffer.write(build.stdout + build.stderr)
        raise ValueError("C# 检查器构建失败，请检查 .NET 8 SDK。")
    request = {"Files": files, "Bootstraps": code.get("bootstraps", []),
               "RuntimeDirectories": code.get("runtimeDirectories", []),
               "PanelDirectories": code.get("panelDirectories", []), "InternalTypes": code.get("internalTypes", [])}
    result = subprocess.run(["dotnet", str(output)], cwd=directory, input=json.dumps(request).encode(),
                            stdout=subprocess.PIPE, check=True, timeout=60)
    return [Diagnostic(item["Path"], item["Code"], item["Message"], item["Line"], item["Column"])
            for item in json.loads(result.stdout)]


def single_line(value):
    """日志每条诊断占一行，文件名或消息中的换行以可读的转义保留。"""
    return value.replace("\r", r"\r").replace("\n", r"\n")


def github_escape(value):
    """按 GitHub Actions 命令格式转义百分号和换行。"""
    return value.replace("%", "%25").replace("\r", "%0D").replace("\n", "%0A")


def print_diagnostic(issue, github):
    """保留全量日志，另发 GitHub 原生注解。文件级规则省略行列属性。"""
    location = single_line(issue.path)
    properties = {"file": issue.path, "title": issue.code}
    if issue.line is not None:
        location += f":{issue.line}"
        properties["line"] = str(issue.line)
    if issue.column is not None:
        location += f":{issue.column}"
        properties["col"] = str(issue.column)
    if github:
        # 常规日志带固定前缀，文件名不会被 runner 当成另一条 workflow 命令。
        print(f"RevPolicy: {location}: {issue.code} {single_line(issue.message)}")
        # 属性值额外转义逗号和冒号，中文路径保持 UTF-8。
        fields = ",".join(f"{key}={github_escape(value).replace(',', '%2C').replace(':', '%3A')}"
                          for key, value in properties.items())
        print(f"::error {fields}::{github_escape(issue.message)}")
    else:
        print(f"{location}: {issue.code} {single_line(issue.message)}")


def main():
    """统一给终端、Git hook 与 CI 返回可定位的诊断和退出码。"""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repo", default=Path.cwd(), type=Path, help="项目仓库或其子目录。")
    parser.add_argument("--format", choices=("text", "github"), default="text",
                        help="github 额外输出 GitHub Actions 文件和行注解。")
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument("--staged", action="store_true", help="只验证准备提交的索引。")
    mode.add_argument("--base", help="比较指定引用与 HEAD 的共同祖先至 HEAD。")
    args = parser.parse_args()
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8")
        sys.stderr.reconfigure(encoding="utf-8")
    try:
        snapshot = Snapshot(args.repo, args.staged, args.base)
        if POLICY not in snapshot.files:
            raise ValueError(f"此 Git 视图缺少 {POLICY}。")
        policy = json.loads(snapshot.read(POLICY).decode("utf-8-sig"))
        if policy["version"] != 1:
            raise ValueError("规范配置版本不受支持。")
        project = PurePosixPath(policy["unityProject"])
        if project.is_absolute() or ".." in project.parts or "\\" in str(project) or ":" in str(project):
            raise ValueError("unityProject 应是仓库内的相对路径。")
        assets = (project / "Assets").as_posix()
        changes = list(snapshot.changes())
        errors = check_assets(snapshot, policy, assets, changes) | check_vendor(snapshot, policy, assets, changes)
        errors.update(check_code(snapshot, policy, assets, changes))
    except (OSError, KeyError, TypeError, ValueError, subprocess.SubprocessError) as exc:
        print(f"无法完成规范检查：{exc}", file=sys.stderr)
        return 2
    for issue in sorted(errors, key=lambda item: (item.path, item.line or 0, item.column or 0, item.code, item.message)):
        print_diagnostic(issue, args.format == "github")
    print(f"Revolution 规范检查：{len(changes)} 个变更，{len(errors)} 个问题。")
    return int(bool(errors))


if __name__ == "__main__":
    raise SystemExit(main())
