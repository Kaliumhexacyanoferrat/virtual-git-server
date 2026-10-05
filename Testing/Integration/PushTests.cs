using GenHTTP.Modules.Git.Tests.Fixtures;
using GenHTTP.Modules.Git.Tests.Infrastructure;
using GenHTTP.Testing;

namespace GenHTTP.Modules.Git.Tests.Integration;

[TestClass]
public sealed class PushTests
{

    #region Supporting data structures

    /// <summary>
    /// Delegates to an in-memory repository, allowing tests to intercept pushes.
    /// </summary>
    private sealed class InterceptingRepository(InMemoryGitRepository inner, Func<GitPush, ValueTask<bool>> interceptor) : IWritableGitRepository
    {

        public ValueTask<GitReferences> GetReferencesAsync() => inner.GetReferencesAsync();

        public ValueTask<GitCommit?> GetCommitAsync(GitObjectId id) => inner.GetCommitAsync(id);

        public ValueTask<GitTree> GetTreeAsync(GitCommit commit) => inner.GetTreeAsync(commit);

        public async ValueTask PushAsync(GitPush push)
        {
            if (await interceptor(push))
            {
                await inner.PushAsync(push);
            }
        }

    }

    #endregion

    #region Tests

    [TestMethod]
    public async Task TestPushedCommitsCanBeCloned()
    {
        var repository = await Repositories.WithHistoryAsync(1);

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

    [TestMethod]
    public async Task TestPushToEmptyRepository()
    {
        var repository = new InMemoryGitRepository();

        await using var host = await TestHost.RunAsync(GitServer.Create().Repository(repository));

        using var git = new GitClient();

        await git.RunAsync(null, "init", "-q", "local");

        git.Write("local", "a.txt", "a");

        await git.CommitAllAsync("local", "First");

        await git.RunAsync("local", "push", host.GetUrl("/"), "main");

        Assert.IsTrue(repository.References.ContainsKey("refs/heads/main"));

        await git.RunAsync(null, "clone", host.GetUrl("/"), "clone");

        Assert.AreEqual("a", git.Read("clone", "a.txt"));
    }

    [TestMethod]
    public async Task TestRevisionsAreReported()
    {
        var updates = new List<GitReferenceUpdate>();

        var inner = await Repositories.WithHistoryAsync(1);

        var repository = new InterceptingRepository(inner, push =>
        {
            updates.AddRange(push.Updates);
            return new(true);
        });

        await using var host = await TestHost.RunAsync(GitServer.Create().Repository(repository));

        using var git = new GitClient();

        await git.RunAsync(null, "clone", host.GetUrl("/"), "clone");

        git.Write("clone", "a.txt", "a");
        await git.CommitAllAsync("clone", "First");

        git.Write("clone", "b.txt", "b");
        await git.CommitAllAsync("clone", "Second");

        await git.RunAsync("clone", "push", "origin", "main");

        var update = updates.Single();

        Assert.AreEqual("refs/heads/main", update.Name);
        Assert.AreEqual("main", update.ShortName);
        Assert.IsTrue(update.IsBranch);
        Assert.IsTrue(update.IsFastForward);
        Assert.IsFalse(update.IsCreate);
        Assert.IsFalse(update.IsDelete);
        Assert.AreEqual(GitUpdateStatus.Accepted, update.Status);

        Assert.AreEqual(2, update.Revisions.Count);

        Assert.AreEqual("First", update.Revisions[0].Commit.Subject);
        Assert.AreEqual("Second", update.Revisions[1].Commit.Subject);

        Assert.AreEqual("Test User", update.Revisions[1].Commit.Author.Name);

        // files the client did not need to send are taken from the repository
        var files = update.Revisions[1].Tree;

        Assert.AreEqual("# Version 1\n", await files.GetFile("README.md")!.ReadStringAsync());
        Assert.AreEqual("b", await files.GetFile("b.txt")!.ReadStringAsync());

        Assert.AreEqual(update.NewId, update.Commit!.Id);
    }

    [TestMethod]
    public async Task TestBranchesCanBeCreatedAndDeleted()
    {
        var repository = await Repositories.WithHistoryAsync(2);

        await using var host = await TestHost.RunAsync(GitServer.Create().Repository(repository));

        using var git = new GitClient();

        await git.RunAsync(null, "clone", host.GetUrl("/"), "clone");

        await git.RunAsync("clone", "checkout", "-q", "-b", "feature/login");

        git.Write("clone", "login.cs", "// login");
        await git.CommitAllAsync("clone", "Add login");

        await git.RunAsync("clone", "push", "-q", "origin", "feature/login");

        Assert.IsTrue(repository.References.ContainsKey("refs/heads/feature/login"));

        // an existing commit can be pushed under a new name as well
        await git.RunAsync("clone", "push", "-q", "origin", "v1:refs/heads/from-tag");

        Assert.AreEqual(repository.References["refs/tags/v1"], repository.References["refs/heads/from-tag"]);

        await git.RunAsync("clone", "push", "-q", "origin", "--delete", "feature/login");

        Assert.IsFalse(repository.References.ContainsKey("refs/heads/feature/login"));
    }

    [TestMethod]
    public async Task TestPushWithDeltas()
    {
        var repository = await Repositories.WithHistoryAsync(1);

        await using var host = await TestHost.RunAsync(GitServer.Create().Repository(repository));

        using var git = new GitClient();

        await git.RunAsync(null, "clone", host.GetUrl("/"), "clone");

        var lines = Enumerable.Range(0, 5000).Select(i => $"This is line number {i} of a rather large file").ToList();

        for (var i = 0; i < 5; i++)
        {
            lines[i * 100] = $"Changed in commit {i}";

            git.Write("clone", "large.txt", string.Join('\n', lines));
            git.Write("clone", $"copy{i}.txt", string.Join('\n', lines));

            await git.CommitAllAsync("clone", $"Commit {i}");
        }

        await git.RunAsync("clone", "push", "-q", "origin", "main");

        await git.RunAsync(null, "clone", host.GetUrl("/"), "verify");

        Assert.AreEqual(string.Join('\n', lines), git.Read("verify", "large.txt"));

        await git.RunAsync("verify", "fsck", "--strict");
    }

    [TestMethod]
    public async Task TestLargePush()
    {
        var repository = await Repositories.WithHistoryAsync(1);

        await using var host = await TestHost.RunAsync(GitServer.Create().Repository(repository));

        using var git = new GitClient();

        await git.RunAsync(null, "clone", host.GetUrl("/"), "clone");

        // exceeds the post buffer of git, so it probes and sends the data chunked
        var data = new byte[5 * 1024 * 1024];
        new Random(5).NextBytes(data);

        git.Write("clone", "large.bin", data);

        await git.CommitAllAsync("clone", "Large file");

        await git.RunAsync("clone", "push", "-q", "origin", "main");

        await git.RunAsync(null, "clone", host.GetUrl("/"), "verify");

        CollectionAssert.AreEqual(data, File.ReadAllBytes(Path.Combine(git.PathOf("verify"), "large.bin")));
    }

    [TestMethod]
    public async Task TestPushSizeIsLimited()
    {
        var repository = await Repositories.WithHistoryAsync(1);

        await using var host = await TestHost.RunAsync(GitServer.Create().Repository(repository).MaximumPushSize(100_000));

        using var git = new GitClient();

        await git.RunAsync(null, "clone", host.GetUrl("/"), "clone");

        var data = new byte[500_000];
        new Random(5).NextBytes(data);

        git.Write("clone", "large.bin", data);

        await git.CommitAllAsync("clone", "Large file");

        var result = await git.TryRunAsync("clone", "push", "origin", "main");

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "413");
    }

