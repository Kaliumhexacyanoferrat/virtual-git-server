using System.Text;

using GenHTTP.Modules.Git.Protocol;

namespace GenHTTP.Modules.Git.Tests.Unit;

[TestClass]
public sealed class PktLineTests
{

    [TestMethod]
    public void TestRoundTrip()
    {
        var writer = new PktLineWriter();

        writer.Line("hello").Delimiter().Data("raw"u8).Flush();

        var data = writer.ToArray();

        Assert.AreEqual("000ahello\n00010007raw0000", Encoding.ASCII.GetString(data));

        var reader = new PktLineReader(data);

        Assert.AreEqual("hello", reader.Read().Text);
        Assert.AreEqual(PktLineKind.Delimiter, reader.Read().Kind);
        Assert.AreEqual("raw", reader.Read().Text);
        Assert.AreEqual(PktLineKind.Flush, reader.Read().Kind);
        Assert.AreEqual(PktLineKind.End, reader.Read().Kind);
    }

    [TestMethod]
    public void TestSideBandSplitsLargePayloads()
    {
        var writer = new PktLineWriter();

        writer.Band(1, new byte[70_000]);

        var reader = new PktLineReader(writer.ToArray());

        var first = reader.Read();
        var second = reader.Read();

        Assert.AreEqual(PktLineWriter.MaximumPacketSize - 4, first.Payload.Length);
        Assert.AreEqual(70_000 - (PktLineWriter.MaximumPacketSize - 5) + 1, second.Payload.Length);
        Assert.AreEqual(1, first.Payload.Span[0]);
    }

    [TestMethod]
    public void TestMalformedInput()
    {
        Assert.ThrowsExactly<ProtocolException>(() => new PktLineReader("00"u8.ToArray()).Read());
        Assert.ThrowsExactly<ProtocolException>(() => new PktLineReader("zzzz"u8.ToArray()).Read());
        Assert.ThrowsExactly<ProtocolException>(() => new PktLineReader("0003"u8.ToArray()).Read());
        Assert.ThrowsExactly<ProtocolException>(() => new PktLineReader("0010abc"u8.ToArray()).Read());
    }

}
