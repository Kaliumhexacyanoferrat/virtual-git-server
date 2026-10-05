# Serving Content

To let clients clone and fetch your data, implement `IGitRepository`. This page walks through the three methods and
the decisions you need to make.

## Mapping your data model

Start by deciding how your data maps to git:

| Your model                       | Git                                                                       |
|----------------------------------|---------------------------------------------------------------------------|
| A saved state (version, release) | A commit, with the previous state as parent                               |
| The current state                | A branch (e.g. `main`) pointing to the newest commit                      |
| Named or numbered states         | Tags (e.g. `v1`, `v2`, ...) - tags should never move                      |
| Work in progress, drafts         | Branches based on the state they started from                             |
| Files, assets                    | The tree of a commit                                                      |
| Authors, notes, time stamps      | The author, committer and message of a commit                             |

Keep in mind that commit messages and file contents become visible to everybody who can clone the repository. Only
include information meant for these users.

## References

```csharp
public async ValueTask<GitReferences> GetReferencesAsync()
{
    var versions = await _store.GetVersionsAsync();

    var references = new GitReferences().Head("main");

    if (versions.Count > 0)
    {
        references.Branch("main", GitObjectId.Parse(versions[^1].CommitId));
    }

    foreach (var version in versions)
    {
        references.Tag($"v{version.Number}", GitObjectId.Parse(version.CommitId));
    }

    return references;
}
```

- `Head(branch)` sets the branch that is checked out after cloning. It defaults to `main` and may point to a branch
  that does not exist yet (an empty repository).
- `Branch(name, id)` and `Tag(name, id)` accept short names (`main`, `v1`) or full names (`refs/heads/main`).
- `Add(name, id)` adds references with a full name in any namespace (e.g. `refs/notes/commits`).
- Names are validated according to `git check-ref-format`; use `GitReference.IsValidName(name)` to check names
  derived from user input.

The method is called once per request and may run a query every time; there is no need to cache the result.

## Commits

```csharp
public async ValueTask<GitCommit?> GetCommitAsync(GitObjectId id)
{
    var version = await _store.FindByCommitAsync(id.ToString());

    return version != null ? GitCommit.Parse(version.CommitData) : null;
}
```

The server calls this method for every commit it needs to traverse, but also for ids sent by clients, which might
not exist (e.g. local commits of the client that have not been pushed). Return `null` for unknown ids. Index the
commit ids in your storage so that these lookups are cheap.

!!! warning "Every commit you return can be fetched"
    Clients can fetch any commit this method returns by its id, even if no branch or tag points to it. Return `null`
    for commits that must not be accessible anymore, e.g. the commits of a deleted draft.

### Creating commits

Create a commit when your data changes, not when it is requested (see
[How It Works](concepts.md#commits-and-why-their-ids-must-never-change)):

```csharp
var tree = GitTree.Create()
                  .Add("index.html", html)
                  .Build();

var commit = GitCommit.Create()
                      .Tree(await tree.ComputeIdAsync())
                      .Parent(previousCommitId)                  // omit for the first commit
                      .Author("Jane Doe", "jane@example.com", savedAt)
                      .Committer("Platform", "noreply@example.com", savedAt) // optional, defaults to the author
                      .Message("Update the landing page")
                      .Build();

await _store.SaveAsync(version, commitId: commit.Id.ToString(), commitData: commit.Data.ToArray());
```

!!! tip "Existing data"
    If you add git support to an application with existing history, create the commits for all existing states once
    (oldest first, so every commit can refer to its parent) and store them. From then on, create the commit whenever
    a new state is saved.

Time stamps are stored with a precision of seconds and with their time zone offset. Messages get a trailing line
break if they do not end with one, as git does. By convention, the first line of a message is a short summary.

## Trees

```csharp
public async ValueTask<GitTree> GetTreeAsync(GitCommit commit)
{
    var version = await _store.FindByCommitAsync(commit.Id.ToString());

    var tree = GitTree.Create();

    foreach (var file in version!.Files)
    {
        tree.Add(file.Path, () => _storage.ReadAsync(file.StorageKey), id: file.BlobId);
    }

    return tree.Build();
}
```

`GitTree` holds `GitFile` instances, which can be created with:

| Content                     | Usage                                                               |
|-----------------------------|---------------------------------------------------------------------|
| A string                    | `Add("README.md", "# Hello")`, encoded as UTF-8 without BOM         |
| Bytes                       | `Add("logo.png", bytes)`                                            |
| A loader                    | `Add("video.mp4", () => LoadAsync(...))`, invoked when needed       |
| A loader and a known id     | `Add("video.mp4", () => LoadAsync(...), id: blobId)`                |

Files can be regular files (default), executables (`GitFileMode.Executable`) or symbolic links
(`GitFileMode.Symlink`, with the content being the target path). Empty directories cannot be represented in git.

Paths are validated: names such as `..`, `.git` (in any spelling treated as `.git` on Windows or macOS) or names
containing backslashes or colons are rejected, as clients would refuse to check them out on some platforms and
providers writing pushed files to disk could be tricked into writing to unexpected locations.

### Known blob ids

To find out which files a client already has, the server needs the blob ids of the files of the commits involved,
which requires reading the files to hash them. If you know the id of a file, pass it as `id`:

- Files received with a push carry their id (`GitFile.Id`), so you can store it along with the file.
- `GitObjectId.ForBlob(content)` computes the id for content you already have in memory.

With known ids, the server only reads a file when it actually sends it. A wrong id corrupts the repository for
clients, so only pass ids you computed from the exact content.

### Reading files more than once

Loaders may be called more than once and must return the same content every time. To send a file, the server needs
its id first (to build the trees) and its content later (to write the pack). Content read to compute an id is kept in
memory until the [content cache](hosting.md#limits) is exhausted, afterwards the file is read again and verified
against its id.

## Truncated histories

If old commits are deleted, return `null` from `GetCommitAsync` for them. The oldest remaining commit then acts as
the shallow boundary of the repository, and clients receive a shallow clone. References must only point to commits
that still exist.

## Read-only access

A repository that only implements `IGitRepository` cannot be pushed to: the server answers attempts to push with
"This repository does not accept pushes". To decide per request (e.g. read-only for anonymous users), resolve
different repository instances, see [Hosting](hosting.md#resolving-repositories-per-request).
