#!/usr/bin/env python3
"""只读已完成RevGame game.perf JSON；拒绝旧/取消/残缺结果，不把观测差异当优化收益。"""
import argparse
from datetime import datetime
import json
import math
from pathlib import Path
import sys


class InvalidResult(ValueError):
    """契约不满足时禁止把该文件列作有效完成采样。"""


def integer(value, field, minimum=0):
    if type(value) is not int or value < minimum:
        raise InvalidResult(f"{field}需要整数且至少{minimum}")
    return value


def number(value, field, minimum=0):
    if type(value) not in (int, float) or not math.isfinite(value) or value < minimum:
        raise InvalidResult(f"{field}需要有限数且至少{minimum}")
    return value


def ratio(hits, misses):
    """未记录/无调用返回null，不把无查询报100%命中。"""
    if hits is None or misses is None or hits + misses == 0:
        return None
    return hits / (hits + misses)


def read_result(path):
    raw = json.loads(Path(path).read_text(encoding="utf-8-sig"))
    if not isinstance(raw, dict):
        raise InvalidResult("根节点需要对象")
    for field in ("runId", "cpu", "gpu", "graphicsApi", "unityVersion", "startedUtc", "endedUtc"):
        if not isinstance(raw.get(field), str) or not raw[field].strip():
            raise InvalidResult(f"缺少{field}身份/环境/完成时间")
    if raw.get("status") != "completed" or raw.get("cleaned") is not True or raw.get("cancelled") is not False:
        raise InvalidResult("需要completed、cleaned=true、cancelled=false")
    if integer(raw.get("errors"), "errors") != 0 or raw.get("error") not in (None, ""):
        raise InvalidResult("结果存在错误")
    if type(raw.get("ab")) is not bool:
        raise InvalidResult("ab需要真实布尔模式值")
    try:
        start = datetime.fromisoformat(raw["startedUtc"].replace("Z", "+00:00"))
        end = datetime.fromisoformat(raw["endedUtc"].replace("Z", "+00:00"))
        if start.tzinfo is None or end.tzinfo is None or end <= start:
            raise InvalidResult("起止时间需要带时区且结束晚于开始")
    except (ValueError, TypeError) as error:
        raise InvalidResult("起止时间格式/先后错误") from error
    for key in ("returnLogic", "returnPieceRefs"):
        # 旧版结果没有这两个计数，不能伪填；若提供则必须符合该夹具战斗退出归零契约。
        if key in raw and integer(raw[key], key) != 0:
            raise InvalidResult(f"{key}战斗退出仍有引用/逻辑")
    first, last = integer(raw.get("first"), "first", 1), integer(raw.get("last"), "last", 1)
    repeats = integer(raw.get("repeats"), "repeats", 1)
    seconds = number(raw.get("requestedSeconds"), "requestedSeconds", 10)
    if not (1 <= first <= last <= 6 and repeats <= 10 and seconds <= 600):
        raise InvalidResult("超出RevGame夹具章1～6、次数1～10、秒10～600契约")
    rows = raw.get("chapters")
    if not isinstance(rows, list) or len(rows) != (last - first + 1) * repeats:
        raise InvalidResult("完整段数不匹配请求")
    expected = [(chapter, repeat) for repeat in range(repeats) for chapter in range(first, last + 1)]
    for index, (row, identity) in enumerate(zip(rows, expected)):
        if not isinstance(row, dict) or (row.get("chapter"), row.get("repeat")) != identity:
            raise InvalidResult(f"第{index}段章/轮次顺序或唯一性不符")
        integer(row.get("chapter"), "chapter", 1); integer(row.get("repeat"), "repeat")
        duration = number(row.get("seconds"), "seconds", seconds)
        frames = integer(row.get("frames"), "frames", 1)
        attacks = integer(row.get("attacks"), "attacks", 40)
        low = integer(row.get("minPieces"), "minPieces", 40)
        high = integer(row.get("maxPieces"), "maxPieces", low)
        if high > 81:
            raise InvalidResult("在盘棋数超过案例81格上限")
        fps = number(row.get("fps"), "fps", 0.000001)
        if not math.isclose(fps, frames / duration, rel_tol=0.001, abs_tol=0.001):
            raise InvalidResult("FPS与帧数/实际秒不一致")
        times = [number(row.get(key), key) for key in ("p50Ms", "p95Ms", "p99Ms", "maxMs")]
        if times != sorted(times):
            raise InvalidResult("帧时分位数顺序错误")
        gpu_samples = integer(row.get("gpuSamples"), "gpuSamples")
        if gpu_samples > frames:
            raise InvalidResult("GPU有效样本不能超过采样帧")
        for key in ("over125Ratio", "over167Ratio", "over333Ratio"):
            if key in row and number(row[key], key) > 1:
                raise InvalidResult(f"{key}超过1")
        for key in ("gpuP50Ms", "gpuP95Ms", "mainP95Ms", "renderP95Ms", "gcBytesPerFrame", "hudMs", "feedbackMs", "arrangeMs"):
            if key in row:
                number(row[key], key, -1)
                if -1 < row[key] < 0:
                    raise InvalidResult(f"{key}无效负数：缺测使用-1")
        if gpu_samples == 0 and any(row.get(k, -1) != -1 for k in ("gpuP50Ms", "gpuP95Ms")):
            raise InvalidResult("GPU缺样不能填0毫秒")
        if gpu_samples > 0 and any(row.get(k, -1) <= 0 for k in ("gpuP50Ms", "gpuP95Ms")):
            raise InvalidResult("有GPU样本需要正有效帧时")
        for hit, miss in (("attackCacheHits", "attackCacheMisses"), ("damageTextHits", "damageTextMisses")):
            if (hit in row) != (miss in row):
                raise InvalidResult(f"{hit}/{miss}需同时存在或同时缺测")
            if hit in row:
                integer(row[hit], hit); integer(row[miss], miss)
        computed = ratio(row.get("attackCacheHits"), row.get("attackCacheMisses"))
        if "attackCacheHitRatio" in row:
            stated = number(row["attackCacheHitRatio"], "attackCacheHitRatio")
            if stated > 1 or not math.isclose(stated, computed or 0, abs_tol=0.000001):
                raise InvalidResult("射界命中率与计数不一致")
        for key in ("snapshotCopies", "snapshotPieceReuses"):
            if key in row: integer(row[key], key)
        if not isinstance(row.get("resolution"), str) or not row["resolution"]:
            raise InvalidResult("缺少实际分辨率")
    return raw


