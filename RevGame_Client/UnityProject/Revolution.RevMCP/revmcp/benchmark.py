"""通过真实 RevMCP 计划构造、预热、采样和清理同一份负载。"""
import math
import statistics

from revautomation.client import RevError
from revautomation.pipeline import pointer
from . import workflow


def run(hub, plan, spec, cancel=None):
    """每轮使用现有断言和 finally，按声明的 JSON Pointer 获取真实样本并可选比较基线。"""
    if not isinstance(spec, dict) or set(spec) - {"identity", "warmup", "repeat", "metrics", "baseline"}:
        raise RevError("benchmark 只允许 identity/warmup/repeat/metrics/baseline。")
    identity = spec.get("identity")
    metrics = spec.get("metrics")
    warmup, repeat = spec.get("warmup", 1), spec.get("repeat", 3)
    if not isinstance(identity, dict) or any(not identity.get(field) for field in ("case", "revision", "device", "workload")):
        raise RevError("性能身份需要 case/revision/device/workload。")
    if (type(warmup) is not int or not 0 <= warmup <= 20 or type(repeat) is not int or not 1 <= repeat <= 50
            or not isinstance(metrics, dict) or not metrics or len(metrics) > 32
            or any(not isinstance(path, str) or not path.startswith("/") for path in metrics.values()) or not plan.get("finally")):
        raise RevError("性能轮次需要有效次数、指标路径和复位 finally。")
    comparison = spec.get("baseline")
    signature = {key: value for key, value in identity.items() if key != "revision"}
    if comparison is not None:
        if not isinstance(comparison, dict) or comparison.get("signature") != signature:
            raise RevError("基线的场景、设备或工作量不同。")
        baseline_metrics = comparison.get("median", {})
        if not isinstance(baseline_metrics, dict) or baseline_metrics.keys() != metrics.keys() or comparison.get("paths") != metrics:
            raise RevError("基线缺少口径匹配的指标。")
        if any(type(value) not in (int, float) or not math.isfinite(value) or value < 0 for value in baseline_metrics.values()):
            raise RevError("基线含无效或缺失样本。")
    else:
        baseline_metrics = None

    samples = []
    for number in range(warmup + repeat):
        report = workflow.run(hub, plan, cancel)
        if not report.get("success"):
            return dict(report, failedRound=number + 1, measuredSamples=samples, identity=identity)
        if number < warmup:
            continue
        sample = {field: pointer(report, path) for field, path in metrics.items()}
        if any(type(value) not in (int, float) or not math.isfinite(value) or value < 0 for value in sample.values()):
            raise RevError("采样包含缺失、无效或负值指标。")
        samples.append(sample)
    median = {field: statistics.median(row[field] for row in samples) for field in metrics}
    receipt = dict(identity=identity, signature=signature, paths=metrics, warmup=warmup, repeat=repeat, samples=samples, median=median)
    if baseline_metrics is not None:
        receipt["comparison"] = {
            field: dict(before=baseline_metrics[field], after=value, delta=value - baseline_metrics[field],
                        percent=(value - baseline_metrics[field]) * 100 / baseline_metrics[field] if baseline_metrics[field] else None)
            for field, value in median.items()
        }
    return dict(report, benchmark=receipt)
