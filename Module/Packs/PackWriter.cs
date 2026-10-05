using System.Buffers.Binary;
using System.IO.Compression;

using GenHTTP.Modules.Git.Objects;

namespace GenHTTP.Modules.Git.Packs;

/// <summary>
/// An object to be written to a pack.
/// </summary>
internal readonly struct PackEntry
{
    private readonly ReadOnlyMemory<byte> _data;

    private readonly BlobReference? _blob;

    public GitObjectType Type { get; }

    public PackEntry(GitObjectType type, ReadOnlyMemory<byte> data)
    {
        Type = type;
        _data = data;
        _blob = null;
    }

    public PackEntry(BlobReference blob)
    {
        Type = GitObjectType.Blob;
        _data = default;
        _blob = blob;
    }

    public ValueTask<ReadOnlyMemory<byte>> GetContentAsync() => _blob != null ? _blob.ReadAsync() : new(_data);

}

/// <summary>
/// Writes pack files (version 2) without any deltas.
/// </summary>
/// <remarks>
/// Deltas are an optimization: a pack consisting of plain objects is
/// perfectly valid, and clients re-compress their repository on their own
/// (<c>git gc</c>). As the server does not store packs, computing deltas
/// for every request would cost more than the bandwidth it saves for
/// the small repositories this library is meant for.
/// </remarks>
internal static class PackWriter
{

    public static async ValueTask WriteAsync(Stream output, IReadOnlyList<PackEntry> entries, CompressionLevel compression)
    {
        using var hashing = new HashingStream(output);

        var header = new byte[12];

        "PACK"u8.CopyTo(header);

        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), 2);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8), (uint)entries.Count);

        await hashing.WriteAsync(header);

        var objectHeader = new byte[16];

        foreach (var entry in entries)
        {
            var content = await entry.GetContentAsync();

            var length = WriteObjectHeader(objectHeader, entry.Type, content.Length);

            await hashing.WriteAsync(objectHeader.AsMemory(0, length));

            await using (var zlib = new ZLibStream(hashing, compression, leaveOpen: true))
            {
                await zlib.WriteAsync(content);
            }
        }

        await output.WriteAsync(hashing.GetHashAndReset());
    }

    /// <summary>
    /// Writes the type and the uncompressed size of an object as a
    /// variable length integer (3 bits type, 4 bits of size in the first byte,
    /// then 7 bits of size per byte).
    /// </summary>
    internal static int WriteObjectHeader(Span<byte> destination, GitObjectType type, long size)
    {
        var position = 0;

        var current = (byte)(((int)type << 4) | (int)(size & 0x0F));

        size >>= 4;

        while (size > 0)
        {
            destination[position++] = (byte)(current | 0x80);

            current = (byte)(size & 0x7F);
            size >>= 7;
        }

        destination[position++] = current;

        return position;
    }

}
