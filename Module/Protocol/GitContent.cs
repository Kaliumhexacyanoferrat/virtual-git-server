using GenHTTP.Api.Protocol;

namespace GenHTTP.Modules.Git.Protocol;

/// <summary>
/// Streams a response of the git protocol.
/// </summary>
internal sealed class GitContent(ContentType type, Func<Stream, ValueTask> writer) : IResponseContent
{

    public ulong? Length => null;

    public ContentType? Type => type;

    public ReadOnlyMemory<byte>? Encoding => null;

    public ValueTask<ulong?> CalculateChecksumAsync() => new((ulong?)null);

    public async ValueTask WriteAsync(IResponseSink sink)
    {
        await writer(sink.Stream);
        await sink.Stream.FlushAsync();
    }

}

internal static class GitContentTypes
{

    public static readonly ContentType UploadPackAdvertisement = new("application/x-git-upload-pack-advertisement");

    public static readonly ContentType ReceivePackAdvertisement = new("application/x-git-receive-pack-advertisement");

    public static readonly ContentType UploadPackResult = new("application/x-git-upload-pack-result");

    public static readonly ContentType ReceivePackResult = new("application/x-git-receive-pack-result");

}
