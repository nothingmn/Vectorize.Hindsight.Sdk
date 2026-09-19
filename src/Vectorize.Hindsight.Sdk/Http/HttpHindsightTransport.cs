using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Vectorize.Hindsight.Sdk.Authentication;
using Vectorize.Hindsight.Sdk.Configuration;
using Vectorize.Hindsight.Sdk.Errors;

namespace Vectorize.Hindsight.Sdk.Http;

/// <summary>
/// The built-in HTTP transport.
/// </summary>
/// <remarks>
/// <para>
/// Two things here are deliberate and each fixes a defect that only shows up at
/// runtime.
/// </para>
/// <para>
/// The per-attempt timeout is enforced with a linked
/// <see cref="CancellationTokenSource"/> rather than by assigning
/// <c>HttpClient.Timeout</c>. That property throws
/// <see cref="InvalidOperationException"/> once the client has started a
/// request, and a pooled or factory-managed client — which is exactly what
/// <c>IHttpClientFactory</c> hands back — has often already served one. A caller
/// supplying such a client must not be punished for it.
/// </para>
/// <para>
/// The body read is bounded by the same deadline. With
/// <see cref="HttpCompletionOption.ResponseHeadersRead"/> the send returns as
/// soon as headers arrive, so a server that sends headers promptly and then
/// stalls the body would otherwise hold the call open past the configured
/// timeout.
/// </para>
/// </remarks>
internal sealed class HttpHindsightTransport : IHindsightTransport
{
    private readonly HindsightClientOptions _options;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;
    private readonly IApiKeyProvider? _apiKeyProvider;
    private bool _disposed;

    internal HttpHindsightTransport(HindsightClientOptions options)
    {
        _options = options;
        _apiKeyProvider = options.ApiKeyProvider
            ?? (options.ApiKey is not null ? new StaticApiKeyProvider(options.ApiKey) : null);

        if (options.HttpClient is not null)
        {
            // Never mutated. Its own Timeout is left exactly as the caller set it.
            _httpClient = options.HttpClient;
            _ownsClient = false;
        }
        else
        {
            _httpClient = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            _ownsClient = true;
        }
    }

