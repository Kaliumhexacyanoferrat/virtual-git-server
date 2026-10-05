# Testing

As your repository is the single source of truth for everything git sees, it is worth testing it with a real git
client. `GenHTTP.Testing` makes this straightforward: it hosts a handler on a free port for the duration of a test.

```csharp
[TestMethod]
public async Task TestPushedVersionsCanBeCloned()
{
    var repository = new DocumentRepository(...);

    await using var host = await TestHost.RunAsync(GitServer.Create().Repository(repository));

    // run git clone/commit/push against host.GetUrl("/") and inspect your repository
}
```

Running git from tests requires a few precautions so the tests neither depend on nor change the configuration of the
machine they run on:

- Use a temporary `HOME` with its own `.gitconfig` (user name and email, `init.defaultBranch`), and set
  `GIT_CONFIG_NOSYSTEM=1` and `GIT_TERMINAL_PROMPT=0`.
- Enable `transfer.fsckObjects` so the client verifies everything it receives, and run `git fsck --strict` on clones.
- Run the important scenarios with `protocol.version` set to `0` and `2`, as the server implements both.

The [test suite of this project](https://github.com/Kaliumhexacyanoferrat/virtual-git-server/tree/main/Testing)
contains a `GitClient` helper doing exactly this, along with tests for many scenarios you can use as a starting
point.

## What to test

- **Stability**: cloning twice (and after restarting your application) yields the same commit ids.
- **Round trips**: pushed commits are returned with the same ids and files, e.g. by cloning into a second directory.
- **Rules**: everything your repository rejects is reported to the client with a helpful message.
- **Concurrency**: changes made outside of git while a client works on a clone result in "fetch first" rejections
  instead of lost changes.
- **History limits**: if you delete old data, clones still work and are shallow.

## Using the in-memory repository

`InMemoryGitRepository` behaves like a bare repository hosted by git and is useful to test code that consumes
repositories (e.g. a CI integration), or as a reference when implementing your own repository:

```csharp
var repository = new InMemoryGitRepository { DenyNonFastForwards = true };

await repository.CommitAsync("main", GitTree.Create().Add("a.txt", "a").Build(), "Initial commit");

repository.SetReference("refs/tags/v1", repository.References["refs/heads/main"]);
```
