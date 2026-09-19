#!/usr/bin/env python3
"""Generate the typed operation surface for Vectorize.Hindsight.Sdk.

Emits one method per non-deprecated operation, grouped into a partial class per
functional area (the spec's own tags). Grouping by area rather than by size is the
"split by function first" rule: a reader looking for the webhook operations opens
one file, and a regeneration of one area cannot disturb another.

Usage:
    python3 eng/generate_operations.py            # write the files
    python3 eng/generate_operations.py --check    # fail if output is stale
    python3 eng/generate_operations.py --report   # coverage table

Coverage is computed, not asserted. Two independent checks run in CI:
  * every non-deprecated operation appears exactly once in the generated output
  * every generated file compiles against the real models

Signatures follow the house rules:
  * `Async` suffix on every method
  * `CancellationToken` required, no default value, always last
  * path parameters are required and typed
  * query parameters live in a generated options type, so an operation with 15
    optional filters does not become a 15-argument method
  * the body, when the operation declares one, is a typed model
"""

from __future__ import annotations

import argparse
import json
import pathlib
import re
import sys
from typing import Any

ROOT = pathlib.Path(__file__).resolve().parent.parent
SPEC = ROOT / "spec" / "openapi.json"
OPS_DIR = ROOT / "src" / "Vectorize.Hindsight.Sdk" / "Operations"

METHODS = ("get", "post", "put", "patch", "delete")

# Operations the caller does not want. Excluded deliberately and by name; the
# report prints them so the coverage claim stays auditable rather than
# approximate.
EXCLUDED_DEPRECATED = True

CS_KEYWORDS = {
    "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char",
    "checked", "class", "const", "continue", "decimal", "default", "delegate",
    "do", "double", "else", "enum", "event", "explicit", "extern", "false",
    "finally", "fixed", "float", "for", "foreach", "goto", "if", "implicit",
    "in", "int", "interface", "internal", "is", "lock", "long", "namespace",
    "new", "null", "object", "operator", "out", "override", "params", "private",
    "protected", "public", "readonly", "ref", "return", "sbyte", "sealed",
    "short", "sizeof", "stackalloc", "static", "string", "struct", "switch",
    "this", "throw", "true", "try", "typeof", "uint", "ulong", "unchecked",
    "unsafe", "ushort", "using", "virtual", "void", "volatile", "while",
}

# Operations whose request is multipart/form-data. Excluded from generation and
# hand-written in HindsightClient.Uploads.cs: a file upload's parameters are
# streams and filenames, not a JSON body, and generating a JSON-shaped signature
# for one would be a lie the compiler cannot catch.
HAND_WRITTEN_MULTIPART = {
    ("POST", "/v1/default/banks/{bank_id}/files/retain"),
    ("POST", "/v1/default/banks/{bank_id}/transfer/import"),

    # POST /v1/default/banks/{bank_id}/document-transfer is deliberately absent:
    # import_documents is marked deprecated in the specification, and decision D2
    # puts deprecated operations out of scope. The live replacement is
    # transfer/import, which is above. It was hand-written once by mistake, which
    # is why the hand-written set is now asserted against the non-deprecated set
    # at generation time rather than merely listed.
}

# Operations whose success body is binary. They return a Stream the caller owns.
BINARY_OPERATIONS = {
    ("GET", "/v1/default/files/download/{key}"),
    ("GET", "/v1/default/banks/{bank_id}/attachments/{attachment_id}"),
}


def cs_name(value: str) -> str:
    parts = re.split(r"[^0-9a-zA-Z]+", value)
    return "".join(p[:1].upper() + p[1:] for p in parts if p)


def cs_property(value: str) -> str:
    parts = re.split(r"[^0-9a-zA-Z]+", value)
    out = "".join(p[:1].upper() + p[1:] for p in parts if p)
    if not out:
        out = "Value"
    if out[0].isdigit():
        out = "_" + out
    if out.lower() in CS_KEYWORDS:
        out = "@" + out
    return out


def cs_param(value: str) -> str:
    """A camelCase parameter name, escaped when it collides with a keyword."""
    prop = cs_property(value)
    camel = prop[:1].lower() + prop[1:]
    if camel.lower() in CS_KEYWORDS:
        camel = "@" + camel
    return camel


