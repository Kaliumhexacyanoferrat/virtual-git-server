# Virtual Git Server

A [GenHTTP](https://genhttp.org/) module that serves **virtual git repositories**: clones, fetches and pushes are
answered from your own data model over the smart HTTP protocol, without a repository on disk and without the `git`
binary. Use it to expose versioned content (documents, configurations, apps, snippets) to developers, CI pipelines and
agents that know how to use git.

[![nuget Package](https://img.shields.io/nuget/v/GenHTTP.Modules.Git.svg)](https://www.nuget.org/packages/GenHTTP.Modules.Git/) [![Build](https://github.com/Kaliumhexacyanoferrat/virtual-git-server/actions/workflows/build.yml/badge.svg)](https://github.com/Kaliumhexacyanoferrat/virtual-git-server/actions/workflows/build.yml) [![View - Documentation](https://img.shields.io/badge/view-Documentation-AB54FF)](https://kaliumhexacyanoferrat.github.io/virtual-git-server/)

## Getting Started

Add the `GenHTTP.Modules.Git` package to your project and serve a repository via `GitServer.Create()`:

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

To serve your own data, implement `IGitRepository` (read-only) or `IWritableGitRepository` (accepting pushes):

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
| Hosting   | Single or multiple repositories, any GenHTTP engine, concerns such as authentication                        |

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
