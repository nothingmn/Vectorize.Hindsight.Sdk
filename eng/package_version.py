#!/usr/bin/env python3
"""Print the version this solution will build, read from the single place it is declared.

`Directory.Build.props` holds `VersionPrefix`, and `global.json` pins the SDK. Both are
read here rather than duplicated, so a release workflow cannot disagree with the build
about what version was produced — the failure mode where a tag says one thing and the
package says another.

Usage:
    python3 eng/package_version.py                     # 0.1.0
    python3 eng/package_version.py --with-suffix ci    # 0.1.0-ci
    python3 eng/package_version.py --sdk               # the pinned SDK line
"""

from __future__ import annotations

import argparse
import os
import re
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
PROPS = ROOT / "Directory.Build.props"
GLOBAL = ROOT / "global.json"


def version_prefix() -> str:
    """Reads VersionPrefix, failing loudly rather than inventing a default."""
    if not PROPS.exists():
        raise SystemExit(f"::error::{PROPS} not found; cannot determine the version.")

    try:
        root = ET.parse(PROPS).getroot()
    except ET.ParseError as ex:
        raise SystemExit(f"::error::{PROPS} is not valid XML: {ex}") from ex

    for property_group in root.findall("PropertyGroup"):
        node = property_group.find("VersionPrefix")
        if node is not None and (node.text or "").strip():
            return node.text.strip()

    raise SystemExit(f"::error::{PROPS} declares no VersionPrefix; the version must come from one place.")


def sdk_version() -> str:
    """Reads the pinned SDK version from global.json."""
    if not GLOBAL.exists():
        raise SystemExit(f"::error::{GLOBAL} not found; the SDK line must be pinned.")

    import json

    try:
        data = json.loads(GLOBAL.read_text())
    except json.JSONDecodeError as ex:
        raise SystemExit(f"::error::{GLOBAL} is not valid JSON: {ex}") from ex

    version = (data.get("sdk") or {}).get("version")
    if not isinstance(version, str) or not version:
        raise SystemExit(f"::error::{GLOBAL} declares no sdk.version.")

    return version


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--with-suffix", default=None, help="Append a pre-release suffix, e.g. 'ci'.")
    parser.add_argument("--sdk", action="store_true", help="Print the pinned SDK version instead.")
    parser.add_argument("--patch", action="store_true", help="Print only the patch component.")
    args = parser.parse_args()

    if args.sdk:
        print(sdk_version())
        return 0

    version = version_prefix()

    if args.patch:
        match = re.match(r"^(\d+)\.(\d+)\.(\d+)", version)
        if not match:
            raise SystemExit(f"::error::VersionPrefix {version!r} is not a three-part semantic version.")
        print(match.group(3))
        return 0

    if args.with_suffix:
        # A build number keeps successive CI packages distinguishable without changing
        # the version a release would use.
        suffix = args.with_suffix
        run = os.environ.get("GITHUB_RUN_NUMBER")
        print(f"{version}-{suffix}.{run}" if run else f"{version}-{suffix}")
        return 0

    print(version)
    return 0


if __name__ == "__main__":
    sys.exit(main())
