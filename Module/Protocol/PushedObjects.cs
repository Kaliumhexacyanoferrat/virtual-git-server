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
/// <remarks>
/// Trees are validated once per tree object, so a push cannot cause work
/// beyond the number of objects it consists of (e.g. by referencing the
/// same tree a thousand times in a thousand directories). The files of a
/// commit are only collected when the repository asks for them.
/// </remarks>
internal sealed class PushedObjects(RepositoryContext context, IReadOnlyDictionary<GitObjectId, ReceivedObject> received, int maximumFiles)
{
    /// <summary>
    /// The number of commits of the repository searched for objects the client did not send.
    /// </summary>
    private const int MaximumIndexedCommits = 10_000;

    private const int MaximumDepth = 256;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private sealed record Entry(string Name, GitFileMode? Mode, GitObjectId Id);

    private sealed record ValidatedTree(List<Entry> Entries, long Files, int Height);

    private readonly Dictionary<GitObjectId, GitCommit> _commits = new();

    private readonly Dictionary<GitObjectId, ValidatedTree> _trees = new();

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
    /// Verifies that the files of the given commit are available and can
    /// be represented as a <see cref="GitTree" />.
    /// </summary>
    /// <param name="commit">A commit sent by the client</param>
    /// <param name="hints">Commits of the repository likely to contain objects not sent by the client</param>
    public async ValueTask ValidateAsync(GitCommit commit, IEnumerable<GitObjectId> hints)
    {
        foreach (var hint in hints)
        {
            if (!_indexed.Contains(hint))
            {
                _indexQueue.Enqueue(hint);
            }
        }

        var tree = await ValidateTreeAsync(commit.Tree, 0);

        if (tree.Files > maximumFiles)
        {
            throw new UpdateRejectedException($"commit {commit.Id} contains more than {maximumFiles} files");
        }
    }

    /// <summary>
    /// Collects the files of a commit previously validated by <see cref="ValidateAsync" />.
    /// </summary>
    public GitTree GetTree(GitCommit commit)
    {
        var files = new List<GitFile>((int)_trees[commit.Tree].Files);

        Collect(commit.Tree, string.Empty, files);

        return new GitTree(files);
    }

    private void Collect(GitObjectId treeId, string prefix, List<GitFile> files)
    {
        foreach (var entry in _trees[treeId].Entries)
        {
            var path = prefix + entry.Name;

            if (entry.Mode == null)
            {
                Collect(entry.Id, path + "/", files);
            }
            else if (received.TryGetValue(entry.Id, out var obj))
            {
                files.Add(new GitFile(path, obj.Data, entry.Mode.Value, entry.Id));
            }
            else
            {
                var blob = _knownBlobs[entry.Id];

                files.Add(blob.Content != null ? new GitFile(path, blob.Content.Value, entry.Mode.Value, entry.Id) : new GitFile(path, blob.ReadAsync, entry.Mode.Value, entry.Id));
            }
        }
    }

    private async ValueTask<ValidatedTree> ValidateTreeAsync(GitObjectId treeId, int depth)
    {
        if (_trees.TryGetValue(treeId, out var cached))
        {
            if (depth + cached.Height > MaximumDepth)
            {
                throw new UpdateRejectedException("the directory structure is nested too deeply");
            }

            return cached;
        }

        if (depth > MaximumDepth)
        {
            throw new UpdateRejectedException("the directory structure is nested too deeply");
        }

        var data = await GetTreeDataAsync(treeId);

        List<TreeEntry> parsed;

        try
        {
            parsed = TreeFormat.Parse(data, strict: true);
        }
        catch (FormatException e)
        {
            throw new UpdateRejectedException($"invalid tree {treeId}: {e.Message}");
        }

        var entries = new List<Entry>(parsed.Count);

        long files = 0;

        var height = 0;

        foreach (var entry in parsed)
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

            if (entry.Mode == TreeModes.Tree)
            {
                var child = await ValidateTreeAsync(entry.Id, depth + 1);

                // directories without files cannot be represented, and the
                // tree would get a different id when served again
                if (child.Files == 0)
                {
                    throw new UpdateRejectedException($"empty directories are not supported ('{name}' in tree {treeId})");
                }

                files += child.Files;
                height = Math.Max(height, child.Height + 1);

                entries.Add(new Entry(name, null, entry.Id));
            }
            else if (entry.Mode == TreeModes.Gitlink)
            {
                throw new UpdateRejectedException($"submodules are not supported ('{name}' in tree {treeId})");
            }
            else
            {
                var mode = TreeModes.ToFileMode(entry.Mode) ?? throw new UpdateRejectedException($"unsupported file mode {Convert.ToString(entry.Mode, 8)} ('{name}' in tree {treeId})");

                await VerifyBlobAsync(entry.Id, name);

                files++;

                entries.Add(new Entry(name, mode, entry.Id));
            }

            if (files > maximumFiles)
            {
                throw new UpdateRejectedException($"the push contains a tree with more than {maximumFiles} files");
            }
        }

        var result = new ValidatedTree(entries, files, height);

        _trees.Add(treeId, result);

        return result;
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

    private async ValueTask VerifyBlobAsync(GitObjectId id, string name)
    {
        if (received.TryGetValue(id, out var obj))
        {
            if (obj.Type != GitObjectType.Blob)
            {
                throw new UpdateRejectedException($"object {id} is expected to be a blob");
            }

            return;
        }

        if (await FindKnownAsync(id))
        {
            if (!_knownBlobs.ContainsKey(id))
            {
                throw new UpdateRejectedException($"object {id} is expected to be a blob");
            }

            return;
        }

        throw new UpdateRejectedException($"missing blob {id} ('{name}')");
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

        while (_indexed.Count < MaximumIndexedCommits)
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

        return false;
    }

    #endregion

}
