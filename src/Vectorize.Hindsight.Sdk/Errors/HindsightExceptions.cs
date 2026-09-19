using System.Text.Json;

namespace Vectorize.Hindsight.Sdk.Errors;

/// <summary>
/// The root of every error this library raises.
/// </summary>
/// <remarks>
/// A single root lets a caller catch the library with one clause, while the
/// derived types let it distinguish a local mistake from a server rejection
/// without parsing a message.
/// </remarks>
public abstract class HindsightException : Exception
{
    /// <summary>Creates an exception.</summary>
    /// <param name="message">A description safe to log: it never contains caller content or a key.</param>
    protected HindsightException(string message)
        : base(message)
    {
    }

    /// <summary>Creates an exception with an inner cause.</summary>
    /// <param name="message">A description safe to log.</param>
    /// <param name="innerException">The underlying failure.</param>
    protected HindsightException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// A failure raised before any request was sent.
/// </summary>
/// <remarks>
/// Deliberately distinct from a server rejection: a locally invalid request is
/// a programming error the caller can fix, not a condition to retry or report as
/// an outage.
/// </remarks>
public sealed class HindsightValidationException : HindsightException
{
    /// <summary>Creates a validation failure.</summary>
    /// <param name="message">What was invalid.</param>
    public HindsightValidationException(string message)
        : base(message)
    {
    }

    /// <summary>Creates a validation failure with a cause.</summary>
    /// <param name="message">What was invalid.</param>
    /// <param name="innerException">The underlying failure.</param>
    public HindsightValidationException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }

    /// <summary>The parameter that was invalid, when it is known.</summary>
    public string? ParamName { get; init; }
}

/// <summary>
/// The client is misconfigured.
/// </summary>
/// <remarks>
/// Raised at construction rather than at first use, so a missing or
/// contradictory configuration fails where it can still be handled.
/// </remarks>
public sealed class HindsightConfigurationException : HindsightException
{
    /// <summary>Creates a configuration failure.</summary>
    /// <param name="message">What was wrong, naming the sources checked.</param>
    public HindsightConfigurationException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// A request could not reach the server, or the connection failed or timed out.
/// </summary>
/// <remarks>
/// Distinguishes a transport failure from a server that answered, which matters
/// because only the former is ambiguous about whether the server saw the
/// request.
/// </remarks>
public sealed class HindsightConnectionException : HindsightException
{
    /// <summary>Creates a connection failure.</summary>
    /// <param name="message">A description safe to log.</param>
    /// <param name="innerException">The underlying failure.</param>
    public HindsightConnectionException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }

    /// <summary>
    /// True when the failure was this library's per-attempt deadline rather than
    /// the caller's own cancellation.
    /// </summary>
    /// <remarks>
    /// A caller that cancelled must not be told it hit a network failure, and
    /// vice versa; that distinction is why this is recorded explicitly rather
    /// than inferred from an exception type.
    /// </remarks>
    public bool WasTimeout { get; init; }
}

/// <summary>
/// The server answered with a non-success status.
/// </summary>
public abstract class HindsightApiException : HindsightException
{
    /// <summary>Creates an API exception.</summary>
    /// <param name="message">A description safe to log.</param>
    /// <param name="statusCode">The HTTP status.</param>
    /// <param name="endpoint">The method and path, without credentials.</param>
    /// <param name="responseBody">The raw body, for diagnosis.</param>
    protected HindsightApiException(string message, int statusCode, string endpoint, string? responseBody)
        : base(message)
    {
        StatusCode = statusCode;
        Endpoint = endpoint;
        ResponseBody = responseBody;
    }

    /// <summary>The HTTP status code.</summary>
    public int StatusCode { get; }

    /// <summary>
    /// The endpoint, as method and path, with credentials and query values
    /// removed.
    /// </summary>
    /// <remarks>
    /// A bank id is caller-chosen and can be identifying, so only the path shape
    /// is recorded — the same reasoning that keeps caller content out of logs.
    /// </remarks>
    public string Endpoint { get; }

    /// <summary>
    /// The raw response body, when one was returned.
    /// </summary>
    /// <remarks>
    /// Bounded by the transport, and caller content only in the sense that the
    /// caller's own request produced it. Useful for diagnosis; never logged
    /// automatically.
    /// </remarks>
    public string? ResponseBody { get; }
}

/// <summary>The request was malformed (400).</summary>
public sealed class HindsightBadRequestException : HindsightApiException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">A description safe to log.</param>
    /// <param name="endpoint">The endpoint.</param>
    /// <param name="responseBody">The raw body.</param>
    public HindsightBadRequestException(string message, string endpoint, string? responseBody)
        : base(message, 400, endpoint, responseBody)
    {
    }
}

