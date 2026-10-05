using System.Net;

using GenHTTP.Api.Protocol;
using GenHTTP.Modules.Git.Tests.Fixtures;
using GenHTTP.Modules.Git.Tests.Infrastructure;
using GenHTTP.Modules.Layouting;
using GenHTTP.Testing;

namespace GenHTTP.Modules.Git.Tests.Integration;

[TestClass]
public sealed class RoutingTests
{

    [TestMethod]
    public async Task TestNamedRepositories()
    {
        var alpha = await Repositories.WithHistoryAsync(1);
        var beta = await Repositories.WithHistoryAsync(2);

        var git = GitServer.Create()
                           .Repositories((_, name) => new ValueTask<IGitRepository?>(name switch
                           {
                               "alpha" => alpha,
                               "beta" => beta,
                               _ => null
                           }));

        await using var host = await TestHost.RunAsync(Layout.Create().Add("repos", git));

        using var client = new GitClient();

        await client.RunAsync(null, "clone", host.GetUrl("/repos/alpha.git"), "alpha");
        await client.RunAsync(null, "clone", host.GetUrl("/repos/beta"), "beta");

        Assert.AreEqual("1", (await client.RunAsync("alpha", "rev-list", "--count", "HEAD")).Trim());
        Assert.AreEqual("2", (await client.RunAsync("beta", "rev-list", "--count", "HEAD")).Trim());

        var missing = await client.TryRunAsync(null, "clone", host.GetUrl("/repos/gamma.git"), "gamma");

        Assert.IsFalse(missing.Success);
        StringAssert.Contains(missing.Error, "not found");
    }

    [TestMethod]
    public async Task TestRequestBasedResolver()
    {
        var repository = await Repositories.WithHistoryAsync(1);

        var git = GitServer.Create()
                           .Repository(request => new ValueTask<IGitRepository?>(request.Header.Headers.ContainsKey("Authorization") ? repository : null));

        await using var host = await TestHost.RunAsync(git);

        using var client = new GitClient();

        var anonymous = await client.TryRunAsync(null, "clone", host.GetUrl("/"), "anonymous");

        Assert.IsFalse(anonymous.Success);

        await client.RunAsync(null, "-c", "http.extraHeader=Authorization: Bearer 123", "clone", host.GetUrl("/"), "authorized");
    }

    [TestMethod]
    public async Task TestCombinedWithOtherContent()
    {
        var repository = await Repositories.WithHistoryAsync(1);

        var app = Layout.Create()
                        .Add("repo", GitServer.Create().Repository(repository))
                        .Add("other", Layout.Create());

        await using var host = await TestHost.RunAsync(app);

        using var response = await host.GetResponseAsync("/repo/");

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);

        using var client = new GitClient();

