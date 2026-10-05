using GenHTTP.Modules.Git.Objects;

namespace GenHTTP.Modules.Git;

/// <summary>
/// A repository keeping all commits and references in memory, behaving
/// like a regular bare repository hosted by git.
/// </summary>
/// <remarks>
/// Intended for tests, prototypes and as a reference for implementing
/// <see cref="IWritableGitRepository" />. Everything is lost when the
/// instance is discarded.
/// </remarks>
public sealed class InMemoryGitRepository : IWritableGitRepository
{
    private readonly Lock _lock = new();

    private readonly Dictionary<GitObjectId, GitRevision> _commits = new();

    private readonly SortedDictionary<string, GitObjectId> _references = new(StringComparer.Ordinal);

    private string _head = ReferenceNames.Branches + "main";

    #region Get-/Setters

    /// <summary>
    /// Whether pushes that rewrite the history of a branch are rejected
    /// (like <c>receive.denyNonFastForwards</c> in git). Defaults to false.
    /// </summary>
    public bool DenyNonFastForwards { get; init; }

    /// <summary>
    /// Whether clients may delete branches and tags. Defaults to true.
    /// </summary>
    public bool AllowDeletes { get; init; } = true;

    /// <summary>
    /// The full name of the branch HEAD points to (defaults to "refs/heads/main").
    /// </summary>
    public string Head
    {
        get
        {
            lock (_lock)
            {
                return _head;
            }
        }
        set
        {
            var name = value.StartsWith("refs/", StringComparison.Ordinal) ? value : ReferenceNames.Branches + value;

            ReferenceNames.Validate(name);

            lock (_lock)
            {
                _head = name;
            }
        }
    }

    /// <summary>
    /// A snapshot of the references of this repository.
    /// </summary>
    public IReadOnlyDictionary<string, GitObjectId> References
    {
        get
        {
            lock (_lock)
            {
                return new Dictionary<string, GitObjectId>(_references);
            }
        }
    }

    #endregion

    #region Functionality

    /// <summary>
    /// Creates a new commit on top of the given branch and moves the branch to it.
    /// </summary>
    /// <param name="branch">The short (e.g. "main") or full name of the branch</param>
    /// <param name="tree">The files of the new commit</param>
    /// <param name="message">The message of the new commit</param>
    /// <param name="author">The author of the commit (defaults to a generic identity and the current time)</param>
    /// <returns>The newly created commit</returns>
    public async ValueTask<GitCommit> CommitAsync(string branch, GitTree tree, string message, GitSignature? author = null)
    {
        var name = branch.StartsWith("refs/", StringComparison.Ordinal) ? branch : ReferenceNames.Branches + branch;

        ReferenceNames.Validate(name);

        var treeId = await tree.ComputeIdAsync();

        lock (_lock)
        {
            var builder = GitCommit.Create()
                                   .Tree(treeId)
                                   .Author(author ?? new GitSignature("GenHTTP", "git@genhttp.org", DateTimeOffset.UtcNow))
                                   .Message(message);

            if (_references.TryGetValue(name, out var parent))
            {
                builder.Parent(parent);
            }

            var commit = builder.Build();

            _commits[commit.Id] = new GitRevision(commit, tree);
            _references[name] = commit.Id;

            return commit;
        }
    }

    /// <summary>
    /// Stores the given commit without changing any reference.
    /// </summary>
    /// <remarks>
    /// The caller is responsible for the tree matching the commit
    /// and for the parents of the commit being known.
    /// </remarks>
    /// <param name="commit">The commit to be stored</param>
    /// <param name="tree">The files of the commit</param>
    public void Store(GitCommit commit, GitTree tree)
    {
        lock (_lock)
        {
            _commits[commit.Id] = new GitRevision(commit, tree);
        }
    }

    /// <summary>
    /// Creates or moves a reference.
    /// </summary>
    /// <param name="name">The full name of the reference (e.g. "refs/tags/v1")</param>
    /// <param name="target">The commit the reference should point to</param>
    public void SetReference(string name, GitObjectId target)
    {
        ReferenceNames.Validate(name);

        lock (_lock)
        {
            if (!_commits.ContainsKey(target))
            {
                throw new ArgumentException($"Commit {target} is not known to the repository", nameof(target));
            }

            _references[name] = target;
        }
    }

    /// <summary>
    /// Removes a reference.
    /// </summary>
    /// <param name="name">The full name of the reference</param>
    /// <returns>true, if the reference existed</returns>
    public bool DeleteReference(string name)
    {
        lock (_lock)
        {
            return _references.Remove(name);
        }
    }

    /// <summary>
    /// Fetches a commit and its files.
    /// </summary>
    /// <param name="id">The id of the commit</param>
    public GitRevision? GetRevision(GitObjectId id)
    {
        lock (_lock)
        {
            return _commits.GetValueOrDefault(id);
        }
    }

    #endregion

    #region Repository

    public ValueTask<GitReferences> GetReferencesAsync()
    {
        lock (_lock)
        {
            var result = new GitReferences().Head(_head);

            foreach (var (name, target) in _references)
            {
                result.Add(name, target);
            }

            return new(result);
        }
    }

    public ValueTask<GitCommit?> GetCommitAsync(GitObjectId id) => new(GetRevision(id)?.Commit);

    public ValueTask<GitTree> GetTreeAsync(GitCommit commit) => new(GetRevision(commit.Id)?.Tree ?? throw new InvalidOperationException($"Unknown commit {commit.Id}"));

    public ValueTask PushAsync(GitPush push)
    {
        lock (_lock)
        {
            foreach (var update in push.Updates)
            {
                var current = _references.GetValueOrDefault(update.Name);

                if (current != update.OldId)
                {
                    update.Reject("the reference has been changed in the meantime, fetch first");
                }
                else if (update.IsDelete)
                {
                    if (AllowDeletes)
                    {
                        _references.Remove(update.Name);
                        update.Accept();
                    }
                    else
                    {
                        update.Reject("deleting references is not allowed");
                    }
                }
                else if (DenyNonFastForwards && !update.IsFastForward)
                {
                    update.Reject("non-fast-forward updates are not allowed");
                }
                else
                {
                    foreach (var revision in update.Revisions)
                    {
                        _commits[revision.Commit.Id] = revision;
                    }

                    _references[update.Name] = update.NewId;

                    update.Accept();
                }
            }
        }

        return ValueTask.CompletedTask;
    }

    #endregion

}
