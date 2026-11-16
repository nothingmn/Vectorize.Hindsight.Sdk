# HANDOFF — moving a body of work to a fresh agent

A handoff is how one role ends its part of a body of work and another begins. This file says what a handoff
must contain, so a fresh agent can start without a chat history, a briefing, or a colleague's memory.

Adapted from a handoff protocol another project developed for its own agents, re-pointed at this
repository's own records. Nothing here names that project: its document paths and decision ids were removed
in the adaptation, and the record locations this file cites are the ones THIS repository maintains.

## The rule that matters most

> **A handoff must stand on its own.** The incoming agent gets the path to the handoff document and nothing
> else — no summary, no chat history, no "as we discussed". If the handoff is not sufficient by itself, it is
> not finished.

Two consequences, both non-negotiable:

- **Unambiguous paths only.** Never "the requirements doc" or "the plan folder". The incoming agent has no working
  memory of where anything is. Name them relative to the repository root (`docs/…`, `scripts/…`) — an absolute host
  path in a tracked document fails this repository's own gate, and the reader is already inside the checkout. Where a
  path is outside the repository (a scratch artefact, a log), say what it is and how to regenerate it instead.
- **Never claim something is verified unless it was actually run.** Say what you ran, what it returned, and when.
  A handoff that overstates the state of the world is worse than no handoff, because the incoming agent will
  build on top of it.

## When a handoff happens here

1. **Requirements → plan.** The product owner's locked set goes to the software architect. The handoff is the
   register, the acceptance clauses, the addendum, and the settled decisions — not a summary of them.
2. **Plan → build.** The architect's plan goes to the project manager and the builders. The handoff is the plan
   directory plus the milestone record that authorises the first wave; the plan alone authorises nothing.
3. **Build → fix loop.** Findings from a review or a gate go back to the role that owns the artefact. The handoff
   is the finding list with file and line citations, never a paraphrase of what the reviewer "felt".

## What every handoff must contain

Write these sections in this order. Keep each one short and concrete.

**Header block** — a small table: the type of handoff, the date and time, the role writing it, the role
receiving it, the status, the body of work, the repo path, the branch, and the current commit.

**1. Read this first.** Two or three sentences: what this body of work is, where it stands, and what the incoming
agent is about to do. Assume they know nothing about it.

**2. Where everything is.** The absolute paths of the authoritative documents — requirements, acceptance,
decisions, plan, evidence — in the order they must be read after this one.

**3. What is locked and what is not.** Requirements and design are either signed off or they are not. Say which.
If something is still open, say so here rather than burying it in a later section.

**4. State of the world — verified.** What is actually true right now, and how you know: for each claim, the
command you ran and what it returned. Include the live system's state where it matters, and test numbers with
their pass/fail/skip counts.

**5. State of the world — not verified.** The honest other half. What you did not check, could not check, and
what you are assuming rather than knowing. **This section is mandatory, and emptiness is a claim in itself.**

**6. What to do next.** The incoming agent's first actions, in order, specific enough to start without asking
anything.

**7. Open questions for the human.** Decisions still owed — one at a time, each with a recommendation. If there
are none, say so explicitly.

**8. Do not.** The guards: things that look reasonable and are wrong, an approach already ruled out, a file not
to touch.

**9. Traps.** What cost you time and would cost the next agent the same — wrong turns, misleading logs, commands
that lie, environment quirks.

## Housekeeping before you hand over

- Commit and push your own work, or say plainly that it is uncommitted and where it sits.
- Leave the tree in the state your handoff describes. A handoff that says "clean" over a dirty tree is a trap in
  section 9 that you wrote yourself.
- Name every artefact you created, by absolute path, including the ones you consider obvious.

## Where the records live

- `docs/STATUS.md` — the running state of the whole project, newest section first.
- `docs/08-evidence-log.md` — durable evidence entries; a claim that matters gets one.
- `docs/manifest.yaml` — every document with its id, status and a one-line summary.
- `docs/decisions/` — settled decisions. Do not re-litigate them in a handoff; cite them.
- The body of work's own folder — requirements, acceptance and plan for that body, and nothing else.
