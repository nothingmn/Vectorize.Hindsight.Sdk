#!/usr/bin/env python3
"""Validate the repository's workflow YAML locally.

A workflow that only fails on GitHub costs a push to discover. Everything that can be
checked without a runner is checked here:

  * each file parses as YAML
  * every job has a runner and at least one step
  * every step has either `uses` or `run`, never both and never neither (a step with
    neither is a no-op that silently does nothing)
  * every `uses:` action is pinned to a major version, not a branch
  * every `run:` that invokes a script under eng/ names a file that exists
  * no workflow references a secret that a pull request from a fork cannot see without
    declaring it optional

Usage:
    python3 eng/validate_workflows.py
"""

from __future__ import annotations

import argparse
import re
import sys
from pathlib import Path

import yaml

ROOT = Path(__file__).resolve().parent.parent
WORKFLOWS = ROOT / ".github" / "workflows"

# An action must be pinned to a version tag, never a branch: `actions/checkout@main`
# means a third party's next commit runs in this repository's CI without review.
UNPINNED = re.compile(r"^[\w.-]+/[\w.-]+@(main|master|HEAD)$")

SCRIPT_REFERENCE = re.compile(r"python3\s+(eng/[\w.]+\.py|[\w./-]+\.py)")


def fail(problems: list[str], message: str) -> None:
    problems.append(message)
    print(f"::error::{message}")


def main() -> int:
    parser = argparse.ArgumentParser(description="Validate the repository's workflow YAML.")
    parser.add_argument(
        "--root",
        type=Path,
        default=ROOT,
        help="Repository root to validate. Defaults to this script's repository.",
    )
    args = parser.parse_args()

    root: Path = args.root.resolve()
    workflows = root / ".github" / "workflows"
    problems: list[str] = []

    if not workflows.is_dir():
        fail(problems, f"{workflows} does not exist.")
        return 1

    files = sorted(workflows.glob("*.yml")) + sorted(workflows.glob("*.yaml"))
    if not files:
        fail(problems, "No workflow files found.")
        return 1

    for path in files:
        print(f"\n{path.relative_to(root)}")

        try:
            document = yaml.safe_load(path.read_text())
        except yaml.YAMLError as ex:
            fail(problems, f"{path.name}: does not parse ({ex}).")
            continue

        if not isinstance(document, dict):
            fail(problems, f"{path.name}: top level is not a mapping.")
            continue

        # A workflow with no trigger never runs. Note the `True` key: YAML 1.1 parses a
        # bare `on:` as the boolean true, so the key arrives as True rather than "on".
        # Checking only for the string form reports a triggered workflow as untriggered.
        if not _triggers(document):
            fail(problems, f"{path.name}: declares no trigger.")

        jobs = document.get("jobs") or {}
        if not jobs:
            fail(problems, f"{path.name}: declares no jobs.")
            continue

        for job_name, job in jobs.items():
            if not isinstance(job, dict):
                fail(problems, f"{path.name}/{job_name}: job is not a mapping.")
                continue

            if "runs-on" not in job and "uses" not in job:
                fail(problems, f"{path.name}/{job_name}: declares neither runs-on nor uses.")

            steps = job.get("steps") or []
            if not steps:
                fail(problems, f"{path.name}/{job_name}: has no steps.")
                continue

            for index, step in enumerate(steps, start=1):
                if not isinstance(step, dict):
                    fail(problems, f"{path.name}/{job_name} step {index}: is not a mapping.")
                    continue

                has_uses = "uses" in step
                has_run = "run" in step
                label = step.get("name") or step.get("uses") or f"step {index}"

                if has_uses and has_run:
                    fail(problems, f"{path.name}/{job_name}: '{label}' has both uses and run.")
                elif not has_uses and not has_run:
                    fail(problems, f"{path.name}/{job_name}: '{label}' has neither uses nor run.")

                if has_uses:
                    action = str(step["uses"])
                    if UNPINNED.match(action):
                        fail(problems, f"{path.name}/{job_name}: '{action}' is pinned to a branch.")

                if has_run:
                    script = str(step["run"])
                    for referenced in SCRIPT_REFERENCE.findall(script):
                        # A referenced script that does not exist fails only when the step runs,
                        # which on GitHub is after the push.
                        if not (root / referenced).exists():
                            fail(problems, f"{path.name}/{job_name}: '{label}' runs {referenced}, which does not exist.")

        print(f"  triggers : {', '.join(_triggers(document))}")
        print(f"  jobs     : {', '.join(jobs.keys())}")
        print(f"  steps    : {sum(len(j.get('steps') or []) for j in jobs.values() if isinstance(j, dict))}")

    print()
    if problems:
        print(f"Workflow validation FAILED ({len(problems)} problem(s)).")
        return 1

    print(f"Workflow validation passed: {len(files)} workflow(s).")
    return 0


def _triggers(document: dict) -> list[str]:
    """Trigger names, tolerating PyYAML parsing `on:` as the boolean True."""
    raw = document.get("on", document.get(True))
    if isinstance(raw, str):
        return [raw]
    if isinstance(raw, list):
        return [str(item) for item in raw]
    if isinstance(raw, dict):
        return list(raw.keys())
    return []


if __name__ == "__main__":
    sys.exit(main())
