using GenHTTP.Api.Infrastructure;
using GenHTTP.Modules.Git.Tests.Fixtures;
using GenHTTP.Modules.Git.Tests.Infrastructure;
using GenHTTP.Testing;

namespace GenHTTP.Modules.Git.Tests.Integration;

/// <summary>
/// Runs a typical workflow on all engines supported by GenHTTP, as they
/// differ in how they handle streamed and chunked bodies.
/// </summary>
[TestClass]
public sealed class EngineTests
{

    [TestMethod]
    [DataRow(ServerEngine.Internal, 0)]
    [DataRow(ServerEngine.Internal, 2)]
    [DataRow(ServerEngine.Kestrel, 0)]
    [DataRow(ServerEngine.Kestrel, 2)]
    public async Task TestCloneAndPush(ServerEngine engine, int protocol)
    {
        var repository = await Repositories.WithHistoryAsync(3);

        await using var host = await TestHost.RunAsync(GitServer.Create().Repository(repository), engine: engine);

        using var git = new GitClient(protocol);

        await git.RunAsync(null, "clone", host.GetUrl("/"), "clone");

        var data = new byte[3 * 1024 * 1024];
        new Random(9).NextBytes(data);

        git.Write("clone", "large.bin", data);

        await git.CommitAllAsync("clone", "Large file");

        await git.RunAsync("clone", "push", "-q", "origin", "main");

        await git.RunAsync(null, "clone", host.GetUrl("/"), "verify");

        CollectionAssert.AreEqual(data, File.ReadAllBytes(Path.Combine(git.PathOf("verify"), "large.bin")));

        await git.RunAsync("verify", "fsck", "--strict");
    }

}
