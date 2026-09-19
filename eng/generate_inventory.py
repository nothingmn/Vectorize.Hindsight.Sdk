#!/usr/bin/env python3
"""Regenerate the derived API inventory from the pinned specification.

Replaces the ad-hoc inline scripts the reconnaissance pass used. Those produced the same
artifacts, but nothing could check them: an inventory that silently drifts from the
specification is worse than no inventory, because it is quoted as authoritative in
`docs/api-surface.md` and `docs/admin-cli-surface.md`.

Everything here is derived from `spec/openapi.json` alone, so `--check` is meaningful in
CI. Two artifacts under `inventory/` are deliberately NOT produced here:

  served_vs_published.json  requires a probe of a running server
  version_diff.json         requires the served spec as well as the published one

Both are environment-specific observations, not derivations, and pretending they were
reproducible would be a false claim of verification. They are recorded once, by hand,
alongside the note explaining what the probe found.

Usage:
    python3 eng/generate_inventory.py            # write the files
    python3 eng/generate_inventory.py --check    # fail if any has drifted
"""

from __future__ import annotations

import argparse
import collections
import csv
import io
import json
import pathlib
import sys
from typing import Any

ROOT = pathlib.Path(__file__).resolve().parent.parent
SPEC = ROOT / "spec" / "openapi.json"
INVENTORY = ROOT / "inventory"

METHODS = ("get", "post", "put", "patch", "delete")

# Artifacts this script owns. Anything else in inventory/ was produced by a live probe.
GENERATED_FILES = (
    "operations.json",
    "operations_full.json",
    "operations.csv",
    "schemas.md",
    "summary.json",
)


def deref(node: Any) -> Any:
    """Resolves a $ref one level to the schema name, or reports the shape."""
    if not isinstance(node, dict):
        return None
    if "$ref" in node:
        return node["$ref"].split("/")[-1]
    if "anyOf" in node:
        return [deref(x) or x.get("type") for x in node["anyOf"]]
    if node.get("type") == "array":
        items = node.get("items", {})
        return [deref(items) or items.get("type")]
    if node.get("additionalProperties") is not None:
        return "map"
    return node.get("type")


def describe(node: Any) -> str:
    """A readable type description for a schema node."""
    if not isinstance(node, dict):
        return "?"
    if "$ref" in node:
        return node["$ref"].split("/")[-1]
    if "anyOf" in node:
        parts = []
        for option in node["anyOf"]:
            if option.get("type") == "null":
                parts.append("null")
            else:
                parts.append(describe(option))
        return "|".join(parts)
    if node.get("type") == "array":
        return f"array<{describe(node.get('items', {}))}>"
    if node.get("type") == "object" and "additionalProperties" in node:
        inner = node["additionalProperties"]
        if inner is True:
            return "map<string, any>"
        return f"map<string, {describe(inner)}>"
    if "enum" in node:
        return f"enum({node['type']})"
    return node.get("type", "?")


def is_deprecated(operation: dict[str, Any]) -> bool:
    """Whether the specification marks an operation deprecated."""
    summary = (operation.get("summary") or "").lower()
    return bool(operation.get("deprecated", False)) or "removed" in summary


def walk_refs(node: Any, out: set[str]) -> None:
    """Collects every schema name reachable from a node, transitively per level."""
    if isinstance(node, dict):
        for key, value in node.items():
            if key == "$ref":
                out.add(value.split("/")[-1])
            else:
                walk_refs(value, out)
    elif isinstance(node, list):
        for item in node:
            walk_refs(item, out)


def build_operations(spec: dict[str, Any]) -> list[dict[str, Any]]:
    """One record per operation, with its parameters, body and responses."""
    schemas = spec["components"]["schemas"]
    records: list[dict[str, Any]] = []

    for path, item in spec["paths"].items():
        for method in METHODS:
            if method not in item:
                continue

            operation = item[method]

            parameters = []
            for raw in list(item.get("parameters", [])) + list(operation.get("parameters", [])):
                if "$ref" in raw:
                    continue
                parameters.append(
                    {
                        "name": raw.get("name"),
                        "in": raw.get("in"),
                        "required": bool(raw.get("required", False)),
                        "schema": describe(raw.get("schema", {})),
                        "description": (raw.get("description") or "").replace("\n", " ").strip(),
                    }
                )

            body = None
            if operation.get("requestBody"):
                request_body = operation["requestBody"]
                for media_type, media in request_body.get("content", {}).items():
                    body = {
                        "contentType": media_type,
                        "schema": describe(media.get("schema", {})),
                        "required": bool(request_body.get("required", False)),
                    }
                    break

            responses = {}
            for code, response in operation.get("responses", {}).items():
                for media_type, media in response.get("content", {}).items():
                    responses[code] = {"contentType": media_type, "schema": describe(media.get("schema", {}))}
                    break
                else:
                    responses[code] = {"contentType": None, "schema": None}

            records.append(
                {
                    "method": method.upper(),
                    "path": path,
                    "operationId": operation.get("operationId", ""),
                    "tags": operation.get("tags", []),
                    "summary": (operation.get("summary") or "").strip(),
                    "deprecated": is_deprecated(operation),
                    "parameters": parameters,
                    "requestBody": body,
                    "responses": responses,
                }
            )

    return records


