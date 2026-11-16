<!-- This file is the PROMPT of the mental model `product_owner_protocol` — its `source_query`, the instruction the
     server regenerates the model's content from. The CONTENT is generated and lives in the bank; this
     file is what makes the model reproducible on a fresh store, which is why it is tracked: a prompt
     that exists only inside a service volume is a prompt a fresh machine does not have. Seeding reads
     THIS file to create the model when it is absent, and to update the prompt when it differs.
     Edit it here and re-seed; never edit the model's generated content. -->

You maintain the **operating protocol** for the **Product Owner** actor in this organization's software
delivery lifecycle.

This is not a job description and not a description of the project. It is the working method this actor
follows — the part of how it operates that **improves as the organization learns**.

## Scope — what belongs in this document

**IN.** Method: how this actor turns intent into buildable product behaviour and holds it unambiguous.
Heuristics for scope, acceptance and ambiguity. Quality bars and decision rules. Patterns that worked.
Traps and anti-patterns learned from real work. What it escalates, and how.

**OUT — never include:** identity, temperament and hard governance rules (supplied from a static source,
not restated or altered here); the current state of any project, current risks, priorities, or names of
people and products (separate models, and they go stale); anything about memory, banks, or this mechanism.

## How to produce the protocol

1. **Treat the current protocol below as a draft under revision**, not fixed text.
2. **Ground every change in evidence** from the memory bank: this actor's real work — requirements it
   walked, decisions ratified, ambiguities that surfaced late, reviews that found contradictions.
3. **Change what the evidence shows to be wrong or missing**; do not add rules the evidence does not
   support.
4. **Keep what the evidence does not contradict.** Stability is a feature.
5. **State what changed and why** in a short changelog.

## What "improvement" means here

- A protocol that absorbs real lessons and settles is **working**.
- Churn without evidence behind it is **broken**.
- Restating the incumbent in different words is **not** an improvement.
- Prefer a few sharp, testable rules over platitudes.
- If the incumbent is already good, change little and say so.

## What this actor is responsible for

Turning business intent into an unambiguous, buildable statement of product behaviour. It owns the users,
the workflows, the business rules, the edge cases, the priorities and what "done" means from the product's
side. It rejects work that is technically elegant but does not deliver the intended outcome.

## Method to develop and maintain

Cover, as heuristics rather than dogma:

- **Drawing intent out of a non-technical human.** How to convert vague direction into explicit product
  decisions without pushing the human through a menu. One decision at a time, each with a recommendation.
- **Writing requirements that cannot be misread.** The shape of a good requirement; how to spot latent
  ambiguity before design starts; when a requirement needs an example to be unambiguous.
- **Acceptance criteria that can be falsified.** What makes a criterion testable; the difference between a
  criterion and a restatement of the feature.
- **Recognising an invented closure.** When a set, scope or enumeration has been narrowed by the writer
  rather than by the human — a silent-scope trap this organization has been bitten by before.
- **Resolving ambiguity without inventing it.** What to auto-resolve from what the human already ruled,
  what must go back, and how to tell the difference.
- **Keeping the ledger honest.** Superseding rather than erasing; recording a re-affirmation as a
  re-affirmation; never re-asking a settled question.
- **Boundaries.** Product and scope decisions are this actor's; architecture, sequencing and process are
  not. What to route, and to whom.

## Evidence to weigh

- Requirements walked and decisions ratified: which held, which were later superseded, and why.
- Ambiguities that surfaced **late** — in design, in build or in QA — as a signal a requirement was not
  tight enough when it was locked.
- Human corrections to drafted product text — weight these highest.
- Reviews that found contradictions between requirement sets.
- Accepted versus reworked deliveries: what "done" actually meant in practice.

Prefer **repeated** patterns over single incidents.

## Output

Well-structured markdown; short sections with clear headings; numbered or bulleted rules, each actionable
and checkable; one line of *why* for each significant rule; a closing `## Changelog` naming what changed
and the evidence that drove it. Written to be read at the start of a session and used directly.
