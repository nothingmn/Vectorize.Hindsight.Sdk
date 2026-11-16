# CODE STANDARDS — shared by every technical role

Conventions the software architect designs to, the developer builds to, and QA and the reviewers verify
against. Repo-wide build, test, deploy and boundary rules live in the root `AGENTS.md`, which wins on any
conflict with this file.

Adapted from a code-standards document another project developed for its own agents, with its specifics
removed. Nothing here names that project. The two evidence rules below are not style preferences: both exist
because the failure they prevent has already happened, and each time it cost a review cycle to catch.

## C# conventions

- **Dependency injection through the constructor**, never property injection.
- **Split by function first, then by size.** A file boundary follows a real seam, not a line count.
- **Use partial classes to keep every file under 100 KB.** The main partial file — the one matching the class
  name — carries a header comment listing every other file in that family and what each one holds. The point is
  to make an agent's reads and writes cheap: it should be obvious which file to open for a given concern.
- **A partial class is not a substitute for a real seam.** A responsibility that does not intrinsically need
  actor state belongs behind a narrow injected interface with a default implementation, before anyone reaches
  for a file-only split.
- **All I/O is async and honours a `CancellationToken`.** A method that blocks or ignores the token it was
  handed is a defect in a service that runs under deadlines.

## Evidence discipline — applies to every technical role

### 1. Never state an inference as a measurement

**If you write a number, run the command that produces it in the same breath.** Do not carry a figure forward
from earlier in the session, from another agent's report, or from a document — **including one you just
wrote.** Record the command beside the number so the claim is checkable.

- **A bounded view is not a lifetime measurement.** `docker logs` is truncated, a query hits a live mutating
  store, a directory listing is one moment in time. Write *"as of `<time>`, `<n>`"* — never "always", never
  "never", never "0 times in the container's life".
- **Say which store you read.** Where a live path and a stale copy both exist, naming the path is part of the
  evidence.
- **This applies hardest to a number an argument rests on.** A wrong number that merely informs is cheap; one
  a decision rests on reads exactly like a verified one. In the revision-4 plan review, the plan's own two
  breakdown headers said "24 builder packages" over a 39-row table and "9 verifier packages" over a 13-row
  table, because both counts were written from memory rather than computed. The reviewer caught them; nobody
  else would have.

### 2. Verify a hand-maintained document's structure after every edit

**Count the rows, confirm the ids are contiguous, and re-read the section boundaries before moving on.** An
edit that *replaces* an entry instead of adding one fails silently: nothing errors, and the loss surfaces much
later, if at all.

- **Never rewrite a large hand-maintained document with a whole-file write.** Edit surgically, then prove the
  result is intact.
- **A generated document must be regenerated, not hand-patched.** If a document claims it "cannot drift from the
  spine because it is generated from it", the claim is only true while every edit goes through the generator. In
  the same review, the requirement-to-package map made that claim and had drifted in four rows.
- These documents are the project's memory. A silently dropped decision is a decision that gets made again,
  wrongly.

## What QA checks

When verifying a delivery, confirm it follows the conventions above — dependencies injected rather than reached
for, files split along real seams, the partial family mapped at the top of its main file, I/O async and
cancellable. **Also check the evidence behind every claim in it:** a number without a command is not evidence, a
count without a timestamp is not a measurement, and a document edited in place without a structure check may have
lost an entry.
