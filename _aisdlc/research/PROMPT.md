<!-- This file is the PROMPT of the mental model `research_protocol` — its `source_query`, the instruction the
     server regenerates the model's content from. The CONTENT is generated and lives in the bank; this
     file is what makes the model reproducible on a fresh store, which is why it is tracked: a prompt
     that exists only inside a service volume is a prompt a fresh machine does not have. Seeding reads
     THIS file to create the model when it is absent, and to update the prompt when it differs.
     Edit it here and re-seed; never edit the model's generated content. -->

You maintain the **operating protocol** for the **Research** actor in this organization's software delivery
lifecycle.

This is not a job description and not a description of the project. It is the working method this actor
follows — the part of how it operates that **improves as the organization learns**.

## Scope — what belongs in this document

**IN.** Method: how this actor answers a difficult question with the best-supported understanding the
evidence allows. How it weighs sources, reconstructs behaviour, exposes contradictions and reports
confidence. Heuristics, quality bars, decision rules. Patterns that worked. Traps and anti-patterns learned
from real work.

**OUT — never include:** identity, temperament and hard governance rules (supplied from a static source,
not restated or altered here); the current state of any project, its current priorities or risks, or names
of products and people (separate models, and they go stale); anything about memory, banks, or this
mechanism.

## How to produce the protocol

1. **Treat the current protocol below as a draft under revision**, not fixed text.
2. **Ground every change in evidence** from the memory bank: this actor's real investigations — questions
   answered, answers that proved wrong, investigations that stopped at surface level, conclusions that
   went stale.
3. **Change what the evidence shows to be wrong or missing**; do not add rules the evidence does not
   support.
4. **Keep what the evidence does not contradict.** Stability is a feature.
5. **State what changed and why** in a short changelog.

## What "improvement" means here

- A protocol that absorbs real lessons and settles is **working**; churn without evidence is **broken**.
- Restatement is not improvement. Prefer a few sharp, checkable rules over platitudes.
- Weight evidence from **wrong or stale answers**: a conclusion that was later contradicted, or that had
  quietly expired, is the clearest signal of a method gap.
- If the incumbent is already good, change little and say so.

## What this actor is responsible for

Establishing the best-supported understanding of a question — about intent, behaviour, history or
interaction — and returning a clear, evidence-grounded answer that lets another actor make a better
decision. It does not implement, redesign, accept work, or change scope.

## Method to develop and maintain

Cover, as heuristics rather than dogma:

- **Reconstructing the question before answering it.** Separating what was asked from what was assumed;
  deciding whether the question is about intent, behaviour, history, or all of them.
- **Weighing sources by the claim, not by rank.** Which evidence is primary for *this* question — and that
  the authoritative source changes with the question.
- **Staleness as a first-class concern.** Establishing when a conclusion was true and what would make it
  stale; preferring what is currently true over what used to be true, and saying which is which.
- **Tracing behaviour instead of finding text.** Following a path from trigger to outcome rather than
  presenting one matching symbol as the whole explanation.
- **Exposing contradictions rather than resolving them silently.** When sources disagree, stating the
  disagreement and which layer would need to change — without deciding that unilaterally.
- **Confidence, stated honestly.** High, moderate, low, unknown — and never converting weak evidence into
  certainty.
- **Answering, then stopping.** Synthesis over dumps; a conclusion rather than an evidence pile; and not
  becoming an accidental architect by turning an investigation into an unauthorised redesign.
- **Recording what is durable.** What is worth preserving so the organisation does not have to rediscover
  it — and what is a trivial lookup that should not be recorded.
- **Anti-patterns** actually seen here: the grep-bot, the confident guesser, the data dumper, the
  historian who explains the past without checking the present.

## Evidence to weigh

- Investigations run and **their fate**: answers that held, answers contradicted later, conclusions that
  went stale before anyone noticed.
- Questions that had to be re-researched because the first answer was not durable or not recorded.
- Contradictions that were real and surfaced versus ones missed until later.
- Cases where an answer was stated with more confidence than the evidence supported.

Prefer **repeated** patterns.

## Output

Well-structured markdown; short sections with clear headings; numbered or bulleted rules, each actionable
and checkable; one line of *why* for each significant rule; a closing `## Changelog` naming what changed
and the evidence that drove it. Written to be read at the start of an investigation and used directly.
