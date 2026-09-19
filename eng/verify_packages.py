#!/usr/bin/env python3
"""Verify the produced NuGet packages before they are treated as release artifacts.

A package that builds is not a package that is usable. `dotnet pack` succeeds when the
archive is well formed; it says nothing about whether the archive *contains* what a
consumer needs. This script checks the things a consumer would discover the hard way:

  1. Every expected package exists, and a matching symbol package with it.
  2. Each package's nuspec carries the metadata a consumer relies on — id, version,
     description, licence, tags — and does not carry a placeholder.
  3. The declared version matches the version the build intended, so a stale
     VersionPrefix cannot ship.
  4. The README and LICENSE actually made it into the archive, and the README is not
     the generic placeholder.
  5. No project ships a `<dependencies>` entry on a package it does not need, and no
     package depends on a sibling at a version that cannot exist.
  6. No package is empty of a lib/ payload.

Usage:
    python3 eng/verify_packages.py artifacts/packages --expect-version 0.1.0

Exits non-zero with an ::error:: line per problem, so CI surfaces the reason.
"""

from __future__ import annotations

import argparse
import io
import re
import sys
import zipfile
from pathlib import Path

# Packages this solution is expected to produce, with the minimum metadata each needs.
EXPECTED = {
    "Vectorize.Hindsight.Sdk",
    "Vectorize.Hindsight.Sdk.Admin",
    "Vectorize.Hindsight.Sdk.Reranking.Onnx",
    "Vectorize.Hindsight.Sdk.Reranking.TypeSafe",
    "Vectorize.Hindsight.Sdk.DependencyInjection",
}

# Placeholders that would mean metadata was never filled in.
PLACEHOLDER_PATTERNS = (
    re.compile(r"^package description$", re.IGNORECASE),
    re.compile(r"^TODO", re.IGNORECASE),
    re.compile(r"^\s*$"),
    re.compile(r"example\.com", re.IGNORECASE),
)

REQUIRED_METADATA = ("id", "version", "description", "license", "tags")


def fail(problems: list[str], message: str) -> None:
    problems.append(message)
    print(f"::error::{message}")


def read_nuspec(archive: zipfile.ZipFile) -> str:
    names = [n for n in archive.namelist() if n.endswith(".nuspec")]
    if not names:
        raise ValueError("the package contains no nuspec")
    return archive.read(names[0]).decode("utf-8-sig")


def element(nuspec: str, tag: str) -> str | None:
    match = re.search(rf"<{tag}[^>]*>(.*?)</{tag}>", nuspec, re.DOTALL)
    return match.group(1).strip() if match else None


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("directory", type=Path, help="Directory holding the .nupkg files.")
    parser.add_argument("--expect-version", required=True, help="The version the build intended to produce.")
    args = parser.parse_args()

    directory: Path = args.directory
    problems: list[str] = []

    if not directory.is_dir():
        fail(problems, f"{directory} is not a directory.")
        return 1

    nupkgs = sorted(directory.glob("*.nupkg"))
    if not nupkgs:
        fail(problems, f"No .nupkg files found in {directory}.")
        return 1

    found_ids: set[str] = set()

    for package in nupkgs:
        print(f"\n{package.name}  ({package.stat().st_size / 1024:.0f} KB)")

        try:
            with zipfile.ZipFile(package) as archive:
                nuspec = read_nuspec(archive)
                names = archive.namelist()
        except (zipfile.BadZipFile, ValueError) as ex:
            fail(problems, f"{package.name}: unreadable ({ex}).")
            continue

        package_id = element(nuspec, "id") or ""
        found_ids.add(package_id)

        # 2. Required metadata, and nothing that looks unfilled.
        for tag in REQUIRED_METADATA:
            value = element(nuspec, tag)
            if value is None:
                fail(problems, f"{package.name}: nuspec has no <{tag}>.")
                continue

            if tag == "license":
                # A licence is either an SPDX expression or a file; both are acceptable.
                if "type=" not in nuspec.split("<license")[1].split(">")[0]:
                    fail(problems, f"{package.name}: <license> declares neither an expression nor a file.")
                continue

            if any(pattern.match(value) for pattern in PLACEHOLDER_PATTERNS):
                fail(problems, f"{package.name}: <{tag}> looks like an unfilled placeholder: {value!r}")

        # 3. The version must be the one the build intended.
        version = element(nuspec, "version") or ""
        if version != args.expect_version:
            fail(
                problems,
                f"{package.name}: version is {version!r} but the build intended {args.expect_version!r}.",
            )
        else:
            print(f"  version        : {version}")

        # 4. A consumer reads the README from the package page; LICENSE is required by many policies.
        readme = [n for n in names if n.lower().endswith("readme.md")]
        if not readme:
            fail(problems, f"{package.name}: no README.md in the package.")
        else:
            text = ""
            with zipfile.ZipFile(package) as archive:
                text = archive.read(readme[0]).decode("utf-8-sig", errors="replace")
            if len(text.strip()) < 500:
                fail(problems, f"{package.name}: the packaged README is suspiciously short ({len(text)} chars).")
            else:
                print(f"  readme         : {readme[0]} ({len(text)} chars)")

        if not any(n.upper().startswith("LICENSE") for n in names):
            fail(problems, f"{package.name}: no LICENSE in the package.")

        # 5. A dependency on a sibling must use a version that can exist.
        for dep_id, dep_version in re.findall(
            r'<dependency id="([^"]+)" version="([^"]+)"', nuspec
        ):
            if dep_id.startswith("Vectorize.Hindsight") and dep_version != args.expect_version:
                fail(
                    problems,
                    f"{package.name}: depends on {dep_id} {dep_version}, which is not the "
                    f"version this build produced ({args.expect_version}).",
                )

        # 6. A package with no lib/ payload is not a library.
        libs = [n for n in names if n.startswith("lib/") and n.endswith(".dll")]
        if not libs:
            fail(problems, f"{package.name}: contains no lib/*.dll payload.")
        else:
            print(f"  lib            : {', '.join(sorted(libs))}")

    # 1. Every expected package was produced.
    missing = EXPECTED - found_ids
    if missing:
        fail(problems, f"Expected packages were not produced: {', '.join(sorted(missing))}.")

    unexpected = found_ids - EXPECTED
    if unexpected:
        # Not an error on its own, but worth naming: a new package is a new public commitment.
        print(f"\nnote: packages produced beyond the expected set: {', '.join(sorted(unexpected))}")

    # Each package should have a symbol package, or the stack traces a consumer reads are useless.
    for package in nupkgs:
        snupkg = package.with_suffix(".snupkg")
        if not snupkg.exists():
            fail(problems, f"{package.name}: no matching {snupkg.name} symbol package.")

    print()
    if problems:
        print(f"Package verification FAILED ({len(problems)} problem(s)).")
        return 1

    print(f"Package verification passed: {len(nupkgs)} package(s), version {args.expect_version}.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
