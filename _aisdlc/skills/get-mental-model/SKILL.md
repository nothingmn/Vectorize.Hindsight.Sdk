---
name: get-mental-model
description: "Use when a role needs its agent-SDLC operating protocol. Reads a named mental model's content from the bank named in _aisdlc/config.yaml."
version: 1.0.0
---

# get-mental-model — read a role's protocol from the memory store

Reads the **content of a named mental model** from the bank named in `_aisdlc/config.yaml`. This is how an
agent-SDLC role gets the adaptable half of its protocol (`_aisdlc/README.md` explains the two layers; the
static half is a file in `_aisdlc/<role>/STATIC.md` and is read directly).

**The bank is fixed in `_aisdlc/config.yaml`, not in a tool argument** — deliberately: a role's `MODEL.md`
names which model, never which bank. A run that must point somewhere else uses the explicit `--bank <name>`
or `$AISDLC_BANK` override, which is the only other place a bank name may appear.

## When to use

- You are acting as an agent-SDLC role (project manager, product owner, software architect, developer, QA,
  research) and you need your operating protocol.
- Any time you need the current *content* of a specific mental model.

## How to read it — the CLI (preferred)

The tool lives at `_aisdlc/tool/` and speaks the store's HTTP API directly; it depends on no sibling
repository.

**Status: the CLI is being rewritten and has not yet been run against a live bank.** The commands below are
its contract; `_aisdlc/tool/README.md` is the full description.

```bash
# read a role's protocol
aisdlc read -m developer_protocol
aisdlc read -m qa_protocol -o /tmp/qa.md

# see what is in the bank
aisdlc list
```

The store address comes from `hindsight_url` in `_aisdlc/config.yaml`; override it with `--url <url>` or
`$AISDLC_HINDSIGHT_URL`. The bank comes from `bank` in the same file; override it with `--bank <name>` or
`$AISDLC_BANK`.

## How to read it — raw HTTP (fallback, only if the CLI is unavailable)

```bash
# BANK and SERVER come from _aisdlc/config.yaml (or $AISDLC_BANK / $AISDLC_HINDSIGHT_URL).
# 1. list the bank's models WITH CONTENT — detail=content is mandatory
curl -s "$AISDLC_HINDSIGHT_URL/v1/default/banks/$AISDLC_BANK/mental-models?detail=content" \
  | jq -r '.items[] | select(.name=="developer_protocol") | .content'
```

## Pitfalls — these are the whole reason the skill exists

- **`detail=content` is NOT the default.** A lookup without it returns metadata only, and you get an
  **empty content field with no error**. The CLI always requests content; a hand-rolled call must too.
- **There is no get-by-name endpoint.** Name resolution is a list plus a match, taking the **first ordinal
  match**; ids are unique, names are not.
- **A model's content is GENERATED and regenerates asynchronously.** A model that was just created or just
  refreshed can return a placeholder. An empty body is not *no content*, and it is not proof of progress
  either: say which of the two states it is in — a refresh completed and wrote nothing, or no refresh has
  completed. Never tell a seat that an empty model is "still generating"; a model that will never fill is
  not a model that is filling.
- **Never treat "no such model" and "the store was down" as the same thing.** Report which one happened;
  the CLI exits `4` for absent and `3` for unreachable, and that split is deliberate.
- **Do not edit the content.** It is regenerated from the model's prompt and your edit will be overwritten.
  To change a protocol, change its **prompt** (`aisdlc set-prompt`).

## What the tool caches

`read` caches generated content locally under `_aisdlc/tool/.aisdlc-cache/`, keyed on the model's
`last_refreshed_at` — so a server-side regeneration invalidates it automatically, and yesterday's protocol
can never be served as today's. Use `--no-cache` to force a fresh read. The cache is not committed.

## Related

- `_aisdlc/README.md` — the overall approach and the precedence rules.
- `_aisdlc/<role>/STATIC.md` — the role's static core (read this *first*).
- `_aisdlc/<role>/MODEL.md` — which model this role reads.
- `_aisdlc/config.yaml` — which bank, and which store.
