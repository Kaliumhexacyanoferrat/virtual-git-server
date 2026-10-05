using System.Buffers;
using System.Text;

namespace GenHTTP.Modules.Git.Protocol;

/// <summary>
/// Collects pkt-lines in memory, to be written to the response
/// stream in larger chunks.
/// </summary>
internal sealed class PktLineWriter
{
    /// <summary>
    /// The maximum size of a packet, including the four bytes of the length.
    /// </summary>
    public const int MaximumPacketSize = 65520;

    private readonly ArrayBufferWriter<byte> _buffer = new(4096);

    #region Get-/Setters

    public int Length => _buffer.WrittenCount;

    #endregion

    #region Functionality

    /// <summary>
    /// Writes the given text, terminated by a line feed.
    /// </summary>
    public PktLineWriter Line(string text)
    {
        var count = Encoding.UTF8.GetByteCount(text);

        var span = Begin(count + 1);

        Encoding.UTF8.GetBytes(text, span);
        span[count] = (byte)'\n';

        _buffer.Advance(4 + count + 1);

        return this;
    }

    /// <summary>
    /// Writes the given payload as is.
    /// </summary>
    public PktLineWriter Data(ReadOnlySpan<byte> payload)
    {
        var span = Begin(payload.Length);

        payload.CopyTo(span);

        _buffer.Advance(4 + payload.Length);

        return this;
    }

    /// <summary>
    /// Writes the given data to a side band, split into multiple
    /// packets if needed.
    /// </summary>
    /// <param name="band">The band to write to (1 = data, 2 = progress, 3 = error)</param>
    /// <param name="data">The data to be written</param>
    /// <param name="maximumPacketSize">The maximum size of a packet, depending on the negotiated capability</param>
    public PktLineWriter Band(byte band, ReadOnlySpan<byte> data, int maximumPacketSize = MaximumPacketSize)
    {
        var chunkSize = maximumPacketSize - 5;

        do
        {
            var chunk = data[..Math.Min(chunkSize, data.Length)];

            var span = Begin(chunk.Length + 1);

            span[0] = band;
            chunk.CopyTo(span[1..]);

            _buffer.Advance(4 + 1 + chunk.Length);

            data = data[chunk.Length..];
        }
        while (!data.IsEmpty);

        return this;
    }

    /// <summary>
    /// Writes a flush packet ("0000").
    /// </summary>
    public PktLineWriter Flush() => Special("0000"u8);

    /// <summary>
    /// Writes a delimiter packet ("0001"), separating sections in protocol v2.
    /// </summary>
    public PktLineWriter Delimiter() => Special("0001"u8);

    /// <summary>
    /// Writes an error packet, which aborts the operation on the client.
    /// </summary>
    public PktLineWriter Error(string message) => Line("ERR " + Sanitize(message));

    /// <summary>
    /// Writes the collected data to the given stream and resets the buffer.
    /// </summary>
    public async ValueTask CopyToAsync(Stream target)
    {
        if (_buffer.WrittenCount > 0)
        {
            await target.WriteAsync(_buffer.WrittenMemory);
            _buffer.ResetWrittenCount();
        }
    }

    public byte[] ToArray() => _buffer.WrittenSpan.ToArray();

    /// <summary>
    /// Reduces a message to a single line, as required for most protocol messages.
    /// </summary>
    public static string Sanitize(string message)
    {
        var result = message.ReplaceLineEndings(" ").Trim();

        return result.Length > 1000 ? result[..1000] : result;
    }

    private PktLineWriter Special(ReadOnlySpan<byte> packet)
    {
        packet.CopyTo(_buffer.GetSpan(4));
        _buffer.Advance(4);

        return this;
    }

    private Span<byte> Begin(int payload)
    {
        var total = payload + 4;

        if (total > MaximumPacketSize)
        {
            throw new InvalidOperationException("The payload exceeds the maximum size of a pkt-line");
        }

        var span = _buffer.GetSpan(total);

        WriteLength(span, total);

        return span.Slice(4, payload);
    }

    private static void WriteLength(Span<byte> destination, int length)
    {
        const string digits = "0123456789abcdef";

        destination[0] = (byte)digits[(length >> 12) & 0xF];
        destination[1] = (byte)digits[(length >> 8) & 0xF];
        destination[2] = (byte)digits[(length >> 4) & 0xF];
        destination[3] = (byte)digits[length & 0xF];
    }

    #endregion

}
