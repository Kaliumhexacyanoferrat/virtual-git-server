using GenHTTP.Modules.Git.Objects;

namespace GenHTTP.Modules.Git;

/// <summary>
/// A named pointer to a commit, such as a branch or a tag.
/// </summary>
/// <param name="Name">The full name of the reference (e.g. "refs/heads/main" or "refs/tags/v1")</param>
/// <param name="Target">The id of the commit the reference points to</param>
public sealed record GitReference(string Name, GitObjectId Target)
{

    /// <summary>
    /// Whether this reference is a branch (located below "refs/heads/").
    /// </summary>
    public bool IsBranch => Name.StartsWith(ReferenceNames.Branches, StringComparison.Ordinal);

    /// <summary>
    /// Whether this reference is a tag (located below "refs/tags/").
    /// </summary>
    public bool IsTag => Name.StartsWith(ReferenceNames.Tags, StringComparison.Ordinal);

    /// <summary>
    /// The name of the reference without its prefix (e.g. "main" for
    /// "refs/heads/main" or "v1" for "refs/tags/v1").
    /// </summary>
    public string ShortName => IsBranch ? Name[ReferenceNames.Branches.Length..] : IsTag ? Name[ReferenceNames.Tags.Length..] : Name;

    /// <summary>
    /// Checks whether the given string is a valid full reference name
    /// according to the rules of git (see <c>git check-ref-format</c>).
    /// </summary>
    /// <param name="name">The name to be checked (e.g. "refs/heads/feature/login")</param>
    public static bool IsValidName(string name) => ReferenceNames.IsValid(name, out _);

    public override string ToString() => $"{Target} {Name}";

}