/// <summary>
/// Authentication failed (401), or the deployment requires a key that was absent
/// or rejected.
/// </summary>
/// <remarks>
/// Not retryable: the credential will not become valid by repetition.
/// </remarks>
public sealed class HindsightAuthenticationException : HindsightApiException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">A description safe to log.</param>
    /// <param name="statusCode">401 or 403.</param>
    /// <param name="endpoint">The endpoint.</param>
    /// <param name="responseBody">The raw body.</param>
    public HindsightAuthenticationException(string message, int statusCode, string endpoint, string? responseBody)
        : base(message, statusCode, endpoint, responseBody)
    {
    }
}

/// <summary>The caller is authenticated but not permitted (403).</summary>
public sealed class HindsightPermissionDeniedException : HindsightApiException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">A description safe to log.</param>
    /// <param name="endpoint">The endpoint.</param>
    /// <param name="responseBody">The raw body.</param>
    public HindsightPermissionDeniedException(string message, string endpoint, string? responseBody)
        : base(message, 403, endpoint, responseBody)
    {
    }
}

/// <summary>
/// The target does not exist (404), or an operation's backing feature is
/// disabled.
/// </summary>
/// <remarks>
/// Two distinct conditions share this status. A missing bank is reported as 404
/// rather than as an empty result — deliberate, so a typo'd <c>bank_id</c> is
/// visible instead of looking like a healthy empty bank. A feature-disabled
/// operation answers 404 too, with a <c>detail</c> naming the variable that
/// enables it; that would otherwise read as "bank not found".
/// <para>
/// <see cref="HindsightApiException.ResponseBody"/> carries the server's own
/// wording, which is what tells the two apart.
/// </para>
/// </remarks>
public sealed class HindsightNotFoundException : HindsightApiException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">A description safe to log.</param>
    /// <param name="endpoint">The endpoint.</param>
    /// <param name="responseBody">The raw body.</param>
    public HindsightNotFoundException(string message, string endpoint, string? responseBody)
        : base(message, 404, endpoint, responseBody)
    {
    }

    /// <summary>
    /// True when the body reads as a disabled-feature notice rather than a
    /// missing resource.
    /// </summary>
    /// <remarks>
    /// Observed on <c>POST .../health/llm</c> when
    /// <c>HINDSIGHT_API_ENABLE_BANK_LLM_HEALTH</c> is false: the specification
    /// declares the operation as 200, the server answers 404, and the body says
    /// the feature is disabled. The two conditions need different handling —
    /// one means "wrong id", the other means "this deployment cannot serve the
    /// call at all" — so they are distinguished here.
    /// </remarks>
    public bool IsFeatureDisabled =>
        ResponseBody is not null &&
        ResponseBody.Contains("is disabled", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The request conflicted with existing state (409).
/// </summary>
/// <remarks>
/// Undeclared by the specification. Documented for a reused <c>operation_id</c>
/// that already belongs to a different operation, though that case did not
/// reproduce against a live server — see the repository's live-verification
/// notes. Mapped anyway, because a status the server can send must not fall
/// through to a generic error.
/// </remarks>
public sealed class HindsightConflictException : HindsightApiException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">A description safe to log.</param>
    /// <param name="endpoint">The endpoint.</param>
    /// <param name="responseBody">The raw body.</param>
    public HindsightConflictException(string message, string endpoint, string? responseBody)
        : base(message, 409, endpoint, responseBody)
    {
    }
}

/// <summary>
/// The request failed server-side validation (422), with per-field detail.
/// </summary>
/// <remarks>
/// Distinct from <see cref="HindsightValidationException"/>: that one is raised
/// locally before a request is sent, this one is the server's rejection of a
/// request that reached it.
/// </remarks>
public sealed class HindsightRequestValidationException : HindsightApiException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">A description safe to log.</param>
    /// <param name="endpoint">The endpoint.</param>
    /// <param name="responseBody">The raw body.</param>
    /// <param name="errors">The per-field detail, when it parsed.</param>
    public HindsightRequestValidationException(
        string message, string endpoint, string? responseBody, IReadOnlyList<ValidationErrorDetail>? errors)
        : base(message, 422, endpoint, responseBody)
    {
        Errors = errors;
    }

    /// <summary>
    /// The per-field errors, when the body parsed into the declared shape.
    /// </summary>
    /// <remarks>
    /// Null when the body did not parse. The specification's 422 shape is a
    /// <c>detail</c> array, but a 404 carries a plain string under the same key,
    /// so a body that does not match is expected rather than exceptional.
    /// </remarks>
    public IReadOnlyList<ValidationErrorDetail>? Errors { get; }
}

/// <summary>
/// The server reported a rate limit (429).
/// </summary>
/// <remarks>
/// Deliberately not derived from <see cref="HindsightServerException"/>: a rate
/// limit is the server declining to serve rather than failing, and collapsing
/// the two would make a caller report an outage when it should slow down.
/// </remarks>
public sealed class HindsightRateLimitException : HindsightApiException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">A description safe to log.</param>
    /// <param name="endpoint">The endpoint.</param>
    /// <param name="responseBody">The raw body.</param>
    /// <param name="retryAfter">The server's requested delay, when it sent one.</param>
    public HindsightRateLimitException(string message, string endpoint, string? responseBody, TimeSpan? retryAfter)
        : base(message, 429, endpoint, responseBody)
    {
        RetryAfter = retryAfter;
    }

    /// <summary>
    /// The server's requested delay, when supplied.
    /// </summary>
    /// <remarks>
    /// Null in practice against the observed server, which sends no
    /// <c>Retry-After</c> header on any response. Present because the header is
    /// legal and a deployment behind a proxy may add it.
    /// </remarks>
    public TimeSpan? RetryAfter { get; }
}