def build_summary(spec: dict[str, Any], operations: list[dict[str, Any]]) -> dict[str, Any]:
    """The headline counts, all derived rather than transcribed."""
    schemas = spec["components"]["schemas"]

    reachable: set[str] = set()
    frontier: set[str] = set()
    walk_refs(spec["paths"], frontier)
    while frontier:
        name = frontier.pop()
        if name in reachable:
            continue
        reachable.add(name)
        if name in schemas:
            nested: set[str] = set()
            walk_refs(schemas[name], nested)
            frontier |= (nested - reachable)

    tag_counts = collections.Counter(
        operation["tags"][0] for operation in operations if operation["tags"]
    )

    return {
        "specVersion": spec["info"]["version"],
        "openapi": spec["openapi"],
        "paths": len(spec["paths"]),
        "operations": len(operations),
        "deprecatedOperations": sorted(
            f"{o['method']} {o['path']}" for o in operations if o["deprecated"]
        ),
        "schemas": len(schemas),
        "reachableSchemas": len(reachable),
        "orphanSchemas": sorted(set(schemas) - reachable),
        "tagCounts": dict(sorted(tag_counts.items())),
    }


def build_schemas_markdown(spec: dict[str, Any]) -> str:
    """Every component schema, property by property."""
    schemas = spec["components"]["schemas"]
    lines = [f"# Hindsight component schemas (spec {spec['info']['version']})", "", f"{len(schemas)} schemas", ""]

    for name in sorted(schemas):
        schema = schemas[name]
        properties = schema.get("properties", {})
        required = set(schema.get("required", []))

        lines.append(f"### {name}")
        if schema.get("description"):
            lines.append(f"_{schema['description'][:200].replace(chr(10), ' ')}_")

        if not properties:
            lines.append(f"- shape: {describe(schema)}")
            lines.append("")
            continue

        for prop_name, prop in properties.items():
            flag = "**REQ**" if prop_name in required else "opt"
            description = (prop.get("description") or "").replace("\n", " ")[:130]
            default = f" default={prop['default']!r}" if "default" in prop else ""
            lines.append(f"- `{prop_name}` {flag} : {describe(prop)}{default}  {description}")

        lines.append("")

    return "\n".join(lines)


def build_csv(operations: list[dict[str, Any]]) -> str:
    """The tabular view, sorted for stable diffs."""
    buffer = io.StringIO()
    writer = csv.writer(buffer)
    writer.writerow(
        [
            "method",
            "path",
            "operation_id",
            "tag",
            "deprecated",
            "summary",
            "path_params",
            "query_params",
            "request_content_type",
            "request_schema",
            "response_codes",
        ]
    )

    for operation in sorted(operations, key=lambda o: (o["tags"][0] if o["tags"] else "", o["path"], o["method"])):
        path_params = [p["name"] for p in operation["parameters"] if p["in"] == "path"]
        query_params = [p["name"] for p in operation["parameters"] if p["in"] == "query"]
        body = operation["requestBody"] or {}

        writer.writerow(
            [
                operation["method"],
                operation["path"],
                operation["operationId"],
                ",".join(operation["tags"]),
                operation["deprecated"],
                operation["summary"],
                ";".join(path_params),
                ";".join(query_params),
                body.get("contentType", ""),
                body.get("schema", ""),
                ";".join(sorted(operation["responses"].keys())),
            ]
        )

    return buffer.getvalue()


def produce() -> dict[str, str]:
    """Builds every artifact as text, so --check can compare without touching disk."""
    spec = json.loads(SPEC.read_text())
    operations = build_operations(spec)

    return {
        "operations.json": json.dumps(operations, indent=1) + "\n",
        "operations_full.json": json.dumps(operations, indent=1) + "\n",
        "operations.csv": build_csv(operations),
        "schemas.md": build_schemas_markdown(spec),
        "summary.json": json.dumps(build_summary(spec, operations), indent=1) + "\n",
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--check", action="store_true", help="fail if the committed inventory has drifted")
    args = parser.parse_args()

    try:
        artifacts = produce()
    except (OSError, KeyError, ValueError) as ex:
        print(f"::error::inventory generation failed: {ex}", file=sys.stderr)
        return 1

    if args.check:
        stale = []
        for name, content in artifacts.items():
            path = INVENTORY / name
            if not path.exists():
                stale.append(f"{name} (missing)")
            elif path.read_text().replace("\r\n", "\n") != content.replace("\r\n", "\n"):
                stale.append(name)

        if stale:
            print("::error::The committed inventory has drifted from the specification:", file=sys.stderr)
            for name in stale:
                print(f"    {name}", file=sys.stderr)
            print("    Run: python3 eng/generate_inventory.py", file=sys.stderr)
            return 1

        print(f"Inventory is up to date ({len(artifacts)} file(s)).")
        return 0

    INVENTORY.mkdir(parents=True, exist_ok=True)
    for name, content in artifacts.items():
        (INVENTORY / name).write_text(content)

    spec = json.loads(SPEC.read_text())
    print(f"Wrote {len(artifacts)} inventory file(s) from spec {spec['info']['version']}.")
    print(f"Untouched (produced by live probes, not derivable): served_vs_published.json, version_diff.json")
    return 0


if __name__ == "__main__":
    sys.exit(main())
