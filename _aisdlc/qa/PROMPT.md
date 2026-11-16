<!-- This file is the PROMPT of the mental model `qa_protocol` — its `source_query`, the instruction the
     server regenerates the model's content from. The CONTENT is generated and lives in the bank; this
     file is what makes the model reproducible on a fresh store, which is why it is tracked: a prompt
     that exists only inside a service volume is a prompt a fresh machine does not have. Seeding reads
     THIS file to create the model when it is absent, and to update the prompt when it differs.
     Edit it here and re-seed; never edit the model's generated content. -->

You maintain the **operating protocol** for the **Quality Assurance** actor in this organization's software
delivery lifecycle.

This is not a job description and not a description of the project. It is the working method this actor
follows — the part of how it operates that **improves as the organization learns**.

## Scope — what belongs in this document

**IN.** Method: how this actor independently establishes whether what was delivered actually satisfies what
was intended. Heuristics, quality bars, decision rules. Patterns that worked. Traps and anti-patterns
learned from real work. What it escalates, and how.

**OUT — never include:** identity, temperament and hard governance rules (supplied from a static source,
not restated or altered here); the current state of any project, its known fragile areas, or names of
products and people (separate models, and they go stale); anything about memory, banks, or this mechanism.

## How to produce the protocol

1. **Treat the current protocol below as a draft under revision**, not fixed text.
2. **Ground every change in evidence** from the memory bank: this actor's real work — what it verified, the
   defects it found, the defects that **escaped past it**, and what those escapes had in common.
3. **Change what the evidence shows to be wrong or missing**; do not add rules the evidence does not
   support.
4. **Keep what the evidence does not contradict.** Stability is a feature.
5. **State what changed and why** in a short changelog.

## What "improvement" means here

- A protocol that absorbs real lessons and settles is **working**; churn without evidence is **broken**.
- Restatement is not improvement. Prefer a few sharp, checkable rules over platitudes.
- Weight evidence from **escapes** hardest: a defect that reached the human despite verification is the
  clearest statement that the verification method has a blind spot. When a class of escape recurs, the
  protocol should carry the specific check that would have caught it.
- If the incumbent is already good, change little and say so.

## What this actor is responsible for

Being the independent technical challenge to the claim that work is done. It establishes, with evidence,
whether the delivered change fulfils the signed product outcome, honours the design, and follows the
project's standards — and it is accountable to the human, not to the developer, for that judgement.

## Method to develop and maintain

Cover, as heuristics rather than dogma:

- **Reasoning across the three sources.** Expected behaviour, intended design, and what actually exists —
  kept distinct, because a system can match the design and violate the requirement, or satisfy both and
  violate the standards, and each is a different finding.
- **Turning acceptance criteria into tests that can fail.** What would prove a criterion; what would
  falsify it; when a criterion is too vague to test and must go back.
- **Looking beyond the happy path.** Boundaries, invalid input, malformed state, partial failure,
  integration failure, retries, repetition, permissions, concurrency, persistence, recovery, regression.
- **Risk-based depth.** Spending scrutiny where failure is most consequential, not evenly.
- **Honest severity.** Consequence, not interestingness; effort is a different axis from severity.
- **Reproduction quality.** What a finding must carry for someone else to understand and reproduce it.
- **Independence.** Treating the author's explanation as context, never as proof.
- **Routing rather than fixing.** Which findings belong to which owner; never closing work that the human
  or the product owner owns.
- **Revalidation.** After rework, retesting the original issue *and* the areas a fix could have broken.
- **Escapes.** The organisation's recurring blind spots, named so they get checked deliberately.
- **Anti-patterns** actually seen here: happy-path-only testing, checkbox acceptance, accepting an
  explanation as evidence, reinterpreting a criterion to fit the delivered software.

## Evidence to weigh

- Defects found and **defects escaped** — the escape set is the most valuable evidence in the bank.
- Classes of defect that recur, and whether the protocol has a check for each.
- Findings that were contested or downgraded and later proved real.
- Acceptance criteria that had to be rewritten because they were untestable.
- Cases where QA closed work without the human's acceptance.

Prefer **repeated** patterns. Name the specific check where a class of escape recurs.

## Output

Well-structured markdown; short sections with clear headings; numbered or bulleted rules, each actionable
and checkable; one line of *why* for each significant rule; a closing `## Changelog` naming what changed
and the evidence that drove it. Written to be read at the start of a verification session and used
directly.
