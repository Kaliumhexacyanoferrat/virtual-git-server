using System.Buffers.Binary;
using System.Security.Cryptography;

using GenHTTP.Modules.Git.Objects;

namespace GenHTTP.Modules.Git.Packs;

/// <summary>
/// An object received with a pack.
/// </summary>
internal sealed record ReceivedObject(GitObjectId Id, GitObjectType Type, byte[] Data);

/// <summary>
/// Restrictions applied while parsing a pack, protecting the
/// server against packs that would exhaust its memory.
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
/// Packs need to be self-contained: deltas against objects not contained
/// in the pack ("thin packs") are rejected, which is why the server
/// advertises the <c>no-thin</c> capability.
/// </remarks>
internal static class PackParser
{
    private const int TypeOffsetDelta = 6;

    private const int TypeReferenceDelta = 7;

    private sealed class Entry
    {

        public long Offset { get; init; }

        public int Kind { get; init; }

        public byte[]? Data { get; set; }

        public long BaseOffset { get; init; }

        public GitObjectId BaseId { get; init; }

        public GitObjectType Type { get; set; }

        public GitObjectId Id { get; set; }

        public int Depth { get; set; }

        public bool Resolved { get; set; }

    }

    /// <summary>
    /// Parses the given pack.
    /// </summary>
    /// <returns>The objects of the pack, by id</returns>
    /// <exception cref="InvalidDataException">Thrown if the pack is malformed or exceeds the given limits</exception>
    public static Dictionary<GitObjectId, ReceivedObject> Parse(ReadOnlySpan<byte> pack, PackLimits limits)
    {
        if (pack.Length < 32 || !pack[..4].SequenceEqual("PACK"u8))
        {
            throw new InvalidDataException("Invalid pack: missing header");
        }

        var version = BinaryPrimitives.ReadUInt32BigEndian(pack[4..]);

        if (version is not (2 or 3))
        {
            throw new InvalidDataException($"Invalid pack: unsupported version {version}");
        }

        var count = BinaryPrimitives.ReadUInt32BigEndian(pack[8..]);

        if (count > limits.MaximumObjects)
        {
            throw new InvalidDataException($"Pack exceeds the maximum number of {limits.MaximumObjects} objects");
        }

        var end = pack.Length - GitObjectId.Size;

        var entries = new List<Entry>((int)count);

        var position = 12;

        long total = 0;

        for (var i = 0; i < count; i++)
        {
            var offset = position;

            if (position >= end)
            {
                throw new InvalidDataException("Invalid pack: unexpected end of data");
            }

            var current = pack[position++];

            var kind = (current >> 4) & 7;

            long size = current & 0x0F;

            var shift = 4;

            while ((current & 0x80) != 0)
            {
                if (position >= end || shift > 56)
                {
                    throw new InvalidDataException("Invalid pack: malformed object header");
                }

                current = pack[position++];

                size |= (long)(current & 0x7F) << shift;
                shift += 7;
            }

            long baseOffset = 0;

            var baseId = GitObjectId.Zero;

            switch (kind)
            {
                case (int)GitObjectType.Commit or (int)GitObjectType.Tree or (int)GitObjectType.Blob or (int)GitObjectType.Tag:
                    break;

                case TypeOffsetDelta:
                    {
                        if (position >= end)
                        {
                            throw new InvalidDataException("Invalid pack: unexpected end of data");
                        }

                        current = pack[position++];

                        long distance = current & 0x7F;

                        while ((current & 0x80) != 0)
                        {
                            if (position >= end || distance > (long.MaxValue >> 8))
                            {
                                throw new InvalidDataException("Invalid pack: malformed delta offset");
                            }

                            current = pack[position++];
                            distance = ((distance + 1) << 7) | (long)(current & 0x7F);
                        }

                        baseOffset = offset - distance;

                        if (distance <= 0 || baseOffset < 12)
                        {
                            throw new InvalidDataException("Invalid pack: delta base offset out of range");
                        }

                        break;
                    }

                case TypeReferenceDelta:
                    {
                        if (position + GitObjectId.Size > end)
                        {
                            throw new InvalidDataException("Invalid pack: unexpected end of data");
                        }

                        baseId = GitObjectId.FromBytes(pack.Slice(position, GitObjectId.Size));
                        position += GitObjectId.Size;
                        break;
                    }

                default:
                    throw new InvalidDataException($"Invalid pack: unknown object type {kind}");
            }

            if (size > limits.MaximumObjectSize)
            {
                throw new InvalidDataException("Pack contains an object exceeding the allowed size");
            }

            total += size;

            if (total > limits.MaximumTotalSize)
            {
                throw new InvalidDataException("Pack exceeds the allowed size when unpacked");
            }

            var data = new byte[size];

            position += Zlib.Inflate(pack[position..end], data);

            entries.Add(new Entry
            {
                Offset = offset,
                Kind = kind,
                Data = data,
                BaseOffset = baseOffset,
                BaseId = baseId
            });
        }

        if (position != end)
        {
            throw new InvalidDataException("Invalid pack: unexpected data after the last object");
        }

        Span<byte> checksum = stackalloc byte[GitObjectId.Size];

        SHA1.HashData(pack[..end], checksum);

        if (!checksum.SequenceEqual(pack[end..]))
        {
            throw new InvalidDataException("Invalid pack: checksum mismatch");
        }

        return Resolve(entries, limits);
    }

