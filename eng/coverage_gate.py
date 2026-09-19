#!/usr/bin/env python3
"""Coverage gate for Vectorize.Hindsight.Sdk.

Reads the Cobertura report(s) produced by
`dotnet test --collect:"XPlat Code Coverage"`, applies the documented exemption
list, and fails the build when coverage drops below the floors.

================================================================================
FLOORS  (LINE_FLOORS / BRANCH_FLOORS, directly below)
================================================================================

  Assembly                                   line floor   branch floor
  Vectorize.Hindsight.Sdk                       >= 90%        >= 60%
  Vectorize.Hindsight.Sdk.DependencyInjection   >= 95%        >= 80%

The Admin package sits at the core floor. Its uncovered lines are the platform branches a unit
test cannot reach without a specific OS state — killing an already-exited process, and the
kill-the-tree fallback on a platform that does not support it.

The DI package gets the higher floor because every one of its members is
reachable from a host, so there is no defensive guard left over. The core
library carries a small number of guards that cannot be reached through
HttpClient's documented behaviour; those are listed in EXEMPT_LINES below with a
reason, rather than excluded by a blanket attribute, so each one has to be
justified in review.

These numbers describe HAND-WRITTEN CODE ONLY. Generated serialization metadata,
the generated model layer, the generated operations, and source-generated logging
are all excluded (see GENERATED_PATH_MARKERS), because including them would
measure the compiler rather than the library, and the number would then move
whenever a generator changed.

MEASURED STATE AT THE TIME OF WRITING (informational, not a justification to
lower the floor): the unit suite is still being written and the measured
hand-written line coverage is well below the floors above, so this gate is
expected to FAIL until that work lands. That is the correct outcome: the floors
are the target, and they are not to be lowered to make a build pass. Raise them
as coverage rises; if a floor ever has to come down, record why in the commit
message rather than silently editing the number.

================================================================================
EXEMPT LINES  (EXEMPT_LINES, directly below)
================================================================================

A line that cannot be reached — a defensive guard against a state the framework
never presents — is excluded by listing its line number and a reason that would
survive review. Nothing is excluded by a blanket `[ExcludeFromCodeCoverage]`
attribute, so every exemption is visible here and has to be justified.

================================================================================
TEST-COUNT FLOOR  (MIN_TEST_COUNT, directly below)
================================================================================

`dotnet test` exits 0 when a test project builds but discovers ZERO tests — this
was observed on this repository, where two projects reported "No test is
available" and the runner still returned exit code 0. A coverage gate alone does
not catch that (the report is simply all-zero, or empty), so the gate also reads
the TRX result files and fails when the total number of detected tests is below
MIN_TEST_COUNT. If no TRX file is found the count cannot be verified and the gate
fails, because "we could not check" is not "the suite ran".

Usage:
    python3 eng/coverage_gate.py
    python3 eng/coverage_gate.py 'artifacts/test-results/**/coverage.cobertura.xml'
    python3 eng/coverage_gate.py '<coverage glob>' '<trx glob>'

The TRX glob defaults to `**/*.trx` under the directory that holds the coverage
reports, and can be overridden with the HINDSIGHT_TRX_GLOB environment variable.
"""

from __future__ import annotations

import glob
import hashlib
import os
import re
import sys
import xml.etree.ElementTree as ET
from collections import defaultdict
from pathlib import Path

# ---------------------------------------------------------------------------
# Floors. Keep these honest: raise them when coverage rises, and never lower one
# to make a build pass without recording why.
# ---------------------------------------------------------------------------
LINE_FLOORS = {
    "Vectorize.Hindsight.Sdk": 90.0,
    "Vectorize.Hindsight.Sdk.Admin": 90.0,
    "Vectorize.Hindsight.Sdk.DependencyInjection": 95.0,
}

BRANCH_FLOORS = {
    "Vectorize.Hindsight.Sdk": 60.0,
    "Vectorize.Hindsight.Sdk.Admin": 60.0,
    "Vectorize.Hindsight.Sdk.DependencyInjection": 80.0,
}

# A run that reports fewer than this many tests is treated as a broken run rather
# than a passing one. Raise it as the suite grows.
MIN_TEST_COUNT = 20

# Lines that cannot be reached through the library's public surface or through
# HttpClient's documented behaviour. Each entry needs a reason, and a reason that
# would survive review. Keys are matched against the report's class filename by
# exact path, then by suffix, so both "Http/File.cs" and "/Http/File.cs" work.
EXEMPT_LINES = {
    "Http/HttpHindsightTransport.cs": {
        247: "HttpResponseMessage.Content never returns null; the framework substitutes "
        "EmptyContent, so this guard against a null body is unreachable through the "
        "public API and exists only in case that contract changes.",
        271: "Same framework guarantee as the body-read guard above: "
        "HttpResponseMessage.Content is never null, so the stream guard is unreachable.",
    },
}

