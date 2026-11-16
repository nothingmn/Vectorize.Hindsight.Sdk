# Portfolio status — the two clocks, the wire, and the morning report

**What this is.** A portfolio is kept in view by two scheduled runs of the program-manager seat and by one wire
from each project. This document is the contract for all three: what fires when, what a project manager sends up,
what the seat reads, and what the human is given. The role protocols point here rather than restating it.

**The rule it serves.** The program manager is the human's **sole point of contact** across the portfolio. No
project manager reports to him: findings, statuses and questions arrive from them and leave through the seat, in
the seat's words.

Deployment choices live where the schedules are created, not here: the clocks below are the defaults, and a
deployment that moves them says so at the job, not in this file.

---

## 1. The wire — what a project manager sends up

While a project manager has work in flight it runs its own continuation schedule: it reads its state document,
drives the first unit that is not green, keeps its children alive, and appends one tick line. **That tick is
local** — it is written to the project's own state document and crosses no network.

Two kinds of message travel, and only two.

**Edges.** When the schedule turns on or off, one message each way:

- **ON** — carries the units it is now carrying, and the cadence it will tick at.
- **OFF** — carries what is terminal, and **what is not**. An off-edge carrying units still not terminal is a
  **finding, not a completion**: a run that stopped short and called it finished is exactly what this arrangement
  exists to catch, and it arrives without anybody asking for it.

**Interrupts** — three, and nothing else:

1. A gate stop at a Critical or Major finding.
2. A blocker that needs a decision only the human can make.
3. A contradiction between the project's own record and what it just measured.

Commits, progress, "still working" and completion do **not** travel; they ride the seat's next read. A project
that sends more than these is not reporting, it is chatting.

**Why the split.** The observer belongs where the work is. A watch that interrogates a session pays a peer call
every tick, and the same facts already sit in the tick line the project writes for itself; only the exceptions
need to cross a network.

---

## 2. The consume clock — default 03:00 local

It **reads**; it does not survey.

1. Open the register and walk its rows.
2. **Only rows whose last edge said ON are read.** A row that said OFF is parked, and polling a parked project is
   the waste this design exists to remove.
3. One cheap read per ON row, and it asks for exactly three things: the current revision and its time, the count of
   changed paths, and whether a build or an agent is running — with the instruction to take no action and commit
   nothing.
4. Record into the row: the reading, its time, the last tick seen, any unconsumed events.
5. **Dead-man check.** A row that said ON but has not ticked since the previous bump is a dead-man hit. Name it; do
   not nudge it. A schedule that stopped is an evidence gap, not a stalled project, and a scheduler reporting
   itself healthy is not proof its ticker is alive — the tick line is.

Silent on success. It speaks only to name a dead-man hit, an unconsumed event, or a row that contradicts itself.

---

## 3. The report clock — default 08:00 local

**It takes its own reading** rather than reporting the consume snapshot: hours of work can land in between, and a
"current status" that old is the one claim this protocol set forbids.

**Every day, one message either way.** When work happened overnight: one table per project with work in flight.
When it did not: one line saying so, and which projects are quiet.

### The table

**One table per in-flight project**, headed with the project, its state, and the time of last activity.

| Feature | Status | Where it's at | Time in | Estimated left |
|---|---|---|---|---|

**Status — four values, in plain words, no others.** They are the project record's own vocabulary translated, so
the report cannot drift from the record behind it:

- **Working** — proven on the real thing.
- **Built, not tried** — the code and a green build exist, and nobody has exercised it.
- **Waiting on you** — a decision only the human can make.
- **Stopped** — something else blocks it, named.

**Where it's at** — one sentence, human, saying what it means rather than what the code does. No identifiers, no
file paths, no test or class names.

**Time in — measured, never estimated.** The span of the row's own cited evidence: earliest to latest dated entry
or commit the row already rests on. It costs nothing, because the read already touches that row. A row citing
nothing dated renders a dash, not a guess.

**Estimated left — never a bare duration.** A row waiting on the human has no duration; it has a dependency, and a
number beside it would be a fiction. Render one of: `done`; `a session with <the rig that must be present>`; `your
decision`; or the named blocker and its owner. **The one permitted range** is a project-level line, and only where
the remaining work is homogeneous — with its basis stated in the same sentence, never asserted.

**Close with one line** naming the projects with nothing in flight.

---

## 4. What may never be claimed in either read

- **No averaging, no single colour.** Each project's condition is stated separately; an aggregate follows only what
  the individual readings support.
- **No progress converted into acceptance.** Built, delivered and clean are not verified and not complete.
- **No stale reading presented as current.** Its own time travels with it.
- **No duration invented for work gated on a decision.**
- **No health claim for a project without a documented route** — its evidence gap is shown instead.

---

## 5. What the register carries

Each row gains two fields, written on its next confirmed reading rather than by a bulk rewrite:

- **schedule state** — `ON <time> · cadence <n>m`, or `OFF <time> · terminal n/n`, or `OFF · outstanding <ids>`
  when the edge carried units that were not terminal.
- **last tick** — the time of the most recent tick line the project reported, with the reading that came with it.

The register stays the tracking surface. Tick lines stay in each project's own state document; the seat holds the
pointer, never a copy.
