# GenHTTP.Modules.Git

Serves **virtual git repositories** from a [GenHTTP](https://genhttp.org/) handler. Clones, fetches and pushes are
answered from your own data model via the smart HTTP protocol - without a repository on disk and without the `git`
binary.

Use it to offer versioned content (documents, configurations, apps, snippets) to developers, CI pipelines and agents
that know how to use git.

## Getting started

Add the package to a project hosting a GenHTTP server:

```sh
dotnet add package GenHTTP.Modules.Git
```

Serve a repository via `GitServer.Create()`. The `InMemoryGitRepository` behaves like a bare repository and is a good
starting point:

```csharp
using GenHTTP.Engine.Internal;

using GenHTTP.Modules.Git;
using GenHTTP.Modules.Practices;

var repository = new InMemoryGitRepository();

await repository.CommitAsync("main", GitTree.Create()
                                            .Add("README.md", "# Hello World")
                                            .Build(), "Initial commit");

var git = GitServer.Create()
                   .Repository(repository);

await Host.Create()
          .Handler(git)
          .Defaults()
          .RunAsync();
```

```sh
git clone http://localhost:8080/ hello
```

## Serving your own data

Implement `IGitRepository` to describe your data as branches, tags, commits and files:

```csharp
public sealed class DocumentRepository : IGitRepository
{

    // the branches and tags, e.g. one tag per version
    public ValueTask<GitReferences> GetReferencesAsync() => ...;

    // a commit by its id, restored from the bytes stored when it was created
    public ValueTask<GitCommit?> GetCommitAsync(GitObjectId id) => ...;

    // the files of a commit, loaded on demand
    public ValueTask<GitTree> GetTreeAsync(GitCommit commit) => ...;

}
```

Commits are created once with `GitCommit.Create()` and stored as raw bytes (`GitCommit.Data`), so their ids never
change - no matter what changes in your code or in this library.

## Accepting pushes

Implement `IWritableGitRepository` to decide what happens when a client pushes. The server parses and verifies the
data and passes every updated branch or tag along with its new commits and their files:

```csharp
public async ValueTask PushAsync(GitPush push)
{
    foreach (var update in push.Updates)
    {
        if (!update.IsFastForward)
        {
            update.Reject("the history cannot be rewritten");
            continue;
        }

        foreach (var revision in update.Revisions)
        {
            await StoreAsync(revision.Commit, revision.Tree);
        }

        await push.MessageAsync("Saved!"); // shown as "remote: Saved!"

        update.Accept();
    }
}
```

## Features

- Protocol v2 (`ls-refs`, `fetch`) and v0/v1 for fetching, v0 for pushing
- Shallow clones (`--depth`, `--shallow-since`, `--shallow-exclude`, `--deepen`, `--unshallow`)
- Truncated histories (e.g. deleted old versions) are served as shallow repositories
- Pushes with deltas, push options (`git push -o`) and messages
- Regular files, executables and symbolic links, loaded lazily
- Single or multiple repositories per handler, all GenHTTP engines and concerns (e.g. authentication)
- Protection against crafted requests (size and file limits, validated file names)

Annotated tags, submodules, partial clones (`--filter`), the dumb HTTP protocol and SHA-256 repositories are not
supported.

## Documentation

The [documentation](https://kaliumhexacyanoferrat.github.io/virtual-git-server/) explains the concepts, how to
implement repositories and accept pushes, and contains complete examples such as a versioned content store with
drafts.

## Feedback

Please report issues and ideas on [GitHub](https://github.com/Kaliumhexacyanoferrat/virtual-git-server/issues).
For questions about GenHTTP in general, join the [GenHTTP Discord](https://discord.gg/PRkwKrnrB4).

## License

[MIT](https://github.com/Kaliumhexacyanoferrat/virtual-git-server/blob/main/LICENSE)
