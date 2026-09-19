# The Hindsight admin CLI — verified surface

Verified against the deployed CLI inside the running container, not read from prose.

```bash
docker exec hindsight-sdk-test hindsight-admin --help
```

The CLI is real and complete, but it is **not an HTTP API**. That distinction drives every
design decision in `Vectorize.Hindsight.Sdk.Admin`, so it is stated first.

---

## 1. It is not an API, and it cannot be wrapped

From the vendor's own module docstring:

> PostgreSQL-only admin utilities (backup, restore, migration, worker management).
> Not supported on Oracle backends. Uses `asyncpg.connect()` directly, binary `COPY`,
> and `TRUNCATE CASCADE` — all inherently PG-specific.

There is no HTTP surface to call. `hindsight-admin` opens a PostgreSQL connection itself. So a
.NET bridge **invokes a process**; it does not send requests. Everything below follows from that:
the result of a command is an exit code and two text streams, not a typed response body.

**Verified: no admin HTTP API exists.** Probed the live server for `/admin`, `/v1/admin`,
`/v1/admin/banks`, `/admin/health`, `/admin/cli`, `/api/admin` — all `404`. There is no
`admin` entry in the published specification, and no `HINDSIGHT_API_*ADMIN*` configuration
variable anywhere in the vendor's `.env.example`. The only "admin" in the specification is a
tag-filter example (`*-admin` for `role-admin`).

---

## 2. The nine commands, as deployed

`--help` on the deployed build lists eight commands, and `rename-bank` is *not* among them:

```
backup                 Backup the Hindsight database to a zip file.
restore                Restore the database from a backup file. WARNING: deletes all data.
run-db-migration       Run database migrations to the latest version.
repair-bank            Reconcile per-(bank, fact_type) vector index coverage.
export-bank            Export an entire bank to a portable ZIP (no embeddings).
import-bank            Restore a whole bank from an export-bank archive into THIS instance.
decommission-worker    Release all tasks owned by a worker (sets status back to pending).
decommission-workers   Release all processing tasks from all workers.
worker-status          Show all currently processing tasks grouped by worker.
```

Nine including `rename-bank`, which is present in the vendor's `main` branch but **absent from
the deployed 0.10.0 build** — running it answers `No such command 'rename-bank'`. It is therefore
not bridged; a bridge for it would compile and fail at run time against every released build.

### Signatures

| Command | Positional | Options |
| --- | --- | --- |
| `backup` | `OUTPUT` (required, `.zip`) | `--schema/-s` (default `public`) |
| `restore` | `INPUT_FILE` (required, `.zip`) | `--schema/-s`, `--yes/-y` |
| `run-db-migration` | — | `--schema/-s`, `--embedding-dimension` (int), `--skip-extension-reconcile` |
| `repair-bank` | — | `--bank/-b` **xor** `--all`, `--schema/-s`, `--dry-run` |
| `export-bank` | — | `--bank/-b` **required**, `--output/-o` **required**, `--schema/-s`, `--include-history` |
| `import-bank` | — | `--archive/-a` **required**, `--schema/-s`, `--target-bank`, `--include-history` |
| `decommission-worker` | `WORKER_ID` (required) | `--schema/-s`, `--yes/-y` |
| `decommission-workers` | — | `--schema/-s`, `--yes/-y` |
| `worker-status` | — | `--schema/-s` |

---

## 3. Exit codes, observed

| Code | Meaning | Example |
| --- | --- | --- |
| `0` | Success | `worker-status` on an empty database |
| `1` | Declined at a confirmation prompt | `decommission-workers` with stdin closed |
| `2` | Usage error | unknown subcommand; missing required argument; `--bank` with `--all` |

A `2` is the CLI rejecting the invocation before doing any work, which a caller should be able to
tell apart from a `1` or a failure part-way through. The bridge surfaces the code rather than
collapsing it.

---

## 4. Confirmation prompts, and why `--yes` is not the default

Three commands prompt before acting (`restore`, `decommission-worker`, `decommission-workers`) and
carry a `--yes/-y` to skip the prompt.

Observed with stdin closed and no `--yes`:

```
This will release ALL processing tasks from ALL workers back to pending. Continue? [y/N]: Aborted.
EXIT=1
```

Two consequences for a bridge:

1. **Never pass `--yes` implicitly.** It is a destructive confirmation. A bridge that defaulted it
   to true would turn every accidental call into a data-losing one. The bridge therefore takes an
   explicit confirmation and does not supply the flag unless asked.
2. **Never leave stdin attached.** A prompt with an open terminal would block forever inside a
   service. The process runner redirects stdin closed, so a missing confirmation fails fast as
   "aborted" rather than hanging. Verified: with stdin closed the command returns `1` immediately.

---

## 5. The output is human text, on stdout

Progress and results go to **stdout**; the streams are separate. Verified with a redirect:

```
--- STDOUT ---
Starting embedded PostgreSQL (instance: hindsight)...
No processing tasks found
--- STDERR ---
(empty)
```

The summary lines are consistently formatted, which is what makes any parsing possible at all:

```
Backed up 8 rows across 23 tables
  [1/23] Backing up banks... 1 rows
  schema 'public': 1 bank(s) scanned, 3 present, 0 created, 0 dropped, 0 to-create (dry-run), 0 to-drop (dry-run), 0 failed
Done: 1 schema(s), 1 bank(s) scanned, 3 already present, 0 created, 0 dropped, 0 to-create (dry-run), 0 to-drop (dry-run), 0 failed
```

The bridge parses these **and always preserves the raw text**. Text parsing is tolerant by design:
a line that does not match yields no structured record rather than an exception, so a wording change
in a future release degrades the convenience surface instead of breaking the call. That is the same
rule the HTTP client applies to unions — never fail a whole result because one field is unfamiliar.

---

## 6. The executable is not guaranteed to be on the host

Verified: `hindsight-admin` is **not** on this host's `PATH`. It lives inside the API container at
`/app/api/.venv/bin/hindsight-admin`, which is the documented deployment shape — the vendor's own
docs say to run it with `docker exec`, `kubectl exec`, or on the host that owns `pg0`.

So the bridge cannot assume a bare executable name. It takes the executable **and** any leading
arguments, which is what makes all of these expressible without special cases:

```
hindsight-admin ...                                  # bare metal, on PATH
docker exec -i hindsight-api hindsight-admin ...      # Docker
kubectl exec deploy/hindsight-api -- hindsight-admin ...  # Kubernetes
```

---

## 7. Two capabilities are already in the HTTP client

`export-bank` and `import-bank` overlap the HTTP operations `transfer/export` and
`transfer/import`. They are not redundant, and both are bridged:

- The **CLI** version connects to the database directly, so it works when the API is not serving,
  and it re-embeds on import (`--embedding-dimension` / a different backend).
- The **HTTP** version runs through the authenticated service and produces a submission the caller
  polls.

The overlap is documented in both directions rather than left for a caller to discover.

---

## 8. What the bridge does not do

- It does not run `hindsight-admin` automatically, at startup, or on a schedule. Every command is
  an explicit call.
- It does not default any destructive flag.
- It does not parse a command's output into a shape the CLI does not consistently produce. Where
  the output is prose, the raw text is the contract.
- It does not touch the database itself. It never opens a PostgreSQL connection; it drives a
  process, which is the only thing the CLI is designed to be driven by.
