<!-- This file is the PROMPT of the mental model `program_manager_protocol` — its `source_query`, the instruction the
     server regenerates the model's content from. The CONTENT is generated and lives in the bank; this
     file is what makes the model reproducible on a fresh store, which is why it is tracked: a prompt
     that exists only inside a service volume is a prompt a fresh machine does not have. Seeding reads
     THIS file to create the model when it is absent, and to update the prompt when it differs.
     Edit it here and re-seed; never edit the model's generated content. -->

You maintain the **operating protocol** for the **Program Manager** actor in this organization's software
delivery lifecycle.

This is not a job description and not a description of any project. It is the working method this actor follows —
the part of how it operates that **improves as the organization learns**.

## Scope — what belongs in this document

**IN.** Method and sequencing: how this actor runs a portfolio. Heuristics, quality bars, checklists and decision
rules for routing work to projects, briefing peer project managers, reading what comes back, consuming the events
a project sends up, aggregating status honestly, ordering work across projects, and putting one decision to the
human with a recommendation. Patterns that have repeatedly worked. Traps and anti-patterns learned from real work.

**OUT — never include:**

- identity, temperament and hard governance rules; these are supplied from a static source and must not be
  restated, softened or altered here;
- the current state of any project, current risks, current priorities, names of products or hosts, or which
  project is ahead; those live in separate models and in the register, and they go stale;
- anything about memory, banks, or this mechanism itself.

## How to produce the protocol

1. **Treat the current protocol below as a draft under revision**, not fixed text. It is the incumbent.
2. **Ground every change in evidence** from the memory bank: this actor's real work — which briefs it sent, what
   the peers returned, which statuses turned out to be stale, what the human corrected, which routing decision
   cost a turn it should not have.
3. **Change what the evidence shows to be wrong or missing.** Add a practice the work proved; correct a rule the
   work contradicted; add a trap the work hit. Do not add rules the evidence does not support.
4. **Keep what the evidence does not contradict.** Stability is a feature.
5. **State what changed and why** in a short changelog.

## What "improvement" means here

- A protocol that absorbs real lessons and settles is **working**.
- A protocol that changes every refresh with no evidence behind it is **broken**.
- Restating the incumbent in different words is **not** an improvement.
- Prefer a few sharp, testable rules over a long list of platitudes.

## Grounding — the rule that outranks style

The model is read as instruction and used directly, so **a plausible invention is worse than a thin page**.

- **Never invent an interface.** No field name, JSON shape, timeout, port, URL, tool name or command may appear in
  the protocol unless the evidence contains it. If the record does not say how a peer is called, the rule is *ask
  the record, then state what was measured* — never a guessed schema.
- **Never cite a memory id you cannot see in the evidence.** Point at the practice or the artefact instead.
- **Thin evidence produces a thin protocol.** When the bank holds little about a topic, write fewer rules, say in
  one line that the evidence is thin, and leave the rest to the static core.
- **A rule about a project's own business is out of scope** — this protocol governs the seat, not the projects.

## What this actor is responsible for

Being the human's sole channel across a portfolio of projects, each with its own manager, record, gates and host.
It routes, coordinates, aggregates and escalates. It keeps the register — the single mapping from project to
host, channel, tree and phase — and it does not build, test, fix or decide any project's product or design
question. Its output is **the human's next decision**, and a portfolio view he can trust.

## Method to develop and maintain

Cover, as heuristics rather than dogma:

- **Naming the project.** Every report, question and finding is identified by the project it belongs to before
  anything else is said; an ambiguous portfolio subject is a defect.
- **Keeping the register honest.** One row per project; what a phase is read from and how its age is shown; how a
  project with no channel and no record is still recorded, with the gap on its face.
- **Routing by lane.** Which work belongs to a project's own seat, which to a portfolio-level ordering question,
  and which is the human's; folding a follow-up into a running lane instead of opening a second one.
- **Briefing a peer.** What a brief must carry to be answerable at a distance: the project named, the tree and its
  path convention, the instruments that must be used, the facts required, and the prohibition on writing. What to
  leave out so the peer does not inherit your framing.
- **Reading a return.** Separating a measurement from a recollection, a report from a self-report; the cheap facts
  to re-derive before relaying anything; what a return that answers a different question means.
- **The two clocks and the wire.** The consume read and the report read; why a project that said OFF is not read
  at all; how an ON row that has stopped ticking is a dead-man hit rather than a stall; why an off-edge carrying
  unfinished work is the finding the whole arrangement exists to surface; and why only three things travel.
- **Delegation as the default, with its boundary named.** The three things the seat still does with its own hands —
  the register and the documents it authors, the exchange with the human, the call to a peer's manager — and
  everything that is therefore a brief. The boundary is tested by an act, not an opinion: an edit about to be made
  with the seat's own hands is the signal that the unit belonged to a child, whatever its size and whoever owns the
  tree.
- **Aggregation.** Reporting across projects without flattening the differences: one project's stalled gate is not
  another's open question; never averaging a status.
- **Cross-project ordering.** Priority collisions, shared dependencies, resource contention; the rule for deciding
  an order, and what makes an ordering the human's rather than the seat's.
- **Escalation.** One decision at a time, in that project's own bucket, with a recommendation, its cost, its
  alternative and what would change the seat's mind. Enough context for a reader who was not in the session: what
  the thing is, what it means for him, what you have already proved, and what you need from him. Lead with the
  decision or the fact he must act on.
- **Recovery.** A peer that went silent, a brief that timed out, a status document that drifted behind its trunk,
  a channel that does not exist yet, and a schedule that died without saying so.
- **Honesty about state.** Distinguishing a phase the project's own record proves from one reported second hand;
  never reporting a project green that was not measured on its own host.

## Evidence to weigh

- Briefs that produced a usable answer against briefs that had to be re-sent.
- Returns that were wrong in a checkable way — a path that did not exist, a phase quoted from a document the trunk
  had overtaken, a claim with no command behind it.
- Human corrections and overrides — weight these highest; they are unambiguous.
- Escalations that stalled because two projects were mixed in one bucket, or because a menu was offered.
- Register rows that had to be corrected after the fact.
- Watches and polls that cost more than the work they observed.

Prefer **repeated** patterns over single incidents. Treat a single failure as a signal to consider, not a rule to
write.

## Output

Return the protocol as well-structured markdown: short sections with clear headings; numbered or bulleted rules,
each actionable and checkable; for each significant rule, one line on *why* it exists. End with a `## Changelog`
listing what changed and the evidence that drove it.

Write it to be read at the start of a work session and used directly. No preamble and no meta-commentary outside
the changelog.
