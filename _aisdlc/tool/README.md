# `aisdlc` — the mental-model tool

Read a role's operating protocol, **recall the team's precedent**, edit the prompt a protocol
regenerates from, seed the whole model set from the tracked files, and cache the result locally.

It speaks the memory store's **HTTP API directly** and depends on no sibling repository. It talks to
the bank named in `_aisdlc/config.yaml` — one configured name, in one file, referenced everywhere else
by reference — because a bank name that appears in twenty documents is a bank name that drifts
between them silently, and a protocol read from the wrong bank is worse than one that refuses to read
at all.

**Implemented and run against a live store.** `aisdlc.py` is a single stdlib-only python3 program
(`python3 _aisdlc/tool/aisdlc.py …`, no interpreter beyond python3, no package to install). Its
commands, its exit codes and its cache design below are what it does, not what it is meant to.

## Configuration

| What | Where it comes from |
|---|---|
| the bank | `bank` in `_aisdlc/config.yaml`; override with `--bank <name>` or `$AISDLC_BANK` |
| the store address | `hindsight_url` in `_aisdlc/config.yaml`; override with `--url <url>` or `$AISDLC_HINDSIGHT_URL` |

Both are deployment configuration. A different project points at a different bank and a different
store; the override flags exist for a run that must not or cannot read the file, and they are the
only other place either value may be named. The file is read line-wise, so the tool needs no YAML
library — and no bank name is written into the code.

The examples below name the file by its path, `_aisdlc/tool/aisdlc.py`, which is always right. The
role documents write the same commands as `aisdlc` — that is this program, wherever a deployment has
put it on `PATH`.

## Use

```bash
# what is in the bank — names, ids, refresh times, and whether content has been generated yet
aisdlc.py list

# the PREFLIGHT — does the configured bank exist, every declared model exist, and each carry content?
aisdlc.py check

# read a protocol (the model's generated CONTENT)
aisdlc.py read -m developer_protocol
aisdlc.py read -m qa_protocol -o /tmp/qa.md
aisdlc.py read -m developer_protocol --no-cache      # bypass the local cache

# read PRECEDENT — what the organisation already learned (static core rule 7)
aisdlc.py recall -q "the decision you are about to make"
aisdlc.py recall -q "..." --budget mid --types observation --limit 8

# change a model's PROMPT (its source_query) — the only thing this tool writes to a model
aisdlc.py set-prompt -m developer_protocol -f _aisdlc/developer/PROMPT.md
aisdlc.py set-prompt -m developer_protocol -f <file> --force    # also create it if absent

# ask the store to regenerate a model's content now
aisdlc.py refresh -m developer_protocol

# create or update every role model from the tracked prompts (idempotent)
aisdlc.py seed

# the local content cache
aisdlc.py cache ; aisdlc.py cache --clear
```

**Exit codes:** `0` ok · `2` bad usage · `3` **store unreachable** · `4` **model absent** ·
`5` refused (needs `--force`) · `6` the store was reached and answered with an error status or an
unusable body. The split between 3 and 4 is deliberate — see below. `6` exists so that a store which
*answers* is never reported as one that could not be reached.

`recall` runs a retrieval over the bank, so it is the one slow surface — measured at 5 s warm and
125 s cold against this deployment. Its default request timeout is therefore 300 s where the model
surfaces get 30 s; `--timeout` overrides either. It prints the top 10 results by default (`--limit 0`
for all) and says how many it did not show.

## The preflight — a bank is not a model

`check` answers the three facts a revision must not start without, in one command:

1. the bank `_aisdlc/config.yaml` names **exists on the store**;
2. every model named in a role's `MODEL.md` is **present** in it;
3. each of those models **carries content** — and the first line of that content is printed beside it,
   so "has content" is visibly a protocol rather than a byte count.

It writes nothing. Exit `0` all three hold · `3` the store could not be asked · `4` a declared model is
absent, or empty in either of its two states · `6` the configured bank does not exist, or the store answered with
another error. The bank's absence is `6` and never `3`: the store **answered**, and the refusal is one
line naming the bank, listing the banks the store does hold, and giving the `PUT` that creates it.

