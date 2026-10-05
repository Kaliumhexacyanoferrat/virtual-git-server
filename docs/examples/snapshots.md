# Directory Snapshots

This example serves the content of a directory as a read-only repository. Every time the content changes, a new
commit is added on top of the previous one, so clients can simply `git pull` to receive updates.

```csharp
using GenHTTP.Modules.Git;

/// <summary>
/// Serves snapshots of a directory, creating a new commit whenever
/// its content changed since the last snapshot.
/// </summary>
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
```

```csharp
var snapshots = new DirectorySnapshots(new DirectoryInfo("./content"));

await snapshots.RefreshAsync();

var git = GitServer.Create()
                   .Repository(snapshots);

await Host.Create()
          .Handler(git)
          .RunAsync();
```

Call `RefreshAsync()` whenever the content changes (e.g. from a `FileSystemWatcher` or a timer). As the repository
only implements `IGitRepository`, clients cannot push to it.

!!! note "Snapshots and history"
    The history only lives in memory, so it starts over when the application restarts. As the commits carry the time
    of the snapshot, the first commit after a restart has a different id than before, and clients receive a forced
    update with an unrelated history. To keep the history across restarts, persist `GitCommit.Data` and the files of
    every snapshot, as described in [How It Works](../concepts.md#commits-and-why-their-ids-must-never-change).
