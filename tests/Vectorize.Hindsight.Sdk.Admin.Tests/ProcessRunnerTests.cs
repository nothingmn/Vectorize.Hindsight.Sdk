using Microsoft.Extensions.Logging;
using Vectorize.Hindsight.Sdk.Admin;
using Xunit;

namespace Vectorize.Hindsight.Sdk.Admin.Tests;

/// <summary>
/// The real process runner, driven against real child processes.
/// </summary>
/// <remarks>
/// <para>
/// Everything else in this test project stubs the runner, which proves the bridge's logic but never
/// spawns anything. These tests do spawn processes — but never <c>hindsight-admin</c>, which copies
/// databases and releases live work. Instead they run a shell, which can be scripted to reproduce
/// each hazard precisely: a hanging prompt, a stream that fills a pipe, a non-zero exit, a process
/// that outlives its deadline.
/// </para>
/// <para>
/// This is the same two-level testing the HTTP client uses: a stub of the seam for logic, and the
/// real implementation exercised against something that behaves like the target.
/// </para>
/// </remarks>
public sealed class ProcessRunnerTests
{
    /// <summary>
    /// A client whose executable is the POSIX shell, so each command is a script we control.
    /// </summary>
    /// <remarks>
    /// <c>sh -c</c> consumes exactly one argument, which is the script; it is carried as the
    /// subcommand so each test reads as the script it runs. Using a shell rather than a bespoke binary
    /// keeps the test project free of any build artifact.
    /// </remarks>
    private static (HindsightAdminClient Client, AdminCommand Command) RunScript(
        string script, Action<HindsightAdminOptions>? configure = null)
    {
        var options = new HindsightAdminOptions { Executable = "/bin/sh" };
        options.LauncherPrefixArguments.Add("-c");
        configure?.Invoke(options);

        var command = new AdminCommand { Subcommand = script };
        return (new HindsightAdminClient(options), command);
    }

    [Fact]
    public async Task A_command_that_succeeds_reports_its_exit_code_and_output()
    {
        var (client, command) = RunScript("echo 'Backed up 8 rows across 23 tables'");
        using (client)
        {
            var result = await client.RunAsync(command, CancellationToken.None);

            Assert.True(result.Succeeded);
            Assert.Equal(0, result.ExitCode);
            Assert.Contains("Backed up 8 rows across 23 tables", result.StandardOutput, StringComparison.Ordinal);
            Assert.Equal(8, result.BackedUpRows);
            Assert.Equal(23, result.BackedUpTables);
        }
    }