    [TestMethod]
    public async Task TestFileModesArePushed()
    {
        var repository = await Repositories.WithHistoryAsync(1);

        await using var host = await TestHost.RunAsync(GitServer.Create().Repository(repository));

        using var git = new GitClient();

        await git.RunAsync(null, "clone", host.GetUrl("/"), "clone");

        git.Write("clone", "run.sh", "#!/bin/sh\n");
        git.Write("clone", "empty.txt", "");

        await git.RunAsync("clone", "add", "-A");
        await git.RunAsync("clone", "update-index", "--chmod=+x", "run.sh");

        // a symbolic link stores the path it points to as its content
        File.WriteAllText(git.PathOf("link-target"), "run.sh");

        var link = (await git.RunAsync("clone", "hash-object", "-w", git.PathOf("link-target"))).Trim();

        await git.RunAsync("clone", "update-index", "--add", "--cacheinfo", $"120000,{link},link");
        await git.RunAsync("clone", "commit", "-q", "-m", "Modes");

        await git.RunAsync("clone", "push", "-q", "origin", "main");

        var revision = repository.GetRevision(repository.References["refs/heads/main"])!;

        Assert.AreEqual(GitFileMode.Executable, revision.Tree.GetFile("run.sh")!.Mode);
        Assert.AreEqual(GitFileMode.Symlink, revision.Tree.GetFile("link")!.Mode);
        Assert.AreEqual(0, (await revision.Tree.GetFile("empty.txt")!.ReadAsync()).Length);

        await git.RunAsync(null, "clone", host.GetUrl("/"), "verify");

        await git.RunAsync("verify", "fsck", "--strict");

        Assert.AreEqual(await git.RunAsync("clone", "ls-files", "-s"), await git.RunAsync("verify", "ls-files", "-s"));
    }

