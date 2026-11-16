# `_aisdlc` — the Agent SDLC operating protocols

> **Why this file is `README.md` and not `AGENTS.md`.** The protocol set this folder was adapted from names
> its index `AGENTS.md`. In this repository the write tools treat a file called `AGENTS.md` as protected
> agent-instruction content and refuse to create one, so the index is carried here as `README.md`. The
> wording that would **adopt** this folder — a section in **this repository's root `AGENTS.md`** telling a
> session to follow it — is a separate proposal owned by the parent, not part of this folder.

**What this folder is.** The operating protocols for **our own agents** as they run a software development
lifecycle in this repository. It is a **portable** set: nothing here is specific to the project it was
adapted from, and nothing here is specific to this repository except the two values in
`_aisdlc/config.yaml` and the record paths named below.

> **Status.** `_aisdlc/` holds the protocol set. A repository adopts it by saying so in its own agent
> contract. Until that wording lands, this folder is followed by reading it directly.

**Boot step.** An agent session: **(1)** identify its role; **(2)** read that role's `STATIC.md`;
**(3)** fetch its mental model from the bank named in `_aisdlc/config.yaml`; **(4)** run on both layers;
**(5)** then ask the human which area to work on.

## Two documents that apply to every role

- **`code-standards.md`** — the language conventions each technical role designs, builds and verifies against,
  and the two evidence rules: a number carries the command that produced it, and a hand-maintained document is
  proved intact after every edit. A role that writes code or a number is held to it.
- **`handoff.md`** — what a handoff to a fresh agent must contain, including the mandatory *state of the world —
  not verified* section. A handoff that is not sufficient by itself is not finished.

- **`learning-loop.md`** — the one rule that makes the memory worth having: a behaviour that worked is recorded
  *with the memory item that caused it*, so consolidation folds it into the bank and each role's protocol
  regenerates preferring it. With memory comes learning, but only if somebody writes down which memory did the
  work.

All three were adapted on 2026-10-22 from the protocol set another project developed for its own agents — the
learning loop there was a single hard rule in the project manager's static core, and this folder writes the
mechanism down beside it. They are derivations, like the rest of this folder.

## Configuration — `_aisdlc/config.yaml`

Two keys, and both are **deployment configuration**, not protocol:

| Key | What it sets |
|---|---|
| `bank` | the name of the memory bank these roles read from |
| `hindsight_url` | the base URL of the store that serves that bank |

A different project points at a different bank and a different store. **No other file in `_aisdlc/` names a
bank or a store address.** Every document says *"the bank named in `_aisdlc/config.yaml`"*; the only other
place a bank may be named is an explicit override — `--bank <name>` / `$AISDLC_BANK` on the CLI, with
`--url <url>` / `$AISDLC_HINDSIGHT_URL` for the address.

**How it works, in one paragraph.** Each role's operating protocol has **two layers**. The **static core**
is a short, version-controlled file in this folder — the rules that must never change: identity, approval
gates, escalation bounds, evidence discipline. The **adaptable bulk** is a **mental model** in the memory
bank named in `_aisdlc/config.yaml`: text the server generates from its stored instruction (`source_query`)
over that bank's accumulated memories of how that role's work actually went. The model **improves as the
organisation learns**, because every time it regenerates it is re-derived from evidence.

```
_aisdlc/
├─ README.md                      ← you are here (the folder's index)
├─ config.yaml                    ← the deployment's bank and store URL (the only place either is named)
├─ skills/get-mental-model/       ← the shared skill every role uses to read its model
├─ skills/recall-evidence/        ← the shared skill a working role uses to read precedent
├─ tool/                          ← the aisdlc CLI (read a protocol, recall precedent, edit a prompt, cache it)
├─ project-manager/   { STATIC.md, MODEL.md }
├─ product-owner/     { STATIC.md, MODEL.md }
├─ software-architect/{ STATIC.md, MODEL.md }
├─ developer/         { STATIC.md, MODEL.md }
├─ qa/                { STATIC.md, MODEL.md }
├─ research/          { STATIC.md, MODEL.md }
├─ product-documentation-reviewer/{ STATIC.md, MODEL.md }   ← independent reviewer of the PO's docs
└─ technical-architect-reviewer/ { STATIC.md, MODEL.md }    ← independent reviewer of the architect's design
```

**Every producing role has an independent reviewer.** The product owner's documentation is challenged by
the *product documentation reviewer*; the architect's design by the *technical architect reviewer*. Their
defining property is **deliberate context starvation** — they start with as little prior context as
possible, because bias is reduced by not inheriting the author's framing. That is a *different actor* from
the one that produced the work, which is why each has its own entry rather than a paragraph inside the
producing role's protocol.

## How to run a role

