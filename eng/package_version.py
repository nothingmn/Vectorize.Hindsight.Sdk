#!/usr/bin/env python3
"""Resolve the version a build should carry, from the declared prefix and git.

One place declares the version: `VersionPrefix` in `Directory.Build.props`. Everything else is
derived, so a workflow cannot disagree with the build about what was produced — the failure mode
where a release page advertises one version and the package inside contains another.

SemVer
------
`VersionPrefix` is a three-part semantic version. The increment (major/minor/patch) is a **human
decision** — a commit graph cannot tell you whether the next release is a patch or a breaking change
— so it is *declared* here, and the build version is derived from it mechanically:

    HEAD is exactly on tag `v<prefix>`   ->  `<prefix>`             a release
    HEAD is past that tag, or untagged   ->  `<prefix>-<sha9>`      a pre-release candidate

A pre-release identifier sorts *before* the version it precedes, so CI artifacts sort below the
release they lead up to. That is the correct order: an untagged build of 0.2.0 is a candidate for
0.2.0, not a replacement for it.

The commit hash as the final segment
------------------------------------
The first nine hex characters of the commit are the last segment of the version, so every CI
artifact is uniquely identifiable and traceable to a commit without a lookup.

It is a **pre-release identifier**, not a fourth numeric segment, because that is the only form
NuGet accepts. Verified by probing `dotnet pack` against .NET 10:

    `0.1.0.abc123456`   REJECTED — "is not a valid version string". The fourth segment of a
                        four-part version must be numeric, and a hex hash contains letters.
    `0.1.0.65535`       REJECTED — the fourth segment is a UInt16; 65535 overflows it, so even a
                        purely numeric 5-digit hash would not fit.
    `0.1.0+gabc123456`  accepted, but the artifact is `X.0.1.0.nupkg`: NuGet strips build metadata
                        from the identity, so the hash would not appear in the package name.
    `0.1.0-abc123456`   ACCEPTED — artifact `X.0.1.0-abc123456.nupkg`. The hash is the final
                        segment and is visible in the package name.

So the hash goes in the pre-release position. Nothing else survives.

Usage
-----
    python3 eng/package_version.py            # 0.1.0-13ef01127   the build version
    python3 eng/package_version.py --prefix   # 0.1.0             the declared prefix
    python3 eng/package_version.py --release  # 0.1.0             bare, for a tag build
    python3 eng/package_version.py --sha      # 13ef01127         just the hash segment
    python3 eng/package_version.py --patch    # 0                 just the patch component
    python3 eng/package_version.py --sdk      # the pinned SDK from global.json
"""

from __future__ import annotations

import argparse
import json
import os
import re
import subprocess
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
PROPS = ROOT / "Directory.Build.props"
GLOBAL = ROOT / "global.json"

# A pre-release identifier: ASCII alphanumerics and hyphens, dot-separated.
PRERELEASE_SEGMENT = re.compile(r"^[0-9A-Za-z-]+$")

SEMVER = re.compile(r"^(?P<major>\d+)\.(?P<minor>\d+)\.(?P<patch>\d+)$")


def git(*args: str) -> tuple[int, str]:
    """Run a git command, returning its exit code and stripped stdout."""
    try:
        result = subprocess.run(
            ["git", *args],
            cwd=ROOT,
            capture_output=True,
            text=True,
            check=False,
        )
    except FileNotFoundError:
        return 127, ""

    return result.returncode, result.stdout.strip()


def version_prefix() -> str:
    """Read VersionPrefix, failing loudly rather than inventing a default."""
    if not PROPS.exists():
        raise SystemExit(f"::error::{PROPS} not found; cannot determine the version.")

    try:
        root = ET.parse(PROPS).getroot()
    except ET.ParseError as ex:
        raise SystemExit(f"::error::{PROPS} is not valid XML: {ex}") from ex

    for property_group in root.findall("PropertyGroup"):
        node = property_group.find("VersionPrefix")
        if node is not None and (node.text or "").strip():
            prefix = (node.text or "").strip()
            if not SEMVER.match(prefix):
                raise SystemExit(
                    f"::error::VersionPrefix {prefix!r} is not a three-part semantic version. "
                    "The build version is derived from it, so it must be MAJOR.MINOR.PATCH."
                )
            return prefix

    raise SystemExit(
        f"::error::{PROPS} declares no VersionPrefix; the version must come from one place."
    )


def sdk_version() -> str:
    """Read the pinned SDK version from global.json."""
    if not GLOBAL.exists():
        raise SystemExit(f"::error::{GLOBAL} not found; the SDK line must be pinned.")

    try:
        data = json.loads(GLOBAL.read_text())
    except json.JSONDecodeError as ex:
        raise SystemExit(f"::error::{GLOBAL} is not valid JSON: {ex}") from ex

    version = (data.get("sdk") or {}).get("version")
    if not isinstance(version, str) or not version:
        raise SystemExit(f"::error::{GLOBAL} declares no sdk.version.")

    return version


