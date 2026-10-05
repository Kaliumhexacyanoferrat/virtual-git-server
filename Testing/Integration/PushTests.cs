using GenHTTP.Modules.Git.Tests.Infrastructure;
using GenHTTP.Testing;

namespace GenHTTP.Modules.Git.Tests.Integration;

[TestClass]
public sealed class PushTests
{

    [TestMethod]
    public async Task TestPushedCommitsCanBeCloned()
    {
        var repository = new InMemoryGitRepository();

        await repository.CommitAsync("main", GitTree.Create().Add("README.md", "# Hello\n").Build(), "Initial commit");

        await using var host = await TestHost.RunAsync(GitServer.Create().Repository(repository));

        using var git = new GitClient();

        await git.RunAsync(null, "clone", host.GetUrl("/"), "first");

        git.Write("first", "README.md", "# Hello World\n");
        git.Write("first", "docs/guide.md", "Read me\n");

        await git.CommitAllAsync("first", "Extend the readme");

        git.Write("first", "docs/guide.md", "Read me, please\n");

        await git.CommitAllAsync("first", "Be polite");

        await git.RunAsync("first", "push", "origin", "main");

        var head = (await git.RunAsync("first", "rev-parse", "HEAD")).Trim();

        Assert.AreEqual(head, repository.References["refs/heads/main"].ToString());

        await git.RunAsync(null, "clone", host.GetUrl("/"), "second");

        Assert.AreEqual("Read me, please\n", git.Read("second", "docs/guide.md"));
        Assert.AreEqual(head, (await git.RunAsync("second", "rev-parse", "HEAD")).Trim());

        await git.RunAsync("second", "fsck", "--strict");
    }

}
