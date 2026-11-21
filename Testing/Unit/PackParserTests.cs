using System.Diagnostics;
using System.Text;

using GenHTTP.Modules.Git.Objects;
using GenHTTP.Modules.Git.Packs;
using GenHTTP.Modules.Git.Tests.Infrastructure;

namespace GenHTTP.Modules.Git.Tests.Unit;

[TestClass]
public sealed class PackParserTests
{

    [TestMethod]
    [DataRow(true, long.MaxValue, 64L * 1024 * 1024)]
    [DataRow(false, long.MaxValue, 64L * 1024 * 1024)]
    [DataRow(true, 0L, 0L)]
    [DataRow(false, 0L, 0L)]
    public async Task TestPacksWithDeltasCanBeParsed(bool offsetDeltas, long memoryLimit, long cacheSize)
    {
        using var git = new GitClient();

        await git.RunAsync(null, "init", "-q", "repo");

        // similar content across commits makes git store deltas
        var random = new Random(7);

        var lines = Enumerable.Range(0, 2000).Select(i => $"line {i} {random.Next()}").ToList();

        for (var i = 0; i < 5; i++)
        {
            lines[random.Next(lines.Count)] = $"changed in {i}";
            lines.Add($"appended in {i}");

            git.Write("repo", "data.txt", string.Join('\n', lines));
            git.Write("repo", $"file{i}.txt", $"file {i}");
            git.Write("repo", "empty.txt", "");

            await git.CommitAllAsync("repo", $"Commit {i}");
        }

        var arguments = offsetDeltas ? "pack-objects --stdout --revs --delta-base-offset -q" : "pack-objects --stdout --revs -q";

        var pack = await CreatePackAsync(git, arguments, "HEAD\n");

        using var buffer = await BufferAsync(pack, memoryLimit);

        Assert.AreEqual(memoryLimit == 0, buffer.IsFileBacked);

        using var objects = PackParser.Parse(buffer, 0, PackLimits.FromPushSize(100_000_000), cacheSize);

        var expected = (await git.RunAsync("repo", "rev-list", "--objects", "HEAD")).Split('\n', StringSplitOptions.RemoveEmptyEntries)
                                                                               .Select(l => GitObjectId.Parse(l[..40]))
                                                                               .ToHashSet();

        Assert.AreEqual(expected.Count, objects.Count);

        foreach (var id in expected)
        {
            var obj = objects.Get(id);

            Assert.IsNotNull(obj, $"Missing object {id}");

            var type = (await git.RunAsync("repo", "cat-file", "-t", id.ToString())).Trim();

            Assert.AreEqual(type, obj.Type.GetName());

            // the content is reconstructed from the buffer
            Assert.AreEqual(id, ObjectHasher.Hash(obj.Type, objects.Read(obj)));
        }
    }

    [TestMethod]
    public async Task TestCorruptPacksAreRejected()
    {
        var limits = PackLimits.FromPushSize(1_000_000);

        Assert.ThrowsExactly<InvalidDataException>(() => Parse("NOPE"u8.ToArray(), limits));

        var pack = CreatePack((GitObjectType.Blob, "Hello"u8.ToArray()));

        var corrupt = pack.ToArray();
        corrupt[^1] ^= 1;

        Assert.ThrowsExactly<InvalidDataException>(() => Parse(corrupt, limits));

        Assert.ThrowsExactly<InvalidDataException>(() => Parse(pack[..^5], limits));

        using var buffer = await BufferAsync(pack);

        using var parsed = PackParser.Parse(buffer, 0, limits, 0);

        Assert.AreEqual(GitObjectId.ForBlob("Hello"u8), parsed.Ids.Single());
    }

    [TestMethod]
    public void TestLimitsAreEnforced()
    {
        var pack = CreatePack((GitObjectType.Blob, new byte[10_000]));

        Assert.ThrowsExactly<InvalidDataException>(() => Parse(pack, new PackLimits(1_000, 1_000_000, 10, 10)));
        Assert.ThrowsExactly<InvalidDataException>(() => Parse(pack, new PackLimits(1_000_000, 1_000, 10, 10)));
        Assert.ThrowsExactly<InvalidDataException>(() => Parse(pack, new PackLimits(1_000_000, 1_000_000, 0, 10)));
    }

