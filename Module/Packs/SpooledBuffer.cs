namespace GenHTTP.Modules.Git.Packs;

/// <summary>
/// Holds data received from a client (e.g. a push), keeping small amounts
/// in memory and writing larger ones to a temporary file.
/// </summary>
/// <remarks>
/// The data can be read at random positions and from multiple threads
/// concurrently. Temporary files are deleted when the buffer is disposed,
/// or when the file handle is finalized if this never happens.
/// </remarks>
internal sealed class SpooledBuffer : IDisposable
{
    private const string FilePrefix = "genhttp-git-";

    private byte[] _memory;

    private FileStream? _file;

    private volatile bool _disposed;

    #region Get-/Setters

    /// <summary>
    /// The number of bytes held by this buffer.
    /// </summary>
    public long Length { get; private set; }

    /// <summary>
    /// Whether the data has been written to a temporary file.
    /// </summary>
    public bool IsFileBacked => _file != null;

    #endregion

    #region Initialization

    private SpooledBuffer(byte[] memory)
    {
        _memory = memory;
    }

    /// <summary>
    /// Reads the given stream to its end.
    /// </summary>
    /// <param name="source">The stream to be read</param>
    /// <param name="limit">The maximum number of bytes to be accepted</param>
    /// <param name="memoryLimit">The number of bytes to be kept in memory before a temporary file is used</param>
    /// <param name="directory">The directory to create temporary files in (defaults to the temp directory of the system)</param>
    /// <exception cref="BufferLimitExceededException">Thrown if the stream provides more data than allowed</exception>
    public static async ValueTask<SpooledBuffer> ReadAsync(Stream source, long limit, long memoryLimit, string? directory)
    {
        var result = new SpooledBuffer([]);

        try
        {
            var chunk = new byte[81920];

            int read;

            while ((read = await source.ReadAsync(chunk)) > 0)
            {
                if (result.Length + read > limit)
                {
                    throw new BufferLimitExceededException(limit);
                }

                await result.AppendAsync(chunk.AsMemory(0, read), memoryLimit, directory);
            }

            if (result._file != null)
            {
                await result._file.FlushAsync();
            }

            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }

    private async ValueTask AppendAsync(ReadOnlyMemory<byte> data, long memoryLimit, string? directory)
    {
        memoryLimit = Math.Min(memoryLimit, Array.MaxLength);

        if (_file == null && Length + data.Length > memoryLimit)
        {
            _file = CreateFile(directory);

            await _file.WriteAsync(_memory.AsMemory(0, (int)Length));

            _memory = [];
        }

        if (_file != null)
        {
            await _file.WriteAsync(data);
        }
        else
        {
            if (Length + data.Length > _memory.Length)
            {
                var size = Math.Min(Math.Max(Math.Max(_memory.Length * 2L, Length + data.Length), 4096), Math.Max(memoryLimit, Length + data.Length));

                Array.Resize(ref _memory, (int)size);
            }

            data.CopyTo(_memory.AsMemory((int)Length));
        }

        Length += data.Length;
    }

    private static FileStream CreateFile(string? directory)
    {
        var path = Path.Combine(directory ?? Path.GetTempPath(), FilePrefix + Guid.NewGuid().ToString("N") + ".tmp");

        return new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
    }

    #endregion

    #region Functionality

    /// <summary>
    /// Reads the data at the given position.
    /// </summary>
    /// <returns>The number of bytes read, which is less than requested only at the end of the data</returns>
    /// <exception cref="ObjectDisposedException">Thrown if the buffer has already been disposed</exception>
    public int Read(long position, Span<byte> destination)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        ArgumentOutOfRangeException.ThrowIfNegative(position);

        if (position >= Length || destination.IsEmpty)
        {
            return 0;
        }

        var count = (int)Math.Min(destination.Length, Length - position);

        if (_file != null)
        {
            var total = 0;

            while (total < count)
            {
                var read = RandomAccess.Read(_file.SafeFileHandle, destination[total..count], position + total);

                if (read == 0)
                {
                    throw new IOException("Unexpected end of the temporary file");
                }

                total += read;
            }
        }
        else
        {
            _memory.AsSpan((int)position, count).CopyTo(destination);
        }

        return count;
    }

    /// <summary>
    /// Opens a stream reading the data sequentially, starting at the given position.
    /// </summary>
    public Stream OpenRead(long position = 0) => new ReadStream(this, position);

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;

            _file?.Dispose();
            _memory = [];
        }
    }

    #endregion

    #region Stream

    private sealed class ReadStream(SpooledBuffer buffer, long position) : Stream
    {
        private long _position = position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => buffer.Length;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] destination, int offset, int count) => Read(destination.AsSpan(offset, count));

        public override int Read(Span<byte> destination)
        {
            var read = buffer.Read(_position, destination);

            _position += read;

            return read;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken cancellationToken = default) => new(Read(destination.Span));

        public override Task<int> ReadAsync(byte[] destination, int offset, int count, CancellationToken cancellationToken) => Task.FromResult(Read(destination.AsSpan(offset, count)));

        public override void Flush() { }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] source, int offset, int count) => throw new NotSupportedException();

    }

    #endregion

}

/// <summary>
/// Thrown if a client sends more data than allowed.
/// </summary>
internal sealed class BufferLimitExceededException(long limit) : Exception($"The request exceeds the maximum size of {limit} bytes")
{

    public long Limit { get; } = limit;

}
