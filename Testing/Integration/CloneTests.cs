using GenHTTP.Modules.Git.Tests.Fixtures;
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

    [TestMethod]
    [DataRow(0)]
    [DataRow(2)]
    public async Task TestCloneEmptyRepository(int protocol)
    {
        await using var host = await TestHost.RunAsync(GitServer.Create().Repository(new InMemoryGitRepository { Head = "trunk" }));

        using var git = new GitClient(protocol);

        var result = await git.TryRunAsync(null, "clone", host.GetUrl("/"), "clone");

        Assert.IsTrue(result.Success, result.ToString());
        StringAssert.Contains(result.Error, "empty repository");

        if (protocol == 2)
        {
            // the name of the default branch is transferred for unborn HEADs
            Assert.AreEqual("trunk", (await git.RunAsync("clone", "symbolic-ref", "--short", "HEAD")).Trim());
        }
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    public async Task TestCloneHistoryWithBranchesAndTags(int protocol)
    {
        var repository = await Repositories.WithHistoryAsync(5);

        var base3 = repository.References["refs/tags/v3"];

        repository.SetReference("refs/heads/feature", base3);

        await repository.CommitAsync("feature", GitTree.Create().Add("feature.txt", "new").Build(), "Add feature");

        await using var host = await TestHost.RunAsync(GitServer.Create().Repository(repository));

        using var git = new GitClient(protocol);

        await git.RunAsync(null, "clone", host.GetUrl("/"), "clone");

        var log = await git.RunAsync("clone", "log", "--format=%s");

        Assert.AreEqual("Version 5\nVersion 4\nVersion 3\nVersion 2\nVersion 1\n", log);

        var tags = await git.RunAsync("clone", "tag", "--list");

        Assert.AreEqual("v1\nv2\nv3\nv4\nv5\n", tags);

        var branches = await git.RunAsync("clone", "branch", "-r");

        StringAssert.Contains(branches, "origin/feature");

        Assert.AreEqual("Add feature\n", await git.RunAsync("clone", "log", "-1", "--format=%s", "origin/feature"));
        Assert.AreEqual(base3.ToString(), (await git.RunAsync("clone", "rev-parse", "origin/feature~1")).Trim());

        await git.RunAsync("clone", "fsck", "--strict");
    }

    [TestMethod]
    public async Task TestDefaultBranchIsCheckedOut()
    {
        var repository = new InMemoryGitRepository { Head = "trunk" };

        await repository.CommitAsync("trunk", GitTree.Create().Add("a.txt", "trunk").Build(), "On trunk");
        await repository.CommitAsync("main", GitTree.Create().Add("a.txt", "main").Build(), "On main");

        await using var host = await TestHost.RunAsync(GitServer.Create().Repository(repository));

        using var git = new GitClient();

        await git.RunAsync(null, "clone", host.GetUrl("/"), "clone");

        Assert.AreEqual("trunk", (await git.RunAsync("clone", "branch", "--show-current")).Trim());
        Assert.AreEqual("trunk", git.Read("clone", "a.txt"));
    }

    [TestMethod]
    public async Task TestFileKinds()
    {
        var random = new byte[2 * 1024 * 1024];
        new Random(3).NextBytes(random);

        var binary = new byte[] { 0, 1, 2, 0, 255, 254, 0 };

        var tree = GitTree.Create()
                          .Add("empty.txt", "")
                          .Add("binary.bin", binary)
                          .Add("large.bin", random)
                          .Add("run.sh", "#!/bin/sh\necho hi\n", GitFileMode.Executable)
                          .Add("link", "run.sh", GitFileMode.Symlink)
                          .Add("deep/nested/directory/structure/file.txt", "deep")
                          .Add("unicode/ümlaut ✓.txt", "unicode")
                          .Build();

        var repository = new InMemoryGitRepository();

        await repository.CommitAsync("main", tree, "All kinds of files");

        await using var host = await TestHost.RunAsync(GitServer.Create().Repository(repository));

        using var git = new GitClient();

        await git.RunAsync(null, "clone", host.GetUrl("/"), "clone");

        var root = git.PathOf("clone");

        Assert.AreEqual(0, new FileInfo(Path.Combine(root, "empty.txt")).Length);
        CollectionAssert.AreEqual(binary, File.ReadAllBytes(Path.Combine(root, "binary.bin")));
        CollectionAssert.AreEqual(random, File.ReadAllBytes(Path.Combine(root, "large.bin")));
        Assert.AreEqual("deep", git.Read("clone", "deep/nested/directory/structure/file.txt"));
        Assert.AreEqual("unicode", git.Read("clone", "unicode/ümlaut ✓.txt"));

        var modes = await git.RunAsync("clone", "ls-files", "-s");

        StringAssert.Contains(modes, "100755");
        StringAssert.Contains(modes, "120000");

        if (!OperatingSystem.IsWindows())
        {
            Assert.AreEqual("run.sh", new FileInfo(Path.Combine(root, "link")).LinkTarget);
        }

        await git.RunAsync("clone", "fsck", "--strict");
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(2)]
    public async Task TestListRemoteDoesNotReadFiles(int protocol)
    {
        var repository = new CountingRepository(await Repositories.WithHistoryAsync(3));

        await using var host = await TestHost.RunAsync(GitServer.Create().Repository(repository));

        using var git = new GitClient(protocol);

        var output = await git.RunAsync(null, "ls-remote", host.GetUrl("/"));

        StringAssert.Contains(output, "HEAD");
        StringAssert.Contains(output, "refs/heads/main");
        StringAssert.Contains(output, "refs/tags/v3");

        Assert.AreEqual(0, repository.TreeRequests);
    }

    [TestMethod]
    public async Task TestLazyFilesAreLoaded()
    {
        var loads = 0;

        var tree = GitTree.Create()
                          .Add("lazy.txt", () =>
                          {
                              loads++;
                              return new ValueTask<ReadOnlyMemory<byte>>("lazy content"u8.ToArray());
                          })
                          .Build();

        var repository = new InMemoryGitRepository();

        await repository.CommitAsync("main", tree, "Lazy");

        loads = 0;

        await using var host = await TestHost.RunAsync(GitServer.Create().Repository(repository));

        using var git = new GitClient();

        await git.RunAsync(null, "clone", host.GetUrl("/"), "clone");

        Assert.AreEqual("lazy content", git.Read("clone", "lazy.txt"));

        // read once to compute the id and kept in memory to send it
        Assert.AreEqual(1, loads);
    }

    [TestMethod]
    public async Task TestKnownBlobIdsAreUsed()
    {
        var loads = 0;

        var tree = GitTree.Create()
                          .Add("lazy.txt", () =>
                          {
                              loads++;
                              return new ValueTask<ReadOnlyMemory<byte>>("lazy content"u8.ToArray());
                          }, id: GitObjectId.ForBlob("lazy content"u8))
                          .Build();

        var repository = new InMemoryGitRepository();

        await repository.CommitAsync("main", tree, "Lazy");

        loads = 0;

        await using var host = await TestHost.RunAsync(GitServer.Create().Repository(repository));

        using var git = new GitClient();

        await git.RunAsync(null, "clone", host.GetUrl("/"), "clone");

        Assert.AreEqual("lazy content", git.Read("clone", "lazy.txt"));

        // only read to be sent
        Assert.AreEqual(1, loads);
    }

}
