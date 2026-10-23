"""Portable JSON Schema subset shared by explicit external and runtime tools."""
import math


def validate(value, schema, path="$"):
    kind = schema.get("type")
    types = {"object": dict, "array": list, "string": str, "integer": int,
             "number": (int, float), "boolean": bool, "null": type(None)}
    if kind not in types or not isinstance(value, types[kind]) or (kind in ("integer", "number") and isinstance(value, bool)):
        raise ValueError(f"{path}: expected {kind}")
    if kind in ("integer", "number") and (not math.isfinite(value)):
        raise ValueError(f"{path}: non-finite number")
    if "enum" in schema and not any(type(value) is type(x) and value == x for x in schema["enum"]):
        raise ValueError(f"{path}: value outside enum")
    if isinstance(value, dict):
        props = schema.get("properties", {})
        for key in schema.get("required", []):
            if key not in value:
                raise ValueError(f"{path}: missing {key}")
        for key, child in value.items():
            if key in props:
                validate(child, props[key], f"{path}.{key}")
            elif schema.get("additionalProperties") is False:
                raise ValueError(f"{path}: unknown field {key}")
    if isinstance(value, list) and "items" in schema:
        for i, child in enumerate(value):
            validate(child, schema["items"], f"{path}[{i}]")
    for key, maximum in (("minimum", False), ("maximum", True), ("minLength", False), ("maxLength", True), ("minItems", False), ("maxItems", True)):
        if key in schema:
            size = len(value) if isinstance(value, (str, list)) else value
            if (size > schema[key]) if maximum else (size < schema[key]):
                raise ValueError(f"{path}: exceeds {key}")


def check_schema(schema):
    supported = {"type", "properties", "required", "additionalProperties", "items", "enum", "minimum", "maximum", "minLength", "maxLength", "minItems", "maxItems", "description", "title", "default"}
    if not isinstance(schema, dict) or set(schema) - supported or schema.get("type") not in ("object", "array", "string", "integer", "number", "boolean", "null"):
        raise ValueError("Unsupported JSON Schema; use the documented portable subset")
    if "additionalProperties" in schema and type(schema["additionalProperties"]) is not bool:
        raise ValueError("additionalProperties must be boolean")
    props = schema.get("properties", {})
    if not isinstance(props, dict) or not isinstance(schema.get("required", []), list) or any(k not in props for k in schema.get("required", [])):
        raise ValueError("Invalid properties/required")
    for child in props.values():
        check_schema(child)
    if "items" in schema:
        check_schema(schema["items"])


def object_schema(properties=None, required=()):
    return dict(type="object", properties=properties or {}, required=list(required), additionalProperties=False)
