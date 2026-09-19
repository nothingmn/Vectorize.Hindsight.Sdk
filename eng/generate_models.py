#!/usr/bin/env python3
"""Generate the typed model layer for Vectorize.Hindsight.Sdk from the OpenAPI spec.

Everything under src/Vectorize.Hindsight.Sdk/Models/ is generated and is safe to
delete and regenerate. Hand-written types (the four unions the generator cannot
express, and their converters) live in src/Vectorize.Hindsight.Sdk/Unions/ so a
regeneration can never remove them.

Why a generator rather than hand-written DTOs: the spec declares 181 component
schemas. Hand-writing them would (a) take longer than the rest of the library,
(b) drift from the spec on the next revision, and (c) make a coverage claim
unverifiable. The generator makes the mapping explicit, reviewable, and
re-runnable — `--check` fails if the committed output differs from the spec, so
drift is caught in CI rather than in a caller's code.

Usage:
    python3 eng/generate_models.py                 # write the files
    python3 eng/generate_models.py --check         # fail if output is stale
    python3 eng/generate_models.py --report        # print the mapping table

Inputs:  spec/openapi.json
Outputs: src/Vectorize.Hindsight.Sdk/Models/*.cs
         src/Vectorize.Hindsight.Sdk/Http/HindsightJsonContext.cs

Mapping decisions, each deliberate:

  date-time        -> string. A DateTimeOffset would re-serialise in the round
                      trip's own format and break byte-for-byte fidelity.
  enums            -> string, plus a constants class. A closed C# enum would
                      throw on a value the server adds later; the spec's own
                      enums are explicitly extensible (TagGroupLeaf.match grew).
  unknown type     -> JsonElement, so an untyped member is still reachable.
  additionalProps  -> Dictionary<string, T>; `true` becomes JsonElement.
  anyOf [X, null]  -> nullable X.
  const            -> a string member initialised to the constant, so a request
                      the caller builds by hand serialises a valid discriminator.
"""

from __future__ import annotations

import argparse
import json
import keyword
import pathlib
import re
import sys
from typing import Any

ROOT = pathlib.Path(__file__).resolve().parent.parent
SPEC = ROOT / "spec" / "openapi.json"
MODELS_DIR = ROOT / "src" / "Vectorize.Hindsight.Sdk" / "Models"

# Properties whose anyOf is a genuine multi-shape union rather than a nullable
# scalar. Each maps to a hand-written type so the shape is explicit in the
# public API instead of collapsing to JsonElement.
UNION_TYPE_OVERRIDES: dict[tuple[str, str], str] = {
    ("MemoryItem", "content"): "HindsightMemoryContent",
    ("MemoryItem", "observation_scopes"): "HindsightObservationScopes",
    ("DocumentResponse", "observation_scopes"): "HindsightObservationScopes",
    ("TagGroupAndInput", "and"): "List<HindsightTagGroup>",
    ("TagGroupOrInput", "or"): "List<HindsightTagGroup>",
    ("TagGroupNotInput", "not"): "HindsightTagGroup",
    ("TagGroupAndOutput", "and"): "List<HindsightTagGroup>",
    ("TagGroupOrOutput", "or"): "List<HindsightTagGroup>",
    ("TagGroupNotOutput", "not"): "HindsightTagGroup",
}

# Schemas that are hand-written rather than generated, because their shape is a
# union the generator cannot express as a single class.
HAND_WRITTEN = {
    "HindsightTagGroup",
    "HindsightMemoryContent",
    "HindsightObservationScopes",
    "HindsightTextContentBlock",
    "HindsightImageContentBlock",
    "HindsightFileContentBlock",
    "HindsightAttachmentSource",
}

# Schemas the generator emits, but which are superseded by a hand-written type
# in the public API. They are still generated so every spec schema has a
# corresponding type, which keeps the coverage claim honest.
SKIP_GENERATION: set[str] = set()

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


def cs_name(schema_name: str) -> str:
    """`TagGroupAnd-Input` -> `TagGroupAndInput`; `Body_import_documents` -> `BodyImportDocuments`."""
    parts = re.split(r"[^0-9a-zA-Z]+", schema_name)
    return "".join(p[:1].upper() + p[1:] for p in parts if p)


