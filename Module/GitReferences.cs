using System.Collections;

using GenHTTP.Modules.Git.Objects;

namespace GenHTTP.Modules.Git;

/// <summary>
/// The branches and tags of a repository, along with the branch
/// that is checked out by default after cloning.
/// </summary>
/// <example>
/// <code>
/// return new GitReferences().Head("main")
///                           .Branch("main", latest.Id)
///                           .Tag("v1", first.Id);
/// </code>
/// </example>
public sealed class GitReferences : IReadOnlyCollection<GitReference>
{
    private readonly SortedDictionary<string, GitReference> _references = new(StringComparer.Ordinal);

    #region Get-/Setters

    /// <summary>
    /// The full name of the branch HEAD points to, which is the branch
    /// checked out after cloning (defaults to "refs/heads/main").
    /// </summary>
    /// <remarks>
    /// The branch does not need to exist, e.g. if the repository is empty.
    /// </remarks>
    public string HeadTarget { get; private set; } = ReferenceNames.Branches + "main";

    /// <summary>
    /// The number of references.
    /// </summary>
    public int Count => _references.Count;

    #endregion

    #region Functionality

    /// <summary>
    /// Sets the branch HEAD points to (see <see cref="HeadTarget" />).
    /// </summary>
    /// <param name="branch">The short (e.g. "main") or full name (e.g. "refs/heads/main") of the branch</param>
    public GitReferences Head(string branch)
    {
        var name = Qualify(branch, ReferenceNames.Branches);

        ReferenceNames.Validate(name);

        HeadTarget = name;
        return this;
    }

    /// <summary>
    /// Adds a branch pointing to the given commit.
    /// </summary>
    /// <param name="name">The short (e.g. "main") or full name (e.g. "refs/heads/main") of the branch</param>
    /// <param name="target">The id of the commit the branch points to</param>
    public GitReferences Branch(string name, GitObjectId target) => Add(Qualify(name, ReferenceNames.Branches), target);

    /// <summary>
    /// Adds a branch pointing to the given commit.
    /// </summary>
    /// <param name="name">The short (e.g. "main") or full name (e.g. "refs/heads/main") of the branch</param>
    /// <param name="target">The commit the branch points to</param>
    public GitReferences Branch(string name, GitCommit target) => Branch(name, target.Id);

    /// <summary>
    /// Adds a (lightweight) tag pointing to the given commit.
    /// </summary>
    /// <param name="name">The short (e.g. "v1") or full name (e.g. "refs/tags/v1") of the tag</param>
    /// <param name="target">The id of the commit the tag points to</param>
    public GitReferences Tag(string name, GitObjectId target) => Add(Qualify(name, ReferenceNames.Tags), target);

    /// <summary>
    /// Adds a (lightweight) tag pointing to the given commit.
    /// </summary>
    /// <param name="name">The short (e.g. "v1") or full name (e.g. "refs/tags/v1") of the tag</param>
    /// <param name="target">The commit the tag points to</param>
    public GitReferences Tag(string name, GitCommit target) => Tag(name, target.Id);

    /// <summary>
    /// Adds a reference with the given full name.
    /// </summary>
    /// <param name="name">The full name of the reference (e.g. "refs/notes/commits")</param>
    /// <param name="target">The id of the commit the reference points to</param>
    public GitReferences Add(string name, GitObjectId target)
    {
        ReferenceNames.Validate(name);

        if (target.IsZero)
        {
            throw new ArgumentException($"Reference '{name}' must not point to the zero id", nameof(target));
        }

        if (!_references.TryAdd(name, new GitReference(name, target)))
        {
            throw new ArgumentException($"Reference '{name}' has already been added", nameof(name));
        }

        return this;
    }

    /// <summary>
    /// Fetches the reference with the given full name, if it exists.
    /// </summary>
    /// <param name="name">The full name of the reference (e.g. "refs/heads/main")</param>
    public GitReference? Get(string name) => _references.GetValueOrDefault(name);

    /// <summary>
    /// Fetches the reference HEAD points to, if it exists.
    /// </summary>
    public GitReference? GetHead() => Get(HeadTarget);

    private static string Qualify(string name, string prefix)
    {
        ArgumentNullException.ThrowIfNull(name);

        return name.StartsWith("refs/", StringComparison.Ordinal) ? name : prefix + name;
    }

    /// <summary>
    /// Enumerates the references, ordered by their name.
    /// </summary>
    public IEnumerator<GitReference> GetEnumerator() => _references.Values.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    #endregion

}
