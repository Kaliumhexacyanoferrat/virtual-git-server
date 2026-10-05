using GenHTTP.Modules.Git.Tests.Infrastructure;
using GenHTTP.Testing;

namespace GenHTTP.Modules.Git.Tests.Integration;

[TestClass]
public sealed class CloneTests
{

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    public async Task TestCloneSingleCommit(int protocol)
    {
        var repository = new InMemoryGitRepository();

        await repository.CommitAsync("main", GitTree.Create().Add("README.md", "# Hello\n").Add("src/Program.cs", "return 42;\n").Build(), "Initial commit");

        await using var host = await TestHost.RunAsync(GitServer.Create().Repository(repository));

        using var git = new GitClient(protocol);

        await git.RunAsync(null, "clone", host.GetUrl("/"), "clone");

        Assert.AreEqual("# Hello\n", git.Read("clone", "README.md"));
        Assert.AreEqual("return 42;\n", git.Read("clone", "src/Program.cs"));

        await git.RunAsync("clone", "fsck", "--strict");
    }

}
