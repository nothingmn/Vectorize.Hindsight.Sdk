using Microsoft.Extensions.Logging;
namespace Vectorize.Hindsight.Sdk.Telemetry;

/// <summary>
/// The library's log messages, source-generated.
/// </summary>
/// <remarks>
/// Every message here is written to carry only bounded, non-caller-supplied
/// values: route templates, statuses, counts and delays. No message takes the
/// content being stored, an API key, or a caller-chosen identifier as a
/// parameter, which is what makes redaction structural rather than a discipline
/// someone has to remember.
/// </remarks>
internal static partial class HindsightLog
{
    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Debug,
        Message = "Hindsight call {Operation} returned {StatusCode} in {ElapsedMs}ms after {RetryCount} retr(ies).")]
    internal static partial void CallCompleted(
        ILogger logger, string operation, int statusCode, double elapsedMs, int retryCount);

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Debug,
        Message = "Hindsight call {Operation} failed on attempt {Attempt}; retrying in {DelayMs}ms.")]
    internal static partial void RetryScheduled(
        ILogger logger, string operation, int attempt, double delayMs);

    [LoggerMessage(
        EventId = 3,
        Level = LogLevel.Warning,
        Message = "Hindsight call {Operation} failed after {AttemptCount} attempt(s) with {StatusCode}.")]
    internal static partial void CallFailed(
        ILogger logger, string operation, int attemptCount, int statusCode);

    [LoggerMessage(
        EventId = 4,
        Level = LogLevel.Warning,
        Message = "Hindsight call {Operation} could not reach the server: {Reason}.")]
    internal static partial void CallUnreachable(ILogger logger, string operation, string reason);

    [LoggerMessage(
        EventId = 5,
        Level = LogLevel.Debug,
        Message = "Hindsight call {Operation} succeeded after {AttemptCount} attempt(s).")]
    internal static partial void CallSucceeded(ILogger logger, string operation, int attemptCount);

    [LoggerMessage(
        EventId = 6,
        Level = LogLevel.Error,
        Message = "Hindsight response from {Operation} could not be parsed into the declared shape.")]
    internal static partial void ProtocolViolation(ILogger logger, string operation, Exception exception);
}
