# THE LEARNING LOOP — credit where behaviour worked

A run of agents improves in two ways: it is told what went wrong, and it is told **what went right and why**. The
first is already covered everywhere in this set — findings, gates, corrections. This file covers the second, which
is the one that is quietly skipped, and then the organisation relearns the same good behaviour from scratch every
few weeks.

Adapted 2026-10-22 from the protocol set another project developed for its own agents, where it exists as one hard
rule in the project manager's static core. This file is that rule with the mechanism written down, because a rule
that says "record it" and does not say what to record produces praise, and praise teaches nothing.

## What this is, and what it is not

**It is not a score, and nothing here trains a model.** Hindsight has no gradient and no reward. What it has is
facts, and mental models whose content is regenerated *from* those facts. So the loop is:

```
a behaviour works  →  it is recorded, naming the memory item that caused it
                   →  consolidation folds the record into the bank
                   →  the role's mental model regenerates (mode: delta, refresh_after_consolidation)
                   →  the next agent reads a protocol that prefers that behaviour
```

That is credit assignment, and the improvement is real but indirect: the bank gets better evidence, the protocol
that regenerates from it gets better advice, and the next session starts from that advice instead of from zero.
**With memory comes learning — but only if somebody writes down which memory did the work.**

## The signal to look for

The case worth reinforcing is the one that looks unremarkable: **the work was right the first time, and the reason
was something already in the record.** Concretely, both halves must be true and both are checkable:

1. **Right first time.** The first attempt passed its own gate: no re-dispatch to the same unit, no re-run of the
   gate that was already green, no finding raised against the same artefact.
2. **Memory did the work.** The agent can name the memory item or precedent it read before starting, and say what
   it changed about the approach. "I had a feeling" is not this; neither is a recital of every search it ran.

An agent that succeeds first time having consulted nothing is not a failure — it is usually ordinary work. It is
worth a **gap** entry only when the record should have had the answer and did not.

## The four outcomes

At the close of a unit, the project manager classifies it into exactly one of these and writes accordingly. Most
units are `noise`, and that is correct.

| Outcome | When | What is written |
|---|---|---|
| **credit** | right first time **and** a named memory item changed the approach | a fact naming the item, the behaviour, and what it changed |
| **gap** | right first time, memory consulted, nothing relevant was there | the missing fact, so the next run has it |
| **correction** | an existing item said better and it was missed or ignored | a fact naming the item and what ignoring it cost |
| **noise** | right first time, no memory involved, nothing generalisable | nothing — say so and move on |

## What an entry looks like — a tool call, not a curl

**The agent already has the write tool.** The memory provider composes three tools into every session it is
configured for — `hindsight_retain`, `hindsight_recall`, `hindsight_reflect` — so a credit entry is a tool call:

```
hindsight_retain(
  content = "Credit: before rebuilding the coordinator image, the builder read the recorded note that the
             analysis profile must be started before readiness can pass, and the deploy verified on the
             first attempt. Source: mental model `operating-this-stack`.",
  context = "credit",
  tags    = ["credit", "role:developer", "body:revision-4", "item:operating-this-stack"]
)
```

The `item:` tag is the one that makes this a learning signal rather than a diary entry: it is what lets anyone
ask later *which memory earned this*, and it is what a regenerated protocol can be traced back to.

**The routed alternative, when the agent works through the coordinator.** `code_memory` takes
`action: "retain"` with `items: [{text, tags}]` and returns a `retained[]` list of `{item_index, operation_id}` —
an operation id per item, which is better evidence than a tool call that returns nothing. It refuses rather than
silently dropping, and the refusal names the key that would allow it. **On this stack that key is off**:
`COORD_MEMORY_WRITE_ENABLED=false` in the live coordinator, so a retain through the coordinator today returns a
refusal that explains itself. Turning it on is a configuration change, and it is the prerequisite for the routed
path being usable at all.

**Curl is the fallback, not the method.** It exists for the recorded failure mode where a session composes **no**
memory provider at all: such an agent has notes and no recall, improvises over HTTP with its terminal, and never
answers through a surface the acceptance can attribute to memory (EV-0159). If you are reaching for curl, check
first whether you are in that state — `hindsight_retain` missing from the tool list is the symptom, and the fix is
the provider selection, not a longer shell command.

```bash
# fallback only, and the same shape the seeders use
curl -sS -X POST "http://127.0.0.1:8888/v1/default/banks/<bank>/memories" \
  -H 'Content-Type: application/json' -d '{"items": [{"content": "...", "context": "credit",
  "tags": ["credit", "role:developer", "body:revision-4", "item:operating-this-stack"]}]}'
```

Then let the loop close. There is no tool for this half: consolidation is an administrative call.
`POST /v1/default/banks/<bank>/consolidate`, or regenerate one model on demand with
`python3 _aisdlc/tool/aisdlc.py refresh -m <model>`. A credit entry that is never consolidated is a note in a
drawer.

## Guards — the five that keep this from becoming self-congratulation

1. **The gate is the evidence, not the impression.** "Right first time" means a green gate on the first attempt,
   recorded with its command. A project manager who watched closely and felt good about it is not evidence.
2. **Never credit a pass that succeeded for the wrong reason.** A test that was weakened, a gate whose control
   string was missing, a verification that ran nothing — those are faults, and crediting them teaches the fault.
3. **Credit the behaviour, never the agent.** "Checked the pinned image before rebuilding" generalises; "the
   developer did well" does not, and it invites the next agent to infer that competence is the standard.
4. **The human's correction outranks any credit.** If he says an approach was wrong, the credit entry is wrong too,
   and it is corrected in place rather than left to argue with him later.
5. **No scores, no leaderboards, no tallies.** A number invites gaming, and a bank cannot hold "this agent is good"
   in any useful form. The evidence is the behaviour and the item, not a rating.

## Who does what

- **A working agent** names the memory it used in its report, or says plainly that it used none. That is the only
  thing asked of it, and it is asked in the brief because an agent that is not asked will not say.
- **The project manager** classifies the unit at close and writes the entry. Children do not write to the bank:
  one writer, and the seat that saw the whole unit is the one that can attribute it.
- **The reviewer seats** credit nothing and are credited for one thing only: proving a claim wrong with a citation
  that changed the outcome. Finding the defects they were sent to find is the job, not the signal.

## Why this is written down at all

**A behaviour nobody names never becomes the standard.** The record is how the next agent inherits it — the same
reflex as catching a fault, pointed the other way. A run that catches every mistake and notices no good work
produces an organisation that knows only what not to do.
