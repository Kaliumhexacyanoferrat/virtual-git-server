# Virtual Git Server

A C# library that turns your .NET application into a **git server for your own data**. Users clone, fetch and push
with the regular `git` client, while every request is answered from your data model (a database, a blob store, an
API, ...) - without a repository on disk and without the `git` binary on the server.

Use it to expose versioned content (documents, configurations, apps, snippets) to developers, CI pipelines and coding
agents that know how to use git, instead of building custom sync tools.

[![nuget Package](https://img.shields.io/nuget/v/GenHTTP.Modules.Git.svg)](https://www.nuget.org/packages/GenHTTP.Modules.Git/) [![Build](https://github.com/Kaliumhexacyanoferrat/virtual-git-server/actions/workflows/build.yml/badge.svg)](https://github.com/Kaliumhexacyanoferrat/virtual-git-server/actions/workflows/build.yml) [![View - Documentation](https://img.shields.io/badge/view-Documentation-AB54FF)](https://kaliumhexacyanoferrat.github.io/virtual-git-server/)

## How it fits in

The library is distributed as the NuGet package `GenHTTP.Modules.Git`. It is a module for
[GenHTTP](https://genhttp.org/), a lightweight, embeddable web server framework for .NET: the module provides a
request handler that speaks git's smart HTTP protocol, and you decide where in your web application it lives.

You can use it in two ways:

- **In a GenHTTP application** - the module plugs into a GenHTTP server like any other handler, either as the only
  content or next to websites, APIs and files ([Getting Started](#getting-started)).
- **In an ASP.NET Core application** - an adapter maps the handler to a path of your existing `WebApplication`, next
  to your controllers and minimal APIs ([Using ASP.NET Core](#using-aspnet-core)).

In both cases, your code describes the repository (branches, commits and files) and decides what to do with pushes;
the module handles the git protocol.

## Getting Started

You need the [.NET SDK](https://dotnet.microsoft.com/download) (10 or newer). Create a console application and add
the module along with a GenHTTP engine to run the server:

```sh
dotnet new console -n GitDemo
cd GitDemo

dotnet add package GenHTTP.Modules.Git
dotnet add package GenHTTP.Engine.Internal
dotnet add package GenHTTP.Modules.Practices
```

Replace the content of `Program.cs` to serve a repository via `GitServer.Create()`:

```csharp
using GenHTTP.Engine.Internal;

using GenHTTP.Modules.Git;
using GenHTTP.Modules.Practices;

// a repository that lives in memory, useful for trying things out
var repository = new InMemoryGitRepository();

await repository.CommitAsync("main", GitTree.Create()
                                            .Add("README.md", "# Hello World")
                                            .Build(), "Initial commit");

// the handler answering git requests
var git = GitServer.Create()
                   .Repository(repository);

// a GenHTTP server listening on port 8080
await Host.Create()
          .Handler(git)
          .Defaults()
          .RunAsync();
```

Start the application with `dotnet run` and use git as with any other server:

```sh
git clone http://localhost:8080/ hello
```

To combine the repository with other content, add it to a layout from the `GenHTTP.Modules.Layouting` package
(e.g. `Layout.Create().Add("repo", git)` to serve it at `/repo`). See the [GenHTTP documentation](https://genhttp.org/documentation/) for everything else a GenHTTP
server can do.

## Using ASP.NET Core

If you already have an ASP.NET Core application, the
[GenHTTP.Adapters.AspNetCore](https://www.nuget.org/packages/GenHTTP.Adapters.AspNetCore/) package lets you map the
git handler to a path, so you do not need to run a separate server:

```sh
dotnet add package GenHTTP.Modules.Git
dotnet add package GenHTTP.Adapters.AspNetCore
```

```csharp
using GenHTTP.Adapters.AspNetCore;

using GenHTTP.Modules.Git;

var repository = new InMemoryGitRepository();

await repository.CommitAsync("main", GitTree.Create()
                                            .Add("README.md", "# Hello World")
                                            .Build(), "Initial commit");

var builder = WebApplication.CreateBuilder(args);

// Kestrel accepts 30 MB request bodies by default, allow larger pushes
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 128 * 1024 * 1024);

var app = builder.Build();

// serve the repository at /repo
app.Map("/repo", GitServer.Create().Repository(repository));

// the rest of your application
app.MapGet("/", () => "Hello from ASP.NET Core");

await app.RunAsync();
```

```sh
# use the address your application listens on
git clone http://localhost:5000/repo hello
```

The handler only answers requests below the mapped path, all other requests are passed on to the rest of your
application. The [documentation](https://kaliumhexacyanoferrat.github.io/virtual-git-server/aspnet-core/) covers
multiple repositories and authentication with ASP.NET Core.

## Serving your own data

The in-memory repository is a good start. To serve your own data, implement `IGitRepository` (read-only) or
`IWritableGitRepository` (accepting pushes):

```csharp
public sealed class DocumentRepository : IWritableGitRepository
{

    // the branches and tags, e.g. one tag per version
    public ValueTask<GitReferences> GetReferencesAsync() => ...;

    // a commit by its id, as stored when the version was created
    public ValueTask<GitCommit?> GetCommitAsync(GitObjectId id) => ...;

    // the files of a commit, loaded lazily if needed
    public ValueTask<GitTree> GetTreeAsync(GitCommit commit) => ...;

    // accept or reject what a client pushed
    public ValueTask PushAsync(GitPush push) => ...;

}
```

The [documentation](https://kaliumhexacyanoferrat.github.io/virtual-git-server/) explains the concepts, how to keep
commit ids stable, how pushes are validated and shows a complete example of a versioned content store.

## Features

| Area      | Support                                                                                                     |
|-----------|-------------------------------------------------------------------------------------------------------------|
| Transport | Smart HTTP, protocol v2 (`ls-refs`, `fetch`) and v0/v1 for fetching, v0 for pushing                           |
| Fetching  | Clones, incremental fetches, shallow clones (`--depth`, `--shallow-since`, `--shallow-exclude`, `--deepen`) |
| Pushing   | Creating, updating and deleting branches and tags, deltas, push options, `remote:` messages                 |
| History   | Truncated histories (e.g. deleted old versions) are served as shallow repositories                          |
| Files     | Regular files, executables and symbolic links, loaded on demand                                             |
| Hosting   | Single or multiple repositories, any GenHTTP engine or ASP.NET Core, concerns such as authentication        |

Not supported: annotated tags, submodules, partial clones (`--filter`), the dumb HTTP protocol and SHA-256
repositories. See [protocol support](https://kaliumhexacyanoferrat.github.io/virtual-git-server/protocol/) for details.

## Building

The project requires the .NET 11 SDK (see `global.json`) and targets .NET 10 and 11. The tests use the `git`
command line client, which needs to be installed.

```sh
dotnet build GenHTTP.Modules.Git.slnx
dotnet test GenHTTP.Modules.Git.slnx

# serves an in-memory repository on http://localhost:8080/
dotnet run --project Playground
```

The documentation is built with [MkDocs](https://www.mkdocs.org/):

```sh
pip install -r docs/requirements.txt
mkdocs serve
```

## License

[MIT](LICENSE)
