using Vectorize.Hindsight.Sdk.Http;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Vectorize.Hindsight.Sdk;

/// <summary>
/// Body serialization and response reading shared by every generated operation.
/// </summary>
/// <remarks>
/// Generated methods call these rather than touching the serializer directly, so
/// the serialization rules — frozen options, source-generated metadata, no
/// reflection over caller types — are implemented once and cannot be forgotten in
/// one of 87 methods.
/// </remarks>
public sealed partial class HindsightClient
{
    /// <summary>
    /// Serializes a request body.
    /// </summary>
    /// <remarks>
    /// The serializer options are the client's frozen instance, so the metadata
    /// cache is populated once per client rather than rebuilt per call.
    /// </remarks>
    /// <typeparam name="TBody">The body model.</typeparam>
    /// <param name="body">The body. Never null: a null body means no body at all.</param>
    internal ReadOnlyMemory<byte> SerializeBody<TBody>(TBody body)
    {
        ArgumentNullException.ThrowIfNull(body);

        var info = (JsonTypeInfo<TBody>)_json.Options.GetTypeInfo(typeof(TBody));
        return JsonSerializer.SerializeToUtf8Bytes(body, info);
    }

    /// <summary>
    /// Reads a response body that was declared as JSON but has no modelled shape.
    /// </summary>
    /// <param name="body">The raw body bytes.</param>
    internal static JsonElement ReadJsonElement(byte[]? body) =>
        body is null || body.Length == 0 ? default : JsonDocument.Parse(body).RootElement.Clone();

    /// <summary>
    /// Builds the multipart payload for the three upload operations.
    /// </summary>
    /// <param name="parts">The parts.</param>
    internal static MultipartPayload BuildMultipart(params IReadOnlyList<MultipartPart> parts) =>
        new() { Parts = parts };
}
