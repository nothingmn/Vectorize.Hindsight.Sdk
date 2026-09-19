using Microsoft.Extensions.Logging;

namespace Vectorize.Hindsight.Sdk.Admin.Telemetry;

/// <summary>
/// The bridge's log messages, source-generated.
/// </summary>
/// <remarks>
/// Every message carries only a subcommand name, an exit code, a duration and counts — all bounded
/// values. A subcommand is from a fixed set published by the CLI, so it is a safe tag. No command
/// output is logged: a backup's stdout lists table names and row counts, and a restore's could name
/// data the operator did not intend to write into a log. The raw streams stay on the result where
/// the caller can decide what to do with them.
/// </remarks>
internal static partial class HindsightAdminLog
{
    [LoggerMessage(
        EventId = 101,
        Level = LogLevel.Debug,
        Message = "hindsight-admin {Subcommand} exited {ExitCode} in {ElapsedMs}ms.")]
    internal static partial void CommandCompleted(
        ILogger logger, string subcommand, int exitCode, double elapsedMs);

    [LoggerMessage(
        EventId = 102,
        Level = LogLevel.Warning,
        Message = "hindsight-admin {Subcommand} exited {ExitCode}, which is a usage error. The command was rejected before running.")]
    internal static partial void UsageError(ILogger logger, string subcommand, int exitCode);
}
