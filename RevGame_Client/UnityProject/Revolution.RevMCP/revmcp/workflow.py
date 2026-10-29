"""Cross-backend test plans. Validate first; always attempt every cleanup step."""
import time
from contextlib import nullcontext
from revautomation.client import RevError, RevCancelled
from revautomation.pipeline import references, resolve, matches, validate_condition


def run(hub, plan, cancel=None):
    """先校验整份计划，再执行 steps；无论失败或取消，最后逐条尝试 finally。

    $ref 保留原始 JSON 类型，不能把对象/数字先转成字符串。until 只重复只读
    查询；业务变更只执行一次。断言检查 response 内的精确值，不解析自然语言。
    """
    if not isinstance(plan, dict) or set(plan) - {"name", "intent", "steps", "finally"} or not isinstance(plan.get("steps"), list) or not plan["steps"]:
        raise RevError("Plan requires nonempty steps; optional name and finally")
    if not isinstance(plan.get("finally", []), list):
        raise RevError("finally must be an array")
    if not isinstance(plan.get("intent", ""), str) or len(plan.get("intent", "")) > 512:
        raise RevError("intent requires a string of at most 512 characters")
    catalog = hub.discover(cancel)
    known = {(s["target"], s["id"]): s for s in catalog["actions"]}
    known.update({("gm", c["name"]): c for c in catalog["commands"]})
    ids = set()
    for phase in ("steps", "finally"):
        for step in plan.get(phase, []):
            if not isinstance(step, dict) or set(step) - {"id", "target", "action", "arguments", "confirm", "expect", "until", "intent"}:
                raise RevError("Unknown plan step fields")
            if not isinstance(step.get("intent", ""), str) or len(step.get("intent", "")) > 512:
                raise RevError("step intent requires a string of at most 512 characters")
            spec = known.get((step.get("target"), step.get("action")))
            if spec is None:
                raise RevError("Plan references an unavailable capability: " + str(step.get("action")))
            if not isinstance(step.get("arguments", {}), dict) or type(step.get("confirm", False)) is not bool or not isinstance(step.get("expect", []), list):
                raise RevError("Invalid arguments/confirm/expect")
            for source, _ in references(step.get("arguments", {})):
                if source not in ids:
                    raise RevError("Plan reference must point to an earlier step: " + source)
            for condition in step.get("expect", []):
                validate_condition(condition)
            until = step.get("until")
            if until is not None:
                if not isinstance(until, dict) or set(until) - {"path", "equals", "exists", "timeoutSeconds", "intervalSeconds"}:
                    raise RevError("Invalid until fields")
                if not spec.get("readOnly", False):
                    raise RevError("until can only repeat explicitly readOnly queries")
                validate_condition({k: v for k, v in until.items() if k not in ("timeoutSeconds", "intervalSeconds")})
                for key, default in (("timeoutSeconds", 30), ("intervalSeconds", .2)):
                    number = until.get(key, default)
                    if type(number) not in (int, float) or not 0 < number <= 3600:
                        raise RevError("Invalid until duration")
            if step.get("id") is not None:
                name = step["id"]
                if not isinstance(name, str) or not name or "#" in name or name in ids:
                    raise RevError("Step ids must be nonempty, unique and contain no #")
                ids.add(name)
            # Resolve-dependent values are checked just before invocation; other
            # parameters are validated for the entire plan before its first mutation.
            if not list(references(step.get("arguments", {}))):
                hub.validate_call(step["target"], step["action"], step.get("arguments", {}), step.get("confirm", False), catalog)
    report = dict(success=True, name=plan.get("name", "agent-test"), steps=[], cleanup=[])
    responses = {}

    def activity_scope(**values):
        activity = getattr(hub, "activity", None)
        return activity.scope(**values) if activity else nullcontext()

    def execute(step, token):
        activity = getattr(hub, "activity", None)
        ticket = activity.start("plan", step["action"], {"expect": step.get("expect", []), "until": step.get("until")}) if activity else None
        try:
            row = execute_core(step, token)
        except (Exception, KeyboardInterrupt) as exc:
            if activity:
                activity.finish(ticket, error=exc)
            raise
        if activity:
            activity.finish(ticket, row["response"])
        return row

    def execute_core(step, token):
        started = time.monotonic()
        until = step.get("until")
        deadline = started + (until.get("timeoutSeconds", 30) if until else hub.timeout)
        count = 0
        while True:
            response = hub.call(step["target"], step["action"], resolve(step.get("arguments", {}), responses), step.get("confirm", False), token, catalog)
            count += 1
            if not response.get("success") or not until or matches(response, until):
                break
            if time.monotonic() >= deadline:
                response = dict(response, success=False, error="ConditionTimeout")
                break
            if token is not None and token.wait(min(until.get("intervalSeconds", .2), max(0, deadline - time.monotonic()))):
                raise RevCancelled("Plan cancelled")
            if token is None:
                time.sleep(min(until.get("intervalSeconds", .2), max(0, deadline - time.monotonic())))
        for condition in step.get("expect", []):
            if not matches(response, condition):
                response = dict(response, success=False, error="AssertionFailed", assertion=condition)
                break
        if step.get("id"):
            responses[step["id"]] = response
        return dict(id=step.get("id"), target=step["target"], action=step["action"], response=response, polls=count, seconds=time.monotonic() - started)

    try:
        for step in plan["steps"]:
            with activity_scope(plan=report["name"], phase="steps", step=step.get("id", step["action"]), intent=step.get("intent", plan.get("intent", ""))):
                row = execute(step, cancel)
            report["steps"].append(row)
            if not row["response"].get("success"):
                report["success"] = False
                break
    except (RevError, OSError, ValueError, TimeoutError, KeyboardInterrupt) as exc:
        report.update(success=False, error=str(exc), cancelled=isinstance(exc, (RevCancelled, KeyboardInterrupt)))
    finally:
        for step in plan.get("finally", []):
            try:
                with activity_scope(plan=report["name"], phase="finally", step=step.get("id", step["action"]), intent=step.get("intent", "")):
                    row = execute(step, None)  # Cancellation must not skip cleanup.
                report["cleanup"].append(row)
                if not row["response"].get("success"):
                    report["success"] = False
            except (RevError, OSError, ValueError, TimeoutError, KeyboardInterrupt) as exc:
                report["cleanup"].append(dict(action=step["action"], error=str(exc)))
                report["success"] = False
    return report
