using System.Text;

using GenHTTP.Api.Infrastructure;

namespace GenHTTP.Modules.Git;

/// <summary>
/// Creates a new commit from the given information.
/// </summary>
/// <remarks>
/// The resulting commit is fully determined by the information passed
/// to this builder. Nevertheless, providers should persist
/// <see cref="GitCommit.Data" /> instead of building the commit again
/// every time it is needed, so that later changes (to their own code
/// or to this library) cannot change the history of a repository.
/// </remarks>
/// <example>
/// <code>
/// var files = GitTree.Create()
///                    .Add("README.md", "# Hello")
///                    .Build();
///
/// var commit = GitCommit.Create()
///                       .Tree(await files.ComputeIdAsync())
///                       .Parent(previous)
///                       .Author("Jane Doe", "jane@example.com", DateTimeOffset.Now)
///                       .Message("Add a readme")
///                       .Build();
/// </code>
/// </example>
public sealed class GitCommitBuilder : IBuilder<GitCommit>
{
    private static readonly UTF8Encoding Utf8 = new(false);

    private GitObjectId? _tree;

    private readonly List<GitObjectId> _parents = [];

    private GitSignature? _author, _committer;

    private string _message = string.Empty;

    #region Functionality

    /// <summary>
    /// Sets the tree holding the files of the commit.
    /// </summary>
    /// <param name="tree">The id of the tree (see <see cref="GitTree.ComputeIdAsync" />)</param>
    public GitCommitBuilder Tree(GitObjectId tree)
    {
        _tree = tree;
        return this;
    }

    /// <summary>
    /// Adds a commit this commit is based on. Typically, a commit has
    /// exactly one parent - except for the very first commit of a history.
    /// </summary>
    /// <param name="parent">The id of the parent commit</param>
    public GitCommitBuilder Parent(GitObjectId parent)
    {
        if (parent.IsZero)
        {
            throw new ArgumentException("The zero id cannot be used as a parent", nameof(parent));
        }

        _parents.Add(parent);
        return this;
    }

    /// <summary>
    /// Adds a commit this commit is based on.
    /// </summary>
    /// <param name="parent">The parent commit</param>
    public GitCommitBuilder Parent(GitCommit parent) => Parent(parent.Id);

    /// <summary>
    /// Sets the person who wrote the change. Also used as the committer,
    /// unless set explicitly.
    /// </summary>
    /// <param name="author">The author of the change</param>
    public GitCommitBuilder Author(GitSignature author)
    {
        _author = author;
        return this;
    }

    /// <summary>
    /// Sets the person who wrote the change. Also used as the committer,
    /// unless set explicitly.
    /// </summary>
    /// <param name="name">The name of the author</param>
    /// <param name="email">The email address of the author</param>
    /// <param name="when">When the change was made</param>
    public GitCommitBuilder Author(string name, string email, DateTimeOffset when) => Author(new GitSignature(name, email, when));

    /// <summary>
    /// Sets the person who created the commit.
    /// </summary>
    /// <param name="committer">The creator of the commit</param>
    public GitCommitBuilder Committer(GitSignature committer)
    {
        _committer = committer;
        return this;
    }

    /// <summary>
    /// Sets the person who created the commit.
    /// </summary>
    /// <param name="name">The name of the committer</param>
    /// <param name="email">The email address of the committer</param>
    /// <param name="when">When the commit was created</param>
    public GitCommitBuilder Committer(string name, string email, DateTimeOffset when) => Committer(new GitSignature(name, email, when));

    /// <summary>
    /// Sets the message describing the change. By convention, the first
    /// line is a short summary, separated from further details by an empty line.
    /// </summary>
    /// <remarks>
    /// A line break is appended if the message does not end with one,
    /// as git does.
    /// </remarks>
    /// <param name="message">The message of the commit</param>
    public GitCommitBuilder Message(string message)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (message.Contains('\0'))
        {
            throw new ArgumentException("The message must not contain NUL characters", nameof(message));
        }

        _message = message;
        return this;
    }

    public GitCommit Build()
    {
        var tree = _tree ?? throw new BuilderMissingPropertyException("Tree");
        var author = _author ?? throw new BuilderMissingPropertyException("Author");

        var committer = _committer ?? author;

        var content = new StringBuilder();

        content.Append("tree ").Append(tree.ToString()).Append('\n');

        foreach (var parent in _parents)
        {
            content.Append("parent ").Append(parent.ToString()).Append('\n');
        }

        content.Append("author ").Append(author).Append('\n');
        content.Append("committer ").Append(committer).Append('\n');
        content.Append('\n');

        content.Append(_message);

        if (_message.Length > 0 && _message[^1] != '\n')
        {
            content.Append('\n');
        }

        return GitCommit.Parse(Utf8.GetBytes(content.ToString()));
    }

    #endregion

}
