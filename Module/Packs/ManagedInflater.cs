namespace GenHTTP.Modules.Git.Packs;

/// <summary>
/// Decompresses zlib streams (RFC 1950 / 1951), reporting the number
/// of input bytes consumed.
/// </summary>
/// <remarks>
/// Pack files store compressed objects back to back without recording
/// their compressed length, so the end of an object can only be found
/// by decompressing it. <c>DeflateStream</c> and <c>ZLibStream</c> read
/// ahead and do not tell how much input they actually used, so this
/// implementation is used on .NET 10, where <c>ZLibDecoder</c> is not
/// available.
/// </remarks>
internal static class ManagedInflater
{
    private const int FastBits = 10;

    private const int MaxBits = 15;

    private static readonly ushort[] LengthBase = [3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 15, 17, 19, 23, 27, 31, 35, 43, 51, 59, 67, 83, 99, 115, 131, 163, 195, 227, 258];

    private static readonly byte[] LengthExtra = [0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0];

    private static readonly ushort[] DistanceBase = [1, 2, 3, 4, 5, 7, 9, 13, 17, 25, 33, 49, 65, 97, 129, 193, 257, 385, 513, 769, 1025, 1537, 2049, 3073, 4097, 6145, 8193, 12289, 16385, 24577];

    private static readonly byte[] DistanceExtra = [0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13];

    private static readonly byte[] CodeLengthOrder = [16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15];

    private static readonly Huffman FixedLiterals;

    private static readonly Huffman FixedDistances;

    static ManagedInflater()
    {
        var lengths = new byte[288];

        lengths.AsSpan(0, 144).Fill(8);
        lengths.AsSpan(144, 112).Fill(9);
        lengths.AsSpan(256, 24).Fill(7);
        lengths.AsSpan(280, 8).Fill(8);

        FixedLiterals = new Huffman(lengths);

        var distances = new byte[30];
        distances.AsSpan().Fill(5);

        FixedDistances = new Huffman(distances);
    }

    #region Huffman codes

    private sealed class Huffman
    {

        public short[] Counts { get; } = new short[MaxBits + 1];

        public short[] Symbols { get; }

        /// <summary>
        /// Lookup table for codes of up to <see cref="FastBits" /> bits, indexed
        /// by the next bits of the input (in stream order). Each entry holds
        /// the symbol and the length of its code, or zero if the code is longer.
        /// </summary>
        public int[] Fast { get; } = new int[1 << FastBits];

        public Huffman(ReadOnlySpan<byte> lengths)
        {
            Symbols = new short[lengths.Length];

            foreach (var length in lengths)
            {
                Counts[length]++;
            }

            Counts[0] = 0;

            var left = 1;

            for (var length = 1; length <= MaxBits; length++)
            {
                left <<= 1;
                left -= Counts[length];

                if (left < 0)
                {
                    throw new InvalidDataException("Invalid deflate stream: over-subscribed code");
                }
            }

            Span<short> offsets = stackalloc short[MaxBits + 2];

            for (var length = 1; length <= MaxBits; length++)
            {
                offsets[length + 1] = (short)(offsets[length] + Counts[length]);
            }

            for (var symbol = 0; symbol < lengths.Length; symbol++)
            {
                if (lengths[symbol] != 0)
                {
                    Symbols[offsets[lengths[symbol]]++] = (short)symbol;
                }
            }

            Span<int> next = stackalloc int[MaxBits + 1];

            var code = 0;

            for (var length = 1; length <= MaxBits; length++)
            {
                code = (code + Counts[length - 1]) << 1;
                next[length] = code;
            }

            for (var symbol = 0; symbol < lengths.Length; symbol++)
            {
                int length = lengths[symbol];

                if (length == 0)
                {
                    continue;
                }

                var assigned = next[length]++;

                if (length > FastBits)
                {
                    continue;
                }

                var reversed = Reverse(assigned, length);

                for (var suffix = 0; suffix < 1 << (FastBits - length); suffix++)
                {
                    Fast[reversed | (suffix << length)] = (symbol << 4) | length;
                }
            }
        }

        private static int Reverse(int code, int length)
        {
            var result = 0;

            for (var i = 0; i < length; i++)
            {
                result = (result << 1) | (code & 1);
                code >>= 1;
            }

            return result;
        }

    }

    #endregion

    #region Bit input

    private ref struct BitReader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;

        private int _position;

