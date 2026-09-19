using Vectorize.Hindsight.Sdk.Admin;
using Vectorize.Hindsight.Sdk.Errors;
using Xunit;

namespace Vectorize.Hindsight.Sdk.Admin.Tests;

/// <summary>
/// The typed commands: that each builds the invocation the CLI actually accepts.
/// </summary>
/// <remarks>
/// These assert the argument list rather than only that a call succeeded, because the failure mode
/// that matters here is a well-formed call the CLI rejects as a usage error. Verified against the
/// deployed CLI's own <c>--help</c> output, which is the authority on flags and on which arguments
/// are required.
/// </remarks>
public sealed class CommandTests
{
    private static (HindsightAdminClient Client, StubRunner Runner) Create()
    {
        var runner = new StubRunner();
        var client = new HindsightAdminClient(new HindsightAdminOptions { Runner = runner });
        return (client, runner);
    }

    // --- backup / restore ---------------------------------------------------

    [Fact]
    public async Task Backup_passes_the_output_path_and_omits_an_unnamed_schema()
    {
        var (client, runner) = Create();
        using (client)
        {
            runner.EnqueueSuccess("Backed up 8 rows across 23 tables");

            var result = await client.BackupAsync("/data/backup.zip", null, CancellationToken.None);

            Assert.Equal("backup", runner.Commands[0].Subcommand);
            Assert.Equal(["/data/backup.zip"], runner.Commands[0].Arguments);
            Assert.False(runner.LastCommandHasOption("--schema"));
            Assert.Equal(8, result.BackedUpRows);
            Assert.Equal(23, result.BackedUpTables);
        }
    }

    [Fact]
    public async Task Backup_passes_a_named_schema()
    {
        var (client, runner) = Create();
        using (client)
        {
            runner.EnqueueSuccess();

            await client.BackupAsync("/tmp/b.zip", "tenant_a", CancellationToken.None);

            Assert.Equal("tenant_a", runner.LastOptions()["--schema"]);
        }
    }

    [Fact]
    public async Task Backup_rejects_a_blank_path_before_running_anything()
    {
        var (client, runner) = Create();
        using (client)
        {
            await Assert.ThrowsAsync<ArgumentException>(
                () => client.BackupAsync("  ", null, CancellationToken.None));

            Assert.Equal(0, runner.RunCount);
        }
    }

    [Fact]
    public async Task Restore_without_confirmation_leaves_the_prompt_in_place()
    {
        // The safety property that matters most: withhold confirmation and the CLI's own prompt is
        // left to abort the run. --yes must not be supplied on the caller's behalf.
        var (client, runner) = Create();
        using (client)
        {
            runner.EnqueueDeclined("This will delete all existing data. Continue? [y/N]: Aborted.");

            var result = await client.RestoreAsync("/tmp/b.zip", null, confirm: false, CancellationToken.None);

            Assert.False(runner.LastCommandHasOption("--yes"));
            Assert.True(result.WasDeclined);
            Assert.False(result.Succeeded);
        }
    }

    [Fact]
    public async Task Restore_with_confirmation_passes_the_yes_flag()
    {
        var (client, runner) = Create();
        using (client)
        {
            runner.EnqueueSuccess("Restored.");

            var result = await client.RestoreAsync("/tmp/b.zip", "public", confirm: true, CancellationToken.None);

            Assert.True(runner.LastCommandHasOption("--yes"));
            Assert.True(result.Succeeded);
        }
    }

    [Fact]
    public async Task Restore_raises_on_a_real_failure_rather_than_treating_it_as_a_decline()
    {
        // A decline is an outcome; a usage error is a mistake. Collapsing them would let a caller
        // believe a rejected invocation was a deliberate no-op.
        var (client, runner) = Create();
        using (client)
        {
            runner.EnqueueUsageError("Missing argument 'INPUT_FILE'.");

            var exception = await Assert.ThrowsAsync<HindsightAdminException>(
                () => client.RestoreAsync("/tmp/b.zip", null, confirm: false, CancellationToken.None));

            Assert.True(exception.Result.IsUsageError);
            Assert.False(exception.Result.WasDeclined);
        }
    }

    // --- migration ----------------------------------------------------------

    [Fact]
    public async Task Migration_omits_every_optional_flag_when_not_asked()
    {
        var (client, runner) = Create();
        using (client)
        {
            runner.EnqueueSuccess();

            await client.RunDatabaseMigrationAsync(null, null, skipExtensionReconcile: false, CancellationToken.None);

            Assert.Equal("run-db-migration", runner.Commands[0].Subcommand);
            Assert.Empty(runner.Commands[0].Arguments);
            Assert.Empty(runner.Commands[0].Options);
        }
    }

