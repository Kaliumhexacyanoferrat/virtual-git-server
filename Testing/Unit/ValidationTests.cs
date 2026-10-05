using GenHTTP.Modules.Git.Objects;

namespace GenHTTP.Modules.Git.Tests.Unit;

[TestClass]
public sealed class ValidationTests
{

    [TestMethod]
    [DataRow("refs/heads/main")]
    [DataRow("refs/heads/feature/login")]
    [DataRow("refs/tags/v1.0")]
    [DataRow("refs/heads/fix-123_abc")]
    public void TestValidReferenceNames(string name) => Assert.IsTrue(GitReference.IsValidName(name));

    [TestMethod]
    [DataRow("main")]
    [DataRow("refs/heads/")]
    [DataRow("refs/heads/a..b")]
    [DataRow("refs/heads/a b")]
    [DataRow("refs/heads/a~1")]
    [DataRow("refs/heads/.hidden")]
    [DataRow("refs/heads/x.lock")]
    [DataRow("refs/heads/a//b")]
    [DataRow("refs/heads/a@{1}")]
    [DataRow("refs/heads/end.")]
    [DataRow("refs/heads/a:b")]
    public void TestInvalidReferenceNames(string name) => Assert.IsFalse(GitReference.IsValidName(name));

    [TestMethod]
    [DataRow("..")]
    [DataRow(".")]
    [DataRow(".git")]
    [DataRow(".GIT")]
    [DataRow(".git.")]
    [DataRow(".git ")]
    [DataRow("git~1")]
    [DataRow(".g‌it")]
    [DataRow("a\\b")]
    [DataRow("")]
    [DataRow(".git::$INDEX_ALLOCATION")]
    [DataRow("a:b")]
    public void TestInvalidFileNames(string name) => Assert.IsFalse(PathRules.IsValidName(name, out _));

    [TestMethod]
    public void TestLongDotGitAliases()
    {
        Assert.IsFalse(PathRules.IsValidName(".git" + new string('.', 300), out _));
        Assert.IsFalse(PathRules.IsValidName(".git" + new string(' ', 300), out _));
        Assert.IsTrue(PathRules.IsValidName(new string('a', 300), out _));
    }

    [TestMethod]
    [DataRow(".gitignore")]
    [DataRow(".github")]
    [DataRow("...")]
    [DataRow("git")]
    [DataRow("ümlaut")]
    public void TestValidFileNames(string name) => Assert.IsTrue(PathRules.IsValidName(name, out _));

    [TestMethod]
    public void TestTreeRejectsInvalidPaths()
    {
        Assert.ThrowsExactly<ArgumentException>(() => GitTree.Create().Add("a/../b", "x"));
        Assert.ThrowsExactly<ArgumentException>(() => GitTree.Create().Add(".git/config", "x"));
        Assert.ThrowsExactly<ArgumentException>(() => GitTree.Create().Add("a//b", "x"));
        Assert.ThrowsExactly<ArgumentException>(() => GitTree.Create().Add("a/", "x"));
    }

    [TestMethod]
    public void TestTreeRejectsConflicts()
    {
        Assert.ThrowsExactly<ArgumentException>(() => GitTree.Create().Add("a", "x").Add("a", "y").Build());
        Assert.ThrowsExactly<ArgumentException>(() => GitTree.Create().Add("a", "x").Add("a/b", "y").Build());
    }

    [TestMethod]
    public void TestTreeNormalizesLeadingSlash()
    {
        var tree = GitTree.Create().Add("/src/a.txt", "x").Build();

        Assert.AreEqual("src/a.txt", tree.Files.Single().Path);
        Assert.AreEqual("a.txt", tree.Files.Single().Name);
        Assert.IsNotNull(tree.GetFile("src/a.txt"));
    }

    [TestMethod]
    public void TestSignatureValidation()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new GitSignature("", "a@b.c", DateTimeOffset.Now));
        Assert.ThrowsExactly<ArgumentException>(() => new GitSignature("A <B>", "a@b.c", DateTimeOffset.Now));
        Assert.ThrowsExactly<ArgumentException>(() => new GitSignature("A", "a@b\n.c", DateTimeOffset.Now));

        var signature = new GitSignature("Jane", "jane@example.com", new DateTimeOffset(2024, 1, 1, 0, 0, 0, 500, TimeSpan.FromMinutes(-150)));

        Assert.AreEqual("Jane <jane@example.com> 1704076200 -0230", signature.ToString());
        Assert.AreEqual(0, signature.When.Millisecond);
    }

    [TestMethod]
    public void TestReferences()
    {
        var id = GitObjectId.Parse("0123456789abcdef0123456789abcdef01234567");

        var references = new GitReferences().Head("trunk")
                                            .Branch("trunk", id)
                                            .Tag("v1", id)
                                            .Add("refs/notes/commits", id);

        Assert.AreEqual("refs/heads/trunk", references.HeadTarget);
        Assert.AreEqual(id, references.GetHead()!.Target);
        Assert.AreEqual(3, references.Count);
        Assert.AreEqual("v1", references.Get("refs/tags/v1")!.ShortName);

        Assert.ThrowsExactly<ArgumentException>(() => references.Branch("trunk", id));
        Assert.ThrowsExactly<ArgumentException>(() => references.Branch("x", GitObjectId.Zero));
        Assert.ThrowsExactly<ArgumentException>(() => references.Branch("in valid", id));
    }

}
