namespace GenHTTP.Modules.Git.Tests.Fixtures;

/// <summary>
/// Serves snapshots of a directory, creating a new commit whenever
/// its content changed since the last snapshot.
/// </summary>
/// <remarks>
/// Shown in the documentation (examples/snapshots.md), keep both in sync.
/// </remarks>
public sealed class DirectorySnapshots(DirectoryInfo directory) : IGitRepository
{
    private readonly InMemoryGitRepository _history = new();

    private GitObjectId? _lastTree;

    public async Task RefreshAsync()
    {
        var tree = GitTree.Create();

        foreach (var file in directory.EnumerateFiles("*", SearchOption.AllDirectories))
        {
            var path = Path.GetRelativePath(directory.FullName, file.FullName).Replace('\\', '/');

            // read lazily, the content is only needed to compute ids and to serve it
            tree.Add(path, async () => await File.ReadAllBytesAsync(file.FullName));
        }

        var snapshot = tree.Build();

        var id = await snapshot.ComputeIdAsync();

        if (id == _lastTree)
        {
            return;
        }

        // keep the content in memory, as the files on disk will change
        var copy = GitTree.Create();

        foreach (var file in snapshot)
        {
            copy.Add(file.Path, await file.ReadAsync(), file.Mode);
        }

        await _history.CommitAsync("main", copy.Build(), $"Snapshot of {DateTime.UtcNow:u}");

        _lastTree = id;
    }

    public ValueTask<GitReferences> GetReferencesAsync() => _history.GetReferencesAsync();

    public ValueTask<GitCommit?> GetCommitAsync(GitObjectId id) => _history.GetCommitAsync(id);

    public ValueTask<GitTree> GetTreeAsync(GitCommit commit) => _history.GetTreeAsync(commit);

}