    [TestMethod]
    public async Task TestPackCanStartWithinBuffer()
    {
        var pack = CreatePack((GitObjectType.Blob, "Hello"u8.ToArray()), (GitObjectType.Blob, "World"u8.ToArray()));

        using var buffer = await BufferAsync([.. "0000"u8.ToArray(), .. pack], 0);

        using var parsed = PackParser.Parse(buffer, 4, PackLimits.FromPushSize(1_000_000), 0);

        var world = parsed.Get(GitObjectId.ForBlob("World"u8))!;

        CollectionAssert.AreEqual("World"u8.ToArray(), parsed.Read(world));
    }

    [TestMethod]
    public async Task TestContentCannotBeReadAfterDisposal()
    {
        using var buffer = await BufferAsync(CreatePack((GitObjectType.Blob, "Hello"u8.ToArray())), 0);

        var parsed = PackParser.Parse(buffer, 0, PackLimits.FromPushSize(1_000_000), 0);

        var obj = parsed.Get(GitObjectId.ForBlob("Hello"u8))!;

        parsed.Dispose();

        Assert.ThrowsExactly<InvalidOperationException>(() => parsed.Read(obj));
    }

    [TestMethod]
    public async Task TestInefficientlyCompressedObjectsCanBeParsed()
    {
        // a valid zlib stream that is much larger than its content, as it
        // consists of thousands of empty blocks before the actual data
        var content = "Hello"u8.ToArray();

        var stream = new List<byte> { 0x78, 0x01 };

        for (var i = 0; i < 10_000; i++)
        {
            stream.AddRange(new byte[] { 0x00, 0x00, 0x00, 0xFF, 0xFF });
        }

        stream.AddRange(new byte[] { 0x01, (byte)content.Length, 0x00, (byte)~content.Length, 0xFF });
        stream.AddRange(content);

        var adler = Adler32(content);

        stream.AddRange(new[] { (byte)(adler >> 24), (byte)(adler >> 16), (byte)(adler >> 8), (byte)adler });

        var header = new byte[16];

        var length = PackWriter.WriteObjectHeader(header, GitObjectType.Blob, content.Length);

        var pack = new List<byte>();

        pack.AddRange("PACK"u8.ToArray());
        pack.AddRange(new byte[] { 0, 0, 0, 2, 0, 0, 0, 2 });
        pack.AddRange(header.Take(length));
        pack.AddRange(stream);

        // followed by another object, so the first one does not extend to the end of the pack
        pack.AddRange(CreatePack((GitObjectType.Blob, "World"u8.ToArray()))[12..^20]);

        pack.AddRange(System.Security.Cryptography.SHA1.HashData(pack.ToArray()));

        using var buffer = await BufferAsync(pack.ToArray(), 0);

        using var parsed = PackParser.Parse(buffer, 0, PackLimits.FromPushSize(1_000_000), 0);

        CollectionAssert.AreEqual(content, parsed.Read(parsed.Get(GitObjectId.ForBlob(content))!));
        CollectionAssert.AreEqual("World"u8.ToArray(), parsed.Read(parsed.Get(GitObjectId.ForBlob("World"u8))!));
    }

    [TestMethod]
    public async Task TestFilesAreNotKeptInMemory()
    {
        var random = new Random(3);

        var blobs = Enumerable.Range(0, 32).Select(_ =>
        {
            var data = new byte[512 * 1024];
            random.NextBytes(data);
            return (GitObjectType.Blob, data);
        });

        var pack = CreatePack(blobs.ToArray());

        using var buffer = await BufferAsync(pack, 0);

        // warm up the pools used for temporary buffers
        PackParser.Parse(buffer, 0, PackLimits.FromPushSize(pack.Length), 0).Dispose();

        var before = GC.GetAllocatedBytesForCurrentThread();

        using var parsed = PackParser.Parse(buffer, 0, PackLimits.FromPushSize(pack.Length), 0);

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.AreEqual(32, parsed.Count);

        // the pack consists of 16 MB of files
        Assert.IsLessThan(1024 * 1024, allocated);
    }

