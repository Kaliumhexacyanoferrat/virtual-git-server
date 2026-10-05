using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace GenHTTP.Modules.Git.Objects;

/// <summary>
/// Computes the ids of git objects.
/// </summary>
/// <remarks>
/// The id of an object is the SHA-1 hash of a header ("blob 42\0")
/// followed by the uncompressed content of the object.
/// </remarks>
internal static class ObjectHasher
{

    public static GitObjectId Hash(GitObjectType type, ReadOnlySpan<byte> content)
    {
        using var hash = Begin(type, content.Length);

        hash.AppendData(content);

        return Complete(hash);
    }

    /// <summary>
    /// Starts an incremental hash for an object of the given type and size,
    /// allowing the content to be appended in chunks.
    /// </summary>
    public static IncrementalHash Begin(GitObjectType type, long size)
    {
        var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);

        Span<byte> header = stackalloc byte[32];

        var length = WriteHeader(header, type, size);

        hash.AppendData(header[..length]);

        return hash;
    }

    public static GitObjectId Complete(IncrementalHash hash)
    {
        Span<byte> result = stackalloc byte[GitObjectId.Size];

        hash.GetHashAndReset(result);

        return GitObjectId.FromBytes(result);
    }

    private static int WriteHeader(Span<byte> destination, GitObjectType type, long size)
    {
        var length = Encoding.ASCII.GetBytes(type.GetName(), destination);

        destination[length++] = (byte)' ';

        if (!size.TryFormat(destination[length..], out var written, default, CultureInfo.InvariantCulture))
        {
            throw new InvalidOperationException("Unable to format object size");
        }

        length += written;

        destination[length++] = 0;

        return length;
    }

}
