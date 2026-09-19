# Vectorize.Hindsight.Sdk.Admin

A .NET bridge for the Hindsight **admin CLI** (`hindsight-admin`).

It covers all nine commands: backup, restore, database migration, bank export/import, vector-index
repair, and worker management.

---

## Read this first: this is not an HTTP client

The admin CLI talks to **PostgreSQL directly**, not to the HTTP API. From the vendor's own module
docstring:

> PostgreSQL-only admin utilities (backup, restore, migration, worker management).
> Uses `asyncpg.connect()` directly, binary `COPY`, and `TRUNCATE CASCADE` — all inherently
> PG-specific.

So this package **invokes a process** rather than calling an endpoint. The consequence runs through
everything below: a call produces an **exit code and two text streams**, not a typed response body.
There is no retry policy and no HTTP error mapping, because there is no request.

**Verified: no admin HTTP API exists.** There are no `/admin` routes in the published OpenAPI
specification or on a running deployment, and no `HINDSIGHT_API_*ADMIN*` configuration variable. The
Admin CLI is the administrative surface.

The HTTP client lives in [`Vectorize.Hindsight.Sdk`](../Vectorize.Hindsight.Sdk/README.md). Two of
its operations overlap this package (`transfer/export` and `transfer/import`); see
[Overlap with the HTTP client](#overlap-with-the-http-client).

---

## Install

```
dotnet add package Vectorize.Hindsight.Sdk.Admin
```

Target framework: `net10.0`.

---

## The executable is probably not on your PATH

Verified: `hindsight-admin` ships inside the `hindsight-api` package, and in a container deployment it
lives at `/app/api/.venv/bin/hindsight-admin` — **not** on the host's PATH. The vendor's own docs run
it with `docker exec`, `kubectl exec`, or on the host that owns `pg0`.

So `Executable` plus `LauncherPrefixArguments` is how every deployment shape is expressed, with no
special cases:

```csharp
// Bare metal, where it IS on the PATH
using var client = new HindsightAdminClient();

// Docker
using var docker = new HindsightAdminClient(new HindsightAdminOptions
{
    Executable = "docker",
    LauncherPrefixArguments = { "exec", "-i", "hindsight-api" },
});

// Kubernetes
using var kubectl = new HindsightAdminClient(new HindsightAdminOptions
{
    Executable = "kubectl",
    LauncherPrefixArguments = { "exec", "deploy/hindsight-api", "--" },
});
```

To confirm what will actually run, before running anything:

```csharp
Console.WriteLine(docker.DescribeInvocationPrefix());
// docker exec -i hindsight-api
```

### The database is chosen by the environment

The CLI reads the same configuration as the API service. Point it at a database with
`HindsightAdminOptions.Environment`:

```csharp
options.Environment["HINDSIGHT_API_DATABASE_URL"] = "postgresql://user:pass@host:5432/hindsight";
```

Explicit rather than inherited, so a stray variable in the ambient environment cannot decide which
database a destructive command runs against. Note that the connection string travels through the
environment and **not** the command line, where any process on the host could read it.

---

## Quick start

```csharp
using Vectorize.Hindsight.Sdk.Admin;

using var admin = new HindsightAdminClient(new HindsightAdminOptions
{
    Executable = "docker",
    LauncherPrefixArguments = { "exec", "-i", "hindsight-api" },
    Timeout = TimeSpan.FromMinutes(20),
});

// Back up the whole database. The result carries the raw progress output and the parsed totals.
var backup = await admin.BackupAsync("/data/hindsight-backup.zip", schema: null, cancellationToken);
Console.WriteLine($"{backup.BackedUpRows} rows across {backup.BackedUpTables} tables");

// Check on workers before deciding whether anything needs freeing.
var status = await admin.GetWorkerStatusAsync(schema: null, cancellationToken);
if (status.ReportedNoProcessingTasks)
{
    Console.WriteLine("nothing is processing");
}

// Migrate before rolling out a new version.
await admin.RunDatabaseMigrationAsync(schema: null, embeddingDimension: null,
    skipExtensionReconcile: false, cancellationToken);
```

---

## Destructive commands require explicit confirmation

Three commands prompt before acting: `restore`, `decommission-worker` and `decommission-workers`.
The CLI offers `--yes` to skip the prompt.

**This bridge does not pass `--yes` unless you ask.** The confirmation is a required parameter with no
default, so these calls cannot be reached by accident:

```csharp
// Withheld: the CLI's prompt is left in place, and stdin is closed, so the run aborts
// immediately and changes nothing.
var declined = await admin.RestoreAsync("/data/backup.zip", schema: null,
    confirm: false, cancellationToken);

if (declined.WasDeclined)
{
    Console.WriteLine("nothing was restored: confirmation was withheld");
}
```

A declined run does **not** throw, because declining is the answer to the question you asked. Read
`WasDeclined`. Every *other* non-zero exit code still throws, so a rejected invocation can never be
mistaken for a deliberate no-op.

### Why a prompt cannot hang your service

The process runner **closes stdin** on every command. Without that, a prompt with a terminal attached
would block forever — inside a service that is an indefinite hang, not a failure.

Verified against the real binary: with stdin closed, a prompted command reads end-of-file, prints its
prompt, and exits `1` immediately.

There is a second guard behind it: the command timeout kills the process **tree**, so even a future
release that prompted through an unexpected path is bounded — and killing the tree rather than the
parent means nothing is orphaned holding a database connection.

---

## Results, and what is parsed

Every call returns an `AdminResult` carrying the exit code, both streams, and the elapsed time.

```csharp
var result = await admin.RepairBankAsync(bankId: null, allBanks: true, schema: null,
    dryRun: true, cancellationToken);

if (result.RepairTotals is { } totals)
{
    Console.WriteLine($"{totals.BanksScanned} banks, {totals.Created} created, {totals.Failed} failed");
}

// The raw text is always there too.
Console.WriteLine(result.StandardOutput);
```

| Member | Meaning |
| --- | --- |
| `Succeeded` | exit code `0` |
| `IsUsageError` | exit code `2` — the CLI rejected the invocation before doing any work |
| `WasDeclined` | exit code `1` — a confirmation prompt was declined |
| `BackedUpRows` / `BackedUpTables` | parsed from a `backup` summary, or **null** if unrecognised |
| `RepairTotals` | parsed from a `repair-bank` `Done:` line, or **null** if unrecognised |
| `ReportedNoProcessingTasks` | a `worker-status` run that found nothing |
| `StandardOutput` / `StandardError` | the raw streams, always preserved |

**Parsing is tolerant by design.** The output is prose, not a contract. A line that does not match
yields *nothing* rather than throwing, so a wording change in a future release degrades the
convenience surface instead of breaking the call — and the raw text is always available alongside.

Note the null-versus-zero distinction: `BackedUpRows` is `null` when the summary was not understood,
not `0`. Conflating them would let a caller believe an empty backup was a populated one.

---

## The command surface

| Method | Command | Notes |
| --- | --- | --- |
| `BackupAsync` | `backup` | Whole database to a `.zip` |
| `RestoreAsync` | `restore` | **Destructive** — deletes all existing data; needs `confirm` |
| `RunDatabaseMigrationAsync` | `run-db-migration` | Separate from API startup, for CI or pre-rollout |
| `RepairBankAsync` | `repair-bank` | Concurrent DDL, idempotent; `bankId` xor `allBanks` |
| `ExportBankAsync` | `export-bank` | Portable `.zip`, no embeddings |
| `ImportBankAsync` | `import-bank` | Re-embeds on import |
| `GetWorkerStatusAsync` | `worker-status` | Read-only |
| `DecommissionWorkerAsync` | `decommission-worker` | Needs `confirm` |
| `DecommissionWorkersAsync` | `decommission-workers` | Needs `confirm` |

Every method is asynchronous and takes a required `CancellationToken` with no default value. There is
no synchronous surface.

### The escape hatch

The CLI gains commands between releases. No caller should be blocked by this library's option model
lagging a binary it does not control:

```csharp
var result = await admin.RunOrThrowAsync(new AdminCommand
{
    Subcommand = "some-future-command",
    Arguments = ["positional"],
    Options = [new KeyValuePair<string, string>("--future-flag", "value")],
}, cancellationToken);
```

Every typed method above is a thin wrapper over `RunAsync`, so nothing is reachable only through a
typed form.

---

## Command reference, from the deployed CLI

Verified with `hindsight-admin <command> --help` against the shipped build:

```
backup                  OUTPUT (required)         [--schema|-s TEXT] [default: public]
restore                 INPUT_FILE (required)     [--schema|-s TEXT] [--yes|-y]
run-db-migration        —                         [--schema|-s TEXT]
                                                  [--embedding-dimension INTEGER]
                                                  [--skip-extension-reconcile]
repair-bank             —                         [--bank|-b TEXT] xor [--all]
                                                  [--schema|-s TEXT] [--dry-run]
export-bank             —                         [--bank|-b TEXT] (required)
                                                  [--output|-o PATH] (required)
                                                  [--schema|-s TEXT] [--include-history]
import-bank             —                         [--archive|-a PATH] (required)
                                                  [--schema|-s TEXT] [--target-bank TEXT]
                                                  [--include-history]
decommission-worker     WORKER_ID (required)      [--schema|-s TEXT] [--yes|-y]
decommission-workers    —                         [--schema|-s TEXT] [--yes|-y]
worker-status           —                         [--schema|-s TEXT]
```

**`rename-bank` is deliberately not bridged.** It is registered in the vendor's `main` branch but is
**absent from the deployed 0.10.0 build**, where it answers `No such command 'rename-bank'`. A bridge
for it would compile and fail against every released build.

The full verified surface — including observed exit codes and output formats — is in
[`docs/admin-cli-surface.md`](../../docs/admin-cli-surface.md).

---

## Overlap with the HTTP client

| Capability | Admin CLI | HTTP API |
| --- | --- | --- |
| Export a bank | `ExportBankAsync` — connects to PostgreSQL directly | `ExportBankTransferAsync` — through the authenticated service |
| Import a bank | `ImportBankAsync` — re-embeds with this instance's model | `ImportBankTransferAsync` — returns a submission to poll |

Neither is redundant. The CLI version works **when the API is not serving**, and is the one that
performs the cross-instance migration with a different embedding backend. The HTTP version goes
through the service and produces an operation the caller polls.

---

## What this package does not do

- It does **not** run any command automatically — not at startup, not on a schedule, not as a side
  effect of another call.
- It does **not** default any destructive flag.
- It does **not** open a PostgreSQL connection itself. It drives the process, which is the only thing
  the CLI is designed to be driven by.
- It does **not** log command output. A subcommand name and exit code are logged; stdout is not,
  because a backup's output names tables and row counts and a restore's could name data you did not
  intend to write into a log.

---

## Testing

The process runner is a seam (`IAdminProcessRunner`), so every code path is reachable without spawning
anything. The suite has three levels:

1. **Command-shape tests** — stub the runner, assert the exact invocation each typed method builds.
   These are what catch a flag the CLI would reject.
2. **Process-runner tests** — spawn a real shell, scripted to reproduce each hazard precisely: a
   hanging prompt, output exceeding a pipe buffer, a non-zero exit, a process outliving its deadline.
   They never run `hindsight-admin` itself, which copies databases and releases live work.
3. **Live CLI tests** — off by default. Set `HINDSIGHT_ADMIN_INTEGRATION=1` and point
   `HINDSIGHT_ADMIN_EXECUTABLE` (and `HINDSIGHT_ADMIN_PREFIX` for a container prefix) at a
   deployment. These drive the real binary, but only the read-only commands: `backup`, `restore`,
   migration, export/import and both decommissions are never driven by a test that a developer may
   point at something real.

Skipped tests report as **skipped** with the reason, never as passed — a test that returned early
would look like coverage while proving nothing.