# Path fragments identifying generated code. The generated model layer and the
# generated operations are not hand-written, and neither is the source-generated
# serialization metadata or the source-generated logging methods. Counting them
# would measure the compiler rather than the library. Note the two forms that
# matter on this repository:
#   * the model layer is Models/*.cs — there is NO .g.cs suffix on those files,
#     so the directory marker is what excludes them;
#   * the operations are Operations/*.g.cs;
#   * generated metadata and logging land under obj/.
# Filenames in the report are relative to the project directory and carry no
# leading slash, so they are normalised with a leading slash before matching.
GENERATED_PATH_MARKERS = (
    "/obj/",
    ".g.cs",
    "/Models/",
    "/Operations/",
    "Microsoft.Extensions.Logging.Generators/",
    "System.Text.Json.SourceGeneration/",
)

DEFAULT_REPORT_GLOB = "artifacts/test-results/**/coverage.cobertura.xml"
DEFAULT_TRX_GLOB = "**/*.trx"


def normalize(filename: str) -> str:
    """Normalises a report filename to a leading-slash, forward-slash path."""
    return "/" + (filename or "").replace("\\", "/")


def canonical_filename(filename: str, package: str) -> str:
    """Collapses the two filename shapes this solution produces into one.

    The two test projects are instrumented with different source roots, so the
    SAME file is reported two ways:

        Tests            -> "Http/HindsightRetryPolicy.cs"
        IntegrationTests -> "Vectorize.Hindsight.Sdk/Http/HindsightRetryPolicy.cs"

    Without collapsing them, one physical line becomes two records and is counted
    twice. Stripping a leading "<assembly>/" segment keys both to the same line.
    """
    normalized = normalize(filename)
    prefix = f"/{package}/"

    if package and normalized.startswith(prefix):
        return "/" + normalized[len(prefix):]

    return normalized


def is_generated(normalized_filename: str) -> bool:
    return any(marker in normalized_filename for marker in GENERATED_PATH_MARKERS)


_EXEMPT_SUFFIXES = {"/" + key.lstrip("/"): value for key, value in EXEMPT_LINES.items()}


def exempt_lines_for(normalized_filename: str) -> dict[int, str]:
    """Returns the exemption map for a file, matching by exact path or suffix."""
    if normalized_filename in EXEMPT_LINES:
        return EXEMPT_LINES[normalized_filename]

    if normalized_filename in _EXEMPT_SUFFIXES:
        return _EXEMPT_SUFFIXES[normalized_filename]

    for suffix, value in _EXEMPT_SUFFIXES.items():
        if normalized_filename.endswith(suffix):
            return value

    return {}


def find_reports(pattern: str) -> list[Path]:
    """Returns every coverage report matching the pattern, oldest first."""
    matches = sorted(glob.glob(pattern, recursive=True))

    if not matches:
        print(f"::error::No coverage report found matching {pattern!r}")
        sys.exit(1)

    return [Path(m) for m in matches]


def merge_reports(reports: list[Path]) -> dict[str, dict[tuple[str, int], list[int]]]:
    """Merges the reports into one record set, per package.

    Why merge rather than pick the most recent: this solution has TWO test
    projects, so `dotnet test` writes TWO coverage reports, one per project.
    Picking only the most recent would measure whichever project happened to
    finish last and silently discard the other project's contribution.

    The `trx` logger ALSO copies each report under an `In/<host>/` directory, so
    the same report can be seen twice. Identical reports are de-duplicated by
    content hash, and the merge keeps the best hit count per (file, line), so a
    duplicate can never inflate the total. Filenames are canonicalised too,
    because the two test projects are instrumented with different source roots
    and would otherwise key the same physical line twice.

    With a single report — the usual case, and the sibling project's case — this
    is identical to using that one report.

    Each record is [hits, branches_covered, branches_total].
    """
    records: dict[str, dict[tuple[str, int], list[int]]] = defaultdict(dict)
    seen_digests: set[str] = set()

    for report in reports:
        digest = hashlib.sha256(report.read_bytes()).hexdigest()
        if digest in seen_digests:
            print(f"    (skipping duplicate report: {report})")
            continue

        seen_digests.add(digest)
        root = ET.parse(report).getroot()

        for package in root.iter("package"):
            name = package.get("name") or "(unnamed)"

            # Touch the entry so an assembly that reports no measurable
            # hand-written lines (for example a package with no tests yet) still
            # appears as a row rather than vanishing.
            records.setdefault(name, {})

            for cls in package.iter("class"):
                filename = canonical_filename(cls.get("filename") or "", name)

                if is_generated(filename):
                    continue

                # `./lines/line` is the class-level, de-duplicated line set.
                # `iter('line')` would also walk the per-method copies and count
                # every instrumented line twice.
                for line in cls.findall("./lines/line"):
                    number = int(line.get("number") or 0)
                    hits = int(line.get("hits") or 0)

                    branches_covered = 0
                    branches_total = 0
                    condition = line.get("condition-coverage")
                    if condition:
                        match = re.match(r"\d+% \((\d+)/(\d+)\)", condition)
                        if match:
                            branches_covered = int(match.group(1))
                            branches_total = int(match.group(2))

                    key = (filename, number)
                    existing = records[name].get(key)
                    if existing is None:
                        records[name][key] = [hits, branches_covered, branches_total]
                    else:
                        existing[0] = max(existing[0], hits)
                        existing[1] = max(existing[1], branches_covered)
                        existing[2] = max(existing[2], branches_total)

    return records


