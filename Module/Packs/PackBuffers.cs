using System.Buffers;

namespace GenHTTP.Modules.Git.Packs;

/// <summary>
/// Provides temporary buffers used while processing packs.
/// </summary>
/// <remarks>
/// Small buffers are pooled. Larger buffers are allocated on demand, so
/// the memory needed for large objects is released after a push has been
/// processed instead of being kept by the pool.
/// </remarks>
internal static class PackBuffers
{
    private const int MaximumPooledSize = 1024 * 1024;

    /// <summary>
    /// Provides a buffer of at least the given size.
    /// </summary>
    public static byte[] Rent(long size)
    {
        if (size == 0)
        {
            return [];
        }

        return size <= MaximumPooledSize ? ArrayPool<byte>.Shared.Rent((int)size) : new byte[size];
    }

    public static void Return(byte[] buffer)
    {
        if (buffer.Length > 0 && buffer.Length <= MaximumPooledSize)
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

}

/// <summary>
/// A temporary buffer that is reused for multiple operations,
/// growing with the size requested.
/// </summary>
internal sealed class ScratchBuffer : IDisposable
{
    private byte[] _buffer = [];

    /// <summary>
    /// Provides a buffer of at least the given size, invalidating
    /// the buffer previously returned.
    /// </summary>
    public byte[] Get(long size)
    {
        if (_buffer.Length < size)
        {
            PackBuffers.Return(_buffer);

            // release the old buffer before allocating the new one
            _buffer = [];
            _buffer = PackBuffers.Rent(size);
        }

        return _buffer;
    }

    public void Dispose()
    {
        PackBuffers.Return(_buffer);
        _buffer = [];
    }

}
