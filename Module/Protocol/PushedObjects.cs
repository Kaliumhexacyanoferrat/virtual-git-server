using System.Text;

using GenHTTP.Modules.Git.Objects;
using GenHTTP.Modules.Git.Packs;

namespace GenHTTP.Modules.Git.Protocol;

/// <summary>
/// Thrown if an update sent by a client cannot be accepted. The
/// message is reported to the client.
/// </summary>
internal sealed class UpdateRejectedException(string message) : Exception(message);

/// <summary>
/// Provides access to the objects received with a push, falling back
/// to the objects of the repository for everything the client did not
/// send because it knows that the server already has it.
/// </summary>
internal sealed class PushedObjects(RepositoryContext context, IReadOnlyDictionary<GitObjectId, ReceivedObject> received)
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private readonly Dictionary<GitObjectId, GitCommit> _commits = new();

    private readonly Dictionary<GitObjectId, TreeObject> _knownTrees = new();

    private readonly Dictionary<GitObjectId, BlobReference> _knownBlobs = new();

    private readonly Queue<GitObjectId> _indexQueue = new();

    private readonly HashSet<GitObjectId> _indexed = new();

    private bool _indexSeeded;

    #region Commits

    /// <summary>
    /// Fetches a commit sent by the client.
    /// </summary>
    public GitCommit? GetReceivedCommit(GitObjectId id)
    {
        if (_commits.TryGetValue(id, out var cached))
        {
            return cached;
        }

        if (!received.TryGetValue(id, out var obj) || obj.Type != GitObjectType.Commit)
        {
            return null;
        }

        GitCommit commit;

        try
        {
            commit = GitCommit.Parse(obj.Data);
        }
        catch (FormatException e)
        {
            throw new UpdateRejectedException($"invalid commit {id}: {e.Message}");
        }

        _commits.Add(id, commit);

        return commit;
    }

    /// <summary>
    /// The type of an object sent by the client, if it was sent.
    /// </summary>
    public GitObjectType? GetReceivedType(GitObjectId id) => received.TryGetValue(id, out var obj) ? obj.Type : null;

    #endregion

    #region Trees

    /// <summary>
    /// Reconstructs the files of the given commit.
    /// </summary>
    /// <param name="commit">A commit sent by the client</param>
    /// <param name="hints">Commits of the repository likely to contain objects not sent by the client</param>
    public async ValueTask<GitTree> GetTreeAsync(GitCommit commit, IEnumerable<GitObjectId> hints)
    {
        foreach (var hint in hints)
        {
            if (!_indexed.Contains(hint))
            {
                _indexQueue.Enqueue(hint);
            }
        }

        var files = new List<GitFile>();

        await CollectAsync(commit.Tree, string.Empty, files, 0);

        GitTree tree;

        try
        {
            tree = new GitTree(files);
        }
        catch (ArgumentException e)
        {
            throw new UpdateRejectedException($"invalid tree in commit {commit.Id}: {e.Message}");
        }

        // a tree that does not result in the same id cannot be served
        // later on (e.g. because it contains an empty directory)
        var materialized = await TreeMaterializer.MaterializeAsync(tree, new ContentCache(0));

        if (materialized.Id != commit.Tree)
        {
            throw new UpdateRejectedException($"the tree of commit {commit.Id} cannot be represented (e.g. empty directories are not supported)");
        }

        return tree;
    }

    private async ValueTask CollectAsync(GitObjectId treeId, string prefix, List<GitFile> files, int depth)
    {
        if (depth > 256)
        {
            throw new UpdateRejectedException("the directory structure is nested too deeply");
        }

        var data = await GetTreeDataAsync(treeId);

        List<TreeEntry> entries;

        try
        {
            entries = TreeFormat.Parse(data, strict: true);
        }
        catch (FormatException e)
        {
            throw new UpdateRejectedException($"invalid tree {treeId}: {e.Message}");
        }

        foreach (var entry in entries)
        {
            string name;

            try
            {
                name = StrictUtf8.GetString(entry.Name);
            }
            catch (DecoderFallbackException)
            {
                throw new UpdateRejectedException($"file names must be valid UTF-8 (in tree {treeId})");
            }

            if (!PathRules.IsValidName(name, out var reason))
            {
                throw new UpdateRejectedException($"invalid file name in tree {treeId}: {reason}");
            }

            var path = prefix + name;

            if (entry.Mode == TreeModes.Tree)
            {
                await CollectAsync(entry.Id, path + "/", files, depth + 1);
                continue;
            }

            if (entry.Mode == TreeModes.Gitlink)
            {
                throw new UpdateRejectedException($"submodules are not supported ('{path}')");
            }

            var mode = TreeModes.ToFileMode(entry.Mode) ?? throw new UpdateRejectedException($"unsupported file mode {Convert.ToString(entry.Mode, 8)} ('{path}')");

            files.Add(await GetFileAsync(entry.Id, path, mode));
        }
    }

    private async ValueTask<byte[]> GetTreeDataAsync(GitObjectId id)
    {
        if (received.TryGetValue(id, out var obj))
        {
            if (obj.Type != GitObjectType.Tree)
            {
                throw new UpdateRejectedException($"object {id} is expected to be a tree");
            }

            return obj.Data;
        }

        if (await FindKnownAsync(id))
        {
            if (_knownTrees.TryGetValue(id, out var tree))
            {
                return tree.Data;
            }

            throw new UpdateRejectedException($"object {id} is expected to be a tree");
        }

        throw new UpdateRejectedException($"missing tree {id}");
    }

    private async ValueTask<GitFile> GetFileAsync(GitObjectId id, string path, GitFileMode mode)
    {
        if (received.TryGetValue(id, out var obj))
        {
            if (obj.Type != GitObjectType.Blob)
            {
                throw new UpdateRejectedException($"object {id} is expected to be a blob");
            }

            return new GitFile(path, obj.Data, mode, id);
        }

        if (await FindKnownAsync(id))
        {
            if (_knownBlobs.TryGetValue(id, out var blob))
            {
                if (blob.Content != null)
                {
                    return new GitFile(path, blob.Content.Value, mode, id);
                }

                return new GitFile(path, blob.ReadAsync, mode, id);
            }

            throw new UpdateRejectedException($"object {id} is expected to be a blob");
        }

        throw new UpdateRejectedException($"missing blob {id} ('{path}')");
    }

    /// <summary>
    /// Searches the objects of the repository for the given id, starting
    /// with the commits the push is based on, followed by the tips of all
    /// references and finally their history.
    /// </summary>
    private async ValueTask<bool> FindKnownAsync(GitObjectId id)
    {
        if (_knownTrees.ContainsKey(id) || _knownBlobs.ContainsKey(id))
        {
            return true;
        }

        while (true)
        {
            if (_indexQueue.Count == 0)
            {
                if (_indexSeeded)
                {
                    return false;
                }

                _indexSeeded = true;

                foreach (var reference in await context.GetReferencesAsync())
                {
                    _indexQueue.Enqueue(reference.Target);
                }

                continue;
            }

            var next = _indexQueue.Dequeue();

            if (!_indexed.Add(next))
            {
                continue;
            }

            var commit = await context.GetCommitAsync(next);

            if (commit == null)
            {
                continue;
            }

            var tree = await context.GetTreeAsync(commit);

            foreach (var treeObject in tree.Trees)
            {
                _knownTrees.TryAdd(treeObject.Id, treeObject);
            }

            foreach (var blob in tree.Blobs)
            {
                _knownBlobs.TryAdd(blob.Id, blob);
            }

            foreach (var parent in commit.Parents)
            {
                _indexQueue.Enqueue(parent);
            }

            if (_knownTrees.ContainsKey(id) || _knownBlobs.ContainsKey(id))
            {
                return true;
            }
        }
    }

    #endregion

}