def doc_comment(text: str, indent: str = "    ") -> list[str]:
    if not text:
        return []
    text = text.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")
    paragraphs = [p.strip() for p in re.split(r"\n\s*\n", text) if p.strip()]
    lines = [f"{indent}/// <summary>"]
    for para in paragraphs:
        for raw in para.split("\n"):
            lines.append(f"{indent}/// {raw.strip()}".rstrip())
        lines.append(f"{indent}///")
    if lines[-1].strip() == "///":
        lines.pop()
    lines.append(f"{indent}/// </summary>")
    return lines


def resolve(node: dict[str, Any]) -> dict[str, Any]:
    """Unwrap a single non-null anyOf arm."""
    if not isinstance(node, dict):
        return {}
    for key in ("anyOf", "oneOf"):
        if key in node:
            options = [o for o in node[key] if o.get("type") != "null"]
            if options:
                return options[0]
            return node
    return node


def schema_to_type(node: Any) -> str:
    """Maps a JSON Schema node to a C# type name."""
    if node is True or not isinstance(node, dict):
        return "string"
    if "$ref" in node:
        ref = node["$ref"].split("/")[-1]
        return ref if ref in ENUM_SCHEMA_NAMES else cs_name(ref)

    resolved = resolve(node)
    if "$ref" in resolved:
        ref = resolved["$ref"].split("/")[-1]
        return ref if ref in ENUM_SCHEMA_NAMES else cs_name(ref)

    t = resolved.get("type")
    if t == "array":
        return f"List<{schema_to_type(resolved.get('items', {}))}>"
    if t == "integer":
        return "long" if resolved.get("format") == "int64" else "int"
    if t == "number":
        return "double"
    if t == "boolean":
        return "bool"
    if t == "object":
        ap = resolved.get("additionalProperties")
        if isinstance(ap, dict):
            return f"Dictionary<string, {schema_to_type(ap)}>"
        return "System.Text.Json.JsonElement"
    return "string"


ENUM_SCHEMA_NAMES: set[str] = set()

# HttpMethod has no enum members for these spellings; map explicitly rather than
# relying on capitalisation.
HTTP_METHOD_NAMES = {
    "GET": "Get",
    "POST": "Post",
    "PUT": "Put",
    "PATCH": "Patch",
    "DELETE": "Delete",
}


def success_response(operation: dict[str, Any]) -> tuple[str, str | None, bool]:
    """Returns (kind, type_name, has_body) for the success path.

    kind is 'json', 'binary', or 'empty'.
    """
    for status in ("200", "201", "202"):
        response = operation.get("responses", {}).get(status)
        if not response:
            continue
        content = response.get("content", {})
        if not content:
            return "empty", None, False
        for media_type, media in content.items():
            if media_type == "application/json":
                schema = media.get("schema", {})
                if "$ref" in schema:
                    return "json", cs_name(schema["$ref"].split("/")[-1]), True
                return "empty", None, False
            if media_type in ("application/octet-stream", "application/zip"):
                return "binary", None, True
        return "empty", None, False
    return "empty", None, False