    [Fact]
    public async Task Migration_passes_the_dimension_as_a_number_and_the_reconcile_skip_as_a_flag()
    {
        var (client, runner) = Create();
        using (client)
        {
            runner.EnqueueSuccess();

            await client.RunDatabaseMigrationAsync("tenant_a", 1536, skipExtensionReconcile: true, CancellationToken.None);

            var options = runner.LastOptions();
            Assert.Equal("tenant_a", options["--schema"]);
            Assert.Equal("1536", options["--embedding-dimension"]);

            // A bare flag carries no value.
            Assert.Equal(string.Empty, options["--skip-extension-reconcile"]);
        }
    }

    // --- repair-bank --------------------------------------------------------

    [Fact]
    public async Task Repair_by_bank_passes_the_bank_and_not_the_all_flag()
    {
        var (client, runner) = Create();
        using (client)
        {
            runner.EnqueueSuccess("Done: 1 schema(s), 1 bank(s) scanned, 3 already present, 0 created, 0 dropped, 0 to-create (dry-run), 0 to-drop (dry-run), 0 failed");

            var result = await client.RepairBankAsync("a-bank", allBanks: false, null, dryRun: true, CancellationToken.None);

            Assert.Equal("a-bank", runner.LastOptions()["--bank"]);
            Assert.False(runner.LastCommandHasOption("--all"));
            Assert.True(runner.LastCommandHasOption("--dry-run"));

            Assert.NotNull(result.RepairTotals);
            Assert.Equal(1, result.RepairTotals.BanksScanned);
            Assert.Equal(3, result.RepairTotals.AlreadyPresent);
            Assert.Equal(0, result.RepairTotals.Failed);
        }
    }

    [Fact]
    public async Task Repair_all_banks_passes_the_all_flag_and_not_the_bank_option()
    {
        var (client, runner) = Create();
        using (client)
        {
            runner.EnqueueSuccess();

            await client.RepairBankAsync(null, allBanks: true, null, dryRun: false, CancellationToken.None);

            Assert.True(runner.LastCommandHasOption("--all"));
            Assert.False(runner.LastCommandHasOption("--bank"));
        }
    }

    [Fact]
    public async Task Repair_rejects_both_a_bank_and_all_before_running_anything()
    {
        // The CLI answers a usage error for this. Catching it here names the argument that was wrong
        // instead of returning an exit code a caller has to look up.
        var (client, runner) = Create();
        using (client)
        {
            var exception = await Assert.ThrowsAsync<HindsightValidationException>(
                () => client.RepairBankAsync("a-bank", allBanks: true, null, dryRun: false, CancellationToken.None));

            Assert.Contains("exactly one", exception.Message, StringComparison.Ordinal);
            Assert.Equal(0, runner.RunCount);
        }
    }

    [Fact]
    public async Task Repair_rejects_neither_a_bank_nor_all()
    {
        var (client, runner) = Create();
        using (client)
        {
            await Assert.ThrowsAsync<HindsightValidationException>(
                () => client.RepairBankAsync(null, allBanks: false, null, dryRun: false, CancellationToken.None));

            Assert.Equal(0, runner.RunCount);
        }
    }

    // --- export / import ---------------------------------------------------

    [Fact]
    public async Task Export_bank_requires_a_bank_and_an_output()
    {
        var (client, runner) = Create();
        using (client)
        {
            runner.EnqueueSuccess();

            await client.ExportBankAsync("a-bank", "/tmp/bank.zip", null, includeHistory: false, CancellationToken.None);

            var options = runner.LastOptions();
            Assert.Equal("a-bank", options["--bank"]);
            Assert.Equal("/tmp/bank.zip", options["--output"]);
            Assert.False(runner.LastCommandHasOption("--include-history"));
        }
    }

    [Fact]
    public async Task Export_bank_can_include_history()
    {
        var (client, runner) = Create();
        using (client)
        {
            runner.EnqueueSuccess();

            await client.ExportBankAsync("b", "/tmp/b.zip", "tenant_a", includeHistory: true, CancellationToken.None);

            Assert.True(runner.LastCommandHasOption("--include-history"));
            Assert.Equal("tenant_a", runner.LastOptions()["--schema"]);
        }
    }

    [Fact]
    public async Task Import_bank_passes_the_archive_and_the_target_override()
    {
        var (client, runner) = Create();
        using (client)
        {
            runner.EnqueueSuccess();

            await client.ImportBankAsync("/tmp/b.zip", null, "new-bank", includeHistory: true, CancellationToken.None);

            var options = runner.LastOptions();
            Assert.Equal("/tmp/b.zip", options["--archive"]);
            Assert.Equal("new-bank", options["--target-bank"]);
            Assert.True(runner.LastCommandHasOption("--include-history"));
        }
    }