def cs_property(name: str) -> str:
    """`bank_id` -> `BankId`; `llm_requests` -> `LlmRequests`."""
    parts = re.split(r"[^0-9a-zA-Z]+", name)
    out = "".join(p[:1].upper() + p[1:] for p in parts if p)
    if not out:
        out = "Value"
    if out[0].isdigit():
        out = "_" + out
    if out.lower() in CS_KEYWORDS:
        out = "@" + out
    return out


# Populated once the spec is loaded: schema names that are bare string enums.
ENUM_SCHEMAS: set[str] = set()


def ref_to_type(ref: str) -> str:
    """A reference to a bare enum schema maps to `string`, not to the class.

    The enum's values live in HindsightValues as constants. Typing the member as
    `string` is deliberate: a closed C# enum would throw on a value the server
    adds later, and these value sets are explicitly extensible.
    """
    name = ref.split("/")[-1]
    if name in ENUM_SCHEMAS:
        return "string"
    return cs_name(name)


def is_nullable(node: dict[str, Any]) -> bool:
    if not isinstance(node, dict):
        return False
    for key in ("anyOf", "oneOf"):
        if key in node:
            return any(o.get("type") == "null" for o in node[key])
    return False


def non_null_options(node: dict[str, Any]) -> list[dict[str, Any]]:
    for key in ("anyOf", "oneOf"):
        if key in node:
            return [o for o in node[key] if o.get("type") != "null"]
    return [node]


def map_type(node: dict[str, Any], schema_name: str, prop_name: str) -> tuple[str, bool]:
    """Returns (csharp_type, is_nullable)."""
    if node is True:
        return "JsonElement", True
    if not isinstance(node, dict):
        return "JsonElement", True

    override = UNION_TYPE_OVERRIDES.get((schema_name, prop_name))
    nullable = is_nullable(node)

    if override:
        return override, nullable

    if "$ref" in node:
        return ref_to_type(node["$ref"]), nullable

    options = non_null_options(node)
    if len(options) > 1:
        # A union we did not anticipate. JsonElement keeps it reachable rather
        # than silently dropping to one arm; --report flags these so they get
        # an explicit override instead of being forgotten.
        return "JsonElement", True

    node = options[0] if options else node
    if "$ref" in node:
        return ref_to_type(node["$ref"]), nullable

    t = node.get("type")
    if t == "array":
        inner, _ = map_type(node.get("items", {}), schema_name, prop_name)
        return f"List<{inner}>", nullable
    if t == "string":
        return "string", nullable
    if t == "integer":
        return ("long" if node.get("format") == "int64" else "int"), nullable
    if t == "number":
        return "double", nullable
    if t == "boolean":
        return "bool", nullable
    if t == "object" or "properties" in node:
        ap = node.get("additionalProperties")
        if ap is not None:
            if ap is True:
                return "Dictionary<string, JsonElement>", nullable
            inner, _ = map_type(ap, schema_name, prop_name)
            return f"Dictionary<string, {inner}>", nullable
        return "JsonElement", nullable
    return "JsonElement", True


def doc_comment(text: str, indent: str = "    ") -> list[str]:
    if not text:
        return []
    text = text.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")
    lines: list[str] = []
    # Collapse blank lines: an empty <summary> line is valid but noisy.
    paragraphs = [p.strip() for p in re.split(r"\n\s*\n", text) if p.strip()]
    lines.append(f"{indent}/// <summary>")
    for para in paragraphs:
        for raw in para.split("\n"):
            # `///` on a line whose content is empty is fine; keep it readable.
            lines.append(f"{indent}/// {raw.strip()}".rstrip())
        lines.append(f"{indent}///")
    if lines[-1].strip() == "///":
        lines.pop()
    lines.append(f"{indent}/// </summary>")
    return lines


