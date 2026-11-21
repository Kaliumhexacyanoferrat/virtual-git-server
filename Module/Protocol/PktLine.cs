using System.Text;

using GenHTTP.Modules.Git.Packs;

namespace GenHTTP.Modules.Git.Protocol;

internal enum PktLineKind
{
    Data,
    Flush,
    Delimiter,
    ResponseEnd,
    End
}

/// <summary>
/// A single packet of the pkt-line framing used by all git protocols.
/// </summary>
internal readonly struct PktLine(PktLineKind kind, ReadOnlyMemory<byte> payload)
{

    public PktLineKind Kind { get; } = kind;

    public ReadOnlyMemory<byte> Payload { get; } = payload;

    public bool IsData => Kind == PktLineKind.Data;

    /// <summary>
    /// The payload with a trailing line feed removed.
    /// </summary>
    public ReadOnlySpan<byte> Line
    {
        get
        {
            var span = Payload.Span;
            return span.Length > 0 && span[^1] == '\n' ? span[..^1] : span;
        }
    }

    public string Text => Encoding.UTF8.GetString(Line);

}

/// <summary>
/// Reads pkt-lines from a buffer holding a complete request.
/// </summary>
internal sealed class PktLineReader(ReadOnlyMemory<byte> buffer)
{
    private int _position;

    /// <summary>
    /// The data that has not been read yet (e.g. a pack following the commands of a push).
    /// </summary>
    public ReadOnlyMemory<byte> Remaining => buffer[_position..];

    public bool IsAtEnd => _position >= buffer.Length;

    public PktLine Read()
    {
        if (_position >= buffer.Length)
        {
            return new PktLine(PktLineKind.End, default);
        }

        if (_position + 4 > buffer.Length)
        {
            throw new ProtocolException("Malformed pkt-line: truncated length");
        }

        var length = ParseLength(buffer.Span.Slice(_position, 4));

        _position += 4;

        if (TryGetControl(length, out var control))
        {
            return control;
        }

        var payload = length - 4;

        if (_position + payload > buffer.Length)
        {
            throw new ProtocolException("Malformed pkt-line: truncated payload");
        }

        var result = buffer.Slice(_position, payload);

        _position += payload;

        return new PktLine(PktLineKind.Data, result);
    }

    /// <summary>
    /// Parses the four hexadecimal digits at the beginning of a pkt-line.
    /// </summary>
    internal static int ParseLength(ReadOnlySpan<byte> header)
    {
        var length = 0;

        foreach (var c in header)
        {
            var digit = c switch
            {
                >= (byte)'0' and <= (byte)'9' => c - '0',
                >= (byte)'a' and <= (byte)'f' => c - 'a' + 10,
                >= (byte)'A' and <= (byte)'F' => c - 'A' + 10,
                _ => throw new ProtocolException("Malformed pkt-line: invalid length")
            };

            length = (length << 4) | digit;
        }

        return length;
    }

    /// <summary>
    /// Checks whether the given length denotes a control packet without payload.
    /// </summary>
    internal static bool TryGetControl(int length, out PktLine line)
    {
        switch (length)
        {
            case 0:
                line = new PktLine(PktLineKind.Flush, default);
                return true;
            case 1:
                line = new PktLine(PktLineKind.Delimiter, default);
                return true;
            case 2:
                line = new PktLine(PktLineKind.ResponseEnd, default);
                return true;
            case 3:
                throw new ProtocolException("Malformed pkt-line: invalid length");
        }

        line = default;
        return false;
    }

}

/// <summary>
/// Reads pkt-lines from a buffer that might be too large to be kept in
/// memory (e.g. the commands of a push, followed by the pack).
/// </summary>
internal sealed class SpooledPktLineReader(SpooledBuffer buffer)
{

    /// <summary>
    /// The position of the data that has not been read yet.
    /// </summary>
    public long Position { get; private set; }

    public PktLine Read()
    {
        if (Position >= buffer.Length)
        {
            return new PktLine(PktLineKind.End, default);
        }

        Span<byte> header = stackalloc byte[4];

        if (buffer.Read(Position, header) != header.Length)
        {
            throw new ProtocolException("Malformed pkt-line: truncated length");
        }

        var length = PktLineReader.ParseLength(header);

        Position += 4;

        if (PktLineReader.TryGetControl(length, out var control))
        {
            return control;
        }

        var payload = new byte[length - 4];

        if (buffer.Read(Position, payload) != payload.Length)
        {
            throw new ProtocolException("Malformed pkt-line: truncated payload");
        }

        Position += payload.Length;

        return new PktLine(PktLineKind.Data, payload);
    }

}

/// <summary>
/// Thrown if a client sends a request that violates the protocol.
/// The message is sent to the client.
/// </summary>
internal sealed class ProtocolException(string message) : Exception(message);