def commit_sha() -> str:
    """The full commit hash, from Actions when available and from git otherwise."""
    # GITHUB_SHA is set by the runner and is the commit under test. Preferring it means a detached
    # or shallow checkout in CI cannot report a different commit than the one being built.
    from_env = os.environ.get("GITHUB_SHA", "").strip()
    if re.fullmatch(r"[0-9a-fA-F]{40}", from_env):
        return from_env.lower()

    code, out = git("rev-parse", "HEAD")
    if code != 0 or not out:
        raise SystemExit("::error::could not resolve the commit hash; git rev-parse HEAD failed.")

    return out.lower()


def sha_segment(length: int = 9) -> str:
    """The leading hex characters of the commit, as the final version segment."""
    segment = commit_sha()[:length]

    if not segment:
        raise SystemExit("::error::the commit hash is empty; cannot build a version.")

    if not PRERELEASE_SEGMENT.match(segment):
        raise SystemExit(
            f"::error::commit hash segment {segment!r} is not a valid pre-release identifier."
        )

    # A pre-release identifier that is *all* digits must not have a leading zero (SemVer 2.0).
    # A hash is hex, so this needs all nine characters to be digits AND the first to be zero —
    # vanishingly unlikely, but it would produce a version NuGet rejects, so it is caught here
    # with a clear message rather than deep in the pack step.
    if segment.isdigit() and segment.startswith("0"):
        raise SystemExit(
            f"::error::commit hash segment {segment!r} is all digits with a leading zero, which "
            "SemVer forbids in a numeric pre-release identifier. Re-run the commit, or pass "
            "-p:Version explicitly."
        )

    return segment


def is_tagged_release(prefix: str) -> bool:
    """True when HEAD carries the tag v<prefix>, making this a release rather than a candidate."""
    code, out = git("describe", "--tags", "--exact-match", "HEAD")
    if code != 0 or not out:
        return False

    return out.strip() == f"v{prefix}"


def latest_tag_prefix() -> str | None:
    """The highest reachable v<semver> tag, or None when the repository has no tags."""
    code, out = git("tag", "--list", "v*", "--sort=-v:refname")
    if code != 0 or not out:
        return None

    for line in out.splitlines():
        candidate = line.strip().lstrip("v")
        if SEMVER.match(candidate):
            return candidate

    return None


def assert_prefix_not_behind_a_release(prefix: str) -> None:
    """Refuse to build a version older than one already released.

    After `v0.2.0` is tagged, declaring `VersionPrefix = 0.1.0` would produce packages NuGet treats
    as older than what is already published, and it would overwrite nothing — it would simply be
    ignored by anyone resolving the package. That is a silent dead end, so it fails here instead.
    """
    released = latest_tag_prefix()
    if released is None:
        return

    def key(version: str) -> tuple[int, int, int]:
        match = SEMVER.match(version)
        assert match is not None
        return (int(match.group(1)), int(match.group(2)), int(match.group(3)))

    if key(prefix) < key(released):
        raise SystemExit(
            f"::error::VersionPrefix is {prefix} but {released} has already been tagged. "
            "Bump VersionPrefix in Directory.Build.props to the next release."
        )


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Resolve the version this build should carry, from the declared prefix and git."
    )
    parser.add_argument("--prefix", action="store_true", help="Print the declared MAJOR.MINOR.PATCH.")
    parser.add_argument("--release", action="store_true", help="Print the bare release version.")
    parser.add_argument("--sha", action="store_true", help="Print only the commit-hash segment.")
    parser.add_argument("--patch", action="store_true", help="Print only the patch component.")
    parser.add_argument("--sdk", action="store_true", help="Print the pinned SDK from global.json.")
    parser.add_argument("--length", type=int, default=9, help="Hash characters to use (default 9).")
    args = parser.parse_args()

    if args.sdk:
        print(sdk_version())
        return 0

    prefix = version_prefix()

    if args.sha:
        print(sha_segment(args.length))
        return 0

    if args.prefix:
        print(prefix)
        return 0

    if args.patch:
        match = SEMVER.match(prefix)
        assert match is not None
        print(match.group("patch"))
        return 0

    if args.release:
        print(prefix)
        return 0

    assert_prefix_not_behind_a_release(prefix)

    if is_tagged_release(prefix):
        # The tag is the identity. Appending a hash here would make the release a pre-release of
        # itself, which is not what a tagged build means.
        print(prefix)
        return 0

    print(f"{prefix}-{sha_segment(args.length)}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