def generate_schema(name: str, schema: dict[str, Any], report: list[str]) -> str:
    cls = cs_name(name)
    properties = schema.get("properties")
    base_desc = schema.get("description", "")

    # A bare enum is emitted as constants, not as a class of its own.
    if "enum" in schema:
        return ""

    # `properties` absent entirely means the shape is unconstrained; an empty
    # `properties` map means a deliberately empty object. The first cannot be a
    # class, the second can and should be.
    if properties is None:
        return ""

    out: list[str] = []
    out.append("// <auto-generated>")
    out.append("//   Generated by eng/generate_models.py from spec/openapi.json.")
    out.append("//   Do not edit by hand: re-run the generator instead. `--check` fails CI on drift.")
    out.append("// </auto-generated>")
    out.append("")
    out.append("#nullable enable")
    out.append("")
    out.append("using System.Text.Json;")
    out.append("using System.Text.Json.Serialization;")
    out.append("")
    out.append("namespace Vectorize.Hindsight.Sdk.Models;")
    out.append("")
    out.extend(doc_comment(base_desc, indent=""))
    out.append(f"public sealed partial class {cls}")
    out.append("{")

    required = set(schema.get("required", []))
    members: list[str] = []
    for prop_name, prop in schema.get("properties", {}).items():
        cs_prop = cs_property(prop_name)
        ctype, nullable = map_type(prop, name, prop_name)

        is_const = isinstance(prop, dict) and "const" in prop
        const_value = prop.get("const") if is_const else None

        # Optional reference-typed members are nullable so a server that omits
        # an optional field deserialises rather than throwing.
        is_value_type = ctype in {"int", "long", "double", "bool"} or (
            ctype.startswith("List<") is False and ctype.endswith("?")
        )
        needs_nullable = nullable or (prop_name not in required)
        if ctype in {"int", "long", "double", "bool"}:
            decl_type = ctype + "?" if needs_nullable else ctype
        elif ctype == "string":
            decl_type = "string?" if needs_nullable else "string"
        elif ctype == "JsonElement" or ctype == "Dictionary<string, JsonElement>":
            decl_type = ctype
        else:
            decl_type = ctype + "?" if needs_nullable and not ctype.startswith("List<") else ctype

        if is_const and const_value is not None:
            if ctype == "string":
                decl_type = "string"

        members.append("")
        members.extend(doc_comment(prop.get("description", ""), indent="    "))
        if is_const and const_value is not None:
            if ctype == "string":
                members.append(f'    [JsonPropertyName("{prop_name}")]')
                members.append(f'    public string {cs_prop} {{ get; set; }} = "{const_value}";')
            else:
                members.append(f'    [JsonPropertyName("{prop_name}")]')
                default_literal = "true" if const_value is True else "false" if const_value is False else str(const_value)
                members.append(f"    public {decl_type} {cs_prop} {{ get; set; }} = {default_literal};")
        else:
            # A required reference-typed member is emitted with C#'s `required`
            # modifier rather than a sentinel default. That gives compile-time
            # enforcement at every construction site for request DTOs, and it is
            # honest about the contract for response DTOs: the specification says
            # the server always sends the field, and a payload that omits one is
            # a contract violation worth surfacing rather than papering over with
            # an empty string.
            reference_typed = (
                ctype == "string"
                or ctype.startswith("List<")
                or ctype.startswith("Dictionary<")
                or ctype.endswith("?") is False
            )
            is_required = prop_name in required and reference_typed and not nullable

            default = ""
            if is_required:
                modifier = "required "
            else:
                modifier = ""
                if ctype.startswith("List<") or ctype.startswith("Dictionary<"):
                    default = " = [];"

            members.append(f'    [JsonPropertyName("{prop_name}")]')
            members.append(f"    public {modifier}{decl_type} {cs_prop} {{ get; set; }}{default}")

    out.extend(members)
    out.append("}")
    out.append("")

    report.append(f"  {name:52} -> {cls}{' (empty)' if not members else ''}")
    return "\n".join(out)



CONTEXT_FILE = ROOT / "src" / "Vectorize.Hindsight.Sdk" / "Http" / "HindsightJsonContext.g.cs"


