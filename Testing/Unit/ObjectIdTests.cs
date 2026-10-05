namespace GenHTTP.Modules.Git.Tests.Unit;

[TestClass]
public sealed class ObjectIdTests
{
    private const string Hex = "0123456789abcdef0123456789abcdef01234567";

    [TestMethod]
    public void TestRoundTrip()
    {
        var id = GitObjectId.Parse(Hex);

        Assert.AreEqual(Hex, id.ToString());
        Assert.AreEqual(id, GitObjectId.FromBytes(id.ToArray()));
        Assert.AreEqual(id, GitObjectId.Parse(Hex.ToUpperInvariant()));
        Assert.IsFalse(id.IsZero);
    }

    [TestMethod]
    public void TestZero()
    {
        Assert.IsTrue(GitObjectId.Zero.IsZero);
        Assert.AreEqual(new string('0', 40), GitObjectId.Zero.ToString());
    }

    [TestMethod]
    public void TestInvalidIds()
    {
        Assert.IsFalse(GitObjectId.TryParse("abc", out _));
        Assert.IsFalse(GitObjectId.TryParse(Hex[..^1] + "x", out _));
        Assert.IsFalse(GitObjectId.TryParse((string?)null, out _));

        Assert.ThrowsExactly<FormatException>(() => GitObjectId.Parse("nope"));
    }

    [TestMethod]
    public void TestOrdering()
    {
        var low = GitObjectId.Parse("0000000000000000000000000000000000000001");
        var high = GitObjectId.Parse("1000000000000000000000000000000000000000");

        Assert.IsLessThan(0, low.CompareTo(high));
        Assert.AreNotEqual(low, high);
        Assert.IsTrue(low != high);
    }

}
