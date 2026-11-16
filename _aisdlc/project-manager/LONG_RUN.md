# PROJECT MANAGER — running a long run

`STATIC.md` carries the rules for a long run. This file carries the mechanics: what to build, in what order,
and with which exact commands. It is a companion to the static core, not a replacement — where the two
disagree, the core wins.

**A long run** is work the human kicks off expecting it to finish without him watching: through the night,
past the model's usage window, past the end of this session. He will not answer questions mid-flight by
design.

Adapted 2026-10-22 from the protocol set another project developed for its own agents
(`_aisdlc/project-manager/LONG_RUN.md` there). The structure and the traps are that project's; the paths,
commands and end-to-end pass are this one's. Anything below that names a host path was re-verified against
this repository and this stack.

---

## 0. The kickoff checklist — fill it in before dispatching anything

Create the run's state document first (template in §1) and record each of these, with the command you ran
and its output. **The recorded check is the only evidence the step was not skipped**, and this checklist
exists because these six steps are the ones that get quietly dropped under time pressure.

```markdown
**Kickoff checks — <body of work>, <date/time>**
- [ ] 1. Continuation job created and enabled — job_id: ______  schedule: ______  next_run_at: ______
- [ ] 2. State document exists at the path the job prompt names — ______
- [ ] 3. Report contract agreed — routine segments are silent; a halt goes to the seat that dispatched this run, via ______
- [ ] 4. Worker brief template attached to the state doc, protocol-delivery lines included — ______
- [ ] 5. Deploy + verify commands written down (image tag, service, marker, control string) — ______
- [ ] 6. The real-path exercise defined (which tool, which role header, what proves it worked) — ______
```

If any line is blank, the run is not started. Creating the job *after* the first worker is dispatched is
the failure this checklist exists to catch: the session ends mid-wave and nothing resumes.

## 1. The state document — the run's only durable memory

A continuation can only resume what a file says. Nothing in a transcript survives, so the state document
is the run.

Put it beside the work: `docs/<body-of-work>/IMPLEMENTATION_STATE.md`. It carries:

- **Status line at the top**, stamped with time and evidence: `RUNNING` / `BLOCKED` / `COMPLETE`.
- **A unit table** — one row per unit of work, each in exactly one state:

  | State | Meaning |
  |---|---|
  | `not started` | no code written |
  | `code complete` | written, not yet compiled |
  | `compiles + unit green` | its own project builds and its own tests pass |
  | `integrated` | wired into the assembled path |
  | `QA passed` | verified at the boundary the claim needs |
  | `deployed` | live in the running stack |
  | `deployed + exercised` | driven through its real entry point after deploy |
  | `blocked` | cannot proceed — with the reason, the evidence, and who owns the answer |

- **A segment log** — append-only, one entry per continuation: timestamp, what was done, what changed,
  what is next. This is what makes "progress" checkable without replaying a conversation.
- **Parked decisions** — every escalation awaiting the human, newest last, each one line: the question,
  the options, the recommendation, what it blocks. **Anything parked here must not stall work that is not
  downstream of it.**

Update it *in the same segment that did the work*, before the segment ends. A segment that ends without
updating it has thrown its work away for the next one.

## 2. The continuation job

One recurring agent job, created at kickoff, whose prompt is **self-contained** — a fresh session with no
chat context reads it cold. It must:

- boot the role two-layer (static core first, then the protocol from the bank named in `_aisdlc/config.yaml`);
- read the state document and take the **first unit that is not green**;
- carry the run's build/deploy rules verbatim (below);
- update the state document before finishing;
- **pause itself when every unit is terminal**, so it does not keep firing against finished work;
- never ask a question and never wait for approval — park it in the state doc and keep going.

Prompt skeleton (fill the blanks; keep the shape):

```text
Resume the long-running implementation in the repository this job's workdir points at. Do NOT ask
questions and do not wait for approval — park anything you cannot decide in the state document and keep going.

Boot as PROJECT MANAGER: read _aisdlc/project-manager/STATIC.md first (it outranks everything else here),
including its long-run section, then _aisdlc/project-manager/LONG_RUN.md, then fetch the protocol:
  python3 _aisdlc/tool/aisdlc.py read -m project_manager_protocol
If the protocol cannot be read, say whether it was ABSENT, the store was UNREACHABLE, or it was EMPTY —
never invent one — and continue on the static core alone.

Read <body>/IMPLEMENTATION_STATE.md. It is authoritative; correct it where reality disagrees with it. Take
the first unit that is not green and drive it to `deployed + exercised`, then the next, until every unit is
terminal or blocked. Every sub-agent you spawn must be briefed per §3 with its own _aisdlc/<role>/STATIC.md
and protocol — a child told to "follow the AISDLC" carries nothing.

Build rules: ONE heavy dotnet at a time under $TMPDIR/hindsight-build.lock with --artifacts-path
$TMPDIR/artifacts/<unit>, then `dotnet build-server shutdown` afterwards. Triage test failures by set
difference against the committed baseline list, never by count.

Ship: build the image tagged with a candidate tag → code-present gate on raw DLL bytes with a control
string → promote to the pinned tag → deploy only the service you changed → verify readiness and
index_status. Correct what verification finds, redeploy, re-verify. Then exercise it on the real path (§6).

Append this segment to the state document's segment log, update the unit table, and pause this job
(cronjob_manage action=pause, job_id=<this job>) if every unit is terminal.

Report: silence. Your final response on a routine segment must be the single word QUIET. Only if the whole
run halts — no unit can proceed without the human — write a short plain-English report: what stopped, what
you already tried, what you need from him.
```

