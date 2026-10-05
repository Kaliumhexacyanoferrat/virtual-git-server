using System.Text;

namespace GenHTTP.Modules.Git.Objects;

/// <summary>
/// A tree object computed from a <see cref="GitTree" />.
/// </summary>
internal sealed record TreeObject(GitObjectId Id, byte[] Data);

/// <summary>
/// A blob referenced by a materialized tree.
/// </summary>
/// <remarks>
/// Keeps the content if it was read anyway to compute the id and
/// the cache allowed to keep it, so it does not need to be read
/// again when the blob is written to a pack.
/// </remarks>
internal sealed class BlobReference(GitObjectId id, GitFile file, ReadOnlyMemory<byte>? content)
{

    public GitObjectId Id { get; } = id;

    public GitFile File { get; } = file;

    public ReadOnlyMemory<byte>? Content { get; } = content;

    /// <summary>
    /// Reads the content of the blob, verifying that it still
    /// matches the id it was referenced with.
    /// </summary>
    public async ValueTask<ReadOnlyMemory<byte>> ReadAsync()
    {
        if (Content != null)
        {
            return Content.Value;
        }

        var content = await File.ReadAsync();

        if (ObjectHasher.Hash(GitObjectType.Blob, content.Span) != Id)
        {
            throw new InvalidOperationException($"The content of file '{File.Path}' does not match its blob id {Id} (the content changed or the id passed to the file is wrong)");
        }

        return content;
    }

}

/// <summary>
/// The git objects representing a <see cref="GitTree" />.
/// </summary>
internal sealed class MaterializedTree(GitObjectId id, IReadOnlyList<TreeObject> trees, IReadOnlyList<BlobReference> blobs)
{

    /// <summary>
    /// The id of the root tree.
    /// </summary>
    public GitObjectId Id { get; } = id;

    /// <summary>
    /// All tree objects, including the root tree.
    /// </summary>
    public IReadOnlyList<TreeObject> Trees { get; } = trees;

    /// <summary>
    /// All blobs referenced by the trees.
    /// </summary>
    public IReadOnlyList<BlobReference> Blobs { get; } = blobs;

}

/// <summary>
/// Limits the amount of file content that is kept in memory
/// while a single request is processed.
/// </summary>
internal sealed class ContentCache(long budget)
{
    private long _remaining = budget;

    public bool TryReserve(long size)
    {
        if (size > _remaining)
        {
            return false;
        }

        _remaining -= size;
        return true;
    }

}

/// <summary>
/// Converts the files of a <see cref="GitTree" /> into git tree
/// and blob objects.
/// </summary>
internal static class TreeMaterializer
{

    private sealed class Directory
    {

        public Dictionary<string, Directory> Directories { get; } = new(StringComparer.Ordinal);

        public List<(string Name, GitFile File)> Files { get; } = [];

    }

    public static async ValueTask<MaterializedTree> MaterializeAsync(GitTree tree, ContentCache cache)
    {
        var root = new Directory();

        foreach (var file in tree.Files)
        {
            var segments = file.Path.Split('/');

            var current = root;

            for (var i = 0; i < segments.Length - 1; i++)
            {
                if (!current.Directories.TryGetValue(segments[i], out var child))
                {
                    child = new Directory();
                    current.Directories.Add(segments[i], child);
                }

                current = child;
            }

            current.Files.Add((segments[^1], file));
        }

        var trees = new List<TreeObject>();
        var blobs = new List<BlobReference>();

        var seen = new HashSet<GitObjectId>();

        var id = await MaterializeAsync(root, cache, trees, blobs, seen);

        return new MaterializedTree(id, trees, blobs);
    }

    private static async ValueTask<GitObjectId> MaterializeAsync(Directory directory, ContentCache cache, List<TreeObject> trees, List<BlobReference> blobs, HashSet<GitObjectId> seen)
    {
        var entries = new List<TreeEntry>(directory.Directories.Count + directory.Files.Count);

        foreach (var (name, child) in directory.Directories)
        {
            var childId = await MaterializeAsync(child, cache, trees, blobs, seen);

            entries.Add(new TreeEntry(Encoding.UTF8.GetBytes(name), TreeModes.Tree, childId));
        }

        foreach (var (name, file) in directory.Files)
        {
            var blob = await GetBlobAsync(file, cache);

            if (seen.Add(blob.Id))
            {
                blobs.Add(blob);
            }

            entries.Add(new TreeEntry(Encoding.UTF8.GetBytes(name), TreeModes.FromFileMode(file.Mode), blob.Id));
        }

        var data = TreeFormat.Serialize(entries);

        var id = ObjectHasher.Hash(GitObjectType.Tree, data);

        if (seen.Add(id))
        {
            trees.Add(new TreeObject(id, data));
        }

        return id;
    }

    private static async ValueTask<BlobReference> GetBlobAsync(GitFile file, ContentCache cache)
    {
        if (file.Id != null)
        {
            if (file.IsLoaded)
            {
                return new BlobReference(file.Id.Value, file, await file.ReadAsync());
            }

            return new BlobReference(file.Id.Value, file, null);
        }

        var content = await file.ReadAsync();

        var id = ObjectHasher.Hash(GitObjectType.Blob, content.Span);

        var keep = file.IsLoaded || cache.TryReserve(content.Length);

        return new BlobReference(id, file, keep ? content : null);
    }

}
