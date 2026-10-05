using GenHTTP.Modules.Authentication;
using GenHTTP.Modules.Git.Tests.Fixtures;
using GenHTTP.Modules.Git.Tests.Infrastructure;
using GenHTTP.Testing;

namespace GenHTTP.Modules.Git.Tests.Integration;

/// <summary>
/// Verifies the scenarios described in the documentation.
/// </summary>
[TestClass]
public sealed class ExampleTests
{

    [TestMethod]
    public async Task TestDirectorySnapshots()
    {
        using var git = new GitClient();

        var content = Directory.CreateDirectory(git.PathOf("content"));

        File.WriteAllText(Path.Combine(content.FullName, "index.html"), "<h1>First</h1>");

        var snapshots = new DirectorySnapshots(content);

        await snapshots.RefreshAsync();

        await using var host = await TestHost.RunAsync(GitServer.Create().Repository(snapshots));

        await git.RunAsync(null, "clone", host.GetUrl("/"), "clone");

        Assert.AreEqual("<h1>First</h1>", git.Read("clone", "index.html"));

        // no changes, no new commit
        await snapshots.RefreshAsync();

        Directory.CreateDirectory(Path.Combine(content.FullName, "css"));
        File.WriteAllText(Path.Combine(content.FullName, "css", "site.css"), "h1 { color: red; }");

        await snapshots.RefreshAsync();

        await git.RunAsync("clone", "pull", "-q", "--ff-only");

        Assert.AreEqual("h1 { color: red; }", git.Read("clone", "css/site.css"));
        Assert.AreEqual("2", (await git.RunAsync("clone", "rev-list", "--count", "HEAD")).Trim());
    }

    [TestMethod]
    public async Task TestBasicAuthentication()
    {
        var repository = await Repositories.WithHistoryAsync(1);

        var server = GitServer.Create()
                              .Repository(repository)
                              .Add(BasicAuthentication.Create().Add("jane", "secret"));

        await using var host = await TestHost.RunAsync(server);

        using var git = new GitClient();

        var anonymous = await git.TryRunAsync(null, "clone", host.GetUrl("/"), "anonymous");

        Assert.IsFalse(anonymous.Success);

        var wrong = await git.TryRunAsync(null, "clone", host.GetUrl("/").Replace("http://", "http://jane:wrong@"), "wrong");

        Assert.IsFalse(wrong.Success);
        StringAssert.Contains(wrong.Error, "Authentication failed");

        await git.RunAsync(null, "clone", host.GetUrl("/").Replace("http://", "http://jane:secret@"), "clone");

        git.Write("clone", "a.txt", "a");
        await git.CommitAllAsync("clone", "Authenticated change");

        await git.RunAsync("clone", "push", "-q", "origin", "main");

        Assert.AreEqual((await git.RunAsync("clone", "rev-parse", "HEAD")).Trim(), repository.References["refs/heads/main"].ToString());
    }

    [TestMethod]
    public async Task TestSignedCommitsAreKept()
    {
        var repository = await Repositories.WithHistoryAsync(1);

        await using var host = await TestHost.RunAsync(GitServer.Create().Repository(repository));

        using var git = new GitClient();

        await git.RunAsync(null, "clone", host.GetUrl("/"), "clone");

        var tree = (await git.RunAsync("clone", "rev-parse", "HEAD^{tree}")).Trim();
        var parent = (await git.RunAsync("clone", "rev-parse", "HEAD")).Trim();

        // a commit with additional headers, as created by signing
        var commit = $"tree {tree}\nparent {parent}\nauthor A <a@example.com> 1700000000 +0000\ncommitter A <a@example.com> 1700000000 +0000\n" +
                     "encoding UTF-8\ngpgsig -----BEGIN PGP SIGNATURE-----\n \n iQEzBAABCAAdFiEE\n -----END PGP SIGNATURE-----\n\nSigned commit\n";

        File.WriteAllText(git.PathOf("commit.txt"), commit);

        var id = (await git.RunAsync("clone", "hash-object", "-t", "commit", "-w", git.PathOf("commit.txt"))).Trim();

        await git.RunAsync("clone", "update-ref", "refs/heads/main", id);

        await git.RunAsync("clone", "push", "-q", "origin", "main");

        await git.RunAsync(null, "clone", host.GetUrl("/"), "verify");

        Assert.AreEqual(id, (await git.RunAsync("verify", "rev-parse", "HEAD")).Trim());

        StringAssert.Contains(await git.RunAsync("verify", "cat-file", "-p", "HEAD"), "gpgsig");
    }

}
