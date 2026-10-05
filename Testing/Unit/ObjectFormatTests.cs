using System.Text;

using GenHTTP.Modules.Git.Tests.Infrastructure;

namespace GenHTTP.Modules.Git.Tests.Unit;

/// <summary>
/// Verifies that objects created by the library are byte-for-byte
/// identical to the ones git creates for the same content.
/// </summary>
[TestClass]
public sealed class ObjectFormatTests
{

    [TestMethod]
    public async Task TestBlobIdMatchesGit()
    {
        using var git = new GitClient();

        await git.RunAsync(null, "init", "-q", "repo");

        foreach (var content in new[] { "", "Hello World\n", "Ünïcödé ✓", new string('x', 100_000) })
        {
            git.Write("repo", "file", content);

            var expected = (await git.RunAsync("repo", "hash-object", "file")).Trim();

            Assert.AreEqual(expected, GitObjectId.ForBlob(Encoding.UTF8.GetBytes(content)).ToString());
        }
    }

    [TestMethod]
    public async Task TestTreeIdMatchesGit()
    {
        using var git = new GitClient();

        await git.RunAsync(null, "init", "-q", "repo");

        // "lib.txt" sorts before "lib/" as '.' < '/', "lib-a" sorts before both
        var files = new Dictionary<string, string>
        {
            ["lib.txt"] = "a",
            ["lib/inner.txt"] = "b",
            ["lib-a"] = "c",
            ["libz"] = "d",
            ["a/b/c/d.txt"] = "e",
            ["Upper.txt"] = "f",
            ["space name.txt"] = "g",
            ["ümlaut.txt"] = "h"
        };

        var builder = GitTree.Create();

        foreach (var (path, content) in files)
        {
            git.Write("repo", path, content);
            builder.Add(path, content);
        }

        git.Write("repo", "run.sh", "#!/bin/sh\n");
        builder.Add("run.sh", "#!/bin/sh\n", GitFileMode.Executable);

        await git.RunAsync("repo", "add", "-A");
        await git.RunAsync("repo", "update-index", "--chmod=+x", "run.sh");

        var expected = (await git.RunAsync("repo", "write-tree")).Trim();

        Assert.AreEqual(expected, (await builder.Build().ComputeIdAsync()).ToString());
    }

    [TestMethod]
    public async Task TestSymlinkTreeIdMatchesGit()
    {
        using var git = new GitClient();

        await git.RunAsync(null, "init", "-q", "repo");

        git.Write("repo", "target.txt", "content");
        git.Write("repo", "link-content", "target.txt");

        // a symbolic link stores the path it points to as its content
        var link = (await git.RunAsync("repo", "hash-object", "-w", "link-content")).Trim();

        await git.RunAsync("repo", "update-index", "--add", "--cacheinfo", $"120000,{link},link");
        await git.RunAsync("repo", "update-index", "--add", "target.txt");

        var expected = (await git.RunAsync("repo", "write-tree")).Trim();

        var tree = GitTree.Create()
                          .Add("target.txt", "content")
                          .Add("link", "target.txt", GitFileMode.Symlink)
                          .Build();

        Assert.AreEqual(expected, (await tree.ComputeIdAsync()).ToString());
    }

    [TestMethod]
    public async Task TestEmptyTree()
    {
        Assert.AreEqual("4b825dc642cb6eb9a060e54bf8d69288fbee4904", (await GitTree.Empty.ComputeIdAsync()).ToString());
    }