        private ulong _buffer;

        private int _count;

        private void Refill()
        {
            while (_count <= 56 && _position < _data.Length)
            {
                _buffer |= (ulong)_data[_position++] << _count;
                _count += 8;
            }
        }

        public int Peek(int bits)
        {
            if (_count < bits)
            {
                Refill();
            }

            return (int)(_buffer & ((1UL << bits) - 1));
        }

        public void Consume(int bits)
        {
            if (bits > _count)
            {
                throw new InvalidDataException("Invalid deflate stream: unexpected end of data");
            }

            _buffer >>= bits;
            _count -= bits;
        }

        public int Read(int bits)
        {
            if (bits == 0)
            {
                return 0;
            }

            var value = Peek(bits);
            Consume(bits);

            return value;
        }

        /// <summary>
        /// Skips to the next byte boundary and returns the position of the
        /// next unread byte, discarding all buffered bits.
        /// </summary>
        public int AlignToByte()
        {
            var position = _position - _count / 8;

            _buffer = 0;
            _count = 0;
            _position = position;

            return position;
        }

        public void Skip(int bytes) => _position += bytes;

    }

    #endregion

    #region Functionality

    /// <summary>
    /// Decompresses the zlib stream at the beginning of the given source.
    /// </summary>
    /// <param name="source">The compressed data, possibly followed by other data</param>
    /// <param name="destination">Receives the decompressed data, sized to exactly the expected length</param>
    /// <returns>The number of bytes of the zlib stream</returns>
    public static int Inflate(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        if (source.Length < 6)
        {
            throw new InvalidDataException("Invalid zlib stream: unexpected end of data");
        }

        var cmf = source[0];
        var flags = source[1];

        if ((cmf & 0x0F) != 8 || cmf >> 4 > 7 || (cmf * 256 + flags) % 31 != 0 || (flags & 0x20) != 0)
        {
            throw new InvalidDataException("Invalid zlib stream: unsupported header");
        }

        var input = new BitReader(source[2..]);

        var written = 0;

        bool last;

        do
        {
            last = input.Read(1) == 1;

            switch (input.Read(2))
            {
                case 0:
                    written = CopyStored(ref input, source[2..], destination, written);
                    break;
                case 1:
                    written = InflateBlock(ref input, FixedLiterals, FixedDistances, destination, written);
                    break;
                case 2:
                    {
                        var (literals, distances) = ReadDynamicCodes(ref input);
                        written = InflateBlock(ref input, literals, distances, destination, written);
                        break;
                    }
                default:
                    throw new InvalidDataException("Invalid deflate stream: invalid block type");
            }
        }
        while (!last);

        if (written != destination.Length)
        {
            throw new InvalidDataException($"Invalid zlib stream: expected {destination.Length} bytes but got {written}");
        }

        var end = 2 + input.AlignToByte();

        if (source.Length < end + 4)
        {
            throw new InvalidDataException("Invalid zlib stream: missing checksum");
        }

        var expected = (uint)(source[end] << 24 | source[end + 1] << 16 | source[end + 2] << 8 | source[end + 3]);

        if (Adler32(destination) != expected)
        {
            throw new InvalidDataException("Invalid zlib stream: checksum mismatch");
        }

        return end + 4;
    }

    private static int CopyStored(ref BitReader input, ReadOnlySpan<byte> data, Span<byte> destination, int written)
    {
        var position = input.AlignToByte();

        if (data.Length < position + 4)
        {
            throw new InvalidDataException("Invalid deflate stream: unexpected end of data");
        }

        var length = data[position] | data[position + 1] << 8;
        var complement = data[position + 2] | data[position + 3] << 8;

        if ((length ^ 0xFFFF) != complement)
        {
            throw new InvalidDataException("Invalid deflate stream: corrupt stored block");
        }

        position += 4;

        if (data.Length < position + length)
        {
            throw new InvalidDataException("Invalid deflate stream: unexpected end of data");
        }

        if (written + length > destination.Length)
        {
            throw new InvalidDataException("Invalid zlib stream: more data than expected");
        }

        data.Slice(position, length).CopyTo(destination[written..]);

        input.Skip(4 + length);

        return written + length;
    }

    private static (Huffman Literals, Huffman Distances) ReadDynamicCodes(ref BitReader input)
    {
        var literalCount = input.Read(5) + 257;
        var distanceCount = input.Read(5) + 1;
        var codeLengthCount = input.Read(4) + 4;

        if (literalCount > 286 || distanceCount > 30)
        {
            throw new InvalidDataException("Invalid deflate stream: too many codes");
        }

        Span<byte> codeLengths = stackalloc byte[19];

        for (var i = 0; i < codeLengthCount; i++)
        {
            codeLengths[CodeLengthOrder[i]] = (byte)input.Read(3);
        }

        var codeLengthCode = new Huffman(codeLengths);

        Span<byte> lengths = stackalloc byte[literalCount + distanceCount];

        var index = 0;

        while (index < lengths.Length)
        {
            var symbol = Decode(ref input, codeLengthCode);

            if (symbol < 16)
            {
                lengths[index++] = (byte)symbol;
                continue;
            }

            byte value = 0;
            int repeat;

            if (symbol == 16)
            {
                if (index == 0)
                {
                    throw new InvalidDataException("Invalid deflate stream: repeat without previous length");
                }

                value = lengths[index - 1];
                repeat = 3 + input.Read(2);
            }
            else if (symbol == 17)
            {
                repeat = 3 + input.Read(3);
            }
            else
            {
                repeat = 11 + input.Read(7);
            }

            if (index + repeat > lengths.Length)
            {
                throw new InvalidDataException("Invalid deflate stream: too many code lengths");
            }

            lengths.Slice(index, repeat).Fill(value);
            index += repeat;
        }

        if (lengths[256] == 0)
        {
            throw new InvalidDataException("Invalid deflate stream: missing end-of-block code");
        }

        return (new Huffman(lengths[..literalCount]), new Huffman(lengths[literalCount..]));
    }

    private static int InflateBlock(ref BitReader input, Huffman literals, Huffman distances, Span<byte> destination, int written)
    {
        while (true)
        {
            var symbol = Decode(ref input, literals);

            if (symbol < 256)
            {
                if (written >= destination.Length)
                {
                    throw new InvalidDataException("Invalid zlib stream: more data than expected");
                }

                destination[written++] = (byte)symbol;
            }
            else if (symbol == 256)
            {
                return written;
            }
            else
            {
                symbol -= 257;

                if (symbol >= LengthBase.Length)
                {
                    throw new InvalidDataException("Invalid deflate stream: invalid length code");
                }

                var length = LengthBase[symbol] + input.Read(LengthExtra[symbol]);

                var distanceSymbol = Decode(ref input, distances);

                if (distanceSymbol >= DistanceBase.Length)
                {
                    throw new InvalidDataException("Invalid deflate stream: invalid distance code");
                }

                var distance = DistanceBase[distanceSymbol] + input.Read(DistanceExtra[distanceSymbol]);

                if (distance > written)
                {
                    throw new InvalidDataException("Invalid deflate stream: distance too far back");
                }

                if (written + length > destination.Length)
                {
                    throw new InvalidDataException("Invalid zlib stream: more data than expected");
                }

                if (distance >= length)
                {
                    destination.Slice(written - distance, length).CopyTo(destination[written..]);
                    written += length;
                }
                else
                {
                    // overlapping copy, repeating the last bytes
                    for (var i = 0; i < length; i++)
                    {
                        destination[written] = destination[written - distance];
                        written++;
                    }
                }
            }
        }
    }

    private static int Decode(ref BitReader input, Huffman huffman)
    {
        var entry = huffman.Fast[input.Peek(FastBits)];

        if (entry != 0)
        {
            input.Consume(entry & 0xF);
            return entry >> 4;
        }

        int code = 0, first = 0, index = 0;

        for (var length = 1; length <= MaxBits; length++)
        {
            code |= input.Read(1);

            int count = huffman.Counts[length];

            if (code - count < first)
            {
                return huffman.Symbols[index + (code - first)];
            }

            index += count;
            first += count;
            first <<= 1;
            code <<= 1;
        }

        throw new InvalidDataException("Invalid deflate stream: invalid code");
    }

    private static uint Adler32(ReadOnlySpan<byte> data)
    {
        const uint modulus = 65521;

        uint a = 1, b = 0;

        while (!data.IsEmpty)
        {
            var chunk = data[..Math.Min(data.Length, 5552)];

            foreach (var value in chunk)
            {
                a += value;
                b += a;
            }

            a %= modulus;
            b %= modulus;

            data = data[chunk.Length..];
        }

        return b << 16 | a;
    }

    #endregion

}