    /// <inheritdoc />
    public async Task<HindsightResponse> SendAsync(
        HindsightRequest request, bool streamResponse, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var uri = BuildUri(request);

        using var message = BuildMessage(request, uri);

        // One deadline per attempt, linked to the caller's token so the two
        // remain distinguishable: a caller that cancelled must not be told it
        // timed out, and vice versa.
        using var attemptCts = CreateAttemptSource(cancellationToken);
        var attemptToken = attemptCts?.Token ?? cancellationToken;

        HttpResponseMessage response;
        try
        {
            response = await _httpClient
                .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, attemptToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            attemptCts is not null && attemptCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new HindsightConnectionException(
                $"The request to {request.Method} {request.PathTemplate} exceeded the {_options.Timeout} per-attempt timeout.",
                new TimeoutException())
            {
                WasTimeout = true,
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            throw new HindsightConnectionException(
                $"The request to {request.Method} {request.PathTemplate} could not be completed.", ex);
        }

        using (response)
        {
            var headers = ReadHeaders(response);

            // A body read is bounded by the attempt deadline too, otherwise a
            // server that stalls mid-body defeats the timeout entirely.
            if (streamResponse)
            {
                var stream = await ReadStreamAsync(response, request, attemptToken).ConfigureAwait(false);
                return new HindsightResponse
                {
                    StatusCode = (int)response.StatusCode,
                    Stream = stream,
                    Headers = headers,
                    ContentType = response.Content.Headers.ContentType?.ToString(),
                };
            }

            var body = await ReadBodyAsync(response, request, attemptToken).ConfigureAwait(false);

            return new HindsightResponse
            {
                StatusCode = (int)response.StatusCode,
                Body = body,
                Headers = headers,
                ContentType = response.Content.Headers.ContentType?.ToString(),
            };
        }
    }

    private CancellationTokenSource? CreateAttemptSource(CancellationToken cancellationToken)
    {
        // Zero or negative means no per-attempt deadline, leaving the client's own
        // timeout in effect, rather than a token that cancels immediately and
        // fails every call.
        if (_options.Timeout <= TimeSpan.Zero)
        {
            return null;
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_options.Timeout);
        return cts;
    }

    private Uri BuildUri(HindsightRequest request)
    {
        var baseAddress = _options.BaseAddress.TrimEnd('/');
        var path = request.Path.StartsWith('/') ? request.Path : "/" + request.Path;

        var builder = new StringBuilder(baseAddress).Append(path);
        if (request.Query.Count > 0)
        {
            builder.Append('?');
            var first = true;
            foreach (var pair in request.Query)
            {
                if (!first)
                {
                    builder.Append('&');
                }

                builder.Append(Uri.EscapeDataString(pair.Key))
                    .Append('=')
                    .Append(Uri.EscapeDataString(pair.Value));
                first = false;
            }
        }

        return new Uri(builder.ToString(), UriKind.Absolute);
    }

    private HttpRequestMessage BuildMessage(HindsightRequest request, Uri uri)
    {
        var message = new HttpRequestMessage(request.Method, uri);

        // A GET must carry no body at all. A null ReadOnlyMemory assigned to a
        // nullable body slot would become an empty one, putting a zero-length
        // body on the wire; the explicit null branch avoids that.
        if (request.Multipart is not null)
        {
            message.Content = new MultipartFormDataContentAdapter(request.Multipart);
        }
        else if (request.Body is not null)
        {
            message.Content = new ByteArrayContent(request.Body.Value.ToArray());
            if (request.ContentType is not null)
            {
                message.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(request.ContentType);
            }
        }

        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var apiKey = _apiKeyProvider?.GetApiKeyAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
        if (!string.IsNullOrEmpty(apiKey))
        {
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        if (!message.Headers.UserAgent.TryParseAdd(_options.UserAgent))
        {
            // A caller-supplied User-Agent that is not a valid header value is
            // their mistake and worth surfacing, but it must not stop the call.
            message.Headers.TryAddWithoutValidation("User-Agent", _options.UserAgent);
        }

        // Caller headers last, so a default such as Accept can be overridden.
        foreach (var header in _options.Headers)
        {
            if (string.Equals(header.Key, "Authorization", StringComparison.OrdinalIgnoreCase))
            {
                throw new HindsightConfigurationException(
                    "Authorization cannot be supplied through Headers. Set HindsightClientOptions.ApiKey or "
                    + "ApiKeyProvider instead, so the credential is never handled as an ordinary string.");
            }

            message.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        return message;
    }

    private static Dictionary<string, string> ReadHeaders(HttpResponseMessage response)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in response.Headers)
        {
            headers[header.Key] = string.Join(",", header.Value);
        }

        foreach (var header in response.Content.Headers)
        {
            headers[header.Key] = string.Join(",", header.Value);
        }

        return headers;
    }

    private static async Task<byte[]?> ReadBodyAsync(
        HttpResponseMessage response, HindsightRequest request, CancellationToken cancellationToken)
    {
        if (response.Content is null)
        {
            return null;
        }

        try
        {
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            return bytes;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            throw new HindsightConnectionException(
                $"The response body from {request.Method} {request.PathTemplate} could not be read.", ex);
        }
    }

    private static async Task<Stream> ReadStreamAsync(
        HttpResponseMessage response, HindsightRequest request, CancellationToken cancellationToken)
    {
        if (response.Content is null)
        {
            return Stream.Null;
        }

        try
        {
            return await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            throw new HindsightConnectionException(
                $"The response body from {request.Method} {request.PathTemplate} could not be read.", ex);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Only dispose a client this transport created. A caller-supplied client
        // belongs to the caller, and disposing it would break their DI container.
        if (_ownsClient)
        {
            _httpClient.Dispose();
        }
    }
}

/// <summary>
/// Bridges the library's multipart model onto <see cref="MultipartFormDataContent"/>.
/// </summary>
internal sealed class MultipartFormDataContentAdapter : HttpContent
{
    private readonly MultipartFormDataContent _inner = new();

    internal MultipartFormDataContentAdapter(MultipartPayload payload)
    {
        foreach (var part in payload.Parts)
        {
            if (part.Content is not null)
            {
                var content = new ByteArrayContent(part.Content.Value.ToArray());
                if (part.ContentType is not null)
                {
                    content.Headers.ContentType = MediaTypeHeaderValue.Parse(part.ContentType);
                }

                _inner.Add(content, part.Name);
                continue;
            }

            if (part.ContentSource is not null)
            {
                var streamContent = new StreamContent(part.ContentSource(CancellationToken.None).GetAwaiter().GetResult());
                if (part.ContentType is not null)
                {
                    streamContent.Headers.ContentType = MediaTypeHeaderValue.Parse(part.ContentType);
                }

                _inner.Add(streamContent, part.Name, part.FileName ?? part.Name);
            }
        }
    }

    /// <inheritdoc />
    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        => await _inner.CopyToAsync(stream).ConfigureAwait(false);

    /// <inheritdoc />
    protected override bool TryComputeLength(out long length)
    {
        length = -1;
        return false;
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }
}
