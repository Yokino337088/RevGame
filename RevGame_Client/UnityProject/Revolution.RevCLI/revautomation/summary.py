"""Limit agent-facing receipts after execution. Keep the original for evidence."""
import json


_MAX_INLINE = 3200
_KEY = (
    "success", "id", "action", "target", "error", "message", "assertion",
    "finished", "requestId", "compiling", "failed", "cancelled", "published",
    "rebuildReason", "totalBundles", "updatedBundles", "removedBundles",
    "updatedMetadata", "destination", "outputDir", "resultPath", "reportPath",
    "evidencePath", "jobDirectory", "path", "codeChanged", "dataOnly", "written",
    "data", "steps", "cleanup", "response", "name", "passed", "skipped", "inconclusive",
)


def compact_json(value):
    return json.dumps(value, ensure_ascii=False, allow_nan=False, separators=(",", ":"))


def summarize(result):
    """Small receipts stay intact. Large lists retain a preview and their exact length."""
    if len(compact_json(result)) <= _MAX_INLINE:
        return result
    shortened = _preview(result)
    if len(compact_json(shortened)) > _MAX_INLINE:
        shortened = {key: value for key, value in shortened.items()
                     if key in _KEY or key.endswith("Count")}
    shortened["detailsTruncated"] = True
    return shortened


def _preview(value, depth=0):
    if isinstance(value, str):
        return value if len(value) <= 320 else value[:320] + "…"
    if isinstance(value, list):
        if depth > 3:
            return []
        # Include a failed row even when it occurs after the preview.
        selected = value[:3]
        failure = next((row for row in value[3:] if isinstance(row, dict)
                        and (row.get("success") is False or row.get("error")
                             or (isinstance(row.get("response"), dict)
                                 and row["response"].get("success") is False))), None)
        if failure is not None:
            selected = selected + [failure]
        return [_preview(row, depth + 1) for row in selected]
    if not isinstance(value, dict):
        return value
    if depth > 4:
        return {}
    keys = sorted(value, key=lambda key: (key not in _KEY, _KEY.index(key) if key in _KEY else key))
    result = {}
    for key in keys:
        item = value[key]
        if isinstance(item, list):
            result.setdefault(key + "Count", len(item))
        if key not in _KEY and len(result) >= 12:
            continue
        result[key] = _preview(item, depth + 1)
    return result
