using GenHTTP.Modules.Git.Packs;

namespace GenHTTP.Modules.Git.Tests.Unit;

[TestClass]
public sealed class DeltaTests
{

    [TestMethod]
    public void TestCopyAndInsert()
    {
        var source = "Hello World"u8.ToArray();

        byte[] delta =
        [
            11, // source size
            13, // target size
            0x91, 0x00, 0x05, // copy offset 0, size 5 ("Hello")
            0x02, (byte)',', (byte)' ', // insert ", "
            0x91, 0x06, 0x05, // copy offset 6, size 5 ("World")
            0x01, (byte)'!' // insert "!"
        ];

        var result = DeltaApplier.Apply(source, delta, 1000);

        CollectionAssert.AreEqual("Hello, World!"u8.ToArray(), result);
    }

    [TestMethod]
    public void TestInvalidDeltas()
    {
        var source = "Hello"u8.ToArray();

        Assert.ThrowsExactly<InvalidDataException>(() => DeltaApplier.Apply(source, [4, 1, 0x01, (byte)'x'], 1000));
        Assert.ThrowsExactly<InvalidDataException>(() => DeltaApplier.Apply(source, [5, 10, 0x91, 0x00, 0x0A], 1000));
        Assert.ThrowsExactly<InvalidDataException>(() => DeltaApplier.Apply(source, [5, 2, 0x00], 1000));
        Assert.ThrowsExactly<InvalidDataException>(() => DeltaApplier.Apply(source, [5, 2, 0x01, (byte)'x'], 1000));
        Assert.ThrowsExactly<InvalidDataException>(() => DeltaApplier.Apply(source, [5, 100], 10));
    }

}