def generate_context(schemas: dict[str, Any], enum_names: set[str]) -> str:
    """Emit the JsonSerializerContext registering every wire type.

    Generated rather than hand-maintained: the context is the thing that makes
    source generation complete, so a type that exists but is not registered is a
    real gap. Deriving it from the same schema set that produced the models keeps
    the two in step by construction.

    The unions and their converters are included explicitly, because they are
    hand-written and the generator cannot discover them from the spec alone.
    """
    types: list[str] = []
    for name in sorted(schemas):
        schema = schemas[name]
        if "enum" in schema:
            types.append("string")
            continue
        if schema.get("properties") is None:
            continue
        types.append(cs_name(name))

    # Hand-written types, declared here so the context is complete. The
    # generator cannot discover these from the spec: the unions come from
    # union-typed properties and the upload options from prose.
    types.extend(
        [
            "FileRetainRequest",
            "FileRetainMetadata",
            "HindsightMemoryContent",
            "HindsightContentBlock",
            "HindsightTagGroup",
            "HindsightTagGroupLeaf",
            "HindsightTextContentBlock",
            "HindsightImageContentBlock",
            "HindsightFileContentBlock",
            "HindsightAttachmentSource",
            "HindsightObservationScopes",
        ]
    )

    # Framework types the transport serializes or deserializes directly.
    types.extend(["JsonElement", "JsonDocument", "JsonNode"])

    seen: set[str] = set()
    ordered: list[str] = []
    for type_name in types:
        if type_name not in seen:
            seen.add(type_name)
            ordered.append(type_name)

    out = [
        "// <auto-generated>",
        "//   Generated by eng/generate_models.py from spec/openapi.json.",
        "//   Do not edit by hand: re-run the generator instead. `--check` fails CI on drift.",
        "// </auto-generated>",
        "",
        "#nullable enable",
        "",
        "using System.Text.Json;",
        "using System.Text.Json.Nodes;",
        "using System.Text.Json.Serialization;",
        "using Vectorize.Hindsight.Sdk.Errors;",
        "using Vectorize.Hindsight.Sdk.Models;",
        "",
        "namespace Vectorize.Hindsight.Sdk.Http;",
        "",
        "/// <summary>",
        "/// The source-generated serializer context covering every wire type.",
        "/// </summary>",
        "/// <remarks>",
        "/// Generated from the same schema set as the models, so the two cannot drift.",
        "/// Source generation is what makes the library trim- and AOT-safe: with no",
        "/// reflection fallback, a type reachable on the wire but missing here fails at",
        "/// build time rather than at the caller's first request.",
        "/// </remarks>",
        "[JsonSourceGenerationOptions(",
        "    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,",
        "    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,",
        "    GenerationMode = JsonSourceGenerationMode.Metadata)]",
    ]
    for type_name in ordered:
        out.append(f"[JsonSerializable(typeof({type_name}))]")
    out.append("[JsonSerializable(typeof(ValidationErrorDetail))]")
    out.append("internal sealed partial class HindsightJsonContext : JsonSerializerContext")
    out.append("{")
    out.append("}")

    # The runtime guard list: the resolver refuses to fall back to reflection.
    out.append("")
    out.append("/// <summary>")
    out.append("/// The wire types the serializer is allowed to handle.")
    out.append("/// </summary>")
    out.append("/// <remarks>")
    out.append("/// Used as a runtime guard so an unregistered type fails with a clear message")
    out.append("/// instead of a reflection fallback that would work in a test host and fail in")
    out.append("/// a trimmed deployment.")
    out.append("/// </remarks>")
    out.append("internal static class HindsightWireModels")
    out.append("{")
    out.append("    /// <summary>Every registered wire type.</summary>")
    out.append("    internal static readonly Type[] All =")
    out.append("    [")
    for type_name in ordered:
        out.append(f"        typeof({type_name}),")
    out.append("        typeof(ValidationErrorDetail),")
    out.append("    ];")
    out.append("}")
    out.append("")
    return "\n".join(out)