1. **Read the static core** — `_aisdlc/<role>/STATIC.md`. It is authoritative and it is not negotiable.
2. **Read the adaptable protocol** — use the shared skill (`skills/get-mental-model/`) or the CLI
   (`_aisdlc/tool/`) to fetch the current content of the model named in `_aisdlc/<role>/MODEL.md`, from the
   bank named in `_aisdlc/config.yaml`.
3. **Before committing to a course, read the record.** A **working** role asks the same bank what the
   organisation has already learned about work like this — past incidents, approaches that were rejected,
   the human's corrections — using `skills/recall-evidence/`. It uses what it finds to *choose its course*:
   the strongest option that completes its assigned work and that the evidence supports. The record informs
   the choice; it does not widen the job.
   - **The two reviewer roles do not do this**, deliberately. Their value is starting with no prior context;
     feeding them the record would soften them, which is the exact failure their starvation prevents.
4. **Compose them.** Static core first (identity and hard rules), then the protocol as the working method.
   The protocol is the bulk; the core is the spine.

## Precedence when things disagree

1. **An explicit instruction from the human, right now.** Always wins.
2. **The static core** in this folder. It outranks the model, because the model is allowed to change and the
   core is not.
3. **The model's protocol.**
4. **Anything recalled from memory as background.**

Never let a recalled item override a current instruction, and **never let the model talk its way out of the
static core.**

## Honest absence — the rule that keeps this trustworthy

Four different things, and conflating any two of them is the failure this rule exists to prevent:

- **Absent** (no model by that name) → run on the static core and **say so**. Do not invent a protocol.
- **Unreachable** (the store could not be asked) → **say the store was unreachable**. This is a different
  thing from absent, and reporting one as the other is the failure this rule exists to prevent.
- **Empty or placeholder content** (the model is still generating) → treat it as absent, and say that it is
  still generating rather than that it does not exist.
- **A recall with nothing relevant** is a fourth case, and a normal one: the record simply holds nothing on
  that decision. Say the record was silent and proceed — never dress this up as an outage, and never invent
  precedent to fill the gap.

## Editing a protocol

- The **static core** is a file: edit it, review it, commit it. (The "consult the record" rule is static —
  the generated protocol is explicitly forbidden from carrying anything about memory or this mechanism, so
  the rule lives in the core or it lives nowhere.)
- The **protocol** is edited by replacing the model's **prompt** (`source_query`) — not its generated text,
  which the server owns and rewrites. Use the CLI: `aisdlc set-prompt -m <model> -f <file>`.
- Never edit a model's content directly. It is generated, and the next refresh overwrites it.

## Where the project's own records live — not here

This folder holds our **protocols**; it does not hold project state. The project's records live in the
repository beside the work they describe, and there is **one canonical document per kind of record**:

- **The body of work register — `docs/STATUS.md`**, with this plan's per-package notes under
  `docs/revisions/2-retrieval-coordinator/design/plan/notes/`. `docs/STATUS.md` is the single file that answers "where does the work
  stand": what is built, what is accepted, what remains open, and the single next action. The per-package
  notes are that register's own evidence, one file per package. Each role updates its own row on lock,
  handoff, first deploy, and when bug-fix mode opens. Because there is **one** register and one per-package
  note, there is one answer and not two that can drift apart.
- **Audit evidence — `docs/08-evidence-log.md`.** Append-only, one entry per measurement, each carrying the
  command and the observed output, each with its own id. A result is not a result until it is in there.
- **Reviews — `docs/reviews/`**, kept verbatim, including the places a reviewer was wrong; the resolution
  record says who won and why.
- **Decisions and their rationale — `docs/decisions/`** (the settled set; `locked` means decided and not
  open for re-argument) and `docs/revisions/2-retrieval-coordinator/design/decisions/` (the next revision's). Decisions are cited by
  their own ids. This folder imports none of another project's.

**A status is grounded in the running system or the repository's trunk — never in another document's
claim.** Documents drift *behind* implementation: work ships while the paperwork still says it was never
approved. So a status is recorded as reached only when the container or the trunk proves it.

## Rules for whoever maintains this folder

- **The static core is for what must never drift** — identity, hard gates, escalation bounds, evidence
  disciplines. If a generation could break it, it does not belong in the model.
- **The protocol is for method** — sequencing, heuristics, checklists, quality bars. It is expected to
  change.
- **Project facts belong in neither.** Current project state, risks, priorities and names go in their own
  models, because they go stale in weeks and a protocol should not.
- **Neither layer names a bank.** The bank is deployment configuration, and it lives in
  `_aisdlc/config.yaml`.

## Provenance

Adapted into this repository on 2026-10-15 from the AISDLC protocol set that another project developed for
its own agents. This is a **derivation, not an original**: the two-layer design, the precedence order, the
honest-absence rules and the reviewer pattern are carried over in substance, and the adaptation is what
removed that project's specifics — its bank name (now the bank named in `_aisdlc/config.yaml`), its record
paths (now this repository's own, above), its decision ids (now prose that stands on its own) and the names
of its people and machinery.
