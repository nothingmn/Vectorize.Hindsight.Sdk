namespace Vectorize.Hindsight.Sdk.Http;

/// <summary>A request about to be sent.</summary>
public sealed class HindsightRequest
{
    /// <summary>The HTTP method.</summary>
    public required HttpMethod Method { get; init; }

    /// <summary>
    /// The request path, relative to the base address, already including the
    /// <c>/v1/{tenant}</c> prefix.
    /// </summary>
    public required string Path { get; init; }

    /// <summary>Query parameters, appended in order.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Query { get; init; } = [];

    /// <summary>
    /// The serialized body, or null for a request with none.
    /// </summary>
    /// <remarks>
    /// Null and empty are different: a GET must send no body at all, and an
    /// empty <see cref="ReadOnlyMemory{T}"/> would put a zero-length body on the
    /// wire.
    /// </remarks>
    public ReadOnlyMemory<byte>? Body { get; init; }

    /// <summary>The <c>Content-Type</c> for the body, when there is one.</summary>
    public string? ContentType { get; init; }

    /// <summary>
    /// The multipart content to send, for the three upload operations.
    /// </summary>
    /// <remarks>
    /// Mutually exclusive with <see cref="Body"/>. Files are streamed rather
    /// than buffered, so a large upload does not have to fit in memory.
    /// </remarks>
    public MultipartPayload? Multipart { get; init; }

    /// <summary>
    /// The operation's path template, with placeholders left in, for error
    /// messages.
    /// </summary>
    /// <remarks>
    /// The resolved path can contain a caller-chosen bank id, which can be
    /// identifying. Errors report this template instead, so a log line carries
    /// the route shape rather than the caller's identifier.
    /// </remarks>
    public required string PathTemplate { get; init; }

    /// <summary>Whether the library may retry this request.</summary>
    /// <remarks>
    /// False for a synchronous retain, whose <c>operation_id</c> is ignored on
    /// that path, so a retry could duplicate a write.
    /// </remarks>
    public bool IsIdempotent { get; init; } = true;
}

/// <summary>A multipart upload.</summary>
public sealed class MultipartPayload
{
    /// <summary>The parts, in order.</summary>
    public required IReadOnlyList<MultipartPart> Parts { get; init; }
}

/// <summary>One part of a multipart upload.</summary>
public sealed class MultipartPart
{
    /// <summary>The form field name.</summary>
    public required string Name { get; init; }

    /// <summary>The bytes, for an in-memory part.</summary>
    public ReadOnlyMemory<byte>? Content { get; init; }

    /// <summary>The streaming source, for a file part.</summary>
    public Func<CancellationToken, Task<Stream>>? ContentSource { get; init; }

    /// <summary>The filename, for a file part.</summary>
    public string? FileName { get; init; }

    /// <summary>The part's content type.</summary>
    public string? ContentType { get; init; }

    /// <summary>Creates a string or JSON part.</summary>
    /// <param name="name">The field name.</param>
    /// <param name="json">The serialized value.</param>
    public static MultipartPart FromJson(string name, ReadOnlyMemory<byte> json) =>
        new() { Name = name, Content = json, ContentType = "application/json" };

    /// <summary>Creates a file part from a stream factory.</summary>
    /// <param name="name">The field name.</param>
    /// <param name="fileName">The filename sent to the server.</param>
    /// <param name="contentSource">Opens the content; called per attempt.</param>
    /// <param name="contentType">The content type, defaulting to octet-stream.</param>
    public static MultipartPart FromStream(
        string name, string fileName, Func<CancellationToken, Task<Stream>> contentSource, string? contentType = null) =>
        new()
        {
            Name = name,
            FileName = fileName,
            ContentSource = contentSource,
            ContentType = contentType ?? "application/octet-stream",
        };
}

/// <summary>A response that arrived.</summary>
public sealed class HindsightResponse
{
    /// <summary>The status code.</summary>
    public required int StatusCode { get; init; }

    /// <summary>
    /// The body bytes, when the operation returns JSON or text.
    /// </summary>
    /// <remarks>
    /// Null for a streamed response, which is delivered through
    /// <see cref="Stream"/> instead so a large archive is not buffered.
    /// </remarks>
    public byte[]? Body { get; init; }

    /// <summary>
    /// A streamed body, when the caller asked for one.
    /// </summary>
    /// <remarks>
    /// The caller owns disposal. Returned for the two binary operations
    /// (<c>octet-stream</c> and <c>zip</c>), which must not be materialised.
    /// </remarks>
    public Stream? Stream { get; init; }

    /// <summary>The response headers, for the few the library reads.</summary>
    public IReadOnlyDictionary<string, string> Headers { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>The response content type.</summary>
    public string? ContentType { get; init; }
}

/// <summary>
/// The one external dependency of the library.
/// </summary>
/// <remarks>
/// A hand-written stub of this interface makes every code path in the client
/// reachable with no network and no credentials, which is why the seam exists.
/// </remarks>
public interface IHindsightTransport : IDisposable
{
    /// <summary>Sends a request and returns the response.</summary>
    /// <param name="request">The request.</param>
    /// <param name="streamResponse">
    /// True when the caller wants the body as a stream rather than buffered.
    /// </param>
    /// <param name="cancellationToken">Honoured through every await.</param>
    /// <returns>The response.</returns>
    Task<HindsightResponse> SendAsync(
        HindsightRequest request, bool streamResponse, CancellationToken cancellationToken);
}