**Report contract.** Routine segments are silent and their output goes to `local`. A run that halts reports
**up to the seat that dispatched it** — never to the human, and never through a delivery target that reaches
him: where a program manager sits above this project the human hears from that seat alone, so a job told to
wake him directly has broken the channel rather than used it. The kickoff checklist records which seat a halt
goes to.

**Never rely on `deliver: origin`.** A job created from a CLI session delivers nowhere: the output is written
to the job's own record and nothing is sent. A job created from the WebUI has been observed carrying
`last_delivery_error: unknown platform 'webui'` — it runs and its output goes nowhere. Name the platform
explicitly, or accept silence and say that you have.

**Single-flight.** A cadence with segments that can run longer than the interval will eventually overlap. The
segment must take a run lock at the top — `flock -n $TMPDIR/hindsight-run.lock` — and exit quietly if it is
held. Two segments editing the same unit table is how a ledger loses rows.

**The tick line.** Every segment appends one line to the segment log: tick time, HEAD, the units still not
green, and what the segment started or stopped. That line is the whole of what the seat above consumes, and a
schedule with no tick line cannot be told apart from a dead one.

## 3. The worker brief — the protocol is delivered, not assumed

A delegated agent starts knowing nothing: no role, no protocol, no memory of this conversation. The brief
is the delivery, in this order:

1. **Its own static core path**, plus the line that it outranks everything else in the brief.
2. **The exact protocol command**: `python3 _aisdlc/tool/aisdlc.py read -m <role>_protocol` — and the
   requirement to report absent / unreachable / empty rather than invent one.
3. **The precedent read** for working seats only (`aisdlc.py recall -q "<the decision>"`), with the explicit
   instruction that the **reviewer seats must not run it** — their value is starting unbiased.
4. **Its hard rules restated as constraints**, not referenced: stay in your wave; invent no requirement;
   no two workers edit the same file; a design you cannot verify is unfinished; never make a test pass by
   weakening it.
5. **Evidence discipline** (`_aisdlc/code-standards.md`): a number carries the command that produced it; a
   `file:line` citation is not enough — paste the lines; an unpasteable claim is marked *unverified*.
6. **Rights by seat**: reviewer and research children are read-only and run no builds. A developer or QA
   child gets the build lock, scoped to its own project, with a test filter over its own area — and its
   **own test folder**, assigned before dispatch, because a shared test project collides on the first
   helper.
7. **What is out of bounds** — the accepted limitations and the settled decisions, named. A child not told
   what is closed spends its budget re-flagging it as a defect.
8. **One file it may write**, and the requirement to return the verdict plus a one-line-per-finding digest
   plus that path. Treat the returned summary as an index: read the file.
9. **Name the memory it used**, or say plainly that it used none — and if a recalled item changed what it did,
   which one and what it changed. An agent that is not asked will not say, and without that line the unit cannot
   be credited and the good behaviour is lost (`_aisdlc/learning-loop.md`).

Then verify the child before believing it: read the artefacts off disk, check their ownership (a child
writing in a sandbox can leave them root-owned), and **re-measure its load-bearing numbers yourself**. A
child's summary is a claim, not a result.

## 4. The build and test commands

```bash
cd "$(git rev-parse --show-toplevel)"
flock "$TMPDIR/hindsight-build.lock" bash -c '
  dotnet build docker/coordinator/RoslynLens.Coordinator.sln -c Release \
    --artifacts-path "$TMPDIR/artifacts/build"
'
echo "EXIT=$?"
dotnet build-server shutdown     # ALWAYS — a surviving compiler server can inherit the lock fd
```

- **Build OR test, never both**, and one heavy `dotnet` across the whole fleet at a time — including
  children. Scope a child's tests to its own area; the PM keeps the full-solution build and the broad suite
  for the wave boundary. A scoped green proves nothing about an entry point its project does not reference.
- **Filter by fully-qualified name, and prove something ran.** `--filter FullyQualifiedName~Token` — never a
  bare token that also matches an unrelated class. A run reporting `Total: 0`, writing no TRX, or printing
  nothing has executed nothing and is **not** a result (EV-0123).
- **Triage failures by name**, against a committed baseline list, never by count: a stale baseline invents
  both false new failures and false fixes. Read skip counts — a `Passed!` line with thirty silent skips is
  not evidence.
- The repository's own gates are cheap and run before any handoff: `scripts/doc-check.sh`,
  `scripts/guardrail-check.sh`, and `scripts/next-revision-plan-check.sh <plan-dir>`.

## 5. Ship, verify, correct

