using Vectorize.Hindsight.Sdk.Admin;
using Xunit;

namespace Vectorize.Hindsight.Sdk.Admin.Tests;

/// <summary>
/// The bridge driven against the real <c>hindsight-admin</c> binary.
/// </summary>
/// <remarks>
/// <para>
/// The one thing no stub can prove: that the arguments this bridge builds are arguments the actual
/// CLI accepts. Every other test in this project asserts the invocation; this one runs it.
/// </para>
/// <para>
/// Off by default, because it needs a deployment this host does not have — verified: the CLI is not
/// on this host's PATH; it lives inside the API container at
/// <c>/app/api/.venv/bin/hindsight-admin</c>. Set <c>HINDSIGHT_ADMIN_INTEGRATION=1</c> and, if
/// needed, <c>HINDSIGHT_ADMIN_EXECUTABLE</c> plus <c>HINDSIGHT_ADMIN_PREFIX</c> (space-separated, for
/// a <c>docker exec</c> prefix).
/// </para>
/// <para>
/// Only the read-only commands run here. <c>backup</c>, <c>restore</c>, <c>run-db-migration</c>,
/// <c>export-bank</c>, <c>import-bank</c> and both decommissions all mutate state or dump the whole
/// database, so they are deliberately not driven by a test suite that a developer may point at
/// something real. The argument-shape tests cover them without that risk.
/// </para>
/// </remarks>
public sealed class LiveAdminCliTests
{
    internal static bool Enabled =>
        Environment.GetEnvironmentVariable("HINDSIGHT_ADMIN_INTEGRATION") == "1";

    private static HindsightAdminClient Client()
    {
        var options = new HindsightAdminOptions
        {
            Executable = Environment.GetEnvironmentVariable("HINDSIGHT_ADMIN_EXECUTABLE") ?? "hindsight-admin",
            Timeout = TimeSpan.FromMinutes(3),
        };

        var prefix = Environment.GetEnvironmentVariable("HINDSIGHT_ADMIN_PREFIX");
        if (!string.IsNullOrWhiteSpace(prefix))
        {
            foreach (var part in prefix.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                options.LauncherPrefixArguments.Add(part);
            }
        }

        return new HindsightAdminClient(options);
    }

    [LiveAdminFact]
    public async Task Worker_status_runs_against_the_real_cli()
    {
        using var client = Client();

        // The invocation this bridge builds, printed so a failure is diagnosable from the test output.
        var result = await client.GetWorkerStatusAsync(null, CancellationToken.None);

        Assert.True(
            result.Succeeded,
            $"exit {result.ExitCode} from `{client.DescribeInvocationPrefix()} worker-status`\n"
            + $"stdout: {result.StandardOutput}\nstderr: {result.StandardError}");

        // Either it reported tasks or reported that there were none; both prove the binary accepted
        // the arguments and answered.
        Assert.False(string.IsNullOrWhiteSpace(result.StandardOutput));
    }

    [LiveAdminFact]
    public async Task Repair_bank_dry_run_runs_against_the_real_cli_and_changes_nothing()
    {
        using var client = Client();

        // --dry-run is deliberate: it exercises the real argument path without creating or dropping
        // an index. It also proves the parsed totals work against genuine output.
        var result = await client.RepairBankAsync(
            bankId: null, allBanks: true, schema: null, dryRun: true, CancellationToken.None);

        Assert.True(
            result.Succeeded,
            $"exit {result.ExitCode}\nstdout: {result.StandardOutput}\nstderr: {result.StandardError}");

        Assert.Contains("Dry run", result.StandardOutput, StringComparison.OrdinalIgnoreCase);

        var totals = result.RepairTotals;
        Assert.NotNull(totals);
        Assert.Equal(0, totals.Failed);
    }

    [LiveAdminFact]
    public async Task An_unknown_subcommand_is_a_usage_error_that_the_bridge_identifies()
    {
        using var client = Client();

        // The real CLI's rejection path, which is what a caller sees when this library's option model
        // drifts from a released binary. It must come back as a usage error, not a generic failure.
        var result = await client.RunAsync(
            new AdminCommand { Subcommand = "definitely-not-a-real-subcommand" }, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.True(result.IsUsageError, $"expected a usage error, got exit {result.ExitCode}");
        Assert.Contains("No such command", result.StandardError, StringComparison.OrdinalIgnoreCase);
    }

    [LiveAdminFact]
    public async Task A_prompted_command_without_confirmation_aborts_rather_than_hanging()
    {
        using var client = Client();

        // The single most important live assertion, and it is safe: withholding confirmation means
        // the CLI aborts. If stdin were left attached this would hang, so a returned result is the
        // proof that the bridge's anti-hang measure works against the real binary.
        var started = DateTimeOffset.UtcNow;

        var result = await client.DecommissionWorkersAsync(schema: null, confirm: false, CancellationToken.None);

        Assert.True(result.WasDeclined, $"expected a decline, got exit {result.ExitCode}");
        Assert.False(result.Succeeded);
        Assert.True(
            DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(60),
            "a prompted command must not hang the caller");
    }
}

/// <summary>
/// Skips the live CLI tests unless the environment points at a deployment.
/// </summary>
/// <remarks>
/// A visible skip rather than an early return. A test that returned early would report as passed,
/// which is the "silently empty run" failure mode this repository guards against elsewhere: it looks
/// like coverage while proving nothing. Reporting as skipped keeps the distinction honest.
/// </remarks>
public sealed class LiveAdminFactAttribute : FactAttribute
{
    public LiveAdminFactAttribute()
    {
        if (!LiveAdminCliTests.Enabled)
        {
            Skip = "Set HINDSIGHT_ADMIN_INTEGRATION=1 and point HINDSIGHT_ADMIN_EXECUTABLE at a "
                 + "hindsight-admin binary. It is not on this host's PATH; in a container deployment "
                 + "set HINDSIGHT_ADMIN_PREFIX to the docker or kubectl exec prefix.";
        }
    }
}
