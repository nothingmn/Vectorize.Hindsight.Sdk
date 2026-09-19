using Vectorize.Hindsight.Sdk.Errors;
using System.Text.Json;
using System.Text.Json.Serialization;
using Vectorize.Hindsight.Sdk.Http;
using Vectorize.Hindsight.Sdk.Models;

namespace Vectorize.Hindsight.Sdk;

/// <summary>
/// The three upload operations, hand-written.
/// </summary>
/// <remarks>
/// <para>
/// These are not generated, because their parameters are byte streams and
/// filenames rather than a JSON body. Generating a JSON-shaped signature for a
/// file upload would be a lie the compiler cannot catch, and a caller would
/// discover it only from the server.
/// </para>
/// <para>
/// Only three operations take <c>multipart/form-data</c>, so hand-writing them is
/// a smaller risk than teaching the generator about streams. The generator names
/// each one and asserts the set is exactly these three, so a fourth appearing in
/// a future revision is a build failure rather than a silent gap.
/// </para>
/// </remarks>
public sealed partial class HindsightClient
{
    /// <summary>
    /// Converts uploaded files into memories.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each file is parsed server-side by the configured parser chain, then the
    /// extracted text is retained. The parser can be overridden per file or for
    /// the whole call through <paramref name="request"/>.
    /// </para>
    /// <para>
    /// The server expects <c>request</c> as a JSON <i>string</i> field, not a JSON
    /// part, so it is serialized to text and sent as one.
    /// </para>
    /// </remarks>
    /// <param name="bankId">The bank to retain into.</param>
    /// <param name="files">The files to upload.</param>
    /// <param name="request">Conversion and retain options. Null sends the defaults.</param>
    /// <param name="cancellationToken">Honoured through every await, including uploads.</param>
    /// <returns>The retain outcome.</returns>
    public async Task<FileRetainResponse> FileRetainAsync(
        string bankId,
        IReadOnlyList<HindsightFileUpload> files,
        FileRetainRequest? request,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bankId);
        ArgumentNullException.ThrowIfNull(files);
        HindsightValidation.RequireAny(files, nameof(files), "At least one file is required.");

        var parts = new List<MultipartPart>(files.Count + 1)
        {
            new()
            {
                Name = "request",
                ContentType = "text/plain",
                Content = SerializeBody(request ?? new FileRetainRequest()),
            },
        };

        foreach (var file in files)
        {
            parts.Add(ToFilePart(file, "files"));
        }

        var path = $"{TenantPrefix}/banks/{Uri.EscapeDataString(bankId)}/files/retain";
        var httpRequest = new HindsightRequest
        {
            Method = HttpMethod.Post,
            Path = path,
            PathTemplate = "POST /v1/default/banks/{bank_id}/files/retain",
            Multipart = BuildMultipart(parts),

            // This writes memories, so a retry could duplicate them.
            IsIdempotent = false,
        };

        return await SendAsync<FileRetainResponse>(httpRequest, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Imports a whole bank from an exported archive.
    /// </summary>
    /// <remarks>
    /// Async on the server: the returned operation id is polled through
    /// <see cref="GetOperationStatusAsync"/>.
    /// </remarks>
    /// <param name="bankId">The bank to import into.</param>
    /// <param name="archive">The archive to upload.</param>
    /// <param name="targetBankId">Import into a different bank id than the archive's.</param>
    /// <param name="includeData">Include the memories themselves, not only the configuration.</param>
    /// <param name="cancellationToken">Honoured through every await.</param>
    /// <returns>The transfer submission.</returns>
    public async Task<BankTransferSubmitResponse> ImportBankTransferAsync(
        string bankId,
        HindsightFileUpload archive,
        string? targetBankId,
        bool? includeData,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bankId);
        ArgumentNullException.ThrowIfNull(archive);

        var query = new List<KeyValuePair<string, string>>();
        if (!string.IsNullOrEmpty(targetBankId))
        {
            query.Add(new KeyValuePair<string, string>("target_bank_id", targetBankId));
        }

        if (includeData is { } include)
        {
            query.Add(new KeyValuePair<string, string>("include_data", include ? "true" : "false"));
        }

        var path = $"{TenantPrefix}/banks/{Uri.EscapeDataString(bankId)}/transfer/import";
        var httpRequest = new HindsightRequest
        {
            Method = HttpMethod.Post,
            Path = path,
            PathTemplate = "POST /v1/default/banks/{bank_id}/transfer/import",
            Query = query,
            Multipart = BuildMultipart([ToFilePart(archive, "file")]),
            IsIdempotent = false,
        };

        return await SendAsync<BankTransferSubmitResponse>(httpRequest, cancellationToken).ConfigureAwait(false);
    }

    private static MultipartPart ToFilePart(HindsightFileUpload upload, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(upload.FileName))
        {
            throw new HindsightValidationException(
                "Every upload needs a filename; the server uses it to pick a parser.")
            {
                ParamName = nameof(upload),
            };
        }

        if (upload.ContentSource is not null)
        {
            return MultipartPart.FromStream(
                fieldName, upload.FileName, upload.ContentSource, upload.ContentType);
        }

