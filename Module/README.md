# GenHTTP.Modules.Git

Serves **virtual git repositories** from a [GenHTTP](https://genhttp.org/) handler. Clones, fetches and pushes are
answered from your own data model via the smart HTTP protocol, without a repository on disk and without the
`git` binary.

```csharp
using GenHTTP.Engine.Internal;
using GenHTTP.Modules.Git;

var git = GitServer.Create()
                   .Repository(new MyRepository()); // implements IGitRepository or IWritableGitRepository

await Host.Create()
          .Handler(git)
          .RunAsync();

// git clone http://localhost:8080/
```

A repository describes its branches and tags, looks up commits by their id and returns the files of a commit:

```csharp
public sealed class MyRepository : IGitRepository
{

    public ValueTask<GitReferences> GetReferencesAsync() => ...;

    public ValueTask<GitCommit?> GetCommitAsync(GitObjectId id) => ...;

    public ValueTask<GitTree> GetTreeAsync(GitCommit commit) => ...;

}
```

Implement `IWritableGitRepository` to accept pushes, deciding for every pushed branch or tag whether to accept it.

## Features

- Protocol v2 (`ls-refs`, `fetch`) and v0/v1 for fetching, v0 for pushing
- Shallow clones (`--depth`, `--shallow-since`, `--shallow-exclude`, `--deepen`, `--unshallow`)
- Truncated histories (e.g. old versions deleted) are served as shallow repositories
- Pushes with deltas, push options and messages shown as `remote: ...`
- Works with all GenHTTP engines and concerns (e.g. authentication)

See the [documentation](https://kaliumhexacyanoferrat.github.io/virtual-git-server/) for a full guide.