def summary(raw):
    """缺测以null给人核对；保留每段与极端帧，不选最佳轮。"""
    rows = []
    for row in raw["chapters"]:
        value = {key: row[key] for key in ("chapter", "repeat", "frames", "seconds", "attacks", "minPieces", "maxPieces", "fps", "p50Ms", "p95Ms", "p99Ms", "maxMs", "resolution")}
        for key in ("gpuP50Ms", "gpuP95Ms", "gcBytesPerFrame", "snapshotCopies", "snapshotPieceReuses"):
            measured = row.get(key)
            value[key] = None if measured == -1 else measured
        value["attackCacheHitRatio"] = ratio(row.get("attackCacheHits"), row.get("attackCacheMisses"))
        value["damageTextHitRatio"] = ratio(row.get("damageTextHits"), row.get("damageTextMisses"))
        rows.append(value)
    return {"runId": raw["runId"], "status": "completed", "ab": raw["ab"], "device": {key: raw[key] for key in ("cpu", "gpu", "graphicsApi", "unityVersion")},
            "cleanup": {key: raw.get(key) for key in ("returnLogic", "returnPieceRefs", "returnBundles")}, "rows": rows,
            "note": "只核对采样契约；预算和提升需同设备同输入及运行状态证据，非目标机帧率通过。"}


def compare(before, after):
    """环境/工作量不同仅列差异，不计算伪收益；匹配时仍只叫观测差。"""
    if before["runId"] == after["runId"]:
        raise InvalidResult("对照必须是两份不同runId，不能把同作业比自身")
    reasons = []
    for key in ("cpu", "gpu", "graphicsApi", "unityVersion", "ab", "first", "last", "repeats", "requestedSeconds"):
        if before[key] != after[key]: reasons.append(f"{key}不同")
    if len(before["chapters"]) == len(after["chapters"]):
        for index, (old, new) in enumerate(zip(before["chapters"], after["chapters"])):
            for key in ("chapter", "repeat", "resolution", "attacks", "minPieces", "maxPieces"):
                if old[key] != new[key]: reasons.append(f"第{index}段{key}不同")
    else:
        reasons.append("段数不同")
    result = {"beforeRunId": before["runId"], "afterRunId": after["runId"], "metadataMatch": not reasons, "mismatches": reasons,
              "causalImprovementVerified": False, "note": "元数据相同也没有代码/资源哈希、温度/频率/调度与冷暖证据；百分比只为观测差异，不能宣布优化收益。", "rows": []}
    if reasons: return result
    for old, new in zip(before["chapters"], after["chapters"]):
        result["rows"].append({"chapter": old["chapter"], "repeat": old["repeat"], "observedFpsChangePercent": (new["fps"] / old["fps"] - 1) * 100,
                               "observedP95ReductionPercent": None if old["p95Ms"] == 0 else (1 - new["p95Ms"] / old["p95Ms"]) * 100})
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("result", type=Path)
    parser.add_argument("--after", type=Path)
    args = parser.parse_args()
    try:
        raw = read_result(args.result)
        report = {"before" if args.after else "result": summary(raw)}
        if args.after:
            after = read_result(args.after)
            report["after"] = summary(after)
            report["comparison"] = compare(raw, after)
        print(json.dumps(report, ensure_ascii=False, indent=2, allow_nan=False))
        return 0
    except (InvalidResult, ValueError, OSError) as error:
        print(json.dumps({"valid": False, "error": str(error)}, ensure_ascii=False), file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
