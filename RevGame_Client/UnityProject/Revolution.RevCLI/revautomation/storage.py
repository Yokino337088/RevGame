"""自动化报告有界保留：只回收明确命名的完整报告组，保留 .keep、活动和重解析点。"""
import json
from pathlib import Path
import re
import shutil
import stat
import time

DEFAULT = dict(automatic=True, jobDays=7, failureDays=14, jobMaxMiB=256,
               screenshotDays=7, screenshotMaxMiB=512, reportDays=30, reportMaxMiB=256)


def policy(root):
    path = Path(root) / "storage-policy.json"
    value = dict(DEFAULT)
    if path.is_file():
        value.update(json.loads(path.read_text(encoding="utf-8-sig")))
    for key in DEFAULT:
        if key == "automatic":
            if type(value[key]) is not bool:
                raise ValueError("automatic 必须是 bool")
        elif type(value[key]) is not int or not 1 <= value[key] <= (102400 if key.endswith("MiB") else 3650):
            raise ValueError("保留设置范围无效：" + key)
    return value


def linked(path):
    info = path.lstat()
    return stat.S_ISLNK(info.st_mode) or bool(getattr(info, "st_file_attributes", 0) & 0x400)


def inventory(path):
    if linked(path):
        return 0, True
    if path.is_file():
        return path.stat().st_size, path.with_name(path.name + ".keep").exists()
    total, protect = 0, (path / ".keep").exists()
    for child in path.iterdir():
        size, keep = inventory(child)
        total += size
        protect |= keep
    return total, protect


def prune_reports(directory, settings, dry_run=True):
    directory = Path(directory).absolute()
    if not directory.is_dir() or linked(directory):
        return dict(dryRun=dry_run, beforeBytes=0, afterBytes=0, removed=0, candidates=0, protectedCount=0)
    items, errors = [], []
    now = time.time()
    for path in directory.iterdir():
        if not re.fullmatch(r"\d{8}-\d{6}-[0-9a-f]{8}(?:\.json)?", path.name):
            continue
        try:
            size, protect = inventory(path)
            if path.is_dir():
                protect |= not (path / ".complete").is_file()
            items.append((path, size, path.stat().st_mtime, protect))
        except OSError as exc:
            errors.append(str(exc))
    before = total = sum(item[1] for item in items)
    budget = settings["reportMaxMiB"] * 1024 * 1024
    removed = candidates = protected = 0
    for path, size, modified, protect in sorted(items, key=lambda item: item[2]):
        if protect or modified > now - 900:
            protected += 1
            continue
        if modified >= now - settings["reportDays"] * 86400 and total <= budget:
            continue
        candidates += 1
        if dry_run:
            total -= size
            continue
        try:
            _, keep = inventory(path)
            if keep or path.parent != directory or linked(path):
                continue
            if path.is_dir():
                shutil.rmtree(path)
            else:
                path.unlink()
            total -= size
            removed += 1
        except OSError as exc:
            errors.append(str(exc))
    return dict(dryRun=dry_run, beforeBytes=before, afterBytes=total, removed=removed, candidates=candidates,
                protectedCount=protected, overBudget=total > budget, errors=errors[:20])
