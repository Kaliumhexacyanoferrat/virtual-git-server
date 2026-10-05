using System.Diagnostics;
using System.Net;
using System.Text;

using GenHTTP.Modules.Git.Tests.Fixtures;
using GenHTTP.Modules.Git.Tests.Infrastructure;
using GenHTTP.Modules.Layouting;
using GenHTTP.Testing;

namespace GenHTTP.Modules.Git.Tests.Integration;

/// <summary>
/// Verifies that the server withstands requests crafted to exhaust its
/// resources and handles unusual but valid input.
/// </summary>
[TestClass]
public sealed class HardeningTests
{

    #region Supporting data structures

    private sealed class InterceptingRepository(InMemoryGitRepository inner, Func<GitPush, ValueTask> interceptor) : IWritableGitRepository
    {

        public ValueTask<GitReferences> GetReferencesAsync() => inner.GetReferencesAsync();

        public ValueTask<GitCommit?> GetCommitAsync(GitObjectId id) => inner.GetCommitAsync(id);

        public ValueTask<GitTree> GetTreeAsync(GitCommit commit) => inner.GetTreeAsync(commit);

        public async ValueTask PushAsync(GitPush push)
        {
            await interceptor(push);
            await inner.PushAsync(push);
        }

    }

    #endregion

    #region Pushes

    [TestMethod]
    public async Task TestTreesExpandingToManyFilesAreRejected()
    {
        var repository = await Repositories.WithHistoryAsync(1);

        await using var host = await TestHost.RunAsync(GitServer.Create().Repository(repository));

        using var git = new GitClient();

        await git.RunAsync(null, "clone", host.GetUrl("/"), "clone");

        File.WriteAllText(git.PathOf("blob"), "x");

        var blob = (await git.RunAsync("clone", "hash-object", "-w", git.PathOf("blob"))).Trim();

        // 50 x 50 x 50 = 125,000 files, from just three tree objects
        var level1 = (await git.RunWithInputAsync("clone", Lines(i => $"100644 blob {blob}\tf{i}"), "mktree")).Trim();
        var level2 = (await git.RunWithInputAsync("clone", Lines(i => $"040000 tree {level1}\td{i}"), "mktree")).Trim();
        var level3 = (await git.RunWithInputAsync("clone", Lines(i => $"040000 tree {level2}\td{i}"), "mktree")).Trim();

        var commit = (await git.RunAsync("clone", "commit-tree", level3, "-p", "HEAD", "-m", "Bomb")).Trim();

        await git.RunAsync("clone", "update-ref", "refs/heads/main", commit);

        var watch = Stopwatch.StartNew();

        var result = await git.TryRunAsync("clone", "push", "origin", "main");

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "more than 100000 files");

