using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;

using GenHTTP.Modules.Git.Objects;

namespace GenHTTP.Modules.Git;

/// <summary>
/// The SHA-1 name of a git object, such as a commit, a tree or a blob.
/// </summary>
/// <remarks>
/// Object ids are derived from the content of an object, so two objects
/// with the same content always have the same id. This is what allows
/// a virtual repository to be served without storing it: as long as the
/// content does not change, the ids do not change either.
/// </remarks>
public readonly struct GitObjectId : IEquatable<GitObjectId>, IComparable<GitObjectId>
{
    /// <summary>
    /// The number of bytes of an object id.
    /// </summary>
    public const int Size = 20;

    /// <summary>
    /// The number of characters of the hexadecimal representation of an object id.
    /// </summary>
    public const int HexSize = 40;

    private readonly ulong _first;

    private readonly ulong _second;

    private readonly uint _third;

    #region Get-/Setters

    /// <summary>
    /// The id consisting of zeros only, used by the protocol to
    /// express that something does not exist.
    /// </summary>
    public static GitObjectId Zero => default;

    /// <summary>
    /// Whether this is the <see cref="Zero" /> id.
    /// </summary>
    public bool IsZero => _first == 0 && _second == 0 && _third == 0;

    #endregion

    #region Initialization

    private GitObjectId(ulong first, ulong second, uint third)
    {
        _first = first;
        _second = second;
        _third = third;
    }

    /// <summary>
    /// Creates an object id from its binary representation.
    /// </summary>
    /// <param name="bytes">Exactly 20 bytes</param>
    public static GitObjectId FromBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != Size)
        {
            throw new ArgumentException($"An object id consists of exactly {Size} bytes", nameof(bytes));
        }

        return new GitObjectId(BinaryPrimitives.ReadUInt64BigEndian(bytes),
                               BinaryPrimitives.ReadUInt64BigEndian(bytes[8..]),
                               BinaryPrimitives.ReadUInt32BigEndian(bytes[16..]));
    }

    /// <summary>
    /// Parses the hexadecimal representation of an object id.
    /// </summary>
    /// <param name="hex">The 40 hexadecimal characters to be parsed</param>
    /// <exception cref="FormatException">Thrown if the given string is not a valid object id</exception>
    public static GitObjectId Parse(string hex) => Parse(hex.AsSpan());

    /// <summary>
    /// Parses the hexadecimal representation of an object id.
    /// </summary>
    /// <param name="hex">The 40 hexadecimal characters to be parsed</param>
    /// <exception cref="FormatException">Thrown if the given string is not a valid object id</exception>
    public static GitObjectId Parse(ReadOnlySpan<char> hex)
    {
        if (!TryParse(hex, out var id))
        {
            throw new FormatException($"'{hex}' is not a valid object id");
        }

        return id;
    }

    /// <summary>
    /// Attempts to parse the hexadecimal representation of an object id.
    /// </summary>
    /// <param name="hex">The 40 hexadecimal characters to be parsed</param>
    /// <param name="id">The parsed id, if successful</param>
    /// <returns>true, if the given string is a valid object id</returns>
    public static bool TryParse([NotNullWhen(true)] string? hex, out GitObjectId id)
    {
        if (hex == null)
        {
            id = default;
            return false;
        }

        return TryParse(hex.AsSpan(), out id);
    }

    /// <summary>
    /// Attempts to parse the hexadecimal representation of an object id.
    /// </summary>
    /// <param name="hex">The 40 hexadecimal characters to be parsed</param>
    /// <param name="id">The parsed id, if successful</param>
    /// <returns>true, if the given characters are a valid object id</returns>
    public static bool TryParse(ReadOnlySpan<char> hex, out GitObjectId id)
    {
        Span<byte> bytes = stackalloc byte[Size];

        if (hex.Length != HexSize || Convert.FromHexString(hex, bytes, out _, out var written) != System.Buffers.OperationStatus.Done || written != Size)
        {
            id = default;
            return false;
        }

        id = FromBytes(bytes);
        return true;
    }

    /// <summary>
    /// Attempts to parse the hexadecimal representation of an object id given as ASCII bytes.
    /// </summary>
    internal static bool TryParse(ReadOnlySpan<byte> hex, out GitObjectId id)
    {
        Span<char> chars = stackalloc char[HexSize];

        if (hex.Length != HexSize)
        {
            id = default;
            return false;
        }

        for (var i = 0; i < HexSize; i++)
        {
            chars[i] = (char)hex[i];
        }

        return TryParse(chars, out id);
    }

    /// <summary>
    /// Computes the id of a blob (file) with the given content.
    /// </summary>
    /// <remarks>
    /// Allows providers to store the id along with a file so that
    /// <see cref="GitFile.Id" /> can be set without the server
    /// having to read the file to compute it.
    /// </remarks>
    /// <param name="content">The content of the file</param>
    /// <returns>The id of the blob</returns>
    public static GitObjectId ForBlob(ReadOnlySpan<byte> content) => ObjectHasher.Hash(GitObjectType.Blob, content);

    #endregion

    #region Functionality

    /// <summary>
    /// Writes the binary representation of this id to the given span.
    /// </summary>
    /// <param name="destination">A span of at least 20 bytes</param>
    public void CopyTo(Span<byte> destination)
    {
        if (destination.Length < Size)
        {
            throw new ArgumentException($"The destination needs to provide at least {Size} bytes", nameof(destination));
        }

        BinaryPrimitives.WriteUInt64BigEndian(destination, _first);
        BinaryPrimitives.WriteUInt64BigEndian(destination[8..], _second);
        BinaryPrimitives.WriteUInt32BigEndian(destination[16..], _third);
    }

    /// <summary>
    /// Returns the binary representation of this id.
    /// </summary>
    public byte[] ToArray()
    {
        var result = new byte[Size];
        CopyTo(result);
        return result;
    }

    /// <summary>
    /// Writes the lower case hexadecimal representation of this id as ASCII bytes.
    /// </summary>
    internal void WriteHex(Span<byte> destination)
    {
        Span<byte> bytes = stackalloc byte[Size];
        CopyTo(bytes);

        for (var i = 0; i < Size; i++)
        {
            destination[i * 2] = HexDigit(bytes[i] >> 4);
            destination[i * 2 + 1] = HexDigit(bytes[i] & 0xF);
        }
    }

    private static byte HexDigit(int value) => (byte)(value < 10 ? '0' + value : 'a' + value - 10);

    /// <summary>
    /// Returns the lower case hexadecimal representation of this id.
    /// </summary>
    public override string ToString()
    {
        Span<byte> bytes = stackalloc byte[Size];
        CopyTo(bytes);

        return Convert.ToHexStringLower(bytes);
    }

    public bool Equals(GitObjectId other) => _first == other._first && _second == other._second && _third == other._third;

    public override bool Equals(object? obj) => obj is GitObjectId other && Equals(other);

    public override int GetHashCode() => (int)_first ^ (int)(_first >> 32) ^ (int)_second;

    public int CompareTo(GitObjectId other)
    {
        var result = _first.CompareTo(other._first);

        if (result != 0)
        {
            return result;
        }

        result = _second.CompareTo(other._second);

        return result != 0 ? result : _third.CompareTo(other._third);
    }

    public static bool operator ==(GitObjectId left, GitObjectId right) => left.Equals(right);

    public static bool operator !=(GitObjectId left, GitObjectId right) => !left.Equals(right);

    #endregion

}
