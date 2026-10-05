# Getting Started

## Installation

Add the module to a project hosting a GenHTTP server:

```sh
dotnet add package GenHTTP.Modules.Git
```

The module targets .NET 10 and .NET 11 and works with every GenHTTP engine (internal, Kestrel and ioxide).

## Serving a repository

The quickest way to see the server in action is the `InMemoryGitRepository`, which behaves like a regular bare
repository but keeps everything in memory:

```csharp
using GenHTTP.Engine.Internal;

using GenHTTP.Modules.Git;
using GenHTTP.Modules.Practices;

var repository = new InMemoryGitRepository();

var files = GitTree.Create()
                   .Add("README.md", "# Hello World\n")
                   .Add("src/Program.cs", "Console.WriteLine(\"Hello World\");\n")
                   .Build();

await repository.CommitAsync("main", files, "Initial commit");

var git = GitServer.Create()
                   .Repository(repository);

await Host.Create()
          .Handler(git)
          .Defaults()
          .RunAsync();
```

Run the project and use git as you would with any other server:

```sh
git clone http://localhost:8080/ hello
cd hello

echo "More content" >> README.md
git commit -am "Extend the readme"
git push
```

The handler only responds to the endpoints used by git (`info/refs`, `git-upload-pack` and `git-receive-pack`) and
returns nothing for other requests, so it can be combined with other content:

```csharp
var app = Layout.Create()
                .Add("repo", GitServer.Create().Repository(repository))
                .Add("api", api);

// git clone http://localhost:8080/repo
```

## Serving your own data

The in-memory repository is useful for tests and prototypes. To serve your own data, implement one of two
interfaces:

| Interface                | Purpose                                                       |
|--------------------------|---------------------------------------------------------------|
| `IGitRepository`         | Clients can clone and fetch the repository.                   |
| `IWritableGitRepository` | Clients can additionally push to the repository.              |

```csharp
public sealed class DocumentRepository : IGitRepository
{

    public ValueTask<GitReferences> GetReferencesAsync()
    {
        // which branches and tags exist, and which commit they point to
    }

    public ValueTask<GitCommit?> GetCommitAsync(GitObjectId id)
    {
        // the commit with the given id, or null if unknown
    }

    public ValueTask<GitTree> GetTreeAsync(GitCommit commit)
    {
        // the files of the given commit
    }

}
```

Before implementing the interfaces, read [How It Works](concepts.md): it explains the one rule a virtual repository
must follow so that clients do not see their history rewritten.

## Trying the playground

The repository contains a `Playground` project that serves an in-memory repository:

```sh
git clone https://github.com/Kaliumhexacyanoferrat/virtual-git-server.git
cd virtual-git-server

dotnet run --project Playground
git clone http://localhost:8080/ playground
```