    private static Dictionary<GitObjectId, ReceivedObject> Resolve(List<Entry> entries, PackLimits limits)
    {
        var result = new Dictionary<GitObjectId, ReceivedObject>(entries.Count);

        var byOffset = new Dictionary<long, List<Entry>>();
        var byId = new Dictionary<GitObjectId, List<Entry>>();

        var queue = new Queue<Entry>();

        foreach (var entry in entries)
        {
            if (entry.Kind == TypeOffsetDelta)
            {
                Register(byOffset, entry.BaseOffset, entry);
            }
            else if (entry.Kind == TypeReferenceDelta)
            {
                Register(byId, entry.BaseId, entry);
            }
            else
            {
                entry.Type = (GitObjectType)entry.Kind;
                entry.Id = ObjectHasher.Hash(entry.Type, entry.Data);
                entry.Resolved = true;

                queue.Enqueue(entry);
            }
        }

        long total = entries.Sum(e => (long)e.Data!.Length);

        while (queue.TryDequeue(out var resolved))
        {
            result.TryAdd(resolved.Id, new ReceivedObject(resolved.Id, resolved.Type, resolved.Data!));

            if (byOffset.Remove(resolved.Offset, out var offsetDependents))
            {
                ResolveDependents(resolved, offsetDependents, queue, limits, ref total);
            }

            if (byId.Remove(resolved.Id, out var idDependents))
            {
                ResolveDependents(resolved, idDependents, queue, limits, ref total);
            }
        }

        if (entries.Any(e => !e.Resolved))
        {
            throw new InvalidDataException("Invalid pack: unable to resolve delta base (thin packs are not supported)");
        }

        return result;
    }

    private static void ResolveDependents(Entry baseEntry, List<Entry> dependents, Queue<Entry> queue, PackLimits limits, ref long total)
    {
        foreach (var dependent in dependents)
        {
            if (dependent.Resolved)
            {
                continue;
            }

            if (baseEntry.Depth + 1 > limits.MaximumDeltaDepth)
            {
                throw new InvalidDataException("Invalid pack: delta chain too long");
            }

            var data = DeltaApplier.Apply(baseEntry.Data, dependent.Data, limits.MaximumObjectSize);

            total += data.Length;

            if (total > limits.MaximumTotalSize)
            {
                throw new InvalidDataException("Pack exceeds the allowed size when unpacked");
            }

            dependent.Data = data;
            dependent.Type = baseEntry.Type;
            dependent.Id = ObjectHasher.Hash(dependent.Type, data);
            dependent.Depth = baseEntry.Depth + 1;
            dependent.Resolved = true;

            queue.Enqueue(dependent);
        }
    }

    private static void Register<T>(Dictionary<T, List<Entry>> index, T key, Entry entry) where T : notnull
    {
        if (!index.TryGetValue(key, out var list))
        {
            list = [];
            index.Add(key, list);
        }

        list.Add(entry);
    }

}