    [Fact]
    public async Task Import_bank_omits_the_target_override_when_unset()
    {
        var (client, runner) = Create();
        using (client)
        {
            runner.EnqueueSuccess();

            await client.ImportBankAsync("/tmp/b.zip", null, null, includeHistory: false, CancellationToken.None);

            Assert.False(runner.LastCommandHasOption("--target-bank"));
        }
    }

    // --- workers ------------------------------------------------------------

    [Fact]
    public async Task Worker_status_is_read_only_and_carries_no_confirmation()
    {
        var (client, runner) = Create();
        using (client)
        {
            runner.EnqueueSuccess("No processing tasks found");

            var result = await client.GetWorkerStatusAsync(null, CancellationToken.None);

            Assert.False(runner.LastCommandHasOption("--yes"));
            Assert.True(result.ReportedNoProcessingTasks);
        }
    }

    [Fact]
    public async Task Decommission_one_worker_passes_the_worker_as_a_positional_argument()
    {
        var (client, runner) = Create();
        using (client)
        {
            runner.EnqueueSuccess();

            await client.DecommissionWorkerAsync("worker-7", null, confirm: true, CancellationToken.None);

            Assert.Equal("decommission-worker", runner.Commands[0].Subcommand);
            Assert.Equal(["worker-7"], runner.Commands[0].Arguments);
            Assert.True(runner.LastCommandHasOption("--yes"));
        }
    }

    [Fact]
    public async Task Decommission_one_worker_without_confirmation_is_declined_not_failed()
    {
        var (client, runner) = Create();
        using (client)
        {
            runner.EnqueueDeclined("Release all tasks owned by worker-7? [y/N]: Aborted.");

            var result = await client.DecommissionWorkerAsync("worker-7", null, confirm: false, CancellationToken.None);

            Assert.False(runner.LastCommandHasOption("--yes"));
            Assert.True(result.WasDeclined);
        }
    }

    [Fact]
    public async Task Decommission_all_workers_without_confirmation_is_declined()
    {
        var (client, runner) = Create();
        using (client)
        {
            runner.EnqueueDeclined("This will release ALL processing tasks from ALL workers back to pending. Continue? [y/N]: Aborted.");

            var result = await client.DecommissionWorkersAsync(null, confirm: false, CancellationToken.None);

            Assert.True(result.WasDeclined);
            Assert.False(result.Succeeded);
        }
    }

    [Fact]
    public async Task Decommission_all_workers_with_confirmation_succeeds()
    {
        var (client, runner) = Create();
        using (client)
        {
            runner.EnqueueSuccess("No processing tasks found");

            var result = await client.DecommissionWorkersAsync("public", confirm: true, CancellationToken.None);

            Assert.True(result.Succeeded);
            Assert.Equal("public", runner.LastOptions()["--schema"]);
        }
    }

    [Fact]
    public async Task Decommission_rejects_a_blank_worker_id_before_running_anything()
    {
        var (client, runner) = Create();
        using (client)
        {
            await Assert.ThrowsAsync<ArgumentException>(
                () => client.DecommissionWorkerAsync("  ", null, confirm: true, CancellationToken.None));

            Assert.Equal(0, runner.RunCount);
        }
    }

    // --- the escape hatch ---------------------------------------------------

    [Fact]
    public async Task An_arbitrary_command_can_be_run_without_a_typed_method()
    {
        // The CLI gains commands between releases. A caller must never be blocked by this library's
        // option model lagging behind.
        var (client, runner) = Create();
        using (client)
        {
            runner.EnqueueSuccess("ok");

            var command = new AdminCommand
            {
                Subcommand = "some-future-command",
                Arguments = ["positional"],
                Options = [new KeyValuePair<string, string>("--future-flag", "value")],
            };

            var result = await client.RunOrThrowAsync(command, CancellationToken.None);

            Assert.True(result.Succeeded);
            Assert.Equal("some-future-command", runner.Commands[0].Subcommand);
            Assert.Equal(["positional"], runner.Commands[0].Arguments);
            Assert.Equal("value", runner.LastOptions()["--future-flag"]);
        }
    }

    [Fact]
    public void Describe_renders_the_invocation_for_diagnosis()
    {
        var command = new AdminCommand
        {
            Subcommand = "export-bank",
            Options = [new KeyValuePair<string, string>("--bank", "a"), new KeyValuePair<string, string>("--include-history", string.Empty)],
            Arguments = ["/tmp/a b.zip"],
        };

        var text = command.Describe();

        Assert.Contains("export-bank", text, StringComparison.Ordinal);
        Assert.Contains("--bank a", text, StringComparison.Ordinal);

        // A bare flag renders without a trailing space, and an argument with a space is quoted.
        Assert.Contains("--include-history", text, StringComparison.Ordinal);
        Assert.Contains("\"/tmp/a b.zip\"", text, StringComparison.Ordinal);
    }
}
