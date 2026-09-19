using Vectorize.Hindsight.Sdk.Errors;

namespace Vectorize.Hindsight.Sdk.Admin;

/// <summary>The result of running an admin command.</summary>
public sealed class AdminResult
{
    /// <summary>The observed exit code for a usage error.</summary>
    public const int UsageErrorExitCode = 2;

    /// <summary>The observed exit code for a declined confirmation.</summary>
    public const int DeclinedExitCode = 1;

    /// <summary>The command that was run.</summary>
    public required AdminCommand Command { get; init; }

    /// <summary>The process exit code.</summary>
    public required int ExitCode { get; init; }

    /// <summary>Everything written to standard output.</summary>
    /// <remarks>
    /// The raw text is always preserved. The parsed summaries are a convenience over it, never a
    /// replacement: the CLI's output is prose, and a caller that needs something this library does
    /// not parse must still be able to read it.
    /// </remarks>
    public required string StandardOutput { get; init; }

    /// <summary>Everything written to standard error.</summary>
    public required string StandardError { get; init; }

    /// <summary>How long the command took.</summary>
    public required TimeSpan Elapsed { get; init; }

    /// <summary>True when the process exited zero.</summary>
    public bool Succeeded => ExitCode == 0;

    /// <summary>True when the invocation was rejected before any work happened.</summary>
    /// <remarks>
    /// Observed for an unknown subcommand, a missing required argument, and two mutually exclusive
    /// options given together. Distinguished from a failure part-way through, because the first is
    /// the caller's mistake and the second is the deployment's.
    /// </remarks>
    public bool IsUsageError => ExitCode == UsageErrorExitCode;

    /// <summary>True when a confirmation prompt was declined.</summary>
    /// <remarks>
    /// Observed when a destructive command runs without its <c>--yes</c> flag and stdin is closed:
    /// the CLI prints its prompt, reads end-of-file, and aborts. A caller should read this as
    /// "nothing happened, and here is why" rather than as a failure.
    /// </remarks>
    public bool WasDeclined => ExitCode == DeclinedExitCode;

    /// <summary>Raises a typed exception when the command did not succeed.</summary>
    /// <returns>This result, so a call can be chained.</returns>
    /// <exception cref="HindsightAdminException">The command did not exit zero.</exception>
    public AdminResult EnsureSuccess()
    {
        if (Succeeded)
        {
            return this;
        }

        throw new HindsightAdminException(this);
    }

    /// <summary>
    /// The row count a <c>backup</c> run reported, when its summary was recognised.
    /// </summary>
    /// <remarks>
    /// Null rather than zero when the line is absent or worded differently, so "nothing was backed
    /// up" is distinguishable from "the summary was not understood". Conflating them would let a
    /// caller believe an empty backup was a successful populated one.
    /// </remarks>
    public long? BackedUpRows => AdminOutputParser.ParseBackedUpRows(StandardOutput);

    /// <summary>The table count a <c>backup</c> run reported, when recognised.</summary>
    public long? BackedUpTables => AdminOutputParser.ParseBackedUpTables(StandardOutput);

    /// <summary>The totals a <c>repair-bank</c> run reported, when recognised.</summary>
    public RepairBankTotals? RepairTotals => AdminOutputParser.ParseRepairTotals(StandardOutput);

    /// <summary>True when a <c>worker-status</c> run reported that nothing was processing.</summary>
    public bool ReportedNoProcessingTasks => AdminOutputParser.ReportsNothingFound(StandardOutput);
}

/// <summary>A command that did not succeed.</summary>
public sealed class HindsightAdminException : HindsightException
{
    /// <summary>Creates the exception from a failed result.</summary>
    /// <param name="result">The result that failed.</param>
    public HindsightAdminException(AdminResult result)
        : base(BuildMessage(result))
    {
        Result = result;
    }

    /// <summary>The result that failed, carrying both raw streams.</summary>
    public AdminResult Result { get; }

    /// <summary>The process exit code.</summary>
    public int ExitCode => Result.ExitCode;

    private static string BuildMessage(AdminResult result)
    {
        // The CLI's own words are the most useful part, so they lead. The exit code follows, and
        // it is spelled out when it carries one of the two meanings worth naming.
        var detail = FirstMeaningfulLine(result.StandardError) ?? FirstMeaningfulLine(result.StandardOutput);

        var meaning = result switch
        {
            { IsUsageError: true } => " (usage error)",
            { WasDeclined: true } => " (declined at a confirmation prompt)",
            _ => string.Empty,
        };

        var text = $"hindsight-admin {result.Command.Subcommand} exited {result.ExitCode}{meaning}";

        return detail is null ? text : $"{text}: {detail}";
    }

    private static string? FirstMeaningfulLine(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        foreach (var line in text.Split('\n'))
        {
            // Strip the box-drawing the CLI wraps errors in, so the message reads as a sentence.
            var trimmed = line.Trim().Trim('│', '╭', '╰', '─', ' ');
            if (trimmed.Length > 0)
            {
                return trimmed;
            }
        }

        return null;
    }
}

/// <summary>
/// Runs an admin command and returns what it printed.
/// </summary>
/// <remarks>
/// The only external dependency of the bridge, and the seam that keeps every code path testable
/// without spawning a process — the same role <c>IHindsightTransport</c> plays on the HTTP side.
/// </remarks>
public interface IAdminProcessRunner
{
    /// <summary>Runs one command.</summary>
    /// <param name="command">The command to run.</param>
    /// <param name="cancellationToken">Honoured through the whole run, including the wait.</param>
    /// <returns>The result, including the exit code and both streams.</returns>
    Task<AdminResult> RunAsync(AdminCommand command, CancellationToken cancellationToken);
}
