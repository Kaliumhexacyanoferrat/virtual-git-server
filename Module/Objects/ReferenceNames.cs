namespace GenHTTP.Modules.Git.Objects;

/// <summary>
/// Validates the names of references the way <c>git check-ref-format</c> does.
/// </summary>
internal static class ReferenceNames
{

    public const string Branches = "refs/heads/";

    public const string Tags = "refs/tags/";

    public static bool IsValid(string name, out string? reason)
    {
        if (!name.StartsWith("refs/", StringComparison.Ordinal))
        {
            reason = "references need to be located below 'refs/'";
            return false;
        }

        if (name.EndsWith('/') || name.EndsWith('.'))
        {
            reason = "references must not end with a slash or a dot";
            return false;
        }

        if (name.Contains("..", StringComparison.Ordinal) || name.Contains("@{", StringComparison.Ordinal) || name.Contains("//", StringComparison.Ordinal))
        {
            reason = "references must not contain '..', '//' or '@{'";
            return false;
        }

        foreach (var c in name)
        {
            if (c < 0x20 || c == 0x7F || c is ' ' or '~' or '^' or ':' or '?' or '*' or '[' or '\\')
            {
                reason = "references must not contain control characters, spaces or any of '~^:?*[\\'";
                return false;
            }
        }

        foreach (var component in name.Split('/'))
        {
            if (component.Length == 0 || component[0] == '.' || component.EndsWith(".lock", StringComparison.Ordinal))
            {
                reason = "components of a reference must not be empty, start with a dot or end with '.lock'";
                return false;
            }
        }

        if (name.Length <= 5)
        {
            reason = "the name of the reference is missing";
            return false;
        }

        reason = null;
        return true;
    }

    public static void Validate(string name)
    {
        if (!IsValid(name, out var reason))
        {
            throw new ArgumentException($"Invalid reference name '{name}': {reason}", nameof(name));
        }
    }

}
