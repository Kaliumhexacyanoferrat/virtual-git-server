using System.Buffers.Binary;
using System.Security.Cryptography;

using GenHTTP.Modules.Git.Objects;

namespace GenHTTP.Modules.Git.Packs;

/// <summary>
/// Restrictions applied while parsing a pack, protecting the
/// server against packs that would exhaust its resources.
/// </summary>
internal sealed record PackLimits(long MaximumObjectSize, long MaximumTotalSize, int MaximumObjects, int MaximumDeltaDepth)
{

    public static PackLimits FromPushSize(long maximumPushSize)
    {
        // objects compress well, so the unpacked size may exceed the size of the pack
        var unpacked = maximumPushSize > long.MaxValue / 8 ? long.MaxValue : maximumPushSize * 8;

        return new PackLimits(Math.Min(unpacked, Array.MaxLength), unpacked, 1_000_000, 4096);
    }

}

/// <summary>
/// Parses pack files sent by clients, resolving deltas.
/// </summary>
/// <remarks>
/// <para>
/// The pack is read from a <see cref="SpooledBuffer" /> and is not kept
/// in memory as a whole. Parsing determines the location, type and id of
/// every object, keeping only commits and trees in memory. The content of
/// all other objects is read from the buffer again when requested (see
/// <see cref="ReceivedPack.Read" />).
/// </para>
/// <para>
/// Packs need to be self-contained: deltas against objects not contained
/// in the pack ("thin packs") are rejected, which is why the server
/// advertises the <c>no-thin</c> capability.
/// </para>
/// </remarks>
internal static class PackParser
{
    private const int HeaderSize = 12;

    /// <summary>
    /// The number of bytes needed to parse the header of any object.
    /// </summary>
    private const int MaximumObjectHeaderSize = 64;

    /// <summary>
    /// Parses the pack stored in the given buffer.
    /// </summary>
    /// <param name="buffer">The buffer holding the pack</param>
    /// <param name="offset">The position of the pack within the buffer, which extends to the end of the buffer</param>
    /// <param name="limits">The limits to be enforced</param>
    /// <param name="cacheSize">The number of bytes of delta bases to be kept in memory</param>
    /// <returns>The objects of the pack, by id</returns>
    /// <exception cref="InvalidDataException">Thrown if the pack is malformed or exceeds the given limits</exception>
    public static ReceivedPack Parse(SpooledBuffer buffer, long offset, PackLimits limits, long cacheSize)
    {
        var end = buffer.Length - GitObjectId.Size;

        if (end - offset < HeaderSize)
        {
            throw new InvalidDataException("Invalid pack: missing header");
        }

        using var reader = new SequentialReader(buffer, offset, end);

        reader.Ensure(HeaderSize);

        var header = reader.Available;

        if (!header[..4].SequenceEqual("PACK"u8))
        {
            throw new InvalidDataException("Invalid pack: missing header");
        }

        var version = BinaryPrimitives.ReadUInt32BigEndian(header[4..]);

        if (version is not (2 or 3))
        {
            throw new InvalidDataException($"Invalid pack: unsupported version {version}");
        }

        var count = BinaryPrimitives.ReadUInt32BigEndian(header[8..]);

        if (count > limits.MaximumObjects)
        {
            throw new InvalidDataException($"Pack exceeds the maximum number of {limits.MaximumObjects} objects");
        }

        reader.Advance(HeaderSize);

        var entries = new List<ReceivedObject>((int)count);

        var byOffset = new Dictionary<long, ReceivedObject>((int)count);

        long total = 0;

        using var scratch = new ScratchBuffer();

        for (var i = 0; i < count; i++)
        {
            var entry = ReadObject(reader, scratch, offset, limits, ref total);

            entries.Add(entry);
            byOffset.Add(entry.Offset, entry);
        }

        if (reader.Remaining != 0)
        {
            throw new InvalidDataException("Invalid pack: unexpected data after the last object");
        }

        Span<byte> checksum = stackalloc byte[GitObjectId.Size];

        if (buffer.Read(end, checksum) != GitObjectId.Size || !checksum.SequenceEqual(reader.GetHash()))
        {
            throw new InvalidDataException("Invalid pack: checksum mismatch");
        }

        var pack = new ReceivedPack(buffer, limits.MaximumObjectSize, cacheSize);

        Resolve(pack, entries, byOffset, limits, total);

        return pack;
    }

