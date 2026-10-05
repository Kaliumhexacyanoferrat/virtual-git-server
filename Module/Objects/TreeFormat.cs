using System.Text;

namespace GenHTTP.Modules.Git.Objects;

/// <summary>
/// The modes git uses for tree entries, as numeric values of the
/// octal representation found in tree objects.
/// </summary>
internal static class TreeModes
{
    public const int Regular = 0x81A4; // 100644

    public const int Executable = 0x81ED; // 100755

    public const int Symlink = 0xA000; // 120000

    public const int Tree = 0x4000; // 40000

    public const int Gitlink = 0xE000; // 160000

    public static int FromFileMode(GitFileMode mode) => mode switch
    {
        GitFileMode.Regular => Regular,
        GitFileMode.Executable => Executable,
        GitFileMode.Symlink => Symlink,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null)
    };

    public static GitFileMode? ToFileMode(int mode) => mode switch
    {
        Regular => GitFileMode.Regular,
        Executable => GitFileMode.Executable,
        Symlink => GitFileMode.Symlink,
        _ => null
    };

}

/// <summary>
/// An entry of a tree object.
/// </summary>
internal readonly record struct TreeEntry(byte[] Name, int Mode, GitObjectId Id)
{

    public bool IsTree => Mode == TreeModes.Tree;

    public string GetName() => Encoding.UTF8.GetString(Name);

}

/// <summary>
/// Reads and writes git tree objects.
/// </summary>
internal static class TreeFormat
{

    #region Writing

    /// <summary>
    /// Serializes the given entries into a tree object, sorting
    /// them the way git expects.
    /// </summary>
    public static byte[] Serialize(List<TreeEntry> entries)
    {
        entries.Sort(Compare);

        var length = 0;

        for (var i = 0; i < entries.Count; i++)
        {
            if (i > 0 && Compare(entries[i - 1], entries[i]) == 0)
            {
                throw new InvalidOperationException($"The tree contains the entry '{entries[i].GetName()}' more than once");
            }

            length += GetModeLength(entries[i].Mode) + 1 + entries[i].Name.Length + 1 + GitObjectId.Size;
        }

        var result = new byte[length];
        var span = result.AsSpan();

        foreach (var entry in entries)
        {
            var written = WriteMode(span, entry.Mode);

            span[written++] = (byte)' ';

            entry.Name.CopyTo(span[written..]);
            written += entry.Name.Length;

            span[written++] = 0;

            entry.Id.CopyTo(span[written..]);
            written += GitObjectId.Size;

            span = span[written..];
        }

        return result;
    }

    /// <summary>
    /// Compares two entries the way git does (see <c>base_name_compare</c>):
    /// byte-wise, with directories being treated as if their name ended
    /// with a slash.
    /// </summary>
    public static int Compare(TreeEntry x, TreeEntry y)
    {
        var length = Math.Min(x.Name.Length, y.Name.Length);

        var result = x.Name.AsSpan(0, length).SequenceCompareTo(y.Name.AsSpan(0, length));

        if (result != 0)
        {
            return result;
        }

        var c1 = x.Name.Length > length ? x.Name[length] : (x.IsTree ? (byte)'/' : (byte)0);
        var c2 = y.Name.Length > length ? y.Name[length] : (y.IsTree ? (byte)'/' : (byte)0);

        return c1.CompareTo(c2);
    }

    private static int GetModeLength(int mode)
    {
        var length = 0;

        do
        {
            length++;
            mode >>= 3;
        }
        while (mode > 0);

        return length;
    }

    private static int WriteMode(Span<byte> destination, int mode)
    {
        var length = GetModeLength(mode);

        for (var i = length - 1; i >= 0; i--)
        {
            destination[i] = (byte)('0' + (mode & 7));
            mode >>= 3;
        }

        return length;
    }

    #endregion

    #region Reading

    /// <summary>
    /// Parses the given tree object.
    /// </summary>
    /// <param name="data">The content of the tree object</param>
    /// <param name="strict">Whether to verify that the entries are sorted and unique</param>
    /// <exception cref="FormatException">Thrown if the tree object is malformed</exception>
    public static List<TreeEntry> Parse(ReadOnlySpan<byte> data, bool strict = true)
    {
        var entries = new List<TreeEntry>();

        while (!data.IsEmpty)
        {
            var space = data.IndexOf((byte)' ');

            if (space <= 0)
            {
                throw new FormatException("Malformed tree entry: missing mode");
            }

            var mode = ParseMode(data[..space]);

            data = data[(space + 1)..];

            var nul = data.IndexOf((byte)0);

            if (nul <= 0)
            {
                throw new FormatException("Malformed tree entry: missing or empty name");
            }

            var name = data[..nul].ToArray();

            if (name.AsSpan().IndexOf((byte)'/') >= 0)
            {
                throw new FormatException("Malformed tree entry: name contains a slash");
            }

            data = data[(nul + 1)..];

            if (data.Length < GitObjectId.Size)
            {
                throw new FormatException("Malformed tree entry: truncated object id");
            }

            var id = GitObjectId.FromBytes(data[..GitObjectId.Size]);

            data = data[GitObjectId.Size..];

            var entry = new TreeEntry(name, mode, id);

            if (strict && entries.Count > 0 && Compare(entries[^1], entry) >= 0)
            {
                throw new FormatException($"Tree entries are not properly sorted or contain duplicates ('{entry.GetName()}')");
            }

            entries.Add(entry);
        }

        if (strict)
        {
            VerifyUniqueNames(entries);
        }

        return entries;
    }

    private static int ParseMode(ReadOnlySpan<byte> mode)
    {
        if (mode[0] == '0')
        {
            throw new FormatException("Malformed tree entry: zero-padded mode");
        }

        var result = 0;

        foreach (var c in mode)
        {
            if (c < '0' || c > '7' || result > 0xFFFFF)
            {
                throw new FormatException("Malformed tree entry: invalid mode");
            }

            result = (result << 3) | (c - '0');
        }

        return result;
    }

    /// <summary>
    /// The sort order allows a file and a directory with the same name
    /// to be separated by other entries ("a", "a.txt", "a/"), so they
    /// need to be checked separately.
    /// </summary>
    private static void VerifyUniqueNames(List<TreeEntry> entries)
    {
        if (entries.Count < 2)
        {
            return;
        }

        var names = new HashSet<string>(entries.Count, StringComparer.Ordinal);

        foreach (var entry in entries)
        {
            if (!names.Add(Convert.ToBase64String(entry.Name)))
            {
                throw new FormatException($"The tree contains the entry '{entry.GetName()}' more than once");
            }
        }
    }

    #endregion

}
