using Vectorize.Hindsight.Sdk.Admin;

namespace Vectorize.Hindsight.Sdk.Admin.Tests;

/// <summary>
/// A hand-written stub of <see cref="IAdminProcessRunner"/>.
/// </summary>
/// <remarks>
/// No mocking framework, matching the rest of this repository. The seam makes every code path in the
/// bridge reachable without spawning a process, which matters here more than usual: the real commands
/// copy databases and release live work, so a test suite must not be able to run one by accident.
/// </remarks>
internal sealed class StubRunner : IAdminProcessRunner
{
    private readonly Queue<Func<AdminCommand, AdminResult>> _results = new();

    /// <summary>Every command this runner was asked to run, in order.</summary>
    internal List<AdminCommand> Commands { get; } = [];

    /// <summary>How many times the runner was entered.</summary>
    internal int RunCount { get; private set; }

    /// <summary>Queues a result.</summary>
    /// <param name="exitCode">The exit code.</param>
    /// <param name="stdout">Standard output.</param>
    /// <param name="stderr">Standard error.</param>
    internal StubRunner Enqueue(int exitCode, string stdout = "", string stderr = "")
    {
        _results.Enqueue(command => new AdminResult
        {
            Command = command,
            ExitCode = exitCode,
            StandardOutput = stdout,
            StandardError = stderr,
            Elapsed = TimeSpan.FromMilliseconds(1),
        });
        return this;
    }

    /// <summary>Queues a success with the given output.</summary>
    /// <param name="stdout">Standard output.</param>
    internal StubRunner EnqueueSuccess(string stdout = "") => Enqueue(0, stdout);

    /// <summary>Queues a usage error, which the CLI reports as exit code 2.</summary>
    /// <param name="stderr">Standard error.</param>
    internal StubRunner EnqueueUsageError(string stderr = "") =>
        Enqueue(AdminResult.UsageErrorExitCode, string.Empty, stderr);

    /// <summary>Queues a declined confirmation, which the CLI reports as exit code 1.</summary>
    /// <param name="stdout">Standard output, usually the prompt text.</param>
    internal StubRunner EnqueueDeclined(string stdout = "") =>
        Enqueue(AdminResult.DeclinedExitCode, stdout);

    /// <inheritdoc />
    public Task<AdminResult> RunAsync(AdminCommand command, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        RunCount++;
        Commands.Add(command);

        if (_results.Count == 0)
        {
            throw new InvalidOperationException(
                $"The stub runner has no queued result for call {RunCount} ({command.Subcommand}).");
        }

        return Task.FromResult(_results.Dequeue()(command));
    }

    /// <summary>The options of the last command run, keyed by option name.</summary>
    internal IReadOnlyDictionary<string, string> LastOptions() =>
        Commands[^1].Options.ToDictionary(option => option.Key, option => option.Value, StringComparer.Ordinal);

    /// <summary>Whether the last command carried an option.</summary>
    /// <param name="name">The option name, with dashes.</param>
    internal bool LastCommandHasOption(string name) =>
        Commands[^1].Options.Any(option => option.Key == name);
}