        Assert.IsLessThan(10, watch.Elapsed.TotalSeconds);
    }

    [TestMethod]
    public async Task TestFileLimitCanBeConfigured()
    {
        var repository = await Repositories.WithHistoryAsync(1);

        await using var host = await TestHost.RunAsync(GitServer.Create().Repository(repository).MaximumFilesPerCommit(3));

        using var git = new GitClient();

        await git.RunAsync(null, "clone", host.GetUrl("/"), "clone");

        git.Write("clone", "another.txt", "a");

        await git.CommitAllAsync("clone", "Fourth file");

        var result = await git.TryRunAsync("clone", "push", "origin", "main");

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "more than 3 files");
    }

    [TestMethod]
    public async Task TestEmptyDirectoriesAreRejected()
    {
        var repository = await Repositories.WithHistoryAsync(1);

        await using var host = await TestHost.RunAsync(GitServer.Create().Repository(repository));

        using var git = new GitClient();

        await git.RunAsync(null, "clone", host.GetUrl("/"), "clone");

        File.WriteAllText(git.PathOf("blob"), "x");

        var blob = (await git.RunAsync("clone", "hash-object", "-w", git.PathOf("blob"))).Trim();
        var empty = (await git.RunWithInputAsync("clone", "", "mktree")).Trim();

        var root = (await git.RunWithInputAsync("clone", $"040000 tree {empty}\tempty\n100644 blob {blob}\tfile.txt\n", "mktree")).Trim();

        var commit = (await git.RunAsync("clone", "commit-tree", root, "-p", "HEAD", "-m", "Empty directory")).Trim();

        await git.RunAsync("clone", "update-ref", "refs/heads/main", commit);

        var result = await git.TryRunAsync("clone", "push", "origin", "main");

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "empty directories are not supported");
    }

    [TestMethod]
    public async Task TestDangerousNamesAreRejected()
    {
        var repository = await Repositories.WithHistoryAsync(1);

        await using var host = await TestHost.RunAsync(GitServer.Create().Repository(repository));

        using var git = new GitClient();

        await git.RunAsync(null, "clone", host.GetUrl("/"), "clone");

        File.WriteAllText(git.PathOf("blob"), "x");

        var blob = (await git.RunAsync("clone", "hash-object", "-w", git.PathOf("blob"))).Trim();

        foreach (var name in new[] { ".GIT", "git~1", ".git::$INDEX_ALLOCATION", "a:b" })
        {
            var root = (await git.RunWithInputAsync("clone", $"100644 blob {blob}\t{name}\n", "mktree")).Trim();

            var commit = (await git.RunAsync("clone", "commit-tree", root, "-p", "HEAD", "-m", "Dangerous")).Trim();

            await git.RunAsync("clone", "update-ref", "refs/heads/dangerous", commit);

            var result = await git.TryRunAsync("clone", "push", "origin", "dangerous");

            Assert.IsFalse(result.Success, name);
            StringAssert.Contains(result.Error, "invalid file name");
        }
    }

    [TestMethod]
    public async Task TestCommitsWithoutNameAreAccepted()
    {
        var repository = await Repositories.WithHistoryAsync(1);

        await using var host = await TestHost.RunAsync(GitServer.Create().Repository(repository));

        using var git = new GitClient();

        await git.RunAsync(null, "clone", host.GetUrl("/"), "clone");

        var tree = (await git.RunAsync("clone", "rev-parse", "HEAD^{tree}")).Trim();
        var parent = (await git.RunAsync("clone", "rev-parse", "HEAD")).Trim();

        File.WriteAllText(git.PathOf("commit.txt"), $"tree {tree}\nparent {parent}\nauthor  <a@example.com> 1700000000 +0000\ncommitter  <a@example.com> 1700000000 +0000\n\nAnonymous\n");

        var id = (await git.RunAsync("clone", "hash-object", "-t", "commit", "-w", git.PathOf("commit.txt"))).Trim();

        await git.RunAsync("clone", "update-ref", "refs/heads/main", id);
        await git.RunAsync("clone", "push", "-q", "origin", "main");

        Assert.AreEqual(id, repository.References["refs/heads/main"].ToString());
    }

    [TestMethod]
    public async Task TestConcurrentAndLateMessages()
    {
        var inner = await Repositories.WithHistoryAsync(1);

        var repository = new InterceptingRepository(inner, async push =>
        {
            await Task.WhenAll(Enumerable.Range(0, 20).Select(i => Task.Run(async () => await push.MessageAsync($"Message {i}"))));

            // a message sent after the push has been processed must not corrupt the response
            _ = Task.Run(async () =>
            {
                await Task.Delay(100);
                await push.MessageAsync("Too late");
            });
        });

        await using var host = await TestHost.RunAsync(GitServer.Create().Repository(repository));

        using var git = new GitClient();

        await git.RunAsync(null, "clone", host.GetUrl("/"), "clone");

        git.Write("clone", "a.txt", "a");
        await git.CommitAllAsync("clone", "Change");

        var result = await git.TryRunAsync("clone", "push", "origin", "main");

        Assert.IsTrue(result.Success, result.ToString());

        // git pads remote messages with spaces to overwrite progress output
        var lines = result.Error.Split('\n').Select(l => l.TrimEnd()).ToHashSet();

        for (var i = 0; i < 20; i++)
        {
            Assert.Contains($"remote: Message {i}", lines);
        }

        await Task.Delay(200);
    }

    #endregion

    #region Fetches

    [TestMethod]
    [DataRow(0)]
    [DataRow(2)]
    public async Task TestShallowClonesWithManyBranchesFetchLittle(int protocol)
    {
        var repository = await Repositories.WithHistoryAsync(30, tags: false);

        for (var i = 10; i < 30; i++)
        {
            repository.SetReference($"refs/heads/branch{i}", repository.GetRevision(repository.References["refs/heads/main"])!.Commit.Id);

            await repository.CommitAsync($"branch{i}", GitTree.Create().Add("branch.txt", $"{i}").Build(), $"Branch {i}");
        }

        await using var host = await TestHost.RunAsync(GitServer.Create().Repository(repository));

        using var git = new GitClient(protocol);

        await git.RunAsync(null, "clone", "--depth", "1", "--no-single-branch", host.GetUrl("/"), "clone");

        var before = await CountObjectsAsync(git);

        await repository.CommitAsync("main", GitTree.Create().Add("new.txt", "new").Build(), "New");

        await git.RunAsync("clone", "fetch", "-q");

        var after = await CountObjectsAsync(git);

        // a commit, its tree and a single blob - not the history behind the shallow commits
        Assert.IsLessThanOrEqualTo(3, after - before);

        await git.RunAsync("clone", "fsck");
    }

    [TestMethod]
    public async Task TestMalformedWantIsReported()
    {
        await using var host = await TestHost.RunAsync(GitServer.Create().Repository(await Repositories.WithHistoryAsync(1)));

        var request = host.GetRequest("/git-upload-pack", HttpMethod.Post);

        request.Content = new ByteArrayContent("0009want 00000009done\n"u8.ToArray());

        using var response = await host.GetResponseAsync(request);

        StringAssert.Contains(await response.Content.ReadAsStringAsync(), "ERR invalid line");
    }

    [TestMethod]
    public async Task TestManyPrefixesAreIgnored()
    {
        await using var host = await TestHost.RunAsync(GitServer.Create().Repository(await Repositories.WithHistoryAsync(2)));

        var body = new StringBuilder();

        body.Append(Line("command=ls-refs\n")).Append("0001");

        for (var i = 0; i < 100; i++)
        {
            body.Append(Line($"ref-prefix refs/nothing/{i}\n"));
        }

        body.Append("0000");

        var request = host.GetRequest("/git-upload-pack", HttpMethod.Post);

        request.Headers.Add("Git-Protocol", "version=2");
        request.Content = new ByteArrayContent(Encoding.ASCII.GetBytes(body.ToString()));

        using var response = await host.GetResponseAsync(request);

        var content = await response.Content.ReadAsStringAsync();

        StringAssert.Contains(content, "refs/heads/main");
        StringAssert.Contains(content, "refs/tags/v2");
    }

    [TestMethod]
    public async Task TestTooManyHavesAreRejected()
    {
        await using var host = await TestHost.RunAsync(GitServer.Create().Repository(await Repositories.WithHistoryAsync(1)));

        var body = new StringBuilder();

        body.Append(Line("command=fetch\n")).Append("0001");

        for (var i = 0; i < 100_001; i++)
        {
            body.Append(Line($"have {i:x40}\n"));
        }

        body.Append("0000");

        var request = host.GetRequest("/git-upload-pack", HttpMethod.Post);

        request.Headers.Add("Git-Protocol", "version=2");
        request.Content = new ByteArrayContent(Encoding.ASCII.GetBytes(body.ToString()));

        using var response = await host.GetResponseAsync(request);

        StringAssert.Contains(await response.Content.ReadAsStringAsync(), "ERR too many haves");
    }

    #endregion

    #region Routing

    [TestMethod]
    public async Task TestEncodedSeparatorsInRepositoryNames()
    {
        var names = new List<string>();

        var git = GitServer.Create().Repositories((_, name) =>
        {
            names.Add(name);
            return new ValueTask<IGitRepository?>((IGitRepository?)null);
        });

        await using var host = await TestHost.RunAsync(Layout.Create().Add("repos", git));

        foreach (var path in new[] { "/repos/%2E%2E/info/refs?service=git-upload-pack", "/repos/a%2Fb/info/refs?service=git-upload-pack", "/repos/a%5Cb/info/refs?service=git-upload-pack" })
        {
            using var response = await host.GetResponseAsync(path);

            Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode, path);
        }

        Assert.IsEmpty(names);
    }

    #endregion

    #region Helpers

    private static string Lines(Func<int, string> line) => string.Join("", Enumerable.Range(0, 50).Select(i => line(i) + "\n"));

    private static string Line(string text) => (text.Length + 4).ToString("x4") + text;

    private static async Task<int> CountObjectsAsync(GitClient git)
    {
        var output = await git.RunAsync("clone", "count-objects", "-v");

        var values = output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                           .Select(l => l.Split(": "))
                           .ToDictionary(p => p[0], p => p[1]);

        return int.Parse(values["count"]) + int.Parse(values["in-pack"]);
    }

    #endregion

}
