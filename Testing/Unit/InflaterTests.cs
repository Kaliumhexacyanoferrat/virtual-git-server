using System.IO.Compression;

using GenHTTP.Modules.Git.Packs;

namespace GenHTTP.Modules.Git.Tests.Unit;

[TestClass]
public sealed class InflaterTests
{

    public static IEnumerable<object[]> Inputs()
    {
        var random = new Random(42);

        foreach (var size in new[] { 0, 1, 7, 100, 1_000, 65_535, 70_000, 300_000 })
        {
            foreach (var kind in new[] { "random", "text", "zeros" })
            {
                var data = new byte[size];

                switch (kind)
                {
                    case "random":
                        random.NextBytes(data);
                        break;
                    case "text":
                        for (var i = 0; i < size; i++)
                        {
                            data[i] = (byte)"The quick brown fox jumps over the lazy dog. "[i % 45];
                        }
                        break;
                }

                foreach (var level in new[] { CompressionLevel.NoCompression, CompressionLevel.Fastest, CompressionLevel.Optimal, CompressionLevel.SmallestSize })
                {
                    yield return [data, level];
                }
            }
        }
    }

    [TestMethod]
    [DynamicData(nameof(Inputs))]
    public void TestManagedInflaterMatchesZLib(byte[] data, CompressionLevel level)
    {
        var compressed = Compress(data, level);

        // trailing data must not be consumed
        var input = compressed.Concat(new byte[] { 1, 2, 3 }).ToArray();

        var output = new byte[data.Length];

        var consumed = ManagedInflater.Inflate(input, output);

        Assert.AreEqual(compressed.Length, consumed);
        CollectionAssert.AreEqual(data, output);
    }

    [TestMethod]
    [DynamicData(nameof(Inputs))]
    public void TestPlatformInflaterMatchesZLib(byte[] data, CompressionLevel level)
    {
        var compressed = Compress(data, level);

        var input = compressed.Concat(new byte[] { 1, 2, 3 }).ToArray();

        var output = new byte[data.Length];

        Assert.AreEqual(compressed.Length, Zlib.Inflate(input, output));
        CollectionAssert.AreEqual(data, output);
    }

    [TestMethod]
    public void TestSizeMismatchIsDetected()
    {
        var compressed = Compress("Hello World"u8.ToArray(), CompressionLevel.Optimal);

        Assert.ThrowsExactly<InvalidDataException>(() => ManagedInflater.Inflate(compressed, new byte[5]));
        Assert.ThrowsExactly<InvalidDataException>(() => ManagedInflater.Inflate(compressed, new byte[20]));

        Assert.ThrowsExactly<InvalidDataException>(() => Zlib.Inflate(compressed, new byte[5]));
        Assert.ThrowsExactly<InvalidDataException>(() => Zlib.Inflate(compressed, new byte[20]));
    }

    [TestMethod]
    public void TestCorruptionIsDetected()
    {
        var data = new byte[10_000];
        new Random(1).NextBytes(data);

        var compressed = Compress(data, CompressionLevel.Optimal);

        var truncated = compressed[..^10];

        Assert.ThrowsExactly<InvalidDataException>(() => ManagedInflater.Inflate(truncated, new byte[data.Length]));
        Assert.ThrowsExactly<InvalidDataException>(() => Zlib.Inflate(truncated, new byte[data.Length]));

        var checksum = compressed.ToArray();
        checksum[^1] ^= 0xFF;

        Assert.ThrowsExactly<InvalidDataException>(() => ManagedInflater.Inflate(checksum, new byte[data.Length]));
        Assert.ThrowsExactly<InvalidDataException>(() => Zlib.Inflate(checksum, new byte[data.Length]));

        Assert.ThrowsExactly<InvalidDataException>(() => ManagedInflater.Inflate(new byte[] { 0x12, 0x34, 0, 0, 0, 0 }, new byte[1]));
    }

    private static byte[] Compress(byte[] data, CompressionLevel level)
    {
        if (data.Length == 0)
        {
            // .NET does not write anything for empty input, while zlib (and therefore git)
            // emits a complete stream - either with a fixed or with a stored block
            return level == CompressionLevel.NoCompression ? [0x78, 0x01, 0x01, 0x00, 0x00, 0xFF, 0xFF, 0x00, 0x00, 0x00, 0x01] : [0x78, 0x9C, 0x03, 0x00, 0x00, 0x00, 0x00, 0x01];
        }

        using var target = new MemoryStream();

        using (var zlib = new ZLibStream(target, level, true))
        {
            zlib.Write(data);
        }

        return target.ToArray();
    }

}
