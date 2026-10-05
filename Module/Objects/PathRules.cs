namespace GenHTTP.Modules.Git.Objects;

/// <summary>
/// Decides which file and directory names may appear in a tree.
/// </summary>
/// <remarks>
/// Applied to the files a provider serves as well as to everything
/// pushed by a client. The rules follow what git itself refuses to
/// check out (see <c>verify_path</c> in git), because a tree a client
/// cannot check out is useless, and a pushed tree containing names such
/// as <c>..</c> or <c>.git</c> could trick a provider writing files to
/// disk into writing outside of its directory.
/// </remarks>
internal static class PathRules
{

    public static bool IsValidName(ReadOnlySpan<char> name, out string? reason)
    {
        if (name.IsEmpty)
        {
            reason = "empty names are not allowed";
            return false;
        }

        if (name is "." or "..")
        {
            reason = $"'{name}' is not allowed as a name";
            return false;
        }

        foreach (var c in name)
        {
            switch (c)
            {
                case '/':
                    reason = $"'{name}' must not contain a slash";
                    return false;
                case '\\':
                    reason = $"'{name}' must not contain a backslash";
                    return false;
                case '\0':
                    reason = $"'{name}' must not contain a NUL character";
                    return false;
            }
        }

        if (IsDotGit(name))
        {
            reason = $"'{name}' is reserved by git";
            return false;
        }

        reason = null;
        return true;
    }

    public static void ValidateName(ReadOnlySpan<char> name)
    {
        if (!IsValidName(name, out var reason))
        {
            throw new ArgumentException($"Invalid file name: {reason}");
        }
    }

    /// <summary>
    /// Splits the given path into its segments, validating each of them.
    /// </summary>
    public static string[] Split(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        if (path.Length == 0)
        {
            throw new ArgumentException("The path of a file must not be empty", nameof(path));
        }

        if (path[0] == '/')
        {
            path = path.TrimStart('/');
        }

        var segments = path.Split('/');

        foreach (var segment in segments)
        {
            if (!IsValidName(segment, out var reason))
            {
                throw new ArgumentException($"Invalid path '{path}': {reason}", nameof(path));
            }
        }

        return segments;
    }

    /// <summary>
    /// Checks whether the given name would be treated as the ".git"
    /// directory by git on any platform.
    /// </summary>
    /// <remarks>
    /// Windows ignores trailing dots and spaces and knows the short
    /// name "git~1", macOS ignores some invisible unicode characters.
    /// </remarks>
    private static bool IsDotGit(ReadOnlySpan<char> name)
    {
        Span<char> buffer = stackalloc char[Math.Min(name.Length, 256)];

        var length = 0;

        foreach (var c in name)
        {
            if (IsHfsIgnorable(c))
            {
                continue;
            }

            if (length == buffer.Length)
            {
                return false;
            }

            buffer[length++] = c;
        }

        var normalized = buffer[..length].TrimEnd(". ");

        return normalized.Equals(".git", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("git~1", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsHfsIgnorable(char c) => c is >= '‌' and <= '‏'
                                                  or >= '‪' and <= '‮'
                                                  or >= '⁪' and <= '⁯'
                                                  or '﻿';

}
