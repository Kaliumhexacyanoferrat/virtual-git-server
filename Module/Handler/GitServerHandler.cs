using System.IO.Compression;

using GenHTTP.Api.Content;
using GenHTTP.Api.Infrastructure;
using GenHTTP.Api.Protocol;

using GenHTTP.Modules.Git.Objects;
using GenHTTP.Modules.Git.Protocol;

using Microsoft.Extensions.Logging;

namespace GenHTTP.Modules.Git.Handler;

/// <summary>
/// Serves virtual git repositories via the smart HTTP protocol.
/// </summary>
/// <remarks>
/// Handles the endpoints a git client uses below the URL of a repository
/// (<c>info/refs</c>, <c>git-upload-pack</c> and <c>git-receive-pack</c>)
/// and returns no response for any other request, so the handler can be
/// combined with other content.
/// </remarks>
public sealed class GitServerHandler : IHandler
{

    private enum Endpoint
    {
        References,
        UploadPack,
        ReceivePack
    }

    private static readonly ReadOnlyMemory<byte> UploadPackSegment = "git-upload-pack"u8.ToArray();

    private static readonly ReadOnlyMemory<byte> ReceivePackSegment = "git-receive-pack"u8.ToArray();

    private static readonly ReadOnlyMemory<byte> InfoSegment = "info"u8.ToArray();

    private static readonly ReadOnlyMemory<byte> RefsSegment = "refs"u8.ToArray();

    #region Get-/Setters

    private Func<IRequest, ValueTask<IGitRepository?>>? Resolver { get; }

    private Func<IRequest, string, ValueTask<IGitRepository?>>? NamedResolver { get; }

    private GitServerOptions Options { get; }

    #endregion

    #region Initialization

    internal GitServerHandler(Func<IRequest, ValueTask<IGitRepository?>>? resolver, Func<IRequest, string, ValueTask<IGitRepository?>>? namedResolver, GitServerOptions options)
    {
        Resolver = resolver;
        NamedResolver = namedResolver;
        Options = options;
    }

    #endregion

    #region Functionality

    public ValueTask PrepareAsync(IServer server) => ValueTask.CompletedTask;

    public async ValueTask<IResponse?> HandleAsync(IRequest request)
    {
        var target = request.Header.Target;

        var offset = 0;

        string? name = null;

        if (NamedResolver != null)
        {
            var segment = target.Next(0);

            if (segment == null)
            {
                return null;
            }

            name = segment.Value.Decode();

            if (name.EndsWith(".git", StringComparison.Ordinal))
            {
                name = name[..^4];
            }

            // the name is URL decoded, so it could contain separators or refer
            // to parent directories - which is never a valid repository name
            if (name.Length == 0 || name is "." or ".." || name.AsSpan().IndexOfAny("/\\\0") >= 0 || name.Any(char.IsControl))
            {
                return null;
            }

            offset = 1;
        }

        var endpoint = GetEndpoint(target, offset);

        if (endpoint == null)
        {
            return null;
        }

        var method = request.Header.Method;

        if (endpoint == Endpoint.References ? method != RequestMethod.Get && method != RequestMethod.Head : method != RequestMethod.Post)
        {
            return null;
        }

        var repository = NamedResolver != null ? await NamedResolver(request, name!) : await Resolver!(request);

        if (repository == null)
        {
            return null;
        }

        var logger = request.Server.Logging.CreateLogger<GitServerHandler>();

        var context = new RepositoryContext(repository, new ContentCache(Options.ContentCacheSize));

        var version = GetProtocolVersion(request);

        return endpoint switch
        {
            Endpoint.References => await AdvertiseAsync(request, context, version, logger),
            Endpoint.UploadPack => await UploadPackAsync(request, context, version, logger),
            Endpoint.ReceivePack => await ReceivePackAsync(request, context, logger),
            _ => null
        };
    }

    private static Endpoint? GetEndpoint(IRequestTarget target, int offset)
    {
        var first = target.Next(offset);

        if (first == null)
        {
            return null;
        }

        var second = target.Next(offset + 1);

        if (second == null)
        {
            if (first.Value == UploadPackSegment)
            {
                return Endpoint.UploadPack;
            }

            if (first.Value == ReceivePackSegment)
            {
                return Endpoint.ReceivePack;
            }

            return null;
        }

        if (target.Next(offset + 2) == null && first.Value == InfoSegment && second.Value == RefsSegment)
        {
            return Endpoint.References;
        }

        return null;
    }

    private static int GetProtocolVersion(IRequest request)
    {
        var header = request.Header.Headers.GetEntry("Git-Protocol");

        if (header == null)
        {
            return 0;
        }

        var version = 0;

        foreach (var parameter in header.Split(':'))
        {
            if (parameter == "version=2")
            {
                version = 2;
            }
            else if (parameter == "version=1" && version < 1)
            {
                version = 1;
            }
        }

        return version;
    }

    #endregion

    #region Endpoints

