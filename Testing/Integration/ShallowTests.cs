using GenHTTP.Modules.Git.Tests.Fixtures;
using GenHTTP.Modules.Git.Tests.Infrastructure;
using GenHTTP.Testing;

namespace GenHTTP.Modules.Git.Tests.Integration;

[TestClass]
public sealed class ShallowTests
{

    [TestMethod]
    [DataRow(0)]
    [DataRow(2)]
    public async Task TestDepth(int protocol)
    {
        var repository = await Repositories.WithHistoryAsync(5);

        await using var host = await TestHost.RunAsync(GitServer.Create().Repository(repository));

        using var git = new GitClient(protocol);

        await git.RunAsync(null, "clone", "--depth", "1", host.GetUrl("/"), "clone");

        Assert.AreEqual("1", (await git.RunAsync("clone", "rev-list", "--count", "HEAD")).Trim());
        Assert.AreEqual("true", (await git.RunAsync("clone", "rev-parse", "--is-shallow-repository")).Trim());
        Assert.AreEqual("# Version 5\n", git.Read("clone", "README.md"));

        await git.RunAsync("clone", "fsck");

        await git.RunAsync("clone", "fetch", "-q", "--depth", "3");

        Assert.AreEqual("3", (await git.RunAsync("clone", "rev-list", "--count", "HEAD")).Trim());

        await git.RunAsync("clone", "fetch", "-q", "--deepen", "1");

        Assert.AreEqual("4", (await git.RunAsync("clone", "rev-list", "--count", "HEAD")).Trim());

        await git.RunAsync("clone", "fetch", "-q", "--unshallow");

        Assert.AreEqual("5", (await git.RunAsync("clone", "rev-list", "--count", "HEAD")).Trim());
        Assert.AreEqual("false", (await git.RunAsync("clone", "rev-parse", "--is-shallow-repository")).Trim());

        await git.RunAsync("clone", "fsck", "--strict");
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(2)]
    public async Task TestShallowCloneReceivesUpdates(int protocol)
    {
        var repository = await Repositories.WithHistoryAsync(5);

        await using var host = await TestHost.RunAsync(GitServer.Create().Repository(repository));

        using var git = new GitClient(protocol);

        await git.RunAsync(null, "clone", "--depth", "1", host.GetUrl("/"), "clone");

        await repository.CommitAsync("main", GitTree.Create().Add("new.txt", "new").Build(), "Version 6");

        await git.RunAsync("clone", "pull", "-q", "--ff-only");

        Assert.AreEqual("2", (await git.RunAsync("clone", "rev-list", "--count", "HEAD")).Trim());
        Assert.AreEqual("new", git.Read("clone", "new.txt"));

        await git.RunAsync("clone", "fsck");
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(2)]
    public async Task TestShallowSince(int protocol)
    {
        var repository = await Repositories.WithHistoryAsync(5);

        await using var host = await TestHost.RunAsync(GitServer.Create().Repository(repository));

        using var git = new GitClient(protocol);

        // commits are one day apart, starting with day 1
        var since = Repositories.Start.AddDays(3).ToUnixTimeSeconds();

        await git.RunAsync(null, "clone", $"--shallow-since={since}", host.GetUrl("/"), "clone");

        Assert.AreEqual("Version 5\nVersion 4\nVersion 3\n", await git.RunAsync("clone", "log", "--format=%s"));

        await git.RunAsync("clone", "fsck");
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(2)]
    public async Task TestShallowExclude(int protocol)
    {
        var repository = await Repositories.WithHistoryAsync(5);

        await using var host = await TestHost.RunAsync(GitServer.Create().Repository(repository));

        using var git = new GitClient(protocol);

        await git.RunAsync(null, "clone", "--shallow-exclude=v2", "--no-tags", host.GetUrl("/"), "clone");

        Assert.AreEqual("Version 5\nVersion 4\nVersion 3\n", await git.RunAsync("clone", "log", "--format=%s"));

        await git.RunAsync("clone", "fsck");
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(2)]
    public async Task TestTruncatedHistory(int protocol)
    {
        var inner = await Repositories.WithHistoryAsync(10);

        var repository = new TruncatedRepository(inner, 4);

        await using var host = await TestHost.RunAsync(GitServer.Create().Repository(repository));

        using var git = new GitClient(protocol);

        await git.RunAsync(null, "clone", host.GetUrl("/"), "clone");

        Assert.AreEqual("4", (await git.RunAsync("clone", "rev-list", "--count", "HEAD")).Trim());
        Assert.AreEqual("true", (await git.RunAsync("clone", "rev-parse", "--is-shallow-repository")).Trim());

        await git.RunAsync("clone", "fsck");

        // newer commits push older ones out of the history
        await inner.CommitAsync("main", GitTree.Create().Add("a.txt", "11").Build(), "Version 11");
        await inner.CommitAsync("main", GitTree.Create().Add("a.txt", "12").Build(), "Version 12");

        await git.RunAsync("clone", "pull", "-q", "--ff-only");

        Assert.AreEqual("6", (await git.RunAsync("clone", "rev-list", "--count", "HEAD")).Trim());
        Assert.AreEqual("12", git.Read("clone", "a.txt"));

        await git.RunAsync("clone", "fsck");
    }

}
