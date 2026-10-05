using System.Collections;

using GenHTTP.Modules.Git.Objects;

namespace GenHTTP.Modules.Git;

/// <summary>
/// The files of a commit, i.e. a snapshot of the repository at
/// a specific point in time.
/// </summary>
/// <remarks>
/// Files are addressed by their full path, directories are derived
/// from these paths. The order the files are added in does not matter,
/// the server sorts them the way git expects.
/// </remarks>
public sealed class GitTree : IReadOnlyCollection<GitFile>
{
    private readonly Dictionary<string, GitFile> _index;

    #region Get-/Setters

    /// <summary>
    /// A tree without any files.
    /// </summary>
    public static GitTree Empty { get; } = new([]);

    /// <summary>
    /// The files of this tree, ordered by their path.
    /// </summary>
    public IReadOnlyList<GitFile> Files { get; }

    /// <summary>
    /// The number of files in this tree.
    /// </summary>
    public int Count => Files.Count;

    #endregion

    #region Initialization

    internal GitTree(List<GitFile> files)
    {
        files.Sort((x, y) => string.CompareOrdinal(x.Path, y.Path));

        Files = files;

        _index = new Dictionary<string, GitFile>(files.Count, StringComparer.Ordinal);

        foreach (var file in files)
        {
            if (!_index.TryAdd(file.Path, file))
            {
                throw new ArgumentException($"The tree contains the file '{file.Path}' more than once");
            }
        }

        foreach (var file in files)
        {
            var index = file.Path.IndexOf('/');

            while (index > 0)
            {
                var directory = file.Path[..index];

                if (_index.ContainsKey(directory))
                {
                    throw new ArgumentException($"The tree contains '{directory}' both as a file and as a directory");
                }

                index = file.Path.IndexOf('/', index + 1);
            }
        }
    }

    /// <summary>
    /// Starts building a new tree.
    /// </summary>
    public static GitTreeBuilder Create() => new();

    #endregion

    #region Functionality

    /// <summary>
    /// Fetches the file with the given path, if it exists.
    /// </summary>
    /// <param name="path">The path of the file (e.g. "assets/app.js")</param>
    public GitFile? GetFile(string path) => _index.GetValueOrDefault(path.TrimStart('/'));

    /// <summary>
    /// Computes the id of the git tree object representing these files.
    /// </summary>
    /// <remarks>
    /// Reads the content of all files whose blob id is not known. The
    /// result is needed to create a commit (see <see cref="GitCommitBuilder.Tree(GitObjectId)" />).
    /// </remarks>
    public async ValueTask<GitObjectId> ComputeIdAsync()
    {
        var tree = await TreeMaterializer.MaterializeAsync(this, new ContentCache(0));

        return tree.Id;
    }

    public IEnumerator<GitFile> GetEnumerator() => Files.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    #endregion

}
