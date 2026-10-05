using GenHTTP.Modules.Git.Tests.Fixtures;
using GenHTTP.Modules.Git.Tests.Infrastructure;
using GenHTTP.Testing;

namespace GenHTTP.Modules.Git.Tests.Integration;

[TestClass]
public sealed class FetchTests
{

    [TestMethod]
    [DataRow(0)]
    [DataRow(2)]
    public async Task TestIncrementalFetch(int protocol)
    {
        var repository = await Repositories.WithHistoryAsync(3);

        await using var host = await TestHost.RunAsync(GitServer.Create().Repository(repository));

        using var git = new GitClient(protocol);

        await git.RunAsync(null, "clone", host.GetUrl("/"), "clone");

        await repository.CommitAsync("main", GitTree.Create().Add("README.md", "# Updated\n").Build(), "Update");

        await git.RunAsync("clone", "pull", "--ff-only", "-q");

        Assert.AreEqual("# Updated\n", git.Read("clone", "README.md"));
        Assert.IsFalse(git.Exists("clone", "src/Shared.cs"));

        Assert.AreEqual("4", (await git.RunAsync("clone", "rev-list", "--count", "HEAD")).Trim());

        await git.RunAsync("clone", "fsck", "--strict");
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(2)]
    public async Task TestIncrementalFetchOnlyReadsNewCommits(int protocol)
    {
        var inner = await Repositories.WithHistoryAsync(10);

        var repository = new CountingRepository(inner);

        await using var host = await TestHost.RunAsync(GitServer.Create().Repository(repository));

        using var git = new GitClient(protocol);

        await git.RunAsync(null, "clone", host.GetUrl("/"), "clone");

        Assert.AreEqual(10, repository.TreeRequests);

        await inner.CommitAsync("main", GitTree.Create().Add("README.md", "# Updated\n").Build(), "Update");

        await git.RunAsync("clone", "fetch", "-q");

        // the files of the new commit and of its parent, which the client already has
        Assert.AreEqual(12, repository.TreeRequests);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(2)]
    public async Task TestFetchWithManyLocalCommits(int protocol)
    {
        var repository = await Repositories.WithHistoryAsync(2);

        await using var host = await TestHost.RunAsync(GitServer.Create().Repository(repository));

        using var git = new GitClient(protocol);

        await git.RunAsync(null, "clone", host.GetUrl("/"), "clone");

        // many unknown commits make the client send large (compressed) requests
        for (var i = 0; i < 60; i++)
        {
            git.Write("clone", "local.txt", $"local {i}");
            await git.CommitAllAsync("clone", $"Local {i}");
        }

        await repository.CommitAsync("main", GitTree.Create().Add("remote.txt", "remote").Build(), "Remote change");

        await git.RunAsync("clone", "fetch", "-q");

        Assert.AreEqual("Remote change\n", await git.RunAsync("clone", "log", "-1", "--format=%s", "origin/main"));

        await git.RunAsync("clone", "fsck", "--strict");
    }

    [TestMethod]
    public async Task TestRewrittenBranchIsForceUpdated()
    {
        var repository = await Repositories.WithHistoryAsync(2);

        await repository.CommitAsync("feature", GitTree.Create().Add("a.txt", "first").Build(), "First attempt");

        await using var host = await TestHost.RunAsync(GitServer.Create().Repository(repository));

        using var git = new GitClient();

        await git.RunAsync(null, "clone", host.GetUrl("/"), "clone");

        // replace the branch with an unrelated commit
        repository.DeleteReference("refs/heads/feature");

        await repository.CommitAsync("feature", GitTree.Create().Add("a.txt", "second").Build(), "Second attempt");

        var result = await git.TryRunAsync("clone", "fetch");

        Assert.IsTrue(result.Success, result.ToString());
        StringAssert.Contains(result.Error, "forced update");

        Assert.AreEqual("Second attempt\n", await git.RunAsync("clone", "log", "-1", "--format=%s", "origin/feature"));
    }

    [TestMethod]
    public async Task TestFetchOfUpToDateClone()
    {
        var repository = await Repositories.WithHistoryAsync(2);

        await using var host = await TestHost.RunAsync(GitServer.Create().Repository(repository));

        using var git = new GitClient();

        await git.RunAsync(null, "clone", host.GetUrl("/"), "clone");

        var result = await git.TryRunAsync("clone", "fetch");

        Assert.IsTrue(result.Success, result.ToString());
        Assert.AreEqual(string.Empty, result.Error.Trim());
    }

}
