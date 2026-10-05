namespace GenHTTP.Modules.Git.Protocol;

/// <summary>
/// Wraps everything written to it into side band packets of band 1,
/// which is how pack data is transferred when the client requested
/// the side band capability (and always with protocol v2).
/// </summary>
internal sealed class SidebandStream(Stream inner, int maximumPacketSize) : Stream
{
    private readonly byte[] _buffer = new byte[maximumPacketSize];

    private int _count = 5;

    #region Get-/Setters

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    #endregion

    #region Functionality

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        while (!buffer.IsEmpty)
        {
            var chunk = Math.Min(_buffer.Length - _count, buffer.Length);

            buffer[..chunk].CopyTo(_buffer.AsSpan(_count));

            _count += chunk;
            buffer = buffer[chunk..];

            if (_count == _buffer.Length)
            {
                inner.Write(Frame().Span);
            }
        }
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (!buffer.IsEmpty)
        {
            var chunk = Math.Min(_buffer.Length - _count, buffer.Length);

            buffer.Span[..chunk].CopyTo(_buffer.AsSpan(_count));

            _count += chunk;
            buffer = buffer[chunk..];

            if (_count == _buffer.Length)
            {
                await EmitAsync(cancellationToken);
            }
        }
    }

    public override void Flush()
    {
        if (_count > 5)
        {
            inner.Write(Frame().Span);
        }

        inner.Flush();
    }

    public override async Task FlushAsync(CancellationToken cancellationToken)
    {
        if (_count > 5)
        {
            await EmitAsync(cancellationToken);
        }

        await inner.FlushAsync(cancellationToken);
    }

    private ValueTask EmitAsync(CancellationToken cancellationToken) => inner.WriteAsync(Frame(), cancellationToken);

    /// <summary>
    /// Completes the packet collected so far and resets the buffer.
    /// </summary>
    /// <remarks>
    /// The returned memory is valid until the next write, as the
    /// buffer is reused.
    /// </remarks>
    private ReadOnlyMemory<byte> Frame()
    {
        const string digits = "0123456789abcdef";

        var length = _count;

        _buffer[0] = (byte)digits[(length >> 12) & 0xF];
        _buffer[1] = (byte)digits[(length >> 8) & 0xF];
        _buffer[2] = (byte)digits[(length >> 4) & 0xF];
        _buffer[3] = (byte)digits[length & 0xF];
        _buffer[4] = 1;

        _count = 5;

        return _buffer.AsMemory(0, length);
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    #endregion

}