    private static ReceivedObject ReadObject(SequentialReader reader, ScratchBuffer scratch, long packOffset, PackLimits limits, ref long total)
    {
        var offset = reader.Position - packOffset;

        if (reader.Remaining <= 0)
        {
            throw new InvalidDataException("Invalid pack: unexpected end of data");
        }

        reader.Ensure(MaximumObjectHeaderSize);

        var header = reader.Available;

        var position = 0;

        var current = header[position++];

        var kind = (current >> 4) & 7;

        long size = current & 0x0F;

        var shift = 4;

        while ((current & 0x80) != 0)
        {
            if (position >= header.Length || shift > 56)
            {
                throw new InvalidDataException("Invalid pack: malformed object header");
            }

            current = header[position++];

            size |= (long)(current & 0x7F) << shift;
            shift += 7;
        }

        long baseOffset = 0;

        var baseId = GitObjectId.Zero;

        switch (kind)
        {
            case (int)GitObjectType.Commit or (int)GitObjectType.Tree or (int)GitObjectType.Blob or (int)GitObjectType.Tag:
                break;

            case ReceivedObject.OffsetDelta:
                {
                    if (position >= header.Length)
                    {
                        throw new InvalidDataException("Invalid pack: unexpected end of data");
                    }

                    current = header[position++];

                    long distance = current & 0x7F;

                    while ((current & 0x80) != 0)
                    {
                        if (position >= header.Length || distance > (long.MaxValue >> 8))
                        {
                            throw new InvalidDataException("Invalid pack: malformed delta offset");
                        }

                        current = header[position++];
                        distance = ((distance + 1) << 7) | (long)(current & 0x7F);
                    }

                    baseOffset = offset - distance;

                    if (distance <= 0 || baseOffset < HeaderSize)
                    {
                        throw new InvalidDataException("Invalid pack: delta base offset out of range");
                    }

                    break;
                }

            case ReceivedObject.ReferenceDelta:
                {
                    if (position + GitObjectId.Size > header.Length)
                    {
                        throw new InvalidDataException("Invalid pack: unexpected end of data");
                    }

                    baseId = GitObjectId.FromBytes(header.Slice(position, GitObjectId.Size));
                    position += GitObjectId.Size;
                    break;
                }

            default:
                throw new InvalidDataException($"Invalid pack: unknown object type {kind}");
        }

        reader.Advance(position);

        if (size > limits.MaximumObjectSize)
        {
            throw new InvalidDataException("Pack contains an object exceeding the allowed size");
        }

        // deflate compresses by a factor of 1032 at most, so a size beyond
        // that cannot be valid and must not be allocated
        if (size > reader.Remaining * 1032 + 64)
        {
            throw new InvalidDataException("Invalid pack: object size exceeds the compressed data");
        }

        total += size;

        if (total > limits.MaximumTotalSize)
        {
            throw new InvalidDataException("Pack exceeds the allowed size when unpacked");
        }

        var entry = new ReceivedObject(offset, reader.Position, kind, size, baseOffset, baseId);

        // commits and trees are needed to validate the push, so they are kept,
        // while everything else is read again when needed
        var retain = kind is (int)GitObjectType.Commit or (int)GitObjectType.Tree;

        var data = retain ? new byte[size] : scratch.Get(size);

        var content = data.AsSpan(0, (int)size);

        entry.CompressedLength = reader.Inflate(content);

        if (!entry.IsDelta)
        {
            entry.Type = (GitObjectType)kind;
            entry.Id = ObjectHasher.Hash(entry.Type, content);
            entry.Resolved = true;

            if (retain)
            {
                entry.Data = data;
            }
        }

        reader.Advance(entry.CompressedLength);

        return entry;
    }

    private static void Resolve(ReceivedPack pack, List<ReceivedObject> entries, Dictionary<long, ReceivedObject> byOffset, PackLimits limits, long total)
    {
        var byBase = new Dictionary<ReceivedObject, List<ReceivedObject>>();
        var byId = new Dictionary<GitObjectId, List<ReceivedObject>>();

        foreach (var entry in entries)
        {
            if (entry.Kind == ReceivedObject.OffsetDelta)
            {
                if (byOffset.TryGetValue(entry.BaseOffset, out var baseEntry))
                {
                    baseEntry.IsBase = true;
                    Register(byBase, baseEntry, entry);
                }
            }
            else if (entry.Kind == ReceivedObject.ReferenceDelta)
            {
                Register(byId, entry.BaseId, entry);
            }
        }

        // objects are processed depth-first, so the base of a delta has been
        // processed recently and is likely to still be cached
        var pending = new Stack<ReceivedObject>();

        for (var i = entries.Count - 1; i >= 0; i--)
        {
            if (entries[i].Resolved)
            {
                pending.Push(entries[i]);
            }
        }

        while (pending.TryPop(out var current))
        {
            if (!current.Resolved)
            {
                var baseEntry = current.Base!;

                if (baseEntry.Depth + 1 > limits.MaximumDeltaDepth)
                {
                    throw new InvalidDataException("Invalid pack: delta chain too long");
                }

                var data = pack.Load(current);

                total += data.Length;

                if (total > limits.MaximumTotalSize)
                {
                    throw new InvalidDataException("Pack exceeds the allowed size when unpacked");
                }

                current.Type = baseEntry.Type;
                current.Id = ObjectHasher.Hash(current.Type, data);
                current.Depth = baseEntry.Depth + 1;
                current.Resolved = true;

                if (current.Type is GitObjectType.Commit or GitObjectType.Tree)
                {
                    current.Data = data;
                }

                if (!current.IsBase && byId.ContainsKey(current.Id))
                {
                    // only known now that the id has been computed
                    current.IsBase = true;
                    pack.Remember(current, data);
                }
            }

            pack.Add(current);

            if (byBase.Remove(current, out var offsetDependents))
            {
                Schedule(current, offsetDependents, pending);
            }

            if (byId.Remove(current.Id, out var idDependents))
            {
                current.IsBase = true;
                Schedule(current, idDependents, pending);
            }
        }

        if (entries.Any(e => !e.Resolved))
        {
            throw new InvalidDataException("Invalid pack: unable to resolve delta base (thin packs are not supported)");
        }
    }