    [TestMethod]
    public async Task TestCommitIdMatchesGit()
    {
        using var git = new GitClient();

        await git.RunAsync(null, "init", "-q", "repo");

        git.Write("repo", "file.txt", "content\n");

        await git.RunAsync("repo", "add", "-A");

        var tree = (await git.RunAsync("repo", "write-tree")).Trim();

        var environment = new Dictionary<string, string>
        {
            ["GIT_AUTHOR_NAME"] = "Jane Doe",
            ["GIT_AUTHOR_EMAIL"] = "jane@example.com",
            ["GIT_AUTHOR_DATE"] = "1700000000 +0130",
            ["GIT_COMMITTER_NAME"] = "John Doe",
            ["GIT_COMMITTER_EMAIL"] = "john@example.com",
            ["GIT_COMMITTER_DATE"] = "1700000100 -0500"
        };

        var first = await git.TryRunAsync("repo", environment, "commit-tree", tree, "-m", "First commit");
        var second = await git.TryRunAsync("repo", environment, "commit-tree", tree, "-p", first.Output.Trim(), "-m", "Second\n\nWith a body");

        var author = new GitSignature("Jane Doe", "jane@example.com", new DateTimeOffset(2023, 11, 14, 22, 13, 20, TimeSpan.Zero).ToOffset(TimeSpan.FromMinutes(90)));
        var committer = new GitSignature("John Doe", "john@example.com", DateTimeOffset.FromUnixTimeSeconds(1700000100).ToOffset(TimeSpan.FromHours(-5)));

        var commit = GitCommit.Create()
                              .Tree(GitObjectId.Parse(tree))
                              .Author(author)
                              .Committer(committer)
                              .Message("First commit")
                              .Build();

        Assert.AreEqual(first.Output.Trim(), commit.Id.ToString());

        var child = GitCommit.Create()
                             .Tree(GitObjectId.Parse(tree))
                             .Parent(commit)
                             .Author(author)
                             .Committer(committer)
                             .Message("Second\n\nWith a body")
                             .Build();

        Assert.AreEqual(second.Output.Trim(), child.Id.ToString());

        Assert.AreEqual("Second", child.Subject);
        Assert.AreEqual(commit.Id, child.Parents.Single());
    }

    [TestMethod]
    public void TestCommitRoundTrip()
    {
        var commit = GitCommit.Create()
                              .Tree(GitObjectId.Parse("4b825dc642cb6eb9a060e54bf8d69288fbee4904"))
                              .Author("Jane Doe", "jane@example.com", new DateTimeOffset(2024, 1, 2, 3, 4, 5, TimeSpan.FromHours(2)))
                              .Message("Message")
                              .Build();

        var parsed = GitCommit.Parse(commit.Data);

        Assert.AreEqual(commit.Id, parsed.Id);
        Assert.AreEqual("Jane Doe", parsed.Author.Name);
        Assert.AreEqual("jane@example.com", parsed.Author.Email);
        Assert.AreEqual(commit.Author.When, parsed.Author.When);
        Assert.AreEqual(TimeSpan.FromHours(2), parsed.Author.When.Offset);
        Assert.AreEqual("Message\n", parsed.Message);
        Assert.AreEqual(0, parsed.Parents.Count);
    }

    [TestMethod]
    public void TestCommitWithSignatureCanBeParsed()
    {
        var data = """
            tree 4b825dc642cb6eb9a060e54bf8d69288fbee4904
            author A <a@example.com> 1700000000 +0000
            committer B <b@example.com> 1700000000 +0000
            gpgsig -----BEGIN PGP SIGNATURE-----
             
             iQEzBAABCAAdFiEE
             -----END PGP SIGNATURE-----

            Signed

            """.ReplaceLineEndings("\n");

        var commit = GitCommit.Parse(Encoding.UTF8.GetBytes(data));

        Assert.AreEqual("Signed\n", commit.Message);
        Assert.AreEqual("B", commit.Committer.Name);
    }

    [TestMethod]
    public void TestMalformedCommitsAreRejected()
    {
        Assert.ThrowsExactly<FormatException>(() => GitCommit.Parse("author A <a> 1 +0000\n\nx"u8.ToArray()));
        Assert.ThrowsExactly<FormatException>(() => GitCommit.Parse("tree 4b825dc642cb6eb9a060e54bf8d69288fbee4904\n\nx"u8.ToArray()));
        Assert.ThrowsExactly<FormatException>(() => GitCommit.Parse("tree 4b825dc642cb6eb9a060e54bf8d69288fbee4904\nauthor A 1 +0000\ncommitter A <a> 1 +0000\n\nx"u8.ToArray()));
    }

}
