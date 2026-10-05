using System.Text;

using GenHTTP.Modules.Git.Tests.Fixtures;
using GenHTTP.Modules.Git.Tests.Infrastructure;
using GenHTTP.Testing;

namespace GenHTTP.Modules.Git.Tests.Integration;

/// <summary>
/// Exercises a repository modelled after GenHTTP Lambda, where versions
/// are immutable and features are prepared on branches.
/// </summary>
[TestClass]
public sealed class VersionedRepositoryTests
{

    private static Dictionary<string, byte[]> Files(params (string Path, string Content)[] files) => files.ToDictionary(f => f.Path, f => Encoding.UTF8.GetBytes(f.Content));

    private static async Task<VersionedRepository> CreateAsync(int versions)
    {
        var repository = new VersionedRepository();

        for (var i = 1; i <= versions; i++)
        {
            await repository.SaveVersionAsync(Files(("lambda.cs", $"return {i};"), ("assets/app.js", "app")), $"Version {i}");
        }

        return repository;
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(2)]
    public async Task TestVersionsAreCommits(int protocol)
    {
        var repository = await CreateAsync(3);

        repository.CreateFeature("login", 2);

        await repository.UpdateFeatureAsync("login", Files(("lambda.cs", "return login;")), "Add login");

        await using var host = await TestHost.RunAsync(GitServer.Create().Repository(repository));

        using var git = new GitClient(protocol);

        await git.RunAsync(null, "clone", host.GetUrl("/"), "clone");

        Assert.AreEqual("Version 3\nVersion 2\nVersion 1\n", await git.RunAsync("clone", "log", "--format=%s"));
        Assert.AreEqual("v1\nv2\nv3\n", await git.RunAsync("clone", "tag"));

        Assert.AreEqual("return 3;", git.Read("clone", "lambda.cs"));

        await git.RunAsync("clone", "checkout", "-q", "login");

        Assert.AreEqual("return login;", git.Read("clone", "lambda.cs"));
        Assert.AreEqual(repository.GetVersion(2).Commit.Id.ToString(), (await git.RunAsync("clone", "rev-parse", "HEAD~1")).Trim());

        await git.RunAsync("clone", "fsck", "--strict");
    }

    [TestMethod]
    public async Task TestCommitsAreStable()
    {
        var repository = await CreateAsync(2);

        await using var host = await TestHost.RunAsync(GitServer.Create().Repository(repository));

        using var git = new GitClient();

        await git.RunAsync(null, "clone", host.GetUrl("/"), "first");
        await git.RunAsync(null, "clone", host.GetUrl("/"), "second");

        Assert.AreEqual(await git.RunAsync("first", "rev-parse", "HEAD"), await git.RunAsync("second", "rev-parse", "HEAD"));
    }

    [TestMethod]
    public async Task TestPushCreatesVersions()
    {
        var repository = await CreateAsync(1);

        await using var host = await TestHost.RunAsync(GitServer.Create().Repository(repository));

        using var git = new GitClient();

        await git.RunAsync(null, "clone", host.GetUrl("/"), "clone");

        git.Write("clone", "lambda.cs", "return 2;");
        await git.CommitAllAsync("clone", "Second version");

        git.Write("clone", "lambda.cs", "return 3;");
        await git.CommitAllAsync("clone", "Third version");

        var result = await git.TryRunAsync("clone", "push", "origin", "main");

        Assert.IsTrue(result.Success, result.ToString());

        StringAssert.Contains(result.Error, "remote: Created version 2");
        StringAssert.Contains(result.Error, "remote: Created version 3");

        Assert.HasCount(3, repository.Versions);
        Assert.AreEqual("Third version", repository.Versions[^1].Note);
        Assert.AreEqual("return 3;", Encoding.UTF8.GetString(repository.Versions[^1].Commit.Files["lambda.cs"]));

        // the commits of the client are kept as they are
        Assert.AreEqual((await git.RunAsync("clone", "rev-parse", "HEAD")).Trim(), repository.Versions[^1].Commit.Id.ToString());

        await git.RunAsync("clone", "fetch", "-q", "--tags");

        Assert.AreEqual(await git.RunAsync("clone", "rev-parse", "HEAD"), await git.RunAsync("clone", "rev-parse", "v3"));
    }

    [TestMethod]
    public async Task TestVersionsCannotBeRewritten()
    {
        var repository = await CreateAsync(2);

        await using var host = await TestHost.RunAsync(GitServer.Create().Repository(repository));

        using var git = new GitClient();

        await git.RunAsync(null, "clone", host.GetUrl("/"), "clone");

        await git.RunAsync("clone", "reset", "-q", "--hard", "HEAD~1");

        git.Write("clone", "lambda.cs", "return rewritten;");
        await git.CommitAllAsync("clone", "Rewrite");

        var forced = await git.TryRunAsync("clone", "push", "--force", "origin", "main");

        Assert.IsFalse(forced.Success);
        StringAssert.Contains(forced.Error, "versions cannot be changed");

        var deleted = await git.TryRunAsync("clone", "push", "origin", "--delete", "main");

        Assert.IsFalse(deleted.Success);

        var tag = await git.TryRunAsync("clone", "push", "origin", "HEAD:refs/tags/v99");

        Assert.IsFalse(tag.Success);
        StringAssert.Contains(tag.Error, "versions are tagged by the platform");

        Assert.HasCount(2, repository.Versions);
    }

