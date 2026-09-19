namespace Vectorize.Hindsight.Sdk.Http;

/// <summary>
/// A response body stream that owns the response it came from.
/// </summary>
/// <remarks>
/// <para>
/// A streamed response has to keep its <see cref="HttpResponseMessage"/> alive for as
/// long as the stream is readable — disposing the message disposes the content and
/// with it the stream. But the transport must not leak the message either.
/// </para>
/// <para>
/// This wrapper is the resolution: the caller owns the stream, and disposing it
/// disposes the response. That gives one disposal point, which is what a caller
/// expects from a <see cref="Stream"/> they were handed, and it means the transport
/// can release the message immediately without breaking the body.
/// </para>
/// </remarks>
internal sealed class OwnedResponseStream : Stream
{
    private readonly Stream _inner;
    private readonly IDisposable _owner;
    private bool _disposed;

    internal OwnedResponseStream(Stream inner, IDisposable owner)
    {
        _inner = inner;
        _owner = owner;
    }

    /// <inheritdoc />
    public override bool CanRead => _inner.CanRead;

    /// <inheritdoc />
    public override bool CanSeek => _inner.CanSeek;

    /// <inheritdoc />
    public override bool CanWrite => _inner.CanWrite;

    /// <inheritdoc />
    public override long Length => _inner.Length;

    /// <inheritdoc />
    public override long Position
    {
        get => _inner.Position;
        set => _inner.Position = value;
    }

    /// <inheritdoc />
    public override void Flush() => _inner.Flush();

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);

    /// <inheritdoc />
    public override void SetLength(long value) => _inner.SetLength(value);

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);

    /// <inheritdoc />
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        _inner.ReadAsync(buffer, offset, count, cancellationToken);

    /// <inheritdoc />
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        _inner.ReadAsync(buffer, cancellationToken);

    /// <inheritdoc />
    public override Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken) =>
        _inner.CopyToAsync(destination, bufferSize, cancellationToken);

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (!_disposed && disposing)
        {
            _disposed = true;

            // Dispose the message first: it owns the content, and the content owns the
            // inner stream. Disposing in the other order would leave the message holding
            // a disposed stream.
            _owner.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;

            if (_owner is IAsyncDisposable asyncOwner)
            {
                await asyncOwner.DisposeAsync().ConfigureAwait(false);
            }
            else
            {
                _owner.Dispose();
            }
        }

        await base.DisposeAsync().ConfigureAwait(false);
    }
}