    [TestMethod]
    public void TestDeclaredSizesAreBoundedByData()
    {
        // a tiny pack claiming to contain a 512 MB object
        var header = new byte[16];

        var length = PackWriter.WriteObjectHeader(header, GitObjectType.Blob, 512L * 1024 * 1024);

        var pack = new List<byte>();

        pack.AddRange("PACK"u8.ToArray());
        pack.AddRange(new byte[] { 0, 0, 0, 2, 0, 0, 0, 1 });
        pack.AddRange(header.Take(length));
        pack.AddRange(new byte[] { 0x78, 0x9C, 0x03, 0x00 });
        pack.AddRange(System.Security.Cryptography.SHA1.HashData(pack.ToArray()));

        var before = GC.GetAllocatedBytesForCurrentThread();

        using var buffer = BufferAsync(pack.ToArray()).AsTask().GetAwaiter().GetResult();

        var exception = Assert.ThrowsExactly<InvalidDataException>(() => PackParser.Parse(buffer, 0, PackLimits.FromPushSize(1024L * 1024 * 1024), 0));

        StringAssert.Contains(exception.Message, "exceeds the compressed data");

        Assert.IsLessThan(1024 * 1024, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [TestMethod]
    public async Task TestWrittenPacksCanBeIndexedByGit()
    {
        using var git = new GitClient();

        await git.RunAsync(null, "init", "-q", "repo");

        var pack = CreatePack((GitObjectType.Blob, "Hello"u8.ToArray()), (GitObjectType.Blob, new byte[100_000]), (GitObjectType.Blob, []));

        File.WriteAllBytes(git.PathOf("pack.pack"), pack);

        await git.RunAsync("repo", "index-pack", "--strict", git.PathOf("pack.pack"));
    }

    private static ValueTask<SpooledBuffer> BufferAsync(byte[] data, long memoryLimit = long.MaxValue)
        => SpooledBuffer.ReadAsync(new MemoryStream(data), long.MaxValue, memoryLimit, null);

    private static void Parse(byte[] pack, PackLimits limits)
    {
        using var buffer = BufferAsync(pack).AsTask().GetAwaiter().GetResult();

        PackParser.Parse(buffer, 0, limits, 0).Dispose();
    }

    private static uint Adler32(ReadOnlySpan<byte> data)
    {
        uint a = 1, b = 0;

        foreach (var value in data)
        {
            a = (a + value) % 65521;
            b = (b + a) % 65521;
        }

        return (b << 16) | a;
    }

    private static byte[] CreatePack(params (GitObjectType Type, byte[] Data)[] objects)
    {
        using var stream = new MemoryStream();

        var task = PackWriter.WriteAsync(stream, objects.Select(o => new PackEntry(o.Type, o.Data)).ToList(), System.IO.Compression.CompressionLevel.Optimal);

        // writing to a memory stream completes synchronously
        Assert.IsTrue(task.IsCompletedSuccessfully);

        return stream.ToArray();
    }

    private static async Task<byte[]> CreatePackAsync(GitClient git, string arguments, string input)
    {
        var info = new ProcessStartInfo("git")
        {
            WorkingDirectory = git.PathOf("repo"),
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        foreach (var argument in arguments.Split(' '))
        {
            info.ArgumentList.Add(argument);
        }

        using var process = Process.Start(info)!;

        await process.StandardInput.WriteAsync(input);
        process.StandardInput.Close();

        using var output = new MemoryStream();

        await process.StandardOutput.BaseStream.CopyToAsync(output);
        await process.WaitForExitAsync();

        Assert.AreEqual(0, process.ExitCode, Encoding.UTF8.GetString(output.ToArray()));

        return output.ToArray();
    }

}
