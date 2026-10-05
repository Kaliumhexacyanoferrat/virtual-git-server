using System.IO.Compression;
using System.Reflection;

using GenHTTP.Api.Content;
using GenHTTP.Api.Infrastructure;
using GenHTTP.Api.Protocol;

using GenHTTP.Modules.Git.Protocol;

namespace GenHTTP.Modules.Git.Handler;

/// <summary>
/// Configures a handler serving virtual git repositories.
/// </summary>
public sealed class GitServerBuilder : IHandlerBuilder<GitServerBuilder>
{
    private readonly List<IConcernBuilder> _concerns = [];

    private Func<IRequest, ValueTask<IGitRepository?>>? _resolver;

    private Func<IRequest, string, ValueTask<IGitRepository?>>? _namedResolver;

    private string _agent = DefaultAgent;

    private long _maximumPushSize = 128L * 1024 * 1024;

    private int _maximumFilesPerCommit = 100_000;

    private long _maximumRequestSize = 16L * 1024 * 1024;

    private long _contentCacheSize = 64L * 1024 * 1024;

    private CompressionLevel _compression = CompressionLevel.Fastest;

    private static string DefaultAgent
    {
        get
        {
            var version = typeof(GitServerBuilder).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0";

            var plus = version.IndexOf('+');

            return $"genhttp-git/{(plus < 0 ? version : version[..plus])}";
        }
    }

    #region Functionality

    /// <summary>
    /// Serves the given repository below the path the handler is mounted at
    /// (e.g. <c>git clone https://example.com/repo</c> if the handler is
    /// added to a layout as "repo").
    /// </summary>
    /// <param name="repository">The repository to be served</param>
    public GitServerBuilder Repository(IGitRepository repository)
    {
        ArgumentNullException.ThrowIfNull(repository);

        return Repository(_ => new ValueTask<IGitRepository?>(repository));
    }

    /// <summary>
    /// Serves the repository returned by the given function, which is invoked
    /// for every request (e.g. to look up the repository by a key that has
    /// been routed by another handler, or to check the user of the request).
    /// </summary>
    /// <remarks>
    /// Return <c>null</c> to respond with "not found". To deny access,
    /// throw a <see cref="ProviderException" /> with an appropriate status.
    /// </remarks>
    /// <param name="resolver">The function returning the repository to be served</param>
    public GitServerBuilder Repository(Func<IRequest, ValueTask<IGitRepository?>> resolver)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _namedResolver = null;

        return this;
    }

    /// <summary>
    /// Serves multiple repositories, addressed by the first segment of the
    /// path below the handler (e.g. <c>git clone https://example.com/git/my-repo.git</c>
    /// if the handler is added to a layout as "git").
    /// </summary>
    /// <remarks>
    /// The function receives the URL decoded name of the repository without a ".git"
    /// suffix. Names that are empty, contain slashes, backslashes or control characters,
    /// or refer to "." or ".." are not passed to the function but answered with "not found".
    /// Return <c>null</c> to respond with "not found". To deny access,
    /// throw a <see cref="ProviderException" /> with an appropriate status.
    /// </remarks>
    /// <param name="resolver">The function returning the repository with the given name</param>
    public GitServerBuilder Repositories(Func<IRequest, string, ValueTask<IGitRepository?>> resolver)
    {
        _namedResolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _resolver = null;

        return this;
    }

    /// <summary>
    /// The agent announced to clients, which is used for statistics and
    /// debugging only (defaults to "genhttp-git/{version}").
    /// </summary>
    /// <param name="agent">The agent string, consisting of printable characters without spaces</param>
    public GitServerBuilder Agent(string agent)
    {
        ArgumentNullException.ThrowIfNull(agent);

        if (agent.Length == 0 || agent.Any(c => c <= ' ' || c >= 127))
        {
            throw new ArgumentException("The agent must consist of printable ASCII characters without spaces", nameof(agent));
        }

        _agent = agent;
        return this;
    }

    /// <summary>
    /// The maximum size of the data sent by a client with a single push
    /// (defaults to 128 MB).
    /// </summary>
    /// <remarks>
    /// Pushes are processed in memory, so this limit protects the server
    /// from running out of memory. The objects within the pushed data may
    /// unpack to up to eight times this size.
    /// </remarks>
    /// <param name="bytes">The maximum number of bytes</param>
    public GitServerBuilder MaximumPushSize(long bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytes);

        _maximumPushSize = bytes;
        return this;
    }

    /// <summary>
    /// The maximum number of files a pushed commit may consist of
    /// (defaults to 100,000).
    /// </summary>
    /// <remarks>
    /// Protects the server from pushes that reference the same directory
    /// over and over again, which take little space but expand to a huge
    /// number of files.
    /// </remarks>
    /// <param name="count">The maximum number of files</param>
    public GitServerBuilder MaximumFilesPerCommit(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);

        _maximumFilesPerCommit = count;
        return this;
    }

    /// <summary>
    /// The maximum size of a request sent by a fetching client (defaults to 16 MB).
    /// </summary>
    /// <remarks>
    /// Fetch requests list the commits a client wants and has, so they
    /// are typically small.
    /// </remarks>
    /// <param name="bytes">The maximum number of bytes</param>
    public GitServerBuilder MaximumRequestSize(long bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytes);

        _maximumRequestSize = bytes;
        return this;
    }

    /// <summary>
    /// How much file content may be kept in memory while a request is
    /// processed (defaults to 64 MB).
    /// </summary>
    /// <remarks>
    /// To send a file, the server needs to read it twice: once to compute
    /// its id (unless provided, see <see cref="GitFile.Id" />) and once to
    /// send it. Files are kept in memory in between until this budget is
    /// exhausted, afterwards they are read again.
    /// </remarks>
    /// <param name="bytes">The maximum number of bytes</param>
    public GitServerBuilder ContentCache(long bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);

        _contentCacheSize = bytes;
        return this;
    }

    /// <summary>
    /// How strongly objects are compressed when sent to clients
    /// (defaults to <see cref="CompressionLevel.Fastest" />).
    /// </summary>
    /// <param name="level">The compression level to be applied</param>
    public GitServerBuilder Compression(CompressionLevel level)
    {
        _compression = level;
        return this;
    }

    public GitServerBuilder Add(IConcernBuilder concern)
    {
        _concerns.Add(concern);
        return this;
    }

    public IHandler Build()
    {
        if (_resolver == null && _namedResolver == null)
        {
            throw new BuilderMissingPropertyException("Repository");
        }

        var options = new GitServerOptions(_agent, _maximumPushSize, _maximumFilesPerCommit, _maximumRequestSize, _contentCacheSize, _compression);

        return Concerns.Chain(_concerns, new GitServerHandler(_resolver, _namedResolver, options));
    }

    #endregion

}
