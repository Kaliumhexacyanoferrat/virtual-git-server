using GenHTTP.Modules.Git.Objects;

namespace GenHTTP.Modules.Git.Protocol;

/// <summary>
/// Thrown if a repository returns inconsistent data, such as files
/// that do not match the tree of their commit.
/// </summary>
internal sealed class RepositoryException(string message) : Exception(message);

/// <summary>
/// Wraps a repository for the duration of a single request, caching
/// everything the repository returned so that it is asked only once.
/// </summary>
internal sealed class RepositoryContext(IGitRepository repository, ContentCache cache)
{
    private GitReferences? _references;

    private readonly Dictionary<GitObjectId, GitCommit?> _commits = new();

    private readonly Dictionary<GitObjectId, MaterializedTree> _trees = new();

    #region Get-/Setters

    public IGitRepository Repository => repository;

    public ContentCache Cache => cache;

    #endregion

    #region Functionality

    public async ValueTask<GitReferences> GetReferencesAsync() => _references ??= await repository.GetReferencesAsync();

    /// <summary>
    /// Discards the cached references, so that the current state is
    /// fetched from the repository with the next call.
    /// </summary>
    public void InvalidateReferences() => _references = null;

    public async ValueTask<GitCommit?> GetCommitAsync(GitObjectId id)
    {
        if (_commits.TryGetValue(id, out var cached))
        {
            return cached;
        }

        var commit = id.IsZero ? null : await repository.GetCommitAsync(id);

        if (commit != null && commit.Id != id)
        {
            throw new RepositoryException($"The repository returned commit {commit.Id} when asked for {id}");
        }

        _commits[id] = commit;

        return commit;
    }

    /// <summary>
    /// Fetches the files of the given commit and converts them into
    /// git objects, verifying that they match the tree of the commit.
    /// </summary>
    public async ValueTask<MaterializedTree> GetTreeAsync(GitCommit commit)
    {
        if (_trees.TryGetValue(commit.Id, out var cached))
        {
            return cached;
        }

        var files = await repository.GetTreeAsync(commit);

        var tree = await TreeMaterializer.MaterializeAsync(files, cache);

        if (tree.Id != commit.Tree)
        {
            throw new RepositoryException($"The files returned for commit {commit.Id} result in tree {tree.Id}, but the commit refers to tree {commit.Tree}");
        }

        _trees[commit.Id] = tree;

        return tree;
    }

    /// <summary>
    /// Collects the given commits and all of their ancestors known to the repository.
    /// </summary>
    /// <param name="starts">The commits to start with</param>
    /// <param name="boundaries">Commits whose parents should not be followed</param>
    public async ValueTask<HashSet<GitObjectId>> GetAncestryAsync(IEnumerable<GitObjectId> starts, IReadOnlySet<GitObjectId>? boundaries = null)
    {
        var result = new HashSet<GitObjectId>();

        var pending = new Stack<GitObjectId>(starts);

        while (pending.TryPop(out var id))
        {
            if (result.Contains(id))
            {
                continue;
            }

            var commit = await GetCommitAsync(id);

            if (commit == null)
            {
                continue;
            }

            result.Add(id);

            if (boundaries != null && boundaries.Contains(id))
            {
                continue;
            }

            foreach (var parent in commit.Parents)
            {
                pending.Push(parent);
            }
        }

        return result;
    }

    /// <summary>
    /// Fetches all parents of the given commit, or null if at least one
    /// of them is unknown to the repository (i.e. the history has been
    /// truncated and the commit is a shallow boundary).
    /// </summary>
    public async ValueTask<List<GitCommit>?> GetParentsAsync(GitCommit commit)
    {
        var result = new List<GitCommit>(commit.Parents.Count);

        foreach (var id in commit.Parents)
        {
            var parent = await GetCommitAsync(id);

            if (parent == null)
            {
                return null;
            }

            result.Add(parent);
        }

        return result;
    }

    /// <summary>
    /// Finds the commits reachable from the given ones whose history has
    /// been truncated, which need to be announced as shallow to clients.
    /// </summary>
    public async ValueTask<List<GitObjectId>> FindShallowBoundariesAsync(IEnumerable<GitObjectId> starts)
    {
        var result = new List<GitObjectId>();

        var visited = new HashSet<GitObjectId>();

        var pending = new Stack<GitObjectId>(starts);

        while (pending.TryPop(out var id))
        {
            if (!visited.Add(id))
            {
                continue;
            }

            var commit = await GetCommitAsync(id);

            if (commit == null)
            {
                continue;
            }

            var parents = await GetParentsAsync(commit);

            if (parents == null)
            {
                result.Add(id);
                continue;
            }

            foreach (var parent in parents)
            {
                pending.Push(parent.Id);
            }
        }

        return result;
    }

    #endregion

}