class Operation:
    def __init__(self, method: str, path: str, spec_op: dict[str, Any], path_item: dict[str, Any]):
        self.method = method.upper()
        self.path = path
        self.spec = spec_op
        self.path_item = path_item
        self.operation_id = spec_op.get("operationId") or ""
        self.tag = (spec_op.get("tags") or ["Operations"])[0]
        self.summary = (spec_op.get("summary") or "").strip()
        self.description = (spec_op.get("description") or "").strip()
        self.deprecated = bool(spec_op.get("deprecated", False)) or "removed" in self.summary.lower()

        self.path_params: list[tuple[str, str, str]] = []
        self.query_params: list[tuple[str, str, str, bool]] = []
        for raw in list(path_item.get("parameters", [])) + list(spec_op.get("parameters", [])):
            if "$ref" in raw:
                continue
            name = raw.get("name", "")
            location = raw.get("in", "")
            desc = (raw.get("description") or "").replace("\n", " ").strip()
            if location == "path":
                self.path_params.append((name, schema_to_type(raw.get("schema", {})), desc))
            elif location == "query":
                self.query_params.append(
                    (name, schema_to_type(raw.get("schema", {})), desc, bool(raw.get("required", False)))
                )

        self.body_type: str | None = None
        self.body_required = False
        self.multipart: str | None = None
        if spec_op.get("requestBody"):
            request_body = spec_op["requestBody"]
            self.body_required = bool(request_body.get("required", False))
            for media_type, media in request_body.get("content", {}).items():
                if media_type == "application/json":
                    schema = media.get("schema", {})
                    if "$ref" in schema:
                        self.body_type = cs_name(schema["$ref"].split("/")[-1])
                elif media_type == "multipart/form-data":
                    schema = media.get("schema", {})
                    if "$ref" in schema:
                        self.multipart = cs_name(schema["$ref"].split("/")[-1])

        self.response_kind, self.response_type, _ = success_response(spec_op)

    @property
    def method_name(self) -> str:
        base = cs_name(self.operation_id) if self.operation_id else cs_name(self.tag + " " + self.path)
        if not base:
            base = cs_name(self.method + " " + self.path)
        return base

    @property
    def key(self) -> str:
        return f"{self.method} {self.path}"


def render_operation(op: Operation, used_names: set[str]) -> str:
    lines: list[str] = []

    # The method name must be unique; the spec's operationIds are, but guard anyway.
    name = op.method_name
    suffix = 2
    while name in used_names:
        name = f"{op.method_name}{suffix}"
        suffix += 1
    used_names.add(name)

    lines.extend(doc_comment(op.description or op.summary, indent="    "))

    # Signature.
    parameters: list[str] = []
    for param_name, param_type, _ in op.path_params:
        parameters.append(f"{param_type} {cs_param(param_name)}")

    if op.body_type is not None:
        if op.body_required:
            parameters.append(f"{op.body_type} body")
        else:
            # Nullable but with no default value, so the required
            # CancellationToken can stay last. Matches the house convention
            # established by the sibling client: the token is mandatory and
            # final, and an omittable value is passed as an explicit null.
            parameters.append(f"{op.body_type}? body")

    query_type = f"{name}Query" if op.query_params else None
    if query_type:
        parameters.append(f"{query_type}? query")

    parameters.append("CancellationToken cancellationToken")

    return_type = "Stream" if op.response_kind == "binary" else (
        op.response_type if op.response_kind == "json" else "System.Text.Json.JsonElement"
    )
    if op.response_kind == "json" and op.response_type is None:
        return_type = "System.Text.Json.JsonElement"

    lines.append(f"    public async Task<{return_type}> {name}Async(")
    for index, parameter in enumerate(parameters):
        separator = "," if index < len(parameters) - 1 else ")"
        lines.append(f"        {parameter}{separator}")

    lines.append("    {")

    # Build the path with substituted parameters.
    path_expr = op.path
    for param_name, _, _ in op.path_params:
        path_expr = path_expr.replace(
            "{" + param_name + "}", '" + Uri.EscapeDataString(' + cs_param(param_name) + ') + "'
        )

    lines.append(f'        var path = $"{path_expr}";')

    # Query parameters.
    lines.append("        var queryParameters = new List<KeyValuePair<string, string>>();")
    for param_name, param_type, _, _ in op.query_params:
        prop = cs_property(param_name)
        if param_type == "bool":
            lines.append(f"        if (query is not null && query.{prop} is {{ }} {cs_property(param_name)}Value)")
            lines.append(
                f'        {{ queryParameters.Add(new("{param_name}", '
                f'{cs_property(param_name)}Value ? "true" : "false")); }}'
            )
        elif param_type in ("int", "long", "double"):
            lines.append(f"        if (query?.{prop} is {{ }} {prop}Value)")
            lines.append(
                f'        {{ queryParameters.Add(new("{param_name}", '
                f'{prop}Value.ToString(System.Globalization.CultureInfo.InvariantCulture))); }}'
            )
        elif param_type.startswith("List<"):
            lines.append(f"        if (query is not null)")
            lines.append("        {")
            lines.append(f"            foreach (var value in query.{prop})")
            lines.append("            {")
            lines.append(f'                queryParameters.Add(new("{param_name}", value));')
            lines.append("            }")
            lines.append("        }")
        else:
            lines.append(f"        if (query?.{prop} is {{ }} {prop}Value)")
            lines.append(f'        {{ queryParameters.Add(new("{param_name}", {prop}Value)); }}')

    # Idempotency: a synchronous retain is not retried.
    is_idempotent = not (op.method == "POST" and "retain" in op.operation_id and op.body_type is not None)
    lines.append(f"        var request = new HindsightRequest")
    lines.append("        {")
    lines.append(f"            Method = HttpMethod.{HTTP_METHOD_NAMES[op.method]},")
    lines.append("            Path = path,")
    lines.append("            PathTemplate = \"" + op.method + " " + op.path.replace('"', '\\"') + "\",")
    lines.append("            Query = queryParameters,")
    if op.body_type is not None:
        lines.append("            Body = body is null ? null : SerializeBody(body),")
        lines.append("            ContentType = body is null ? null : \"application/json\",")
    lines.append(f"            IsIdempotent = {str(is_idempotent).lower()},")
    lines.append("        };")

    if op.response_kind == "binary":
        lines.append("        return await SendForStreamAsync(request, cancellationToken).ConfigureAwait(false);")
    elif op.response_kind == "json":
        lines.append(
            f"        return await SendAsync<{return_type}>(request, cancellationToken).ConfigureAwait(false);"
        )
    else:
        lines.append("        return await SendForJsonAsync(request, cancellationToken).ConfigureAwait(false);")

    lines.append("    }")
    return "\n".join(lines)


