namespace GenHTTP.Modules.Git.Objects;

/// <summary>
/// The kinds of objects git knows, numbered as in the pack format.
/// </summary>
internal enum GitObjectType
{
    Commit = 1,
    Tree = 2,
    Blob = 3,
    Tag = 4
}

internal static class GitObjectTypeExtensions
{

    public static string GetName(this GitObjectType type) => type switch
    {
        GitObjectType.Commit => "commit",
        GitObjectType.Tree => "tree",
        GitObjectType.Blob => "blob",
        GitObjectType.Tag => "tag",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null)
    };

}
