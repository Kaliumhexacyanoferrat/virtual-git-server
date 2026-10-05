using System.Text;

using GenHTTP.Modules.Git.Objects;

namespace GenHTTP.Modules.Git;

/// <summary>
/// A single file within a <see cref="GitTree" />.
/// </summary>
/// <remarks>
/// The content of a file can either be passed directly or be loaded
/// on demand, so that providers do not need to read files that are
/// never requested (e.g. when a client only lists the references of
/// a repository). The loader may be invoked more than once and must
/// return the same content every time.
/// </remarks>
public sealed class GitFile
{
    private static readonly UTF8Encoding Utf8 = new(false);

    private readonly ReadOnlyMemory<byte>? _content;

    private readonly Func<ValueTask<ReadOnlyMemory<byte>>>? _loader;

    #region Get-/Setters

    /// <summary>
    /// The path of the file within the tree, using slashes to separate
    /// directories (e.g. "assets/app.js").
    /// </summary>
    public string Path { get; }

    /// <summary>
    /// The name of the file, without the directory it is located in.
    /// </summary>
    public string Name
    {
        get
        {
            var index = Path.LastIndexOf('/');
            return index < 0 ? Path : Path[(index + 1)..];
        }
    }

    /// <summary>
    /// Whether this is a regular file, an executable or a symbolic link.
    /// </summary>
    public GitFileMode Mode { get; }

    /// <summary>
    /// The id of the blob holding the content of this file, if known
    /// without reading the content.
    /// </summary>
    /// <remarks>
    /// Set for all files received with a push. Providers may store this
    /// id along with the file and pass it back when serving the file,
    /// so the server does not need to read the content just to compute
    /// the id (which is required to calculate which objects a client
    /// already has). A wrong id will corrupt the repository for clients,
    /// so only set this if you know the id is correct.
    /// </remarks>
    public GitObjectId? Id { get; }

    /// <summary>
    /// Whether the content of this file is available without invoking a loader.
    /// </summary>
    internal bool IsLoaded => _content != null;

    #endregion

    #region Initialization

    /// <summary>
    /// Creates a file with the given content.
    /// </summary>
    /// <param name="path">The path of the file within the tree (e.g. "assets/app.js")</param>
    /// <param name="content">The content of the file</param>
    /// <param name="mode">The kind of file</param>
    public GitFile(string path, ReadOnlyMemory<byte> content, GitFileMode mode = GitFileMode.Regular)
        : this(path, mode, null)
    {
        _content = content;
    }

    /// <summary>
    /// Creates a file with the given text, encoded as UTF-8 without a byte order mark.
    /// </summary>
    /// <remarks>
    /// The text is stored as given, so line endings are not converted.
    /// </remarks>
    /// <param name="path">The path of the file within the tree (e.g. "src/Program.cs")</param>
    /// <param name="content">The content of the file</param>
    /// <param name="mode">The kind of file</param>
    public GitFile(string path, string content, GitFileMode mode = GitFileMode.Regular)
        : this(path, Utf8.GetBytes(content), mode)
    {

    }

    /// <summary>
    /// Creates a file whose content is loaded on demand.
    /// </summary>
    /// <param name="path">The path of the file within the tree (e.g. "assets/logo.png")</param>
    /// <param name="loader">The function to be invoked to read the content of the file</param>
    /// <param name="mode">The kind of file</param>
    /// <param name="id">The id of the blob, if known (see <see cref="Id" />)</param>
    public GitFile(string path, Func<ValueTask<ReadOnlyMemory<byte>>> loader, GitFileMode mode = GitFileMode.Regular, GitObjectId? id = null)
        : this(path, mode, id)
    {
        _loader = loader ?? throw new ArgumentNullException(nameof(loader));
    }

    /// <summary>
    /// Creates a file with the given content and a known blob id.
    /// </summary>
    internal GitFile(string path, ReadOnlyMemory<byte> content, GitFileMode mode, GitObjectId id)
        : this(path, mode, id)
    {
        _content = content;
    }

    private GitFile(string path, GitFileMode mode, GitObjectId? id)
    {
        Path = string.Join('/', PathRules.Split(path));

        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown file mode");
        }

        Mode = mode;
        Id = id;
    }

    /// <summary>
    /// Creates a copy of this file located at another path.
    /// </summary>
    internal GitFile WithPath(string path)
    {
        if (_content != null)
        {
            return Id != null ? new GitFile(path, _content.Value, Mode, Id.Value) : new GitFile(path, _content.Value, Mode);
        }

        return new GitFile(path, _loader!, Mode, Id);
    }

    #endregion

    #region Functionality

    /// <summary>
    /// Reads the content of this file.
    /// </summary>
    public ValueTask<ReadOnlyMemory<byte>> ReadAsync() => _content != null ? new(_content.Value) : _loader!();

    /// <summary>
    /// Reads the content of this file as UTF-8 encoded text.
    /// </summary>
    public async ValueTask<string> ReadStringAsync() => Utf8.GetString((await ReadAsync()).Span);

    public override string ToString() => Path;

    #endregion

}
