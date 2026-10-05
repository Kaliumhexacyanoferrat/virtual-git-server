using GenHTTP.Api.Infrastructure;

namespace GenHTTP.Modules.Git;

/// <summary>
/// Collects the files of a <see cref="GitTree" />.
/// </summary>
public sealed class GitTreeBuilder : IBuilder<GitTree>
{
    private readonly List<GitFile> _files = [];

    #region Functionality

    /// <summary>
    /// Adds a text file, encoded as UTF-8 without a byte order mark.
    /// </summary>
    /// <param name="path">The path of the file (e.g. "src/Program.cs")</param>
    /// <param name="content">The content of the file</param>
    /// <param name="mode">The kind of file</param>
    public GitTreeBuilder Add(string path, string content, GitFileMode mode = GitFileMode.Regular) => Add(new GitFile(path, content, mode));

    /// <summary>
    /// Adds a file with the given content.
    /// </summary>
    /// <param name="path">The path of the file (e.g. "assets/logo.png")</param>
    /// <param name="content">The content of the file</param>
    /// <param name="mode">The kind of file</param>
    public GitTreeBuilder Add(string path, ReadOnlyMemory<byte> content, GitFileMode mode = GitFileMode.Regular) => Add(new GitFile(path, content, mode));

    /// <summary>
    /// Adds a file whose content will be loaded when needed.
    /// </summary>
    /// <param name="path">The path of the file (e.g. "assets/logo.png")</param>
    /// <param name="loader">The function to be invoked to read the content of the file</param>
    /// <param name="mode">The kind of file</param>
    /// <param name="id">The id of the blob, if known (see <see cref="GitFile.Id" />)</param>
    public GitTreeBuilder Add(string path, Func<ValueTask<ReadOnlyMemory<byte>>> loader, GitFileMode mode = GitFileMode.Regular, GitObjectId? id = null)
        => Add(new GitFile(path, loader, mode, id));

    /// <summary>
    /// Adds the given file.
    /// </summary>
    /// <param name="file">The file to be added</param>
    public GitTreeBuilder Add(GitFile file)
    {
        _files.Add(file);
        return this;
    }

    /// <summary>
    /// Adds all files of the given tree.
    /// </summary>
    /// <param name="tree">The tree to copy the files from</param>
    public GitTreeBuilder Add(GitTree tree)
    {
        _files.AddRange(tree.Files);
        return this;
    }

    public GitTree Build() => new([.. _files]);

    #endregion

}
