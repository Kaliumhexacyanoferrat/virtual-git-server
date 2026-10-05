# GenHTTP.Modules.Git

A C# library that turns your .NET application into a **git server for your own data**. Users clone, fetch and push
with the regular `git` client, while every request is answered from your data model (a database, a blob store, an
API, ...) - without a repository on disk and without the `git` binary on the server.

Use it to offer versioned content (documents, configurations, apps, snippets) to developers, CI pipelines and agents
that know how to use git.

The package is a module for [GenHTTP](https://genhttp.org/), a lightweight, embeddable web server framework for
.NET. It provides a request handler that speaks git's smart HTTP protocol, which you can host in a GenHTTP
application or map to a path of an existing ASP.NET Core application.

## Getting started

Create a console application and add the module along with a GenHTTP engine, which runs the server:

```sh
dotnet new console -n GitDemo
cd GitDemo

dotnet add package GenHTTP.Modules.Git
dotnet add package GenHTTP.Engine.Internal
dotnet add package GenHTTP.Modules.Practices
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

## Using ASP.NET Core

With the [GenHTTP.Adapters.AspNetCore](https://www.nuget.org/packages/GenHTTP.Adapters.AspNetCore/) package, the
handler can be mapped to a path of an existing ASP.NET Core application:

```sh
dotnet add package GenHTTP.Modules.Git
dotnet add package GenHTTP.Adapters.AspNetCore
```

```csharp
using GenHTTP.Adapters.AspNetCore;

using GenHTTP.Modules.Git;

var builder = WebApplication.CreateBuilder(args);

// Kestrel accepts 30 MB request bodies by default, allow larger pushes
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 128 * 1024 * 1024);

var app = builder.Build();

// serve the repository (created as shown above) at /repo,
// all other requests are handled by your application
app.Map("/repo", GitServer.Create().Repository(repository));

await app.RunAsync();
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
- Single or multiple repositories per handler, all GenHTTP engines and concerns (e.g. authentication), ASP.NET Core
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