    private static void Schedule(ReceivedObject baseEntry, List<ReceivedObject> dependents, Stack<ReceivedObject> pending)
    {
        for (var i = dependents.Count - 1; i >= 0; i--)
        {
            var dependent = dependents[i];

            if (!dependent.Resolved && dependent.Base == null)
            {
                dependent.Base = baseEntry;
                pending.Push(dependent);
            }
        }
    }

    private static void Register<T>(Dictionary<T, List<ReceivedObject>> index, T key, ReceivedObject entry) where T : notnull
    {
        if (!index.TryGetValue(key, out var list))
        {
            list = [];
            index.Add(key, list);
        }

        list.Add(entry);
    }

    #region Reading

    /// <summary>
    /// Reads a pack sequentially through a window kept in memory,
    /// computing the checksum of the data read.
    /// </summary>
    private sealed class SequentialReader(SpooledBuffer buffer, long position, long end) : IDisposable
    {
        private const int InitialSize = 64 * 1024;

        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);

        private byte[] _window = PackBuffers.Rent(InitialSize);

        private int _start;

        private int _count;

        /// <summary>
        /// The position of the next byte to be read within the buffer.
        /// </summary>
        public long Position { get; private set; } = position;

        /// <summary>
        /// The number of bytes left to be read.
        /// </summary>
        public long Remaining => end - Position;

        /// <summary>
        /// The bytes read into the window, starting at the current position.
        /// </summary>
        public ReadOnlySpan<byte> Available => _window.AsSpan(_start, _count);

        /// <summary>
        /// Makes sure that the given number of bytes is available, unless
        /// the end of the data is reached before.
        /// </summary>
        public void Ensure(long count)
        {
            var needed = (int)Math.Min(Math.Min(count, Remaining), Array.MaxLength);

            if (_count >= needed)
            {
                return;
            }

            if (needed > _window.Length)
            {
                var larger = PackBuffers.Rent(Math.Min(Math.Max(needed, _window.Length * 2L), Array.MaxLength));

                Available.CopyTo(larger);

                PackBuffers.Return(_window);

                _window = larger;
                _start = 0;
            }
            else if (_start + needed > _window.Length)
            {
                Available.CopyTo(_window);
                _start = 0;
            }

            // read as much as fits into the window to reduce the number of reads
            var target = (int)Math.Min(_window.Length - _start, Remaining);

            while (_count < target)
            {
                var read = buffer.Read(Position + _count, _window.AsSpan(_start + _count, target - _count));

                if (read == 0)
                {
                    throw new InvalidDataException("Invalid pack: unexpected end of data");
                }

                _count += read;
            }
        }

        public void Advance(int count)
        {
            _hash.AppendData(_window, _start, count);

            _start += count;
            _count -= count;

            Position += count;
        }

        /// <summary>
        /// Decompresses the zlib stream at the current position without advancing.
        /// </summary>
        /// <returns>The length of the zlib stream</returns>
        public int Inflate(Span<byte> destination)
        {
            // the compressed size is not stored in the pack, so the window needs to
            // be large enough for the stream - which may exceed the decompressed size
            // a little for incompressible data
            long window = destination.Length + (destination.Length >> 10) + 1024;

            while (true)
            {
                Ensure(window);

                var available = Available;

                try
                {
                    return Zlib.Inflate(available, destination);
                }
                catch (InvalidDataException) when (available.Length < Remaining && available.Length < Array.MaxLength)
                {
                    // the stream might be longer than expected, so try again with more data
                    window = Math.Max(window, available.Length) * 2;
                }
            }
        }

        public byte[] GetHash() => _hash.GetHashAndReset();

        public void Dispose()
        {
            _hash.Dispose();

            PackBuffers.Return(_window);
            _window = [];
        }

    }

    #endregion

}
