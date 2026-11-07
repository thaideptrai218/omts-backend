namespace OMTS.Api.Common;

/// <summary>
/// Read-only view of a request body that refuses to read past <c>limit</c> bytes, the way Kestrel's own request
/// body limit does (<see cref="BadHttpRequestException"/> with 413).
/// </summary>
internal sealed class LengthLimitedStream(Stream inner, long limit) : Stream
{
    private long _read;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => _read; set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        Count(await inner.ReadAsync(buffer, cancellationToken));

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    private int Count(int bytes)
    {
        _read += bytes;
        if (_read > limit)
        {
            throw new BadHttpRequestException($"Request body too large. The max request body size is {limit} bytes.", StatusCodes.Status413PayloadTooLarge);
        }

        return bytes;
    }
}