    [TestMethod]
    public async Task TestRejectionsAreReported()
    {
        var inner = await Repositories.WithHistoryAsync(1);

        var repository = new InterceptingRepository(inner, async push =>
        {
            await push.MessageAsync("Checking your changes ...");

            foreach (var update in push.Updates)
            {
                update.Reject("we do not like this change");
            }

            return false;
        });

        await using var host = await TestHost.RunAsync(GitServer.Create().Repository(repository));

        using var git = new GitClient();

        await git.RunAsync(null, "clone", host.GetUrl("/"), "clone");

        git.Write("clone", "a.txt", "a");
        await git.CommitAllAsync("clone", "Change");

        var result = await git.TryRunAsync("clone", "push", "origin", "main");

        Assert.IsFalse(result.Success);

        StringAssert.Contains(result.Error, "remote: Checking your changes ...");
        StringAssert.Contains(result.Error, "[remote rejected] main -> main (we do not like this change)");
    }

    [TestMethod]
    public async Task TestUndecidedUpdatesAreRejected()
    {
        var inner = await Repositories.WithHistoryAsync(1);

        var repository = new InterceptingRepository(inner, _ => new(false));

        await using var host = await TestHost.RunAsync(GitServer.Create().Repository(repository));

        using var git = new GitClient();

        await git.RunAsync(null, "clone", host.GetUrl("/"), "clone");

        git.Write("clone", "a.txt", "a");
        await git.CommitAllAsync("clone", "Change");

        var result = await git.TryRunAsync("clone", "push", "origin", "main");

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "[remote rejected]");
    }

    [TestMethod]
    public async Task TestExceptionsAreReported()
    {
        var inner = await Repositories.WithHistoryAsync(1);

        var repository = new InterceptingRepository(inner, _ => throw new InvalidOperationException("Something secret went wrong"));

        await using var host = await TestHost.RunAsync(GitServer.Create().Repository(repository));

        using var git = new GitClient();

        await git.RunAsync(null, "clone", host.GetUrl("/"), "clone");

        git.Write("clone", "a.txt", "a");
        await git.CommitAllAsync("clone", "Change");

        var result = await git.TryRunAsync("clone", "push", "origin", "main");

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "(internal server error)");
        Assert.DoesNotContain("secret", result.Error);
    }

    [TestMethod]
    public async Task TestPushOptions()
    {
        var options = new List<string>();

        var inner = await Repositories.WithHistoryAsync(1);

        var repository = new InterceptingRepository(inner, push =>
        {
            options.AddRange(push.Options);
            return new(true);
        });

        await using var host = await TestHost.RunAsync(GitServer.Create().Repository(repository));

        using var git = new GitClient();

        await git.RunAsync(null, "clone", host.GetUrl("/"), "clone");

        git.Write("clone", "a.txt", "a");
        await git.CommitAllAsync("clone", "Change");

        await git.RunAsync("clone", "push", "-q", "-o", "deploy", "-o", "message=hello world", "origin", "main");

        CollectionAssert.AreEqual(new[] { "deploy", "message=hello world" }, options);
    }

    [TestMethod]
    public async Task TestReadOnlyRepository()
    {
        var repository = new CountingRepository(await Repositories.WithHistoryAsync(1));

        await using var host = await TestHost.RunAsync(GitServer.Create().Repository(repository));

        using var git = new GitClient();

        await git.RunAsync(null, "clone", host.GetUrl("/"), "clone");

        git.Write("clone", "a.txt", "a");
        await git.CommitAllAsync("clone", "Change");

        var result = await git.TryRunAsync("clone", "push", "origin", "main");

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "does not accept pushes");
    }

    [TestMethod]
    public async Task TestNonFastForwardCanBeDenied()
    {
        var repository = new InMemoryGitRepository { DenyNonFastForwards = true };

        await repository.CommitAsync("main", GitTree.Create().Add("a.txt", "1").Build(), "One");
        await repository.CommitAsync("main", GitTree.Create().Add("a.txt", "2").Build(), "Two");

        await using var host = await TestHost.RunAsync(GitServer.Create().Repository(repository));

        using var git = new GitClient();

        await git.RunAsync(null, "clone", host.GetUrl("/"), "clone");

        await git.RunAsync("clone", "reset", "-q", "--hard", "HEAD~1");

        git.Write("clone", "a.txt", "rewritten");
        await git.CommitAllAsync("clone", "Rewrite");

        var result = await git.TryRunAsync("clone", "push", "--force", "origin", "main");

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "non-fast-forward updates are not allowed");
    }

    [TestMethod]
    public async Task TestForcePush()
    {
        var updates = new List<GitReferenceUpdate>();

        var inner = new InMemoryGitRepository();

        await inner.CommitAsync("main", GitTree.Create().Add("a.txt", "1").Build(), "One");
        await inner.CommitAsync("main", GitTree.Create().Add("a.txt", "2").Build(), "Two");

        var repository = new InterceptingRepository(inner, push =>
        {
            updates.AddRange(push.Updates);
            return new(true);
        });

        await using var host = await TestHost.RunAsync(GitServer.Create().Repository(repository));

        using var git = new GitClient();

        await git.RunAsync(null, "clone", host.GetUrl("/"), "clone");

        await git.RunAsync("clone", "reset", "-q", "--hard", "HEAD~1");

        git.Write("clone", "a.txt", "rewritten");
        await git.CommitAllAsync("clone", "Rewrite");

        await git.RunAsync("clone", "push", "-q", "--force", "origin", "main");

        var update = updates.Single();

        Assert.IsFalse(update.IsFastForward);
        Assert.AreEqual("Rewrite", update.Revisions.Single().Commit.Subject);
    }

    [TestMethod]
    public async Task TestStaleUpdatesAreRejected()
    {
        var repository = await Repositories.WithHistoryAsync(1);

        await using var host = await TestHost.RunAsync(GitServer.Create().Repository(repository));

        using var git = new GitClient();

        await git.RunAsync(null, "clone", host.GetUrl("/"), "clone");

        git.Write("clone", "a.txt", "a");
        await git.CommitAllAsync("clone", "Change");

        // someone else changes the branch on the server
        await repository.CommitAsync("main", GitTree.Create().Add("b.txt", "b").Build(), "Concurrent change");

        var result = await git.TryRunAsync("clone", "push", "origin", "main");

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "fetch first");
    }

    [TestMethod]
    public async Task TestAnnotatedTagsAreRejected()
    {
        var repository = await Repositories.WithHistoryAsync(1);

        await using var host = await TestHost.RunAsync(GitServer.Create().Repository(repository));

        using var git = new GitClient();

        await git.RunAsync(null, "clone", host.GetUrl("/"), "clone");

        await git.RunAsync("clone", "tag", "-a", "release", "-m", "Release");

        var annotated = await git.TryRunAsync("clone", "push", "origin", "release");

        Assert.IsFalse(annotated.Success);
        StringAssert.Contains(annotated.Error, "annotated tags are not supported");

        await git.RunAsync("clone", "tag", "lightweight");
        await git.RunAsync("clone", "push", "-q", "origin", "lightweight");

        Assert.IsTrue(repository.References.ContainsKey("refs/tags/lightweight"));
    }

    [TestMethod]
    public async Task TestSubmodulesAreRejected()
    {
        var repository = await Repositories.WithHistoryAsync(1);

        await using var host = await TestHost.RunAsync(GitServer.Create().Repository(repository));

        using var git = new GitClient();

        await git.RunAsync(null, "clone", host.GetUrl("/"), "clone");

        await git.RunAsync("clone", "update-index", "--add", "--cacheinfo", $"160000,{repository.References["refs/heads/main"]},module");
        await git.RunAsync("clone", "commit", "-q", "-m", "Add submodule");

        var result = await git.TryRunAsync("clone", "push", "origin", "main");

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "submodules are not supported");
    }

    [TestMethod]
    public async Task TestPushFromShallowClone()
    {
        var repository = await Repositories.WithHistoryAsync(5);

        await using var host = await TestHost.RunAsync(GitServer.Create().Repository(repository));

        using var git = new GitClient();

        await git.RunAsync(null, "clone", "--depth", "1", host.GetUrl("/"), "clone");

        git.Write("clone", "a.txt", "a");
        await git.CommitAllAsync("clone", "Change");

        await git.RunAsync("clone", "push", "-q", "origin", "main");

        Assert.AreEqual((await git.RunAsync("clone", "rev-parse", "HEAD")).Trim(), repository.References["refs/heads/main"].ToString());
    }

    [TestMethod]
    public async Task TestMultipleReferencesAtOnce()
    {
        var repository = await Repositories.WithHistoryAsync(1);

        await using var host = await TestHost.RunAsync(GitServer.Create().Repository(repository));

        using var git = new GitClient();

        await git.RunAsync(null, "clone", host.GetUrl("/"), "clone");

        git.Write("clone", "a.txt", "a");
        await git.CommitAllAsync("clone", "On main");

        await git.RunAsync("clone", "branch", "other");
        await git.RunAsync("clone", "tag", "t1");

        await git.RunAsync("clone", "push", "-q", "origin", "main", "other", "t1");

        var head = (await git.RunAsync("clone", "rev-parse", "HEAD")).Trim();

        Assert.AreEqual(head, repository.References["refs/heads/main"].ToString());
        Assert.AreEqual(head, repository.References["refs/heads/other"].ToString());
        Assert.AreEqual(head, repository.References["refs/tags/t1"].ToString());
    }

    #endregion

}