    [Fact]
    public async Task A_non_zero_exit_is_reported_rather_than_inferred()
    {
        var (client, command) = RunScript("echo 'usage problem' >&2; exit 2");
        using (client)
        {
            var result = await client.RunAsync(command, CancellationToken.None);

            Assert.False(result.Succeeded);
            Assert.Equal(2, result.ExitCode);
            Assert.True(result.IsUsageError);
            Assert.Contains("usage problem", result.StandardError, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Both_streams_are_captured_separately()
    {
        var (client, command) = RunScript("echo out; echo err >&2");
        using (client)
        {
            var result = await client.RunAsync(command, CancellationToken.None);

            Assert.Contains("out", result.StandardOutput, StringComparison.Ordinal);
            Assert.DoesNotContain("err", result.StandardOutput, StringComparison.Ordinal);
            Assert.Contains("err", result.StandardError, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_prompt_reading_a_closed_stdin_aborts_instead_of_hanging()
    {
        // The behaviour the bridge depends on. The script reads a line from stdin; the runner closes
        // stdin, so the read fails immediately and the script can exit. Without that, a service would
        // block here forever.
        var (client, command) = RunScript("read answer || { echo 'Aborted.'; exit 1; }; echo \"answered: $answer\"");
        using (client)
        {
            var started = DateTimeOffset.UtcNow;

            var result = await client.RunAsync(command, CancellationToken.None);

            Assert.Equal(1, result.ExitCode);
            Assert.True(result.WasDeclined);
            Assert.Contains("Aborted.", result.StandardOutput, StringComparison.Ordinal);

            // The assertion that matters: it returned, and quickly.
            Assert.True(
                DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(20),
                "a closed stdin must not leave the process waiting for input");
        }
    }

    [Fact]
    public async Task A_stdin_hang_is_killed_by_the_timeout_rather_than_hanging_the_caller()
    {
        // The belt-and-braces case: even if a future release prompted through a path that ignored a
        // closed stdin, the deadline bounds the call and the process is killed.
        var (client, command) = RunScript(
            "sleep 300",
            options => options.Timeout = TimeSpan.FromSeconds(2));

        using (client)
        {
            var exception = await Assert.ThrowsAsync<HindsightAdminProcessException>(
                () => client.RunAsync(command, CancellationToken.None));

            Assert.True(exception.WasTimeout);
            Assert.Contains("timeout", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task A_zero_timeout_means_no_deadline()
    {
        var (client, command) = RunScript(
            "echo done",
            options => options.Timeout = TimeSpan.Zero);

        using (client)
        {
            var result = await client.RunAsync(command, CancellationToken.None);

            Assert.True(result.Succeeded);
        }
    }

    [Fact]
    public async Task Output_larger_than_a_pipe_buffer_does_not_deadlock()
    {
        // The defect this guards: reading one stream to completion before the other deadlocks once
        // the unread pipe fills. `backup` prints twenty-three progress lines, so a real run does
        // produce meaningful output. Here the script deliberately exceeds a typical 64 KB pipe.
        var (client, command) = RunScript(
            "i=0; while [ $i -lt 4000 ]; do echo \"line $i of a long progress report\"; i=$((i+1)); done");

        using (client)
        {
            var result = await client.RunAsync(command, CancellationToken.None);

            Assert.True(result.Succeeded);
            Assert.Contains("line 0 of", result.StandardOutput, StringComparison.Ordinal);
            Assert.Contains("line 3999 of", result.StandardOutput, StringComparison.Ordinal);

            // Four thousand lines comfortably exceeds a single pipe buffer, so reaching here proves
            // both streams were drained concurrently.
            Assert.True(result.StandardOutput.Length > 65536, $"expected more than a pipe buffer, got {result.StandardOutput.Length}");
        }
    }

    [Fact]
    public async Task A_command_that_writes_nothing_succeeds_with_empty_streams()
    {
        var (client, command) = RunScript("true");
        using (client)
        {
            var result = await client.RunAsync(command, CancellationToken.None);

            Assert.True(result.Succeeded);
            Assert.Equal(string.Empty, result.StandardOutput);
            Assert.Equal(string.Empty, result.StandardError);
        }
    }

    [Fact]
    public async Task Caller_cancellation_propagates_and_does_not_wait_for_the_command()
    {
        var (client, command) = RunScript("sleep 300");
        using (client)
        {
            using var cts = new CancellationTokenSource();
            await cts.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => client.RunAsync(command, cts.Token));
        }
    }

    [Fact]
    public async Task A_cancelled_command_does_not_leave_its_child_running()
    {
        // Killing only the parent would orphan the child, and an orphaned admin command can hold a
        // database connection. The marker file proves the child never got to run its exit path.
        var marker = Path.Combine(Path.GetTempPath(), $"hsdk-orphan-{Guid.NewGuid():N}.txt");
        var (client, command) = RunScript(
            $"sh -c 'sleep 2; touch {marker}'",
            options => options.Timeout = TimeSpan.FromSeconds(1));

        using (client)
        {
            await Assert.ThrowsAsync<HindsightAdminProcessException>(
                () => client.RunAsync(command, CancellationToken.None));
        }

        // Wait past the grandchild's own delay, then confirm it was killed with the tree.
        await Task.Delay(TimeSpan.FromSeconds(4), CancellationToken.None);
        Assert.False(File.Exists(marker), "the child process survived the timeout kill");
    }

    [Fact]
    public async Task A_missing_executable_fails_with_a_typed_error_rather_than_a_raw_one()
    {
        var options = new HindsightAdminOptions
        {
            Executable = "/nonexistent/definitely-not-here-hindsight-admin",
        };
        using var client = new HindsightAdminClient(options);

        var exception = await Assert.ThrowsAsync<HindsightAdminProcessException>(
            () => client.RunAsync(new AdminCommand { Subcommand = "worker-status" }, CancellationToken.None));

        Assert.Contains("could not be started", exception.Message, StringComparison.Ordinal);
        Assert.False(exception.WasTimeout);
    }

    [Fact]
    public async Task Environment_variables_are_passed_to_the_process()
    {
        // The CLI reads its database connection from the environment, so this is how a bridge targets
        // a particular database - and why it must be explicit rather than inherited.
        var (client, command) = RunScript(
            "echo $HINDSIGHT_API_DATABASE_URL",
            options => options.Environment["HINDSIGHT_API_DATABASE_URL"] = "postgresql://example/db");

        using (client)
        {
            var result = await client.RunAsync(command, CancellationToken.None);

            Assert.Contains("postgresql://example/db", result.StandardOutput, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Launcher_prefix_arguments_precede_the_subcommand()
    {
        // The shape that makes `docker exec` and `kubectl exec` expressible with no special case.
        var options = new HindsightAdminOptions { Executable = "/bin/sh" };
        options.LauncherPrefixArguments.Add("-c");
        options.LauncherPrefixArguments.Add("echo prefix-honoured");

        using var client = new HindsightAdminClient(options);
        var result = await client.RunAsync(
            new AdminCommand { Subcommand = "ignored-because-c-consumes-one-argument" },
            CancellationToken.None);

        Assert.Contains("prefix-honoured", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void The_invocation_prefix_is_describable_for_diagnosis()
    {
        var options = new HindsightAdminOptions { Executable = "docker" };
        options.LauncherPrefixArguments.Add("exec");
        options.LauncherPrefixArguments.Add("-i");
        options.LauncherPrefixArguments.Add("hindsight-api");

        using var client = new HindsightAdminClient(options);

        Assert.Equal("docker exec -i hindsight-api", client.DescribeInvocationPrefix());
    }

    [Fact]
    public async Task A_usage_error_on_a_real_process_is_not_reported_as_a_decline()
    {
        var (client, command) = RunScript("exit 2");
        using (client)
        {
            var result = await client.RunAsync(command, CancellationToken.None);

            Assert.True(result.IsUsageError);
            Assert.False(result.WasDeclined);
        }
    }

    [Fact]
    public async Task A_decline_on_a_real_process_is_not_reported_as_a_usage_error()
    {
        var (client, command) = RunScript("exit 1");
        using (client)
        {
            var result = await client.RunAsync(command, CancellationToken.None);

            Assert.True(result.WasDeclined);
            Assert.False(result.IsUsageError);
        }
    }

    [Fact]
    public async Task The_exception_carries_the_clis_own_message_rather_than_only_an_exit_code()
    {
        // The CLI wraps errors in box-drawing characters; the message must read as a sentence.
        var (client, command) = RunScript("printf '\\xe2\\x94\\x82 Bad argument. \\xe2\\x94\\x82\\n' >&2; exit 2");
        using (client)
        {
            var exception = await Assert.ThrowsAsync<HindsightAdminException>(
                () => client.RunOrThrowAsync(command, CancellationToken.None));

            Assert.Contains("Bad argument.", exception.Message, StringComparison.Ordinal);
            Assert.Contains("usage error", exception.Message, StringComparison.Ordinal);

            // The raw streams survive on the result for a caller that wants them.
            Assert.Contains("Bad argument.", exception.Result.StandardError, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void An_empty_executable_is_rejected_at_construction()
    {
        Assert.Throws<Errors.HindsightConfigurationException>(
            () => new HindsightAdminClient(new HindsightAdminOptions { Executable = "  " }));
    }

    [Fact]
    public void A_negative_timeout_is_rejected_at_construction()
    {
        Assert.Throws<Errors.HindsightConfigurationException>(
            () => new HindsightAdminClient(new HindsightAdminOptions { Timeout = TimeSpan.FromSeconds(-1) }));
    }

    [Fact]
    public void An_empty_launcher_prefix_argument_is_rejected_at_construction()
    {
        var options = new HindsightAdminOptions();
        options.LauncherPrefixArguments.Add(string.Empty);

        Assert.Throws<Errors.HindsightConfigurationException>(() => new HindsightAdminClient(options));
    }

    [Fact]
    public async Task Disposing_the_client_twice_is_harmless()
    {
        var runner = new StubRunner();
        var client = new HindsightAdminClient(new HindsightAdminOptions { Runner = runner });

        client.Dispose();
        client.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => client.RunAsync(new AdminCommand { Subcommand = "worker-status" }, CancellationToken.None));
    }

    [Fact]
    public async Task A_caller_supplied_runner_is_not_disposed_with_the_client()
    {
        // It belongs to the caller, or to a DI container that will dispose it.
        var runner = new StubRunner();
        runner.EnqueueSuccess();

        var client = new HindsightAdminClient(new HindsightAdminOptions { Runner = runner });
        await client.RunAsync(new AdminCommand { Subcommand = "worker-status" }, CancellationToken.None);
        client.Dispose();

        // Still usable: the client did not dispose it.
        runner.EnqueueSuccess();
        Assert.Equal(1, runner.RunCount);
    }

    [Fact]
    public async Task A_logger_records_the_outcome_but_not_the_command_output()
    {
        // The logged subcommand is a fixed name from the CLI's own command set, never caller data -
        // which is why logging it is safe. The OUTPUT is a different matter: a backup's stdout names
        // tables and row counts, and a restore's could name data an operator did not intend to write
        // into a log. The assertion is therefore that the outcome is recorded and the stdout is not.
        //
        // The script here is reached through a launcher whose prefix consumes it, exactly as
        // `bash -c "<script>"` does, so the SUBCOMMAND the bridge logs is a plain name while the
        // process still produces output containing the sentinel.
        var logs = new List<string>();
        using var factory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Trace);
            builder.AddProvider(new CapturingLoggerProvider(logs));
        });

        using var client = new HindsightAdminClient(new HindsightAdminOptions
        {
            Executable = "/bin/sh",
            Logger = factory.CreateLogger("admin"),
            LauncherPrefixArguments = { "-c", "echo 'sensitive table names and row counts'" },
        });

        // The prefix consumes one argument, so this name is what gets logged; it is never executed.
        var result = await client.RunAsync(
            new AdminCommand { Subcommand = "backup" }, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Contains("sensitive table names", result.StandardOutput, StringComparison.Ordinal);

        Assert.NotEmpty(logs);
        Assert.Contains(logs, line => line.Contains("backup exited 0", StringComparison.Ordinal));
        Assert.DoesNotContain(logs, line => line.Contains("sensitive table names", StringComparison.Ordinal));
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly List<string> _logs;

        internal CapturingLoggerProvider(List<string> logs) => _logs = logs;

        public ILogger CreateLogger(string categoryName) =>
            new CapturingLogger(categoryName, _logs);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger : ILogger
        {
            private readonly string _category;
            private readonly List<string> _logs;

            internal CapturingLogger(string category, List<string> logs)
            {
                _category = category;
                _logs = logs;
            }

            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                _logs.Add($"{_category}|{logLevel}|{formatter(state, exception)}");
        }
    }
}
