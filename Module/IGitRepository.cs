namespace GenHTTP.Modules.Git;

/// <summary>
/// The content of a virtual repository served by a <see cref="GitServer" />.
/// </summary>
/// <remarks>
/// <para>
/// The server never stores anything - every request is answered by
/// asking this interface. Implementations translate their own data
/// model (e.g. the versions of a document) into references, commits
/// and trees.
/// </para>
/// <para>
/// Everything returned must be stable: a commit returned for an id
/// must always have this id (see <see cref="GitCommit" /> for how to
/// achieve this), and the tree returned for a commit must always
/// produce the tree id stored in the commit. The server verifies the
/// latter and refuses to serve inconsistent data.
/// </para>
/// <para>
/// To also accept pushes, implement <see cref="IWritableGitRepository" />.
/// </para>
/// </remarks>
public interface IGitRepository
{

    /// <summary>
    /// Lists the branches and tags of the repository.
    /// </summary>
    /// <remarks>
    /// Called once per request, so the returned instance does not need
    /// to be cached by the implementation.
    /// </remarks>
    ValueTask<GitReferences> GetReferencesAsync();

    /// <summary>
    /// Looks up the commit with the given id.
    /// </summary>
    /// <remarks>
    /// Invoked for the commits the server needs to traverse the history,
    /// but also for ids sent by clients, which may not exist. Return
    /// <c>null</c> for unknown ids. If the history of a repository has
    /// been truncated (e.g. old versions have been deleted), returning
    /// <c>null</c> for a parent of a commit makes this commit the shallow
    /// boundary of the repository, which clients can handle.
    /// Clients can fetch every commit this method returns by its id, even if no
    /// reference points to it - so do not return commits that must not be
    /// accessible anymore (e.g. of deleted drafts).
    /// </remarks>
    /// <param name="id">The id of the commit to be fetched</param>
    /// <returns>The commit with the given id, or null if it does not exist</returns>
    ValueTask<GitCommit?> GetCommitAsync(GitObjectId id);

    /// <summary>
    /// Returns the files of the given commit.
    /// </summary>
    /// <remarks>
    /// Only called when the content of a commit is actually needed,
    /// e.g. to send it to a client. Content of files can be loaded
    /// lazily (see <see cref="GitFile" />).
    /// </remarks>
    /// <param name="commit">A commit previously returned by <see cref="GetCommitAsync" /></param>
    ValueTask<GitTree> GetTreeAsync(GitCommit commit);

}
