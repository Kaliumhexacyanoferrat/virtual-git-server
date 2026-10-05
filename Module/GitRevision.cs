namespace GenHTTP.Modules.Git;

/// <summary>
/// A commit along with its files.
/// </summary>
/// <remarks>
/// For commits received with a push, the files are collected when
/// <see cref="Tree" /> is accessed for the first time.
/// </remarks>
public sealed class GitRevision
{
    private readonly Lazy<GitTree> _tree;

    #region Get-/Setters

    /// <summary>
    /// The commit.
    /// </summary>
    public GitCommit Commit { get; }

    /// <summary>
    /// The files of the commit.
    /// </summary>
    public GitTree Tree => _tree.Value;

    #endregion

    #region Initialization

    /// <summary>
    /// Creates a revision from a commit and its files.
    /// </summary>
    /// <param name="commit">The commit</param>
    /// <param name="tree">The files of the commit</param>
    public GitRevision(GitCommit commit, GitTree tree)
    {
        Commit = commit;
        _tree = new Lazy<GitTree>(tree);
    }

    internal GitRevision(GitCommit commit, Func<GitTree> tree)
    {
        Commit = commit;
        _tree = new Lazy<GitTree>(tree, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    #endregion

    #region Functionality

    public override string ToString() => Commit.ToString();

    #endregion

}
