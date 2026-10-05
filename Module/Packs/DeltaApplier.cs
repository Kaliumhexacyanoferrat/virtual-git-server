namespace GenHTTP.Modules.Git.Packs;

/// <summary>
/// Reconstructs objects stored as a delta against another object.
/// </summary>
/// <remarks>
/// A delta starts with the sizes of the base and the resulting object,
/// followed by instructions to either copy a range of the base or to
/// insert literal bytes (see gitformat-pack).
/// </remarks>
internal static class DeltaApplier
{

    public static byte[] Apply(ReadOnlySpan<byte> source, ReadOnlySpan<byte> delta, long maximumSize)
    {
        var position = 0;

        var sourceSize = ReadSize(delta, ref position);
        var targetSize = ReadSize(delta, ref position);

        if (sourceSize != source.Length)
        {
            throw new InvalidDataException("Invalid delta: size of base object does not match");
        }

        if (targetSize > maximumSize || targetSize > Array.MaxLength)
        {
            throw new InvalidDataException("Invalid delta: resulting object exceeds the allowed size");
        }

        // every byte of instructions produces at most 127 inserted bytes or a
        // copy of (a part of) the source, so larger sizes cannot be valid
        var bound = (long)(delta.Length - position) * Math.Max(127, Math.Min(source.Length, 0xFFFFFF));

        if (targetSize > bound)
        {
            throw new InvalidDataException("Invalid delta: resulting size exceeds what the instructions can produce");
        }

        var target = new byte[targetSize];

        var written = 0;

        while (position < delta.Length)
        {
            int instruction = delta[position++];

            if ((instruction & 0x80) != 0)
            {
                long offset = 0, size = 0;

                for (var i = 0; i < 4; i++)
                {
                    if ((instruction & (1 << i)) != 0)
                    {
                        offset |= (long)ReadByte(delta, ref position) << (8 * i);
                    }
                }

                for (var i = 0; i < 3; i++)
                {
                    if ((instruction & (0x10 << i)) != 0)
                    {
                        size |= (long)ReadByte(delta, ref position) << (8 * i);
                    }
                }

                if (size == 0)
                {
                    size = 0x10000;
                }

                if (offset + size > source.Length || written + size > target.Length)
                {
                    throw new InvalidDataException("Invalid delta: copy instruction out of bounds");
                }

                source.Slice((int)offset, (int)size).CopyTo(target.AsSpan(written));
                written += (int)size;
            }
            else if (instruction != 0)
            {
                if (position + instruction > delta.Length || written + instruction > target.Length)
                {
                    throw new InvalidDataException("Invalid delta: insert instruction out of bounds");
                }

                delta.Slice(position, instruction).CopyTo(target.AsSpan(written));

                position += instruction;
                written += instruction;
            }
            else
            {
                throw new InvalidDataException("Invalid delta: reserved instruction");
            }
        }

        if (written != target.Length)
        {
            throw new InvalidDataException("Invalid delta: resulting object is incomplete");
        }

        return target;
    }

    private static long ReadSize(ReadOnlySpan<byte> delta, ref int position)
    {
        long result = 0;

        var shift = 0;

        byte current;

        do
        {
            if (shift > 56)
            {
                throw new InvalidDataException("Invalid delta: size too large");
            }

            current = ReadByte(delta, ref position);

            result |= (long)(current & 0x7F) << shift;
            shift += 7;
        }
        while ((current & 0x80) != 0);

        return result;
    }

    private static byte ReadByte(ReadOnlySpan<byte> delta, ref int position)
    {
        if (position >= delta.Length)
        {
            throw new InvalidDataException("Invalid delta: unexpected end of data");
        }

        return delta[position++];
    }

}