        if (upload.Content is { } content)
        {
            return new MultipartPart
            {
                Name = fieldName,
                FileName = upload.FileName,
                Content = content,
                ContentType = upload.ContentType ?? "application/octet-stream",
            };
        }

        throw new HindsightValidationException(
            $"Upload '{upload.FileName}' supplies neither a stream nor bytes.")
        {
            ParamName = nameof(upload),
        };
    }
}

/// <summary>
/// Options for <see cref="HindsightClient.FileRetainAsync"/>.
/// </summary>
/// <remarks>
/// The specification describes this as "JSON string with FileRetainRequest model"
/// in prose without declaring the model, so it is modelled here from the
/// documented field names. Unknown fields are preserved, so a caller targeting a
/// newer server can send an option this version does not know about.
/// </remarks>
public sealed class FileRetainRequest
{
    /// <summary>
    /// The parser, or an ordered fallback chain of parsers.
    /// </summary>
    /// <remarks>
    /// A single value tries one parser; a list is tried in sequence until one
    /// succeeds. Null falls back to the server's <c>HINDSIGHT_API_FILE_PARSER</c>.
    /// Only parsers enabled on the server may be requested; others answer 400.
    /// </remarks>
    [JsonPropertyName("parser")]
    public List<string>? Parser { get; set; }

    /// <summary>Per-file parser control.</summary>
    [JsonPropertyName("files_metadata")]
    public List<FileRetainMetadata>? FilesMetadata { get; set; }

    /// <summary>A label describing the source, passed through to extraction.</summary>
    [JsonPropertyName("context")]
    public string? Context { get; set; }

    /// <summary>Whether to process in the background. The server defaults to true for uploads.</summary>
    [JsonPropertyName("async")]
    public bool? Async { get; set; }

    /// <summary>
    /// Fields this version does not model.
    /// </summary>
    /// <remarks>
    /// Reachable in both directions, so a newer server option survives a
    /// round trip rather than being dropped silently.
    /// </remarks>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

/// <summary>Per-file conversion options.</summary>
public sealed class FileRetainMetadata
{
    /// <summary>The filename this entry applies to.</summary>
    [JsonPropertyName("filename")]
    public string? FileName { get; set; }

    /// <summary>The parser, or an ordered fallback chain, for this file.</summary>
    [JsonPropertyName("parser")]
    public List<string>? Parser { get; set; }

    /// <summary>Fields this version does not model.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

/// <summary>
/// One file to upload.
/// </summary>
/// <remarks>
/// Exactly one of <see cref="ContentSource"/> and <see cref="Content"/> is
/// supplied. A stream keeps a large file out of memory; bytes suit a small
/// fixture.
/// </remarks>
public sealed class HindsightFileUpload
{
    /// <summary>The filename sent to the server, which uses it to pick a parser.</summary>
    public required string FileName { get; init; }

    /// <summary>
    /// Opens the file's content. Called once per attempt.
    /// </summary>
    /// <remarks>
    /// A factory rather than a stream, because a retry needs a fresh,
    /// unconsumed stream and a <see cref="Stream"/> cannot be rewound in
    /// general. It is also the only route by which this library reads a file:
    /// the library itself performs no file I/O.
    /// </remarks>
    public Func<CancellationToken, Task<Stream>>? ContentSource { get; init; }

    /// <summary>The file's bytes, for content already in memory.</summary>
    public ReadOnlyMemory<byte>? Content { get; init; }

    /// <summary>The content type. Defaults to <c>application/octet-stream</c>.</summary>
    public string? ContentType { get; init; }

    /// <summary>Creates an upload from a stream factory.</summary>
    /// <param name="fileName">The filename.</param>
    /// <param name="contentSource">Opens the content; called per attempt.</param>
    /// <param name="contentType">The content type.</param>
    public static HindsightFileUpload FromStream(
        string fileName, Func<CancellationToken, Task<Stream>> contentSource, string? contentType = null) =>
        new() { FileName = fileName, ContentSource = contentSource, ContentType = contentType };

    /// <summary>Creates an upload that reads a file from disk.</summary>
    /// <param name="path">The path to read.</param>
    /// <param name="contentType">The content type.</param>
    public static HindsightFileUpload FromPath(string path, string? contentType = null) =>
        new()
        {
            FileName = Path.GetFileName(path),
            ContentSource = _ => Task.FromResult<Stream>(
                new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true)),
            ContentType = contentType,
        };

    /// <summary>Creates an upload from bytes.</summary>
    /// <param name="fileName">The filename.</param>
    /// <param name="content">The bytes.</param>
    /// <param name="contentType">The content type.</param>
    public static HindsightFileUpload FromBytes(
        string fileName, ReadOnlyMemory<byte> content, string? contentType = null) =>
        new() { FileName = fileName, Content = content, ContentType = contentType };
}

/// <summary>Argument checks shared by the hand-written operations.</summary>
internal static class HindsightValidation
{
    /// <summary>Requires a non-empty collection.</summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <param name="values">The collection.</param>
    /// <param name="paramName">The parameter name, for the exception.</param>
    /// <param name="message">The failure message.</param>
    internal static void RequireAny<T>(IReadOnlyCollection<T> values, string paramName, string message)
    {
        if (values.Count == 0)
        {
            throw new HindsightValidationException(message) { ParamName = paramName };
        }
    }
}