/// <summary>
/// A non-success status this library does not map to a dedicated type.
/// </summary>
/// <remarks>
/// Carries the status the server actually sent. Mapping an unmapped status onto
/// <see cref="HindsightBadRequestException"/> would have reported 400 for
/// everything, which is a property that lies about the response — 405 is real and
/// undeclared by the specification, and a caller reading <c>StatusCode</c> must be
/// able to trust it.
/// </remarks>
public sealed class HindsightUnexpectedStatusException : HindsightApiException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">A description safe to log.</param>
    /// <param name="statusCode">The status the server sent.</param>
    /// <param name="endpoint">The route template.</param>
    /// <param name="responseBody">The raw body.</param>
    public HindsightUnexpectedStatusException(string message, int statusCode, string endpoint, string? responseBody)
        : base(message, statusCode, endpoint, responseBody)
    {
    }
}

/// <summary>A server-side failure (5xx).</summary>
public class HindsightServerException : HindsightApiException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">A description safe to log.</param>
    /// <param name="statusCode">The 5xx status.</param>
    /// <param name="endpoint">The endpoint.</param>
    /// <param name="responseBody">The raw body.</param>
    public HindsightServerException(string message, int statusCode, string endpoint, string? responseBody)
        : base(message, statusCode, endpoint, responseBody)
    {
    }
}

/// <summary>
/// The server could not complete the request because it is overloaded.
/// </summary>
/// <remarks>
/// Derives from <see cref="HindsightServerException"/> so one catch covers every
/// server-side condition. No status maps here yet: the vendor's own client
/// treats 503 as capacity pressure, but this server has not been observed to
/// return a distinct overload status, so the type exists for the documented
/// 529 case rather than a guess about which status carries it.
/// </remarks>
public sealed class HindsightOverloadedException : HindsightServerException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">A description safe to log.</param>
    /// <param name="statusCode">The status.</param>
    /// <param name="endpoint">The endpoint.</param>
    /// <param name="responseBody">The raw body.</param>
    public HindsightOverloadedException(string message, int statusCode, string endpoint, string? responseBody)
        : base(message, statusCode, endpoint, responseBody)
    {
    }
}

/// <summary>
/// A success response body could not be parsed into the declared shape.
/// </summary>
/// <remarks>
/// A protocol error, distinct from a validation failure and deliberately not
/// retryable: a body that did not parse on this attempt will not parse on the
/// next, and retrying would turn a contract violation into repeated traffic.
/// </remarks>
public sealed class HindsightProtocolException : HindsightException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">A description safe to log.</param>
    /// <param name="endpoint">The endpoint.</param>
    /// <param name="innerException">The parse failure.</param>
    public HindsightProtocolException(string message, string endpoint, Exception? innerException = null)
        : base(message, innerException)
    {
        Endpoint = endpoint;
    }

    /// <summary>The endpoint that produced the unparseable body.</summary>
    public string Endpoint { get; }
}

/// <summary>One entry of the 422 <c>HTTPValidationError.detail</c> array.</summary>
public sealed class ValidationErrorDetail
{
    /// <summary>
    /// The location of the failure.
    /// </summary>
    /// <remarks>
    /// A mixed array of strings and integers in the specification, so it is
    /// carried as raw JSON rather than forced into one type. Observed values
    /// include <c>["body","query"]</c>.
    /// </remarks>
    public JsonElement Location { get; init; }

    /// <summary>The message.</summary>
    public string? Message { get; init; }

    /// <summary>The error type, e.g. <c>missing</c>.</summary>
    public string? ErrorType { get; init; }

    /// <summary>
    /// The offending input, when the server echoes it.
    /// </summary>
    /// <remarks>
    /// Untyped in the specification — a legal null — so it stays as raw JSON.
    /// </remarks>
    public JsonElement? Input { get; init; }

    /// <summary>The location rendered as a dotted path, for a readable message.</summary>
    public string LocationPath
    {
        get
        {
            if (Location.ValueKind != JsonValueKind.Array)
            {
                return string.Empty;
            }

            var parts = new List<string>();
            foreach (var segment in Location.EnumerateArray())
            {
                parts.Add(segment.ValueKind switch
                {
                    JsonValueKind.String => segment.GetString() ?? string.Empty,
                    JsonValueKind.Number => segment.GetRawText(),
                    _ => segment.GetRawText(),
                });
            }

            return string.Join('.', parts);
        }
    }
}