```bash
cd "$(git rev-parse --show-toplevel)"
git status --porcelain          # FIRST: the image builds from the working tree, not the commit
TAG=hindsight-coordinator:$(date +%Y%m%d-%H%M)
docker build -t "$TAG" docker/coordinator

# code-present gate: raw DLL bytes. UTF-16LE for string literals, UTF-8 for type names.
# Probe BOTH a marker that must be present AND a control string present in old and new —
# a gate with no control cannot fail.
docker run --rm --entrypoint sh "$TAG" -c "grep -c '<Marker>' /app/Coordinator.Host.dll"

docker tag "$TAG" hindsight-coordinator:<pinned-tag>
docker compose -p self --env-file .env --env-file instances/self.env up -d --force-recreate coordinator

docker inspect self-coordinator-1 --format 'health={{.State.Health.Status}} restarts={{.RestartCount}}'
docker logs --tail 80 self-coordinator-1 2>&1 | grep -iE 'fail|exception' | grep -v 'failCount'  # expect none
```

**Self-correction ladder**, per failed verification — and bounded, so a bad loop cannot eat the night:

1. Diagnose from the evidence, not from a guess — quote the log line or the failing assertion.
2. Fix, rebuild, redeploy, re-verify. Never re-tag a stale image and call it fixed.
3. **Same failure twice → stop fixing blind.** Capture the evidence, roll back to the last image that
   served, verify the rollback is healthy, mark the unit `blocked` with the diagnosis, and move to the next
   unit that does not depend on it.
4. **Never leave the stack down.** A failed deploy ends with either a verified new image or a verified
   rollback.

Then the record: the image tag, image id and source commit in `deps.lock.yaml`, and the `docs/STATUS.md`
section for the body of work moved to its deployed state. A release without its row is not complete.
`deployed` is not `verified` — say which one you have.

## 6. Exercise it on the real path

The deploy is not the finish line. Drive the deployed system the way the agent does, then read the logs.

```bash
# the coordinator refuses a connection that declares no role (ADR-0055, fail-closed)
curl -s -X POST http://127.0.0.1:8890/mcp -H 'x-coord-role: qa' \
  -H 'Content-Type: application/json' -H 'Accept: application/json, text/event-stream' \
  -d '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"probe","version":"1"}}}'
```

What "end to end" means, all four:

1. the request was accepted on the route (an `initialize` that succeeds, not one that is refused);
2. the tool call returned the answer shape the contract declares — parsed, not merely non-empty;
3. the logs show the handling path with no error severity tag;
4. the behaviour claimed is visible in the answer or its evidence block — not merely that *something*
   answered.

**Grep the severity tag, never a bare word.** Counting `exception|ERROR` matches `"error":null` in debug
payloads and hides the real ones.

Two traps specific to this stack, both measured:

- **A refusal can look like an empty success.** `baseline/harness/mcp_lite.py` sends no role header; the
  coordinator refuses, `tools()` returns `[]` and `call()` returns `{"isError": false, "text": ""}`. An empty
  answer is a candidate for a refusal, never for a pass — check the raw response before believing a zero.
- **The search server's path is `/zoekt/mcp`, not `/mcp`.** On `/mcp` it answers with zero tools and looks
  alive.

## 7. Closing the run

1. Every unit terminal — `deployed + exercised`, or `blocked` with reason, evidence and owner.
2. Continuation job paused, and its id recorded in the state document.
3. The lockfile row and the `docs/STATUS.md` section written; claims that reality has left behind corrected
   in place, not appended to.
4. The completion report written for the human: what now works that did not, what is deployed, what is still
   open and what would close it. **Plain English, no identifiers** — what it means for him, not what the code
   does.
5. Close the learning loop for the run: one classification per unit — `credit` where the work was right first
   time and a named memory item caused it, `gap` where the record should have had the answer, `correction` where
   an existing item was missed — then `POST /v1/default/banks/<bank>/consolidate` so the next generation of each
   role's protocol sees it. A credit entry that is never consolidated is a note in a drawer.

## 8. Traps that cost a night

- **A cron `script` field takes no arguments.** The scheduler resolves it as a path and passes nothing;
  `script: "x.py --cron"` looks up a file with that literal name and fails every tick. Cron-safe behaviour
  must be the default.
- **`gateway_running: false` in the job list is not proof the ticker is dead.** Check
  `~/.hermes/cron/ticker_heartbeat` and a recent `last_run_at` before reporting the schedule as broken.
- **A job whose `workdir` is a host-only path fails every tick** with `No such file or directory`.
- **A full scratch directory silently kills builds.** Set `TMPDIR` and `--artifacts-path` explicitly, and
  check free space before a fleet run.
- **A child's intermediate red run is not a failure.** A mid-flight red on a mutation demonstration (revert →
  red → restore) looks like a broken unit. Wait for its report, then verify the end state.
- **The image is built from the working tree, not from the commit.** Another session's uncommitted edit rides
  into your release. Read `git status` before you build; commit only your own paths.
- **`--no-build` lies after a failed build** — it reruns the previous binaries.
- **A checker that passes is not a review.** Both of this repository's gates passed a plan with two wrong
  counts and a drifted map in it. The gates check what they can see; the rest is somebody's job.