    private async ValueTask<IResponse> AdvertiseAsync(IRequest request, RepositoryContext context, int version, ILogger logger)
    {
        var service = request.Header.Query.GetEntry("service");

        if (service is not ("git-upload-pack" or "git-receive-pack"))
        {
            return Text(request, ResponseStatus.Forbidden, "This server only supports the smart HTTP protocol, please use a recent version of git.");
        }

        if (service == "git-receive-pack")
        {
            if (context.Repository is not IWritableGitRepository)
            {
                return Text(request, ResponseStatus.Forbidden, "This repository does not accept pushes.");
            }

            return await RespondAsync(request, GitContentTypes.ReceivePackAdvertisement, logger, async () =>
            {
                var writer = await Advertisement.ReceivePackAsync(context, Options, version == 1 ? 1 : 0);
                return writer.CopyToAsync;
            });
        }

        return await RespondAsync(request, GitContentTypes.UploadPackAdvertisement, logger, async () =>
        {
            var writer = await Advertisement.UploadPackAsync(context, Options, version);
            return writer.CopyToAsync;
        });
    }

    private async ValueTask<IResponse> UploadPackAsync(IRequest request, RepositoryContext context, int version, ILogger logger)
    {
        var body = await ReadBodyAsync(request, Options.MaximumRequestSize);

        return await RespondAsync(request, GitContentTypes.UploadPackResult, logger, async () =>
        {
            if (version == 2)
            {
                return await UploadPack.HandleV2Async(context, Options, body);
            }

            return await UploadPack.HandleV0Async(context, Options, body);
        });
    }

    private async ValueTask<IResponse> ReceivePackAsync(IRequest request, RepositoryContext context, ILogger logger)
    {
        if (context.Repository is not IWritableGitRepository writable)
        {
            return Text(request, ResponseStatus.Forbidden, "This repository does not accept pushes.");
        }

        var body = await ReadBodyAsync(request, Options.MaximumPushSize);

        return await RespondAsync(request, GitContentTypes.ReceivePackResult, logger, () => new(ReceivePack.Handle(context, writable, Options, body, logger)));
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Prepares the response, converting errors into the format git
    /// clients understand ("fatal: remote error: ...").
    /// </summary>
    private static async ValueTask<IResponse> RespondAsync(IRequest request, ContentType type, ILogger logger, Func<ValueTask<Func<Stream, ValueTask>>> handler)
    {
        Func<Stream, ValueTask> writer;

        try
        {
            writer = await handler();
        }
        catch (ProtocolException e)
        {
            writer = new PktLineWriter().Error(e.Message).CopyToAsync;
        }
        catch (RepositoryException e)
        {
            logger.LogError(e, "Git repository returned inconsistent data");

            writer = new PktLineWriter().Error("the repository is inconsistent, see the server log for details").CopyToAsync;
        }
        catch (ProviderException)
        {
            throw;
        }
        catch (Exception e)
        {
            logger.LogError(e, "Failed to handle git request");

            writer = new PktLineWriter().Error("internal server error").CopyToAsync;
        }

        return request.Respond()
                      .Status(ResponseStatus.Ok)
                      .Header("Cache-Control", "no-cache, max-age=0, must-revalidate")
                      .Header("Pragma", "no-cache")
                      .Header("Expires", "Fri, 01 Jan 1980 00:00:00 GMT")
                      .Content(new GitContent(type, writer))
                      .Build();
    }

    private static IResponse Text(IRequest request, ResponseStatus status, string message)
    {
        var content = new GitContent(ContentType.TextPlain, async stream => await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(message + "\n")));

        return request.Respond()
                      .Status(status)
                      .Content(content)
                      .Build();
    }

    /// <summary>
    /// Reads the complete body of the request, decompressing it if needed.
    /// </summary>
    /// <remarks>
    /// Git compresses larger fetch requests with gzip. The content is
    /// detected by its magic number instead of the header, as a
    /// decompression concern might already have taken care of it.
    /// </remarks>
    private static async ValueTask<ReadOnlyMemory<byte>> ReadBodyAsync(IRequest request, long limit)
    {
        var body = request.GetBody();

        if (body == null)
        {
            return ReadOnlyMemory<byte>.Empty;
        }

        await using var stream = body.AsStream();

        var raw = await ReadLimitedAsync(stream, limit);

        if (raw.Length >= 2 && raw.Span[0] == 0x1F && raw.Span[1] == 0x8B)
        {
            await using var decompressed = new GZipStream(new MemoryStream(raw.ToArray()), CompressionMode.Decompress);

            return await ReadLimitedAsync(decompressed, limit);
        }

        return raw;
    }

    private static async ValueTask<ReadOnlyMemory<byte>> ReadLimitedAsync(Stream stream, long limit)
    {
        using var buffer = new MemoryStream();

        var chunk = new byte[81920];

        int read;

        while ((read = await stream.ReadAsync(chunk)) > 0)
        {
            if (buffer.Length + read > limit)
            {
                throw new ProviderException(ResponseStatus.RequestEntityTooLarge, $"The request exceeds the maximum size of {limit} bytes");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.GetBuffer().AsMemory(0, (int)buffer.Length);
    }

    #endregion

}