        await client.RunAsync(null, "clone", host.GetUrl("/repo"), "clone");
    }

    [TestMethod]
    public async Task TestDumbProtocolIsNotSupported()
    {
        await using var host = await TestHost.RunAsync(GitServer.Create().Repository(new InMemoryGitRepository()));

        using var response = await host.GetResponseAsync("/info/refs");

        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [TestMethod]
    public async Task TestAdvertisementHeaders()
    {
        await using var host = await TestHost.RunAsync(GitServer.Create().Repository(await Repositories.WithHistoryAsync(1)));

        using var response = await host.GetResponseAsync("/info/refs?service=git-upload-pack");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("application/x-git-upload-pack-advertisement", response.Content.Headers.ContentType?.MediaType);
        Assert.IsTrue(response.Headers.CacheControl?.NoCache);

        var body = await response.Content.ReadAsStringAsync();

        Assert.StartsWith("001e# service=git-upload-pack\n0000", body);
        StringAssert.Contains(body, "HEAD\0");
        StringAssert.Contains(body, "symref=HEAD:refs/heads/main");
    }

    [TestMethod]
    public async Task TestReadOnlyRepositoryRefusesReceivePack()
    {
        await using var host = await TestHost.RunAsync(GitServer.Create().Repository(new CountingRepository(new InMemoryGitRepository())));

        using var advertisement = await host.GetResponseAsync("/info/refs?service=git-receive-pack");

        Assert.AreEqual(HttpStatusCode.Forbidden, advertisement.StatusCode);

        var request = host.GetRequest("/git-receive-pack", HttpMethod.Post);

        request.Content = new ByteArrayContent("0000"u8.ToArray());

        using var push = await host.GetResponseAsync(request);

        Assert.AreEqual(HttpStatusCode.Forbidden, push.StatusCode);
    }

    [TestMethod]
    public async Task TestProbeRequests()
    {
        await using var host = await TestHost.RunAsync(GitServer.Create().Repository(new InMemoryGitRepository()));

        foreach (var service in new[] { "git-upload-pack", "git-receive-pack" })
        {
            var request = host.GetRequest($"/{service}", HttpMethod.Post);

            request.Content = new ByteArrayContent("0000"u8.ToArray());

            using var response = await host.GetResponseAsync(request);

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.AreEqual(string.Empty, await response.Content.ReadAsStringAsync());
        }
    }

    [TestMethod]
    public async Task TestProtocolErrorsAreReported()
    {
        await using var host = await TestHost.RunAsync(GitServer.Create().Repository(await Repositories.WithHistoryAsync(1)));

        var request = host.GetRequest("/git-upload-pack", HttpMethod.Post);

        request.Content = new ByteArrayContent("0032want 0000000000000000000000000000000000000001\n00000009done\n"u8.ToArray());

        using var response = await host.GetResponseAsync(request);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();

        StringAssert.Contains(body, "ERR upload-pack: not our ref 0000000000000000000000000000000000000001");
    }

    [TestMethod]
    public async Task TestOtherMethodsAreIgnored()
    {
        await using var host = await TestHost.RunAsync(GitServer.Create().Repository(new InMemoryGitRepository()));

        using var response = await host.GetResponseAsync("/git-upload-pack");

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    [TestMethod]
    public void TestRepositoryIsRequired()
    {
        Assert.ThrowsExactly<GenHTTP.Api.Infrastructure.BuilderMissingPropertyException>(() => GitServer.Create().Build());
    }

    [TestMethod]
    public void TestAgentIsValidated()
    {
        Assert.ThrowsExactly<ArgumentException>(() => GitServer.Create().Agent("with space"));
    }

    [TestMethod]
    public async Task TestCustomAgentIsAnnounced()
    {
        await using var host = await TestHost.RunAsync(GitServer.Create().Repository(new InMemoryGitRepository()).Agent("my-server/1.0"));

        var request = host.GetRequest("/info/refs?service=git-upload-pack");

        request.Headers.Add("Git-Protocol", "version=2");

        using var response = await host.GetResponseAsync(request);

        var body = await response.Content.ReadAsStringAsync();

        StringAssert.Contains(body, "agent=my-server/1.0");
        StringAssert.Contains(body, "version 2");
    }

    [TestMethod]
    public async Task TestInconsistentRepositoryIsReported()
    {
        var inner = await Repositories.WithHistoryAsync(1);

        var repository = new InconsistentRepository(inner);

        await using var host = await TestHost.RunAsync(GitServer.Create().Repository(repository));

        using var client = new GitClient();

        var result = await client.TryRunAsync(null, "clone", host.GetUrl("/"), "clone");

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "the repository is inconsistent");
    }

    private sealed class InconsistentRepository(IGitRepository inner) : IGitRepository
    {

        public ValueTask<GitReferences> GetReferencesAsync() => inner.GetReferencesAsync();

        public ValueTask<GitCommit?> GetCommitAsync(GitObjectId id) => inner.GetCommitAsync(id);

        public ValueTask<GitTree> GetTreeAsync(GitCommit commit) => new(GitTree.Create().Add("changed.txt", "not what the commit says").Build());

    }

    [TestMethod]
    public async Task TestProviderExceptionsArePassedThrough()
    {
        var git = GitServer.Create()
                           .Repository(_ => throw new GenHTTP.Api.Content.ProviderException(ResponseStatus.Forbidden, "Nope"));

        await using var host = await TestHost.RunAsync(git);

        using var response = await host.GetResponseAsync("/info/refs?service=git-upload-pack");

        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

}
