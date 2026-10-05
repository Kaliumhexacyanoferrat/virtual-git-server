#if NET11_0_OR_GREATER
using System.Buffers;
using System.IO.Compression;
#endif

namespace GenHTTP.Modules.Git.Packs;

/// <summary>
/// Decompresses zlib streams embedded into pack files.
/// </summary>
internal static class Zlib
{

    /// <summary>
    /// Decompresses the zlib stream at the beginning of the given source.
    /// </summary>
    /// <param name="source">The compressed data, possibly followed by other data</param>
    /// <param name="destination">Receives the decompressed data, sized to exactly the expected length</param>
    /// <returns>The number of bytes of the zlib stream</returns>
    /// <exception cref="InvalidDataException">Thrown if the data is corrupt or does not match the expected length</exception>
    public static int Inflate(ReadOnlySpan<byte> source, Span<byte> destination)
    {
#if NET11_0_OR_GREATER
        using var decoder = new ZLibDecoder();

        var status = decoder.Decompress(source, destination, out var consumed, out var written);

        if (status == OperationStatus.Done)
        {
            if (written != destination.Length)
            {
                throw new InvalidDataException($"Invalid zlib stream: expected {destination.Length} bytes but got {written}");
            }

            return consumed;
        }

        if (status == OperationStatus.DestinationTooSmall && written == destination.Length)
        {
            // the output is complete, but the end of the stream has not
            // been read yet - which must not produce any further data
            Span<byte> scratch = stackalloc byte[1];

            status = decoder.Decompress(source[consumed..], scratch, out var trailer, out var extra);

            if (status == OperationStatus.Done && extra == 0)
            {
                return consumed + trailer;
            }

            throw new InvalidDataException("Invalid zlib stream: more data than expected");
        }

        throw new InvalidDataException(status == OperationStatus.NeedMoreData ? "Invalid zlib stream: unexpected end of data" : "Invalid zlib stream: corrupt data");
#else
        return ManagedInflater.Inflate(source, destination);
#endif
    }

}
