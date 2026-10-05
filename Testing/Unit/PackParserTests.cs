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
    [DataRow(true)]
    [DataRow(false)]
    public async Task TestPacksWithDeltasCanBeParsed(bool offsetDeltas)
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

        var objects = PackParser.Parse(pack, PackLimits.FromPushSize(100_000_000));

        var expected = (await git.RunAsync("repo", "rev-list", "--objects", "HEAD")).Split('\n', StringSplitOptions.RemoveEmptyEntries)
                                                                               .Select(l => GitObjectId.Parse(l[..40]))
                                                                               .ToHashSet();

        Assert.AreEqual(expected.Count, objects.Count);

        foreach (var id in expected)
        {
            Assert.IsTrue(objects.ContainsKey(id), $"Missing object {id}");

            var type = (await git.RunAsync("repo", "cat-file", "-t", id.ToString())).Trim();

            Assert.AreEqual(type, objects[id].Type.GetName());
        }
    }

    [TestMethod]
    public void TestCorruptPacksAreRejected()
    {
        var limits = PackLimits.FromPushSize(1_000_000);

        Assert.ThrowsExactly<InvalidDataException>(() => PackParser.Parse("NOPE"u8, limits));

        var pack = CreatePack((GitObjectType.Blob, "Hello"u8.ToArray()));

        var corrupt = pack.ToArray();
        corrupt[^1] ^= 1;

        Assert.ThrowsExactly<InvalidDataException>(() => PackParser.Parse(corrupt, limits));

        var parsed = PackParser.Parse(pack, limits);

        Assert.AreEqual(GitObjectId.ForBlob("Hello"u8), parsed.Keys.Single());
    }

    [TestMethod]
    public void TestLimitsAreEnforced()
    {
        var pack = CreatePack((GitObjectType.Blob, new byte[10_000]));

        Assert.ThrowsExactly<InvalidDataException>(() => PackParser.Parse(pack, new PackLimits(1_000, 1_000_000, 10, 10)));
        Assert.ThrowsExactly<InvalidDataException>(() => PackParser.Parse(pack, new PackLimits(1_000_000, 1_000, 10, 10)));
        Assert.ThrowsExactly<InvalidDataException>(() => PackParser.Parse(pack, new PackLimits(1_000_000, 1_000_000, 0, 10)));
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

        var exception = Assert.ThrowsExactly<InvalidDataException>(() => PackParser.Parse(pack.ToArray(), PackLimits.FromPushSize(1024L * 1024 * 1024)));

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