def count_package(records: dict[tuple[str, int], list[int]]) -> tuple[int, int, int, int, int]:
    """Returns (covered, total, branch_covered, branch_total, exempted) lines."""
    covered = 0
    total = 0
    exempted = 0
    branch_covered = 0
    branch_total = 0

    for (filename, number), (hits, branches_c, branches_t) in records.items():
        if hits > 0:
            covered += 1
            total += 1
            branch_covered += branches_c
            branch_total += branches_t
            continue

        if number in exempt_lines_for(filename):
            # Unreachable by construction, so its branches are unreachable too.
            exempted += 1
            continue

        total += 1
        branch_covered += branches_c
        branch_total += branches_t

    return covered, total, branch_covered, branch_total, exempted


def find_trx_files(reports: list[Path], trx_glob: str) -> list[Path]:
    """Finds the TRX result files that accompany the coverage reports."""
    override = os.environ.get("HINDSIGHT_TRX_GLOB")
    if override:
        return [Path(p) for p in sorted(glob.glob(override, recursive=True))]

    roots = {report.parent.parent for report in reports}

    found: list[Path] = []
    for root in roots:
        if root.is_dir():
            found.extend(sorted(root.glob(trx_glob)))

    return sorted(set(found))


def count_detected_tests(trx_files: list[Path]) -> int:
    """Sums the `total` counter across every TRX result file.

    The TRX document declares the VisualStudio TeamTest XML namespace, so elements
    carry a `{namespace}Counters` tag rather than a bare `Counters`. Matching on
    the tag name with `endswith` is what makes this work regardless of the exact
    namespace the runner emits.
    """
    total = 0

    for trx in trx_files:
        try:
            root = ET.parse(trx).getroot()
        except ET.ParseError as error:
            print(f"::warning::Could not parse TRX file {trx}: {error}")
            continue

        for element in root.iter():
            if isinstance(element.tag, str) and element.tag.endswith("Counters"):
                total += int(element.get("total") or 0)

    return total


def main() -> int:
    pattern = sys.argv[1] if len(sys.argv) > 1 else DEFAULT_REPORT_GLOB
    trx_glob = sys.argv[2] if len(sys.argv) > 2 else DEFAULT_TRX_GLOB

    reports = find_reports(pattern)

    print(f"Coverage report(s) matched: {len(reports)}")
    for report in reports:
        print(f"    {report}")

    package_records = merge_reports(reports)

    failures: list[str] = []

    print()
    print(f"{'assembly':44s} {'lines':>11s} {'line %':>8s} {'branch %':>9s}")
    print("-" * 76)

    exempted_total = 0

    for name in sorted(package_records):
        covered, total, branch_c, branch_t, exempted = count_package(package_records[name])
        exempted_total += exempted

        line_pct = (covered / total * 100.0) if total else 0.0
        branch_pct = (branch_c / branch_t * 100.0) if branch_t else 0.0

        print(f"{name:44s} {covered:5d}/{total:<5d} {line_pct:7.2f}% {branch_pct:8.2f}%")

        if total == 0:
            print(
                f"::warning::{name}: no measurable lines after excluding generated code; "
                "the line floor was not applied."
            )
            continue

        if name in LINE_FLOORS and line_pct < LINE_FLOORS[name]:
            failures.append(
                f"{name}: line coverage {line_pct:.2f}% is below the {LINE_FLOORS[name]:.2f}% floor"
            )

        if name in BRANCH_FLOORS and branch_pct < BRANCH_FLOORS[name]:
            failures.append(
                f"{name}: branch coverage {branch_pct:.2f}% is below the {BRANCH_FLOORS[name]:.2f}% floor"
            )

    print()
    print(f"Exempted {exempted_total} documented unreachable line(s).")

    # A build that discovers no tests still exits 0, so the test count is checked
    # here rather than trusted.
    trx_files = find_trx_files(reports, trx_glob)
    if not trx_files:
        failures.append(
            "no TRX result file was found, so the number of detected tests could not be "
            "verified; run dotnet test with --logger \"trx\""
        )
        print("Detected tests: unverified (no TRX file found)")
    else:
        detected = count_detected_tests(trx_files)
        print(f"Detected test count: {detected} (from {len(trx_files)} TRX file(s))")
        if detected < MIN_TEST_COUNT:
            failures.append(
                f"only {detected} test(s) were detected, below the minimum of {MIN_TEST_COUNT}; "
                "a run that discovers no tests still exits 0, so this is treated as a broken run"
            )

    print()

    if failures:
        for failure in failures:
            print(f"::error::{failure}")

        print(f"Coverage gate FAILED ({len(failures)} issue(s)).")
        return 1

    print("Coverage gate passed.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
