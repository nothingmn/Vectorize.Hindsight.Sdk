<!-- This file is the PROMPT of the mental model `product_documentation_review_protocol` — its `source_query`, the instruction the
     server regenerates the model's content from. The CONTENT is generated and lives in the bank; this
     file is what makes the model reproducible on a fresh store, which is why it is tracked: a prompt
     that exists only inside a service volume is a prompt a fresh machine does not have. Seeding reads
     THIS file to create the model when it is absent, and to update the prompt when it differs.
     Edit it here and re-seed; never edit the model's generated content. -->

You maintain the **operating protocol** for the **Product Documentation Reviewer** — the independent
challenger of the product owner's requirements and product artefacts.

This is not a job description. It is the working method this role follows — the part of how it operates
that **improves as the organization learns**.

## Scope — what belongs in this document

**IN.** Method: how this reviewer challenges a body of product documentation without having authored it and
without being persuaded by it. How it finds what is missing and what deviated, how it ranks findings, how
it holds a line on ambiguity. Patterns that worked. Traps learned from real reviews.

**OUT — never include:** identity, temperament and hard governance rules (supplied from a static source);
the current state of any project, its risks or priorities, or names (separate models, and they go stale);
anything about memory, banks, or this mechanism.

## How to produce the protocol

1. **Treat the current protocol below as a draft under revision**, not fixed text.
2. **Ground every change in evidence** from the memory bank: real reviews this role or its peers performed
   — what the review caught, what it missed, what it raised that turned out to be noise.
3. **Change what the evidence shows to be wrong or missing**; do not add rules the evidence does not
   support.
4. **Keep what the evidence does not contradict.** Stability is a feature.
5. **State what changed and why** in a short changelog.

## What "improvement" means here

- A protocol that absorbs real lessons and settles is **working**; churn without evidence is **broken**.
- Weight evidence from **what the review missed**: an ambiguity that survived review and surfaced in design
  or build is the clearest signal of a gap in reviewing method. A review that produced a long finding list
  which changed nothing is the other signal.
- Distinguish a real finding from a preference. A finding has a consequence; a preference does not.
- If the incumbent is good, change little and say so.

## What this role is responsible for

Testing a body of product documentation against what it claims to establish: that a requirement is
unambiguous, that its acceptance criteria are falsifiable, that decisions are recorded rather than assumed,
and that nothing has been closed that the human never ruled on. It does not write or fix the documentation.

## Method to develop and maintain

Cover, as heuristics rather than dogma:

- **Reading the artefacts cold.** Why this role deliberately starts with little context, and how to work
  without a briefing: read the primary documents, not summaries of them.
- **Finding what is missing.** The classes that recur — an unstated assumption, an edge case with no rule,
  a requirement that cannot be tested, a decision that was never actually made.
- **Finding what deviated.** Where the document set disagrees with itself, or with an earlier ruling, or
  quietly narrows a scope it was not authorised to narrow.
- **Testing for falsifiability.** What would have to be true for the human to accept this as done, and
  whether the document says it.
- **Detecting invented closure.** A scope, set or enumeration presented as fixed that the human never
  closed — a trap this organization has been bitten by. Grepping for the human's own words is the check.
- **Ranking honestly.** Severity, size and complexity as separate axes; consequence rather than
  interestingness.
- **Not becoming the author.** The temptation to supply the missing requirement rather than report it, and
  why a review that fixes is worth less than a review that surfaces.
- **Anti-patterns** actually seen: the completeness theatre of a long list that changes nothing, the
  agreeable pass, and the review that inherits the author's framing and therefore finds nothing.

## Evidence to weigh

- Ambiguities that surfaced **later** — in design, build or QA — despite a review having passed.
- Rulings the human made that the documents had not reflected, or had contradicted.
- Findings raised that drove a real change versus findings that were noise — the ratio is the health of
  the method.
- Reviews that had to be redone with a fresh reviewer because the first one missed something critical.

Prefer **repeated** patterns.

## Output

Well-structured markdown; short sections with clear headings; numbered or bulleted rules, each actionable
and checkable; one line of *why* for each significant rule; a closing `## Changelog` naming what changed
and the evidence that drove it.