    [TestMethod]
    public async Task TestMergeCommitsAreRejected()
    {
        var repository = await CreateAsync(1);

        await using var host = await TestHost.RunAsync(GitServer.Create().Repository(repository));

        using var git = new GitClient();

        await git.RunAsync(null, "clone", host.GetUrl("/"), "clone");

        await git.RunAsync("clone", "checkout", "-q", "-b", "side");

        git.Write("clone", "side.txt", "side");
        await git.CommitAllAsync("clone", "Side");

        await git.RunAsync("clone", "checkout", "-q", "main");

        git.Write("clone", "main.txt", "main");
        await git.CommitAllAsync("clone", "Main");

        await git.RunAsync("clone", "merge", "-q", "--no-edit", "side");

        var result = await git.TryRunAsync("clone", "push", "origin", "main");

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "merge commits are not supported");
    }

    [TestMethod]
    public async Task TestPushedBranchesBecomeFeatures()
    {
        var repository = await CreateAsync(2);

        await using var host = await TestHost.RunAsync(GitServer.Create().Repository(repository));

        using var git = new GitClient();

        await git.RunAsync(null, "clone", host.GetUrl("/"), "clone");

        await git.RunAsync("clone", "checkout", "-q", "-b", "dark-mode", "v1");

        git.Write("clone", "assets/app.css", "body { background: black; }");
        await git.CommitAllAsync("clone", "Dark mode");

        await git.RunAsync("clone", "push", "-q", "-u", "origin", "dark-mode");

        var feature = repository.Features["dark-mode"];

        Assert.AreEqual(1, feature.BaseVersion);
        Assert.HasCount(1, feature.Commits);

        // the feature is updated with another commit
        git.Write("clone", "assets/app.css", "body { background: #111; }");
        await git.CommitAllAsync("clone", "Softer");

        await git.RunAsync("clone", "push", "-q", "origin", "dark-mode");

        Assert.HasCount(2, feature.Commits);

        // move the base by rebasing onto the newest version
        await git.RunAsync("clone", "rebase", "-q", "v2");
        await git.RunAsync("clone", "push", "-q", "--force", "origin", "dark-mode");

        Assert.AreEqual(2, feature.BaseVersion);

        // changed via the platform
        await repository.UpdateFeatureAsync("dark-mode", Files(("lambda.cs", "return 2;"), ("assets/app.js", "app"), ("assets/app.css", "body { background: #222; }")), "Edited online");

        await git.RunAsync("clone", "pull", "-q", "--ff-only");

        Assert.AreEqual("body { background: #222; }", git.Read("clone", "assets/app.css"));

        // merged via the platform
        await repository.MergeFeatureAsync("dark-mode", "Dark mode");

        await git.RunAsync("clone", "checkout", "-q", "main");
        await git.RunAsync("clone", "pull", "-q", "--ff-only", "--prune");

        Assert.AreEqual("body { background: #222; }", git.Read("clone", "assets/app.css"));
        Assert.DoesNotContain("dark-mode", await git.RunAsync("clone", "branch", "-r"));

        await git.RunAsync("clone", "fsck", "--strict");
    }

    [TestMethod]
    public async Task TestFeaturesCanBeDeleted()
    {
        var repository = await CreateAsync(1);

        repository.CreateFeature("obsolete", 1);

        await using var host = await TestHost.RunAsync(GitServer.Create().Repository(repository));

        using var git = new GitClient();

        await git.RunAsync(null, "clone", host.GetUrl("/"), "clone");

        await git.RunAsync("clone", "push", "-q", "origin", "--delete", "obsolete");

        Assert.IsFalse(repository.Features.ContainsKey("obsolete"));
    }

    [TestMethod]
    public async Task TestOnlyRegularFilesAreAccepted()
    {
        var repository = await CreateAsync(1);

        await using var host = await TestHost.RunAsync(GitServer.Create().Repository(repository));

        using var git = new GitClient();

        await git.RunAsync(null, "clone", host.GetUrl("/"), "clone");

        git.Write("clone", "run.sh", "#!/bin/sh");

        await git.RunAsync("clone", "add", "-A");
        await git.RunAsync("clone", "update-index", "--chmod=+x", "run.sh");
        await git.RunAsync("clone", "commit", "-q", "-m", "Executable");

        var result = await git.TryRunAsync("clone", "push", "origin", "main");

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "only regular files are supported");
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(2)]
    public async Task TestRetention(int protocol)
    {
        var repository = await CreateAsync(5);

        repository.Retention = 3;

        await repository.SaveVersionAsync(Files(("lambda.cs", "return 6;")), "Version 6");

        await using var host = await TestHost.RunAsync(GitServer.Create().Repository(repository));

        using var git = new GitClient(protocol);

        await git.RunAsync(null, "clone", host.GetUrl("/"), "clone");

        Assert.AreEqual("Version 6\nVersion 5\nVersion 4\n", await git.RunAsync("clone", "log", "--format=%s"));
        Assert.AreEqual("v4\nv5\nv6\n", await git.RunAsync("clone", "tag"));

        git.Write("clone", "lambda.cs", "return 7;");
        await git.CommitAllAsync("clone", "Version 7");

        await git.RunAsync("clone", "push", "-q", "origin", "main");

        Assert.AreEqual(7, repository.Versions[^1].Number);
        Assert.HasCount(3, repository.Versions);

        await git.RunAsync("clone", "fsck");
    }

}