`seed` runs the same check before it writes anything, and `read`/`recall`/`sync` name the bank instead
of showing the raw 404 when it is missing. That is the fix for the failure that made this necessary:
the bank a deployment named had never been created, every role's first read answered `404
Bank '…' not found`, the tool reported exit 6 truthfully, and every role then worked from its static
core alone while nothing anywhere refused. **A configuration that names a bank nobody created is a
defect the roles cannot see from inside a session; the preflight is where it becomes visible.**

The name checked is always the configured one — `bank` in `_aisdlc/config.yaml`, or `--bank` /
`$AISDLC_BANK`. A scaffolded deployment is therefore checked against *its* bank, and this file carries
no bank name to drift.

## The four cases it must never conflate

`_aisdlc/README.md` calls this the rule that keeps the mode trustworthy. Each is a different code
and a different message, and conflating any two is the failure the tool exists to prevent:

| What happened | Exit | What it says |
|---|---|---|
| the store could not be asked | `3` | "STORE UNREACHABLE" — an outage |
| no model by that name | `4` | "It is ABSENT — the store was asked and answered" |
| the model exists, its content is **empty** | `4` | the CLI names which of the two empty states it is — "empty (last refreshed <t> — a refresh completed and wrote nothing…)" or "empty (never refreshed — no refresh has completed…)" — with the timestamp, and no empty document printed as content |
| a recall that found nothing relevant | `0` | "the record is SILENT on this" — a normal condition |

A missing model is a normal condition and an unreachable store is an outage. Reporting one as the
other — in either direction — is the defect this file's first paragraph is about.

## The three things it gets right

**1. Content is always requested explicitly.** The store's read surface **defaults to metadata**, so a
naive fetch returns a model with an empty content field and *no error*. The tool only ever asks for
`detail=content`; a hand-rolled call must do the same.

**2. "Absent" and "unreachable" are never conflated.** They are separate exceptions in the code and
separate exit codes at the boundary (3 versus 4), and the absent path never runs on a failed read.

**3. It never writes content — only the prompt.** A mental model's `content` is *generated* by the
server from its `source_query`; writing content directly would be overwritten on the next refresh.
`set-prompt` replaces the prompt and lets the server regenerate (PATCHing the prompt does not
regenerate by itself — the next refresh does, and `refresh -m` asks for one now). It also **refuses to
create a model that does not exist** without `--force`, so a typo in a model name cannot silently
create a second, empty protocol.

## `seed` — what makes the mode survive a wiped volume

The eight role protocols are mental models in the store, and the store keeps them in its own
storage. A fresh machine therefore has the protocol set in `_aisdlc/` and **none of the models**.
What makes them recoverable is that the **prompts** are tracked, one per role in
`_aisdlc/<role>/PROMPT.md`.

`seed` walks the tracked tree, and for each role directory holding both `MODEL.md` and `PROMPT.md`:

- reads the **model name out of that role's `MODEL.md`** (the mode's rule is that `MODEL.md` names
  which model and never which bank), so no role list is hardcoded here and a role added to the
  folder is seeded without touching this tool;
- **creates** the model when it is absent — with that prompt, and the deployment's trigger: full
  regeneration on a daily cron, refresh tracing on;
- **updates** the prompt when it differs from the tracked file;
- otherwise reports it **unchanged**.

It is idempotent: a second run on a seeded bank creates nothing, patches nothing, and says so.
`scripts/up.sh` runs it as a non-fatal bring-up step, so a fresh machine gets the models without
anyone remembering to ask.

**One normalisation, and why.** A `PROMPT.md` opens with an HTML comment addressed to whoever
maintains this folder — it explains what the file is and how seeding uses it. That comment is not
part of the instruction the server runs, so it is stripped before the prompt is compared or sent.
That is what makes a re-seed idempotent against the models this bank already holds, a prompt that
says "seeding reads THIS file" is meaningless to the model that consumes it, and a comment about
banks and memory inside a prompt would put the mechanism's own meta-commentary into a document whose
output rules forbid exactly that.

## The local cache

`read` caches to `_aisdlc/tool/.aisdlc-cache/` (gitignored). The design avoids TTL guesswork:

- the metadata file is keyed on the **model name** and stores the server's `last_refreshed_at` and a
  **content hash**;
- the content itself is stored under a **hash-named file**.

The read is two-phase: a cheap metadata request (which is also what settles absent versus
unreachable versus still-generating, and never fetches content at all), then the content request only
on a cache miss. A cache hit requires the server's live `last_refreshed_at` to equal the cached one
**and** the cached bytes to still hash to what the metadata recorded — so when the server regenerates
a model, the fresh read writes a *new* metadata file pointing at a *new* content file, and the old
content is simply orphaned. There is no window in which yesterday's protocol is served as today's.
Content that is empty — in either of its two states — is never cached, so a placeholder cannot be served later
as if it were a protocol. `--no-cache` bypasses the cache entirely (it neither reads nor writes);
`cache --clear` empties it.

## What it deliberately does not do

- **No refresh scheduling.** The server owns regeneration. The trigger `seed` creates with carries a
  cron, but honouring it is the server's job; the tool reads protocols, edits prompts, and can ask
  for one explicit refresh now.
- **No bank hardcoded in code.** One configured name in `_aisdlc/config.yaml`, plus the explicit
  `--bank` / `$AISDLC_BANK` override — and no third place.
- **No sibling-repository dependency, and no other project's runtime.** This is this repository's
  tooling; it talks to the HTTP API and nothing else — no SDK, no YAML parser, no build step.

## Provenance

Adapted into this repository on 2026-10-15 from the tool description another project wrote for its
own AISDLC set. This is a **derivation, not an original**. The adaptation removed that project's
specifics — its bank name is now the bank named in `_aisdlc/config.yaml`, its own draft-prompt paths
are gone — and replaced the original implementation notes (a vendored SDK, a C# project) with the
plain-HTTP tool now in this repository: `aisdlc.py`, beside this file.
