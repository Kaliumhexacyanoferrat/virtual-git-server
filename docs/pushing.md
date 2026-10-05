# Accepting Pushes

To accept pushes, implement `IWritableGitRepository`, which extends `IGitRepository` by a single method:

```csharp
public interface IWritableGitRepository : IGitRepository
{

    ValueTask PushAsync(GitPush push);

}
```

## What the server does for you

Before `PushAsync` is invoked, the server has already:

- parsed the pack sent by the client, including all deltas, and verified its checksum,
- checked that the names of the references are valid and that every update is based on the current state
  of the reference (otherwise the client is told to fetch first),
- collected the commits that are new to the repository and reconstructed their files, taking files the client did
  not send (because the server already has them) from your repository,
- rejected content it cannot represent: annotated tags, submodules, unusual file modes, empty directories, invalid
  or dangerous file names (such as `..` or `.git`) and malformed objects.

Updates rejected by these checks are not passed to your repository.

## Deciding about updates

A push consists of one or more `GitReferenceUpdate` instances, one for every branch or tag the client wants to create,
move or delete. Every update needs to be either accepted or rejected:

```csharp
public async ValueTask PushAsync(GitPush push)
{
    foreach (var update in push.Updates)
    {
        if (update.Name != "refs/heads/main")
        {
            update.Reject("only main can be pushed");
            continue;
        }

        if (!update.IsFastForward)
        {
            update.Reject("the history cannot be rewritten");
            continue;
        }

        foreach (var revision in update.Revisions)
        {
            await StoreAsync(revision.Commit, revision.Tree);
        }

        update.Accept();
    }
}
```

Updates neither accepted nor rejected are reported as rejected. The client shows rejections like this:

```text
 ! [remote rejected] main -> main (the history cannot be rewritten)
```

### Properties of an update

| Property                     | Description                                                                      |
|------------------------------|----------------------------------------------------------------------------------|
| `Name`, `ShortName`          | The full (`refs/heads/main`) and short (`main`) name of the reference            |
| `IsBranch`, `IsTag`          | Whether the reference is located below `refs/heads/` or `refs/tags/`             |
| `OldId`, `NewId`             | The previous and the new commit, `GitObjectId.Zero` for creations and deletions  |
| `IsCreate`, `IsDelete`       | Whether the reference is created or deleted                                      |
| `IsFastForward`              | Whether the new commit is based on the old one (false for `git push --force`)    |
| `Commit`                     | The commit the reference should point to (`null` for deletions)                  |
| `Revisions`                  | The commits new to the repository with their files, parents before children      |

`Revisions` contains the commits the repository did not know before, in an order that lists parents before their
children (for a linear history: oldest first). If a client creates a branch for an existing commit (e.g.
`git push origin v1:refs/heads/fix`), `Revisions` is empty and `Commit` refers to the existing commit.

### Storing pushed commits

!!! warning "Store commits exactly as received"
    After accepting an update, `GetCommitAsync` must return the pushed commits with exactly the same data, and
    `GetTreeAsync` must return exactly the same files and modes. Store `GitCommit.Data` and the content of every
    file. If you cannot store something (e.g. executable files), reject the update instead of storing a modified
    version, as the client already uses the ids of the commits it pushed.

The files of a revision are available via `revision.Tree`. Their content is either already in memory (sent by the
client) or loaded from your repository on demand (files the client did not send). Each file carries its blob id
(`GitFile.Id`), which you can store to speed up later fetches.

```csharp
foreach (var file in revision.Tree)
{
    if (file.Mode != GitFileMode.Regular)
    {
        update.Reject("only regular files are supported");
        return;
    }

    files.Add(file.Path, (await file.ReadAsync()).ToArray(), file.Id);
}
```

### Concurrency

The server checks `OldId` against the references returned by your repository, but the state may change while the
push is processed (e.g. someone saves a version via a web interface). Verify `OldId` again while holding a lock or
within a transaction, and reject the update if it no longer matches:

```csharp
lock (_lock)
{
    if (CurrentMainCommit != update.OldId)
    {
        update.Reject("a new version has been saved in the meantime, fetch first");
        return;
    }

    // apply the update
}
```

## Messages

`MessageAsync` sends a message to the client, which is shown immediately with a `remote:` prefix. Use it to report
what happened or to stream the progress of a longer operation such as a build:

```csharp
await push.MessageAsync($"Created version {version.Number}");
await push.MessageAsync("Compiling ...");
```

```text
remote: Created version 13
remote: Compiling ...
To https://example.com/repo
   3f2a1c9..8d0e4b2  main -> main
```

## Push options

Clients can pass options with `git push -o <option>`, which are available as `push.Options`. Use them to let users
control what happens with a push:

```csharp
if (push.Options.Contains("deploy"))
{
    await DeployAsync();
}
```

```sh
git push -o deploy
```

## Errors

Exceptions thrown by `PushAsync` are logged, and all updates that have not been decided are reported as
"internal server error" without revealing the exception to the client. Throw only for unexpected failures and use
`Reject` for everything the user can fix.

## Limits

Pushes are processed in memory. The maximum size of a push defaults to 128 MB and can be changed via
`MaximumPushSize` (see [Hosting](hosting.md#limits)). Clients exceeding the limit receive an HTTP 413 error.