def render_query_type(op: Operation) -> str:
    if not op.query_params:
        return ""
    name = f"{op.method_name}Query"
    lines = [
        "",
        "/// <summary>",
        f"/// The query parameters for <c>{op.method_name}Async</c>.",
        "/// </summary>",
        "/// <remarks>",
        "/// A generated options type rather than fifteen optional parameters, so adding",
        "/// a filter later is additive and a call site does not have to name every",
        "/// argument it does not use.",
        "/// </remarks>",
        f"public sealed class {name}",
        "{",
    ]
    seen: set[str] = set()
    for param_name, param_type, desc, _ in op.query_params:
        prop = cs_property(param_name)
        if prop in seen:
            continue
        seen.add(prop)
        lines.extend(doc_comment(desc, indent="    "))
        if param_type in ("int", "long", "double", "bool"):
            lines.append(f"    public {param_type}? {prop} {{ get; set; }}")
        elif param_type.startswith("List<"):
            lines.append(f"    public {param_type} {prop} {{ get; set; }} = [];")
        else:
            lines.append(f"    public {param_type}? {prop} {{ get; set; }}")
    lines.append("}")
    return "\n".join(lines)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--check", action="store_true")
    parser.add_argument("--report", action="store_true")
    args = parser.parse_args()

    spec = json.loads(SPEC.read_text())
    ENUM_SCHEMA_NAMES.update(n for n, s in spec["components"]["schemas"].items() if "enum" in s)

    operations: list[Operation] = []
    for path, item in spec["paths"].items():
        for method in METHODS:
            if method in item:
                operations.append(Operation(method, path, item[method], item))

    included = [
        o for o in operations
        if not (EXCLUDED_DEPRECATED and o.deprecated)
        and (o.method, o.path) not in HAND_WRITTEN_MULTIPART
    ]
    excluded = [o for o in operations if o.deprecated]
    hand_written = [o for o in operations if (o.method, o.path) in HAND_WRITTEN_MULTIPART]

    # Cross-check the hand-written set against the specification. A mismatch means
    # either a deprecated operation was given a method it should not have, or a
    # live multipart operation has no method at all. Both are coverage defects
    # that would otherwise be invisible, so they fail generation in --check mode.
    spec_multipart = {
        (o.method, o.path)
        for o in operations
        if o.multipart is not None and not o.deprecated
    }
    hand_written_set = {(o.method, o.path) for o in hand_written}
    mismatches: list[str] = []
    for method, path in sorted(spec_multipart - hand_written_set):
        mismatches.append(f"live multipart operation has no hand-written method: {method} {path}")
    for method, path in sorted(hand_written_set - spec_multipart):
        operation = next((o for o in operations if (o.method, o.path) == (method, path)), None)
        reason = "deprecated" if operation is not None and operation.deprecated else "not multipart"
        mismatches.append(f"hand-written method is {reason} and should not exist: {method} {path}")

    by_tag: dict[str, list[Operation]] = {}
    for op in included:
        by_tag.setdefault(op.tag, []).append(op)

    if mismatches:
        print("::error::Hand-written multipart set does not match the specification:", file=sys.stderr)
        for mismatch in mismatches:
            print(f"    {mismatch}", file=sys.stderr)
        if args.check:
            return 1

    if args.report:
        print(f"Spec operations        : {len(operations)}")
        print(f"Excluded (deprecated)  : {len(excluded)}")
        print(f"Hand-written (multipart): {len(hand_written)}")
        print(f"Generated              : {len(included)}")
        print(f"Total covered          : {len(included) + len(hand_written)}")
        print(f"Functional areas       : {len(by_tag)}")
        print()
        for op in excluded:
            print(f"  EXCLUDED  {op.key}  ({op.operation_id})")
        print()
        for tag in sorted(by_tag):
            ops = sorted(by_tag[tag], key=lambda o: o.path)
            print(f"  {tag:22} {len(ops):>3} operations")
            for op in ops:
                body = f" body={op.body_type}" if op.body_type else ""
                query = f" query={len(op.query_params)}" if op.query_params else ""
                print(f"      {op.method:6} {op.path}{body}{query}")
        return 0

    files: dict[pathlib.Path, str] = {}
    for tag in sorted(by_tag):
        ops = sorted(by_tag[tag], key=lambda o: (o.path, o.method))
        used_names: set[str] = set()

        header = [
            "// <auto-generated>",
            "//   Generated by eng/generate_operations.py from spec/openapi.json.",
            "//   Do not edit by hand: re-run the generator instead. `--check` fails CI on drift.",
            "// </auto-generated>",
            "",
            "#nullable enable",
            "",
            "using System.Text.Json;",
            "using Vectorize.Hindsight.Sdk.Configuration;",
            "using Vectorize.Hindsight.Sdk.Errors;",
            "using Vectorize.Hindsight.Sdk.Http;",
            "using Vectorize.Hindsight.Sdk.Models;",
            "",
            "namespace Vectorize.Hindsight.Sdk;",
            "",
            "/// <summary>",
            f"/// The {tag} operations.",
            "/// </summary>",
            "/// <remarks>",
            f"/// Part of <c>HindsightClient</c>. Generated from the specification; {len(ops)} operation(s).",
            "/// </remarks>",
            "public sealed partial class HindsightClient",
            "{",
        ]

        body_lines: list[str] = []
        query_types: list[str] = []
        for op in ops:
            body_lines.append("")
            body_lines.append(render_operation(op, used_names))
            query_types.append(render_query_type(op))

        footer = ["}"]
        content = "\n".join(header + body_lines + footer) + "\n"
        for query_type in query_types:
            if query_type:
                content += query_type + "\n"

        filename = f"HindsightClient.{cs_name(tag)}.g.cs"
        files[OPS_DIR / filename] = content

    if args.check:
        stale = [str(p.relative_to(ROOT)) for p, c in files.items() if not p.exists() or p.read_text() != c]
        # A stale file that exists but is no longer generated is also drift.
        if OPS_DIR.exists():
            for existing in OPS_DIR.glob("*.g.cs"):
                if existing not in files:
                    stale.append(str(existing.relative_to(ROOT)) + " (obsolete)")
        if stale:
            print("::error::Generated operations are stale:", file=sys.stderr)
            for s in stale:
                print(f"    {s}", file=sys.stderr)
            return 1
        print(f"Generated operations are up to date ({len(files)} files, {len(included)} operations).")
        return 0

    OPS_DIR.mkdir(parents=True, exist_ok=True)
    for existing in OPS_DIR.glob("*.g.cs"):
        if existing not in files:
            existing.unlink()
    for path, content in files.items():
        path.write_text(content)

    print(f"Wrote {len(files)} operation files covering {len(included)} generated operations")
    print(f"{len(hand_written)} multipart operation(s) are hand-written in HindsightClient.Uploads.cs")
    print(f"Excluded {len(excluded)} deprecated operation(s)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