def generate_values(schemas: dict[str, Any]) -> str:
    """Emit a constants class for every enum the spec declares.

    Constants rather than a C# enum, for the reason given in ref_to_type: these
    sets are extensible, and a C# enum deserialised from an unknown string throws
    where a string member simply carries the new value through.
    """
    groups: list[tuple[str, str, list[str], str]] = []

    def add(owner: str, prop: str, values: list[Any], desc: str) -> None:
        if not values or not all(isinstance(v, str) for v in values):
            return
        groups.append((owner, prop, list(values), desc))

    for name in sorted(schemas):
        schema = schemas[name]
        if "enum" in schema:
            add(cs_name(name), "", schema["enum"], schema.get("description", ""))
        for prop, node in (schema.get("properties") or {}).items():
            if isinstance(node, dict) and "enum" in node:
                add(cs_name(name), cs_property(prop), node["enum"], node.get("description", ""))
            # Enums can also sit inside an anyOf arm.
            for arm in (node.get("anyOf") or []) if isinstance(node, dict) else []:
                if isinstance(arm, dict) and "enum" in arm:
                    add(cs_name(name), cs_property(prop), arm["enum"], node.get("description", ""))

    if not groups:
        return ""

    out = [
        "// <auto-generated>",
        "//   Generated by eng/generate_models.py from spec/openapi.json.",
        "//   Do not edit by hand: re-run the generator instead. `--check` fails CI on drift.",
        "// </auto-generated>",
        "",
        "#nullable enable",
        "",
        "namespace Vectorize.Hindsight.Sdk.Models;",
        "",
        "/// <summary>",
        "/// The closed value sets the specification declares.",
        "///",
        "/// These are constants rather than C# enums on purpose. The sets are",
        "/// extensible — <c>TagGroupLeaf.match</c> has already grown once — and an",
        "/// enum member deserialised from an unrecognised string throws, where the",
        "/// <c>string</c> member it replaced carries the new value through to the",
        "/// caller untouched.",
        "/// </summary>",
        "public static class HindsightValues",
        "{",
    ]
    for owner, prop, values, desc in groups:
        group_name = f"{owner}{prop}" if prop else owner
        for line in doc_comment(desc, indent="    "):
            out.append(line)
        out.append(f"    public static class {group_name}")
        out.append("    {")
        for v in values:
            const = re.sub(r"[^0-9a-zA-Z]+", "_", v).strip("_")
            const = re.sub(r"([a-z0-9])([A-Z])", r"\1_\2", const).upper()
            if not const or const[0].isdigit():
                const = "_" + const
            out.append(f'        /// <summary>Value <c>{v}</c>.</summary>')
            out.append(f'        public const string {const} = "{v}";')
        out.append("    }")
        out.append("")
    out.append("}")
    out.append("")
    return "\n".join(out)


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--check", action="store_true", help="fail if committed output differs")
    ap.add_argument("--report", action="store_true", help="print the mapping table")
    args = ap.parse_args()

    spec = json.loads(SPEC.read_text())
    schemas = spec["components"]["schemas"]

    ENUM_SCHEMAS.update(n for n, s in schemas.items() if "enum" in s)

    report: list[str] = []
    generated: dict[pathlib.Path, str] = {}
    skipped: list[str] = []

    for name in sorted(schemas):
        if name in SKIP_GENERATION:
            continue
        text = generate_schema(name, schemas[name], report)
        if not text:
            skipped.append(name)
            continue
        generated[MODELS_DIR / f"{cs_name(name)}.cs"] = text

    values = generate_values(schemas)
    if values:
        generated[MODELS_DIR / "HindsightValues.cs"] = values

    generated[CONTEXT_FILE] = generate_context(schemas, ENUM_SCHEMAS)

    if args.report:
        print(f"Generated {len(generated)} model files from {len(schemas)} schemas")
        print(f"Skipped (no properties, mapped to a hand-written or framework type): {len(skipped)}")
        for s in skipped:
            print(f"    {s}")
        print()
        print("\n".join(report))
        return 0

    if args.check:
        stale: list[str] = []
        for path, content in generated.items():
            if not path.exists() or path.read_text() != content:
                stale.append(str(path.relative_to(ROOT)))
        if stale:
            print("::error::Generated models are stale:", file=sys.stderr)
            for s in stale:
                print(f"    {s}", file=sys.stderr)
            return 1
        print(f"Generated models are up to date ({len(generated)} files).")
        return 0

    MODELS_DIR.mkdir(parents=True, exist_ok=True)
    for path, content in generated.items():
        path.write_text(content)
    print(f"Wrote {len(generated)} model files to {MODELS_DIR.relative_to(ROOT)}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
