namespace GenHTTP.Modules.Git;

/// <summary>
/// The kinds of files a git tree can hold.
/// </summary>
/// <remarks>
/// Git does not store permissions or timestamps, only whether a file
/// is executable or a symbolic link. Directories are implied by the
/// paths of the files, and empty directories cannot be represented.
/// Submodules are not supported.
/// </remarks>
public enum GitFileMode
{

    /// <summary>
    /// A regular file (mode 100644).
    /// </summary>
    Regular,

    /// <summary>
    /// An executable file (mode 100755).
    /// </summary>
    Executable,

    /// <summary>
    /// A symbolic link (mode 120000), with the content being the path the link points to.
    /// </summary>
    Symlink

}
